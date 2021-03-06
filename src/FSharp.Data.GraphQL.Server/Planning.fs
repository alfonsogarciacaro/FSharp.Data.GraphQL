﻿/// The MIT License (MIT)
/// Copyright (c) 2016 Bazinga Technologies Inc

module FSharp.Data.GraphQL.Planning

open System
open System.Reflection
open System.Collections.Generic
open System.Collections.Concurrent
open FSharp.Data.GraphQL.Ast
open FSharp.Data.GraphQL.Types
open FSharp.Data.GraphQL.Types.Introspection
open FSharp.Data.GraphQL.Introspection

let SchemaMetaFieldDef = Define.Field(
    name = "__schema",
    description = "Access the current type schema of this server.",
    typedef = __Schema,
    resolve = fun ctx (_: obj) -> ctx.Schema.Introspected)
    
let TypeMetaFieldDef = Define.Field(
    name = "__type",
    description = "Request the type information of a single type.",
    typedef = __Type,
    args = [
        { Name = "name"
          Description = None
          Type = String
          DefaultValue = None
          ExecuteInput = variableOrElse(coerceStringInput >> Option.map box >> Option.toObj) }
    ],
    resolve = fun ctx (_:obj) -> 
        ctx.Schema.Introspected.Types 
        |> Seq.find (fun t -> t.Name = ctx.Arg("name")) 
        |> IntrospectionTypeRef.Named)
    
let TypeNameMetaFieldDef : FieldDef<obj> = Define.Field(
    name = "__typename",
    description = "The name of the current Object type at runtime.",
    typedef = String,
    resolve = fun ctx (_:obj) -> ctx.ParentType.Name)
        
let private tryFindDef (schema: ISchema) (objdef: ObjectDef) (field: Field) : FieldDef option =
        match field.Name with
        | "__schema" when Object.ReferenceEquals(schema.Query, objdef) -> Some (upcast SchemaMetaFieldDef)
        | "__type" when Object.ReferenceEquals(schema.Query, objdef) -> Some (upcast TypeMetaFieldDef)
        | "__typename" -> Some (upcast TypeNameMetaFieldDef)
        | fieldName -> objdef.Fields |> Map.tryFind fieldName
    
let private coerceVariables (schema: #ISchema) (variables: VariableDefinition list) (inputs: Map<string, obj> option) =
    match inputs with
    | None -> 
        variables
        |> List.filter (fun vardef -> Option.isSome vardef.DefaultValue)
        |> List.fold (fun acc vardef ->
            let variableName = vardef.VariableName
            Map.add variableName (coerceVariable schema vardef Map.empty) acc) Map.empty
    | Some vars -> 
        variables
        |> List.fold (fun acc vardef ->
            let variableName = vardef.VariableName
            Map.add variableName (coerceVariable schema vardef vars) acc) Map.empty

let objectData(ctx: PlanningContext, parentDef: ObjectDef, field: Field, includer: Includer) =
    match tryFindDef ctx.Schema parentDef field with
    | Some fdef ->
        { Identifier = field.AliasOrName
          Kind = ResolveValue
          ParentDef = parentDef
          Definition = fdef
          Ast = field
          Include = includer }
    | None ->
        raise (GraphQLException (sprintf "No field '%s' was defined in object definition '%s'" field.Name parentDef.Name))

let rec abstractionData (ctx:PlanningContext) (parentDef: AbstractDef) (field: Field) typeCondition includer =
    let objDefs = ctx.Schema.GetPossibleTypes parentDef
    match typeCondition with
    | None ->
        objDefs
        |> Array.choose (fun objDef ->
            match tryFindDef ctx.Schema objDef field with
            | Some fdef ->
                let data = 
                    { Identifier = field.AliasOrName
                      ParentDef = parentDef
                      Definition = fdef
                      Ast = field
                      Kind = ResolveAbstraction Map.empty
                      Include = includer }
                Some (objDef.Name, data)
            | None -> None)
        |> Map.ofArray
    | Some typeName ->
        match objDefs |> Array.tryFind (fun o -> o.Name = typeName) with
        | Some objDef ->
            match tryFindDef ctx.Schema objDef field with
            | Some fdef ->
                let data = 
                    { Identifier = field.AliasOrName
                      ParentDef = parentDef
                      Definition = fdef
                      Ast = field
                      Kind = ResolveAbstraction Map.empty
                      Include = includer }
                Map.ofList [ objDef.Name, data ]
            | None -> Map.empty
        | None -> 
            match ctx.Schema.TryFindType typeName with
            | Some (Abstract abstractDef) -> 
                abstractionData ctx abstractDef field None includer
            | _ ->
                let pname = parentDef :?> NamedDef
                raise (GraphQLException (sprintf "There is no object type named '%s' that is a possible type of '%s'" typeName pname.Name))
    
let private directiveIncluder (directive: Directive) : Includer =
    fun variables ->
        match directive.If.Value with
        | Variable vname -> downcast variables.[vname]
        | other -> 
            match coerceBoolInput other with
            | Some s -> s
            | None -> raise (
                GraphQLException (sprintf "Expected 'if' argument of directive '@%s' to have boolean value but got %A" directive.Name other))

let incl: Includer = fun _ -> true
let excl: Includer = fun _ -> false
let private getIncluder (directives: Directive list) topIncluder : Includer =
    directives
    |> List.fold (fun acc directive ->
        match directive.Name with
        | "skip" ->
            fun vars -> acc vars && not(directiveIncluder directive vars)
        | "include" -> 
            fun vars -> acc vars && (directiveIncluder directive vars)
        | _ -> acc) topIncluder

let private doesFragmentTypeApply (schema: ISchema) fragment (objectType: ObjectDef) = 
    match fragment.TypeCondition with
    | None -> true
    | Some typeCondition ->
        match schema.TryFindType typeCondition with
        | None -> false
        | Some conditionalType when conditionalType.Name = objectType.Name -> true
        | Some (Abstract conditionalType) -> schema.IsPossibleType conditionalType objectType
        | _ -> false
                
let rec private plan (ctx: PlanningContext) (info) (typedef: TypeDef) : ExecutionPlanInfo =
    match typedef with
    | Leaf leafDef -> planLeaf ctx info leafDef
    | Object objDef -> planSelection ctx { info with ParentDef = objDef } info.Ast.SelectionSet (ref [])
    | Nullable innerDef -> plan ctx info innerDef
    | List innerDef -> planList ctx info innerDef
    | Abstract abstractDef -> planAbstraction ctx { info with ParentDef = abstractDef } info.Ast.SelectionSet (ref []) None

and private planSelection (ctx: PlanningContext) (info) (selectionSet: Selection list) visitedFragments : ExecutionPlanInfo = 
    let parentDef = downcast info.ParentDef
    let plannedFields =
        selectionSet
        |> List.fold(fun (fields: ExecutionPlanInfo list) selection ->
            //FIXME: includer is not passed along from top level fragments (both inline and spreads)
            let includer = getIncluder selection.Directives info.Include
            let innerData = { info with Include = includer }
            match selection with
            | Field field ->
                let identifier = field.AliasOrName
                if fields |> List.exists (fun f -> f.Identifier = identifier) 
                then fields
                else 
                    let data = objectData(ctx, parentDef, field, includer)
                    let executionPlan = plan ctx data data.Definition.Type
                    fields @ [executionPlan]    // unfortunatelly, order matters here
            | FragmentSpread spread ->
                let spreadName = spread.Name
                if !visitedFragments |> List.exists (fun name -> name = spreadName) 
                then fields  // fragment already found
                else
                    visitedFragments := spreadName::!visitedFragments
                    match ctx.Document.Definitions |> List.tryFind (function FragmentDefinition f -> f.Name.Value = spreadName | _ -> false) with
                    | Some (FragmentDefinition fragment) when doesFragmentTypeApply ctx.Schema fragment parentDef ->
                        // retrieve fragment data just as it was normal selection set
                        let fragmentInfo = planSelection ctx innerData fragment.SelectionSet visitedFragments
                        let (SelectFields(fragmentFields)) = fragmentInfo.Kind
                        // filter out already existing fields
                        List.mergeBy (fun field -> field.Identifier) fields fragmentFields
                    | _ -> fields
            | InlineFragment fragment when doesFragmentTypeApply ctx.Schema fragment parentDef ->
                 // retrieve fragment data just as it was normal selection set
                 let fragmentInfo = planSelection ctx innerData fragment.SelectionSet visitedFragments
                 let (SelectFields(fragmentFields)) = fragmentInfo.Kind
                 // filter out already existing fields
                 List.mergeBy (fun field -> field.Identifier) fields fragmentFields
            | _ -> fields
        ) []
    { info with Kind = SelectFields plannedFields }

and private planList (ctx: PlanningContext) (info) (innerDef: TypeDef) : ExecutionPlanInfo =
    { info with Kind = ResolveCollection(plan ctx info innerDef) }

and private planLeaf (ctx: PlanningContext) (info) (leafDef: LeafDef) : ExecutionPlanInfo =
    info

and private planAbstraction (ctx:PlanningContext) (info) (selectionSet: Selection list) visitedFragments typeCondition : ExecutionPlanInfo =
    let parentDef = downcast info.ParentDef
    let plannedTypeFields =
        selectionSet
        |> List.fold(fun (fields: Map<string, ExecutionPlanInfo list>) selection ->
            let includer = getIncluder selection.Directives info.Include
            let innerData = { info with Include = includer }
            match selection with
            | Field field ->
                abstractionData ctx parentDef field typeCondition includer
                |> Map.map (fun typeName data -> [ plan ctx data data.Definition.Type ])
                |> Map.merge (fun typeName oldVal newVal -> oldVal @ newVal) fields
            | FragmentSpread spread ->
                let spreadName = spread.Name
                if !visitedFragments |> List.exists (fun name -> name = spreadName) 
                then fields  // fragment already found
                else
                    visitedFragments := spreadName::!visitedFragments
                    match ctx.Document.Definitions |> List.tryFind (function FragmentDefinition f -> f.Name.Value = spreadName | _ -> false) with
                    | Some (FragmentDefinition fragment) ->
                        // retrieve fragment data just as it was normal selection set
                        let fragmentInfo = planAbstraction ctx innerData fragment.SelectionSet visitedFragments fragment.TypeCondition
                        let (ResolveAbstraction(fragmentFields)) = fragmentInfo.Kind
                        // filter out already existing fields
                        Map.merge (fun typeName oldVal newVal -> oldVal @ newVal) fields fragmentFields
                    | _ -> fields
            | InlineFragment fragment ->
                 // retrieve fragment data just as it was normal selection set
                 let fragmentInfo = planAbstraction ctx innerData fragment.SelectionSet visitedFragments fragment.TypeCondition
                 let (ResolveAbstraction(fragmentFields)) = fragmentInfo.Kind
                 // filter out already existing fields
                 Map.merge (fun typeName oldVal newVal -> oldVal @ newVal) fields fragmentFields
            | _ -> fields
        ) Map.empty
    { info with Kind = ResolveAbstraction plannedTypeFields }

let planOperation (ctx: PlanningContext) (operation: OperationDefinition) : ExecutionPlan =
    let data = { 
        Identifier = null
        Kind = Unchecked.defaultof<ExecutionPlanKind>
        Ast = Unchecked.defaultof<Field>
        ParentDef = ctx.RootDef
        Definition = Unchecked.defaultof<FieldDef> 
        Include = incl }
    let resolvedInfo = planSelection ctx data operation.SelectionSet (ref [])
    let (SelectFields(topFields)) = resolvedInfo.Kind
    match operation.OperationType with
    | Query ->
        { Operation = operation 
          Fields = topFields
          RootDef = ctx.Schema.Query
          Strategy = Parallel }
    | Mutation ->
        match ctx.Schema.Mutation with
        | Some mutationDef ->
            { Operation = operation
              Fields = topFields
              RootDef = mutationDef
              Strategy = Serial }
        | None -> 
            raise (GraphQLException "Tried to execute a GraphQL mutation on schema with no mutation type defined")