﻿(*
Copyright (c) 2012, Jack Pappas
All rights reserved.

This code is provided under the terms of the 2-clause ("Simplified") BSD license.
See LICENSE.TXT for licensing details.
*)

//
namespace FSharpYacc.LR

open System.Diagnostics
open LanguagePrimitives
open FSharpYacc.Grammar
open AugmentedPatterns
open FSharpYacc.Analysis
open FSharpYacc.Graph


/// An LALR(1) item.
type internal Lalr1Item<'Nonterminal, 'Terminal
    when 'Nonterminal : comparison
    and 'Terminal : comparison> =
    LrItem<'Nonterminal, 'Terminal, Set<'Terminal>>

/// An LALR(1) parser state -- i.e., a set of LR(1) items.
type internal Lalr1ParserState<'Nonterminal, 'Terminal
    when 'Nonterminal : comparison
    and 'Terminal : comparison> =
    LrParserState<'Nonterminal, 'Terminal, Set<'Terminal>>

/// LALR(1) parser table generation state.
type internal Lalr1TableGenState<'Nonterminal, 'Terminal
    when 'Nonterminal : comparison
    and 'Terminal : comparison> =
    LrTableGenState<'Nonterminal, 'Terminal, Set<'Terminal>>

/// An LALR(1) parser table.
type Lalr1ParserTable<'Nonterminal, 'Terminal
    when 'Nonterminal : comparison
    and 'Terminal : comparison> =
    LrParserTable<
        AugmentedNonterminal<'Nonterminal>,
        AugmentedTerminal<'Terminal>,
        Set<AugmentedTerminal<'Terminal>>>

/// <summary>LALR(1) parser table generator.</summary>
/// <remarks>Look-Ahead LR(1) (LALR(1)) parsing is a simplified form of LR(1) parsing;
/// it has "just enough" power to parse most languages while avoiding the huge
/// tables associated with canonical LR(1) parsers. A LALR(1) parser table has the
/// the same number of parser states (table rows) as an LR(0) or SLR(1) parser table
/// for the same grammar; the only difference is that the LALR(1) algorithm is able
/// to resolve more conflicts automatically than SLR(1) by using a more powerful algorithm
/// for computing lookaheads.</remarks>
[<RequireQualifiedAccess>]
module Lalr1 =
    module Graph = VertexLabeledSparseDigraph
    module BiGraph = VertexLabeledSparseBipartiteDigraph

    //
    type private TraversalStatus =
        //
        | Untraversed
        //
        | Traversing of int // depth
        //
        | Traversed

    //
    let rec private traverse (x, N, stack, F, X : Set<'T>, R, F' : Map<'T, Set<'U>>)
        : Map<_,_> * Map<_,_> * _ list =
        let stack = x :: stack
        let d = List.length stack
        let N = Map.add x (Traversing d) N
        let F =
            let ``F'(x)`` = Map.find x F'
            Map.add x ``F'(x)`` F

        // Find the 'y' values related to 'x' and compute xRy
        // by recursively traversing them.
        let F, N, stack =
            match Map.tryFind x R with
            | None ->
                F, N, stack
            | Some ``R(x)`` ->
                ((F, N, stack), ``R(x)``)
                ||> Set.fold (fun (F, N, stack) y ->
                    let F, N, stack =
                        match Map.find y N with
                        | Untraversed ->
                            traverse (y, N, stack, F, X, R, F')
                        | _ ->
                            F, N, stack

                    let N =
                        let ``N(x)`` = Map.find x N
                        let ``N(y)`` = Map.find y N
                        Map.add x (min ``N(x)`` ``N(y)``) N

                    let F =
                        match Map.tryFind y F with
                        | None -> F
                        | Some ``F(y)`` ->
                            let ``F(x)`` = Map.find x F
                            Map.add x (Set.union ``F(x)`` ``F(y)``) F

                    F, N, stack)

        // Walk back up the stack, if necessary.
        match Map.find x N with
        | Traversing depth when depth = d ->
            let ``F(x)`` = Map.find x F
            let rec unwind (F, N, stack) =
                match stack with
                | [] ->
                    failwith "Unexpectedly empty stack."
                | element :: stack ->
                    let N = Map.add element Traversed N
                    let F = Map.add element ``F(x)`` F

                    if element = x then
                        F, N, stack
                    else
                        unwind (F, N, stack)

            unwind (F, N, stack)

        | _ ->
            F, N, stack

    /// <summary>The 'digraph' algorithm from DeRemer and Pennello's paper.</summary>
    /// <remarks>This algorithm quickly computes set relations by 'condensing'
    /// a relation graph's strongly-connected components (SCCs), then performing
    /// a bottom-up traversal of the resulting DAG.</remarks>
    /// <param name="X">The set on which the relation is defined.</param>
    /// <param name="R">A relation on <paramref name="X"/>.</param>
    /// <param name="F'">A function from <paramref name="X"/> to sets.</param>
    /// <returns><c>F</c>, a function from X to sets, such that <c>F x</c> satisfies
    /// equation 4.1 in DeRemer and Pennello's paper.</returns>
    let private digraph (X : Set<'T>) (R : Map<'T, Set<'T>>) (F' : Map<'T, Set<'U>>) =
        //
        let N =
            (Map.empty, X)
            ||> Set.fold (fun N x ->
                Map.add x Untraversed N)

        ((Map.empty, N, []), X)
        ||> Set.fold (fun (F, N, stack) x ->
            match Map.find x N with
            | Untraversed ->
                traverse (x, N, stack, F, X, R, F')
            | _ ->
                F, N, stack)
        // Discard the intermediate variables
        |> fun (F, N, _) ->
            // DEBUG : Make sure all set elements have been completely traversed.
            #if DEBUG
            let untraversed =
                X |> Set.filter (fun x ->
                    match Map.find x N with Traversed -> false | _ -> true)
            Debug.Assert (
                Set.isEmpty untraversed,
                sprintf "There are %i elements of X (Count = %i) which have not been completely traversed." (Set.count untraversed) (Set.count X))
            #endif

            // Return the computed relation.
            F

    /// Computes the "direct read symbols" for each nonterminal transition; that is, it computes the set
    /// of terminals which label the out-edges of the state targeted by a nonterminal transition.
    let private directRead (lr0ParserTable : Lr0ParserTable<'Nonterminal, 'Terminal>) =
        (Map.empty, lr0ParserTable.GotoTable)
        ||> Map.fold (fun directRead transition succStateId ->
            // OPTIMIZE : Use a different data structure for the GOTO and ACTION tables
            // so this can be made into a simple lookup instead of having to traverse the ACTION table repeatedly.
            let directReadSymbols =
                (Set.empty, lr0ParserTable.ActionTable)
                ||> Map.fold (fun directReadSymbols (stateId, terminal) actions ->
                    if stateId = succStateId &&
                        actions |> Set.exists (function Shift _ | Accept -> true | _ -> false) then
                        Set.add terminal directReadSymbols
                    else
                        directReadSymbols)

            // Add the direct read symbols for this transition into the map.
            Map.add transition directReadSymbols directRead)

    // D. Compute 'Read' using the SCC-based transitive closure algorithm.
    // If a cycle is detected, announce that the grammar is not LR(k) for any 'k'.
    let private read (lr0ParserTable : Lr0ParserTable<'Nonterminal, 'Terminal>, nonterminalTransitions, nullable) =
        // B. Initialize 'Read' to DR. One set for each nonterminal transition,
        // by inspection of the transition's successor state.
        let directRead = directRead lr0ParserTable

        // C. Compute 'reads'. One set of nonterminal transitions per nonterminal transition,
        // by inspection of the successor state of the later transition.
        let reads =
            (Map.empty, lr0ParserTable.GotoTable)
            ||> Map.fold (fun reads transition succStateId ->
                (reads, lr0ParserTable.GotoTable)
                ||> Map.fold (fun reads ((stateId, nonterminal) as succTransition) _ ->
                    // We only care about successors of the original transition;
                    // also, the nonterminal for this (successor) transition must be nullable.
                    if stateId = succStateId &&
                        Map.find nonterminal nullable then
                        // Add the edge to the adjacency map representing the induced 'reads' graph.
                        let readsTransition =
                            match Map.tryFind transition reads with
                            | None ->
                                Set.singleton succTransition
                            | Some readsTransition ->
                                Set.add succTransition readsTransition

                        Map.add transition readsTransition reads
                    else
                        reads))

        //
        digraph nonterminalTransitions reads directRead

    //
    let private lookbackAndIncludes (grammar : AugmentedGrammar<'Nonterminal, 'Terminal>, lr0ParserTable : Lr0ParserTable<'Nonterminal, 'Terminal>, nonterminalTransitions, nullable) =
        ((Graph.empty, Graph.empty), nonterminalTransitions)
        ||> Set.fold (fun lookback_includes (stateId, nonterminal) ->
            //
            let parserState = Map.find stateId lr0ParserTable.ParserStates

            // Fold over the LR(0) items in this parser state.
            (lookback_includes, parserState)
            ||> Set.fold (fun (lookback, includes) item ->
                // Only consider items with rules which produce this nonterminal.
                if item.Nonterminal <> nonterminal then
                    lookback, includes
                else
                    // Add edges to the 'includes' relation graph.
                    let includes, j =
                        let rhsPositions = seq {
                            int item.Position .. Array.length item.Production - 1 }
                        ((includes, stateId), rhsPositions)
                        ||> Seq.fold (fun (includes, j) position ->
                            let t = item.Production.[position]
                            let includes =
                                // Only care about nonterminal transitions here
                                match t with
                                | Symbol.Terminal _ ->
                                    includes
                                | Symbol.Nonterminal t ->
                                    if Set.contains (j, t) nonterminalTransitions &&
                                        // At this point, we just need to check if the rest of the
                                        // right context of the production is nullable; if so, then
                                        // we can add the edge to the 'includes' graph.
                                        PredictiveSets.allNullableInSlice (item.Production, position + 1, Array.length item.Production - 1, nullable) then
                                            Graph.addEdgeAndVertices (stateId, nonterminal) (j, t) includes
                                    else
                                        includes

                            let j =
                                match t with
                                | Symbol.Terminal t ->
                                    lr0ParserTable.ActionTable
                                    |> Map.tryFind (j, t)
                                    |> Option.bind (fun actions ->
                                        // There can be at most one (1) Shift action in each set
                                        // of actions; if this set contains a Shift action,
                                        // get the 'target' state from it.
                                        (None, actions)
                                        ||> Set.fold (fun shift action ->
                                            match action with
                                            | Shift target ->
                                                Some target
                                            | _ -> shift))
                                | Symbol.Nonterminal t ->
                                    lr0ParserTable.GotoTable
                                    |> Map.tryFind (j, t)

                            // TODO : For safety and clarity, change this fold to use an F# option
                            // instead of representing the 'invalid' state as -1.
                            let j = defaultArg j -1<_>
                            includes, j)

                    // Add edges to the 'lookback' relation graph.
                    let lookback : VertexLabeledSparseBipartiteDigraph<_,_> =
                        if j = -1<_> then
                            lookback
                        else
                            // 'j' represents the final/last state of the path through the parser transition graph
                            // which describes the derivation of a rule (thereby producing a nonterminal).
                            (lookback, Map.find j lr0ParserTable.ParserStates)
                            ||> Set.fold (fun lookback item' ->
                                if item.Nonterminal = item'.Nonterminal
                                    && item.Production = item'.Production then
                                    let rule = item.Nonterminal, item.Production
                                    BiGraph.addEdgeAndVertices (stateId, nonterminal) (j, rule) lookback
                                else
                                    lookback)

                    // Pass 'lookback' and 'includes' through to the next iteration.
                    lookback, includes))

    //
    let private lookaheadSets (grammar : AugmentedGrammar<'Nonterminal, 'Terminal>, lr0ParserTable : Lr0ParserTable<'Nonterminal, 'Terminal>)
        : Choice<Map<_,_>, string> =
        (* DeRemer and Penello's algorithm for computing LALR look-ahead sets. *)

        /// Denotes which nonterminals are nullable.
        let nullable = PredictiveSets.computeNullable grammar.Productions

        /// The set of nonterminal transitions in the LR(0) parser table (i.e., the GOTO table).
        let nonterminalTransitions =
            (Set.empty, lr0ParserTable.GotoTable)
            ||> Map.fold (fun nonterminalTransitions transition _ ->
                Set.add transition nonterminalTransitions)

        // D. Compute 'Read' using the SCC-based transitive closure algorithm.
        // If a cycle is detected, announce that the grammar is not LR(k) for any 'k'.
        // TODO : Implement cycle detection.
        let Read = read (lr0ParserTable, nonterminalTransitions, nullable)

        // E. Compute 'includes' and 'lookback': one set of nonterminal transitions per
        // nonterminal transition and reduction, respectively, by inspection of each nonterminal
        // transition and the associated production right parts, and by considering
        // nullable nonterminals appropriately.
        let lookback, (includes : VertexLabeledSparseDigraph<NonterminalTransition<_>>) =
            lookbackAndIncludes (grammar, lr0ParserTable, nonterminalTransitions, nullable)
            
        // TEST
        let includes =
            (Map.empty, includes.Edges)
            ||> Set.fold (fun includes (source, target) ->
                let targetIncludes =
                    match Map.tryFind target includes with
                    | None ->
                        Set.singleton source
                    | Some targetIncludes ->
                        Set.add source targetIncludes

                Map.add target targetIncludes includes)

        // F. Compute the transitive closure of the 'includes' relation (via the SCC algorithm)
        // to compute 'Follow'. Use the same sets as initialized in part B and completed in part D,
        // both as initial values and as workspace. If a cycle is detected in which a Read set
        // is non-empty, announce that the grammar is not LR(k) for any 'k'.
        let Follow =
            // TODO : Fix this so it returns an error if the grammar is not LR(k).
            digraph nonterminalTransitions includes Read

        // TEMP : Create a map from the edges of the lookback graph
        // so it's easier to compute the LA sets.
        // TODO : Modify the 'includesAndLookback' function to
        // create relation maps instead of graphs.
        let lookback =
            (Map.empty, lookback.Edges)
            ||> Set.fold (fun lookback edge ->
                match edge with
                | Choice1Of2 (source : NonterminalTransition<_>), Choice2Of2 target ->
                    let targetSources =
                        match Map.tryFind target lookback with
                        | None ->
                            Set.singleton source
                        | Some targetSources ->
                            Set.add source targetSources

                    Map.add target targetSources lookback
                    
                | _ ->
                    failwith "Invalid edge.")

        // G. Union the Follow sets to form the LA sets according
        // to the 'lookback' links computed in part F.
        lookback
        |> Map.map (fun _ transitions ->
            (Set.empty, transitions)
            ||> Set.fold (fun lookaheadTokens transition ->
                Map.find transition Follow
                |> Set.union lookaheadTokens))
        |> Choice1Of2

    //
    let ofLr0Table (grammar : AugmentedGrammar<'Nonterminal, 'Terminal>, lr0ParserTable : Lr0ParserTable<'Nonterminal, 'Terminal>)
        : Choice<Lr0ParserTable<'Nonterminal, 'Terminal>, string> =
        // Compute the lookahead sets.
        // TODO : Simplify this by using the Either/Choice workflow.
        match lookaheadSets (grammar, lr0ParserTable) with
        | Choice2Of2 error ->
            Choice2Of2 error
        | Choice1Of2 lookaheadSets ->
            // Use the LALR(1) lookahead sets to resolve conflicts in the LR(0) parser table.
            // TODO

            // H. Check for conflicts; if there are none, the grammar is LALR(1).
            // TODO

            //
            raise <| System.NotImplementedException "Lalr1.ofLr0Table"

