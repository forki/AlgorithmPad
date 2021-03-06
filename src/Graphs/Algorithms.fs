namespace Graphs

open System
open System.Collections.Generic 
open System.Collections
open Nessos.Streams


module Algorithms =          

    open Chessie.ErrorHandling
    open Graph   
    open Heaps

    type VisitedSet(graph: Graph) = 
        let flags = new BitArray(int32 graph.Vertices.Length)
        member this.Contains(vertexId: VertexId) : bool = flags.Get(int32 vertexId.Id)
        member this.Insert(vertexId: VertexId) = flags.Set(int32 vertexId.Id, true)

            
    /// Return whether a path exists between two vertices on a graph
    let pathExists graph (v1: VertexId) (v2: VertexId) : GraphResult<bool> = 
        let visitedSet = VisitedSet(graph)
                
        let rec explore (v: VertexId): GraphResult<unit> =             
            visitedSet.Insert(v)
            trial {
                let! vertex = vertexFromId graph v            
                for neighbour in vertex.Neighbours do 
                    if not (visitedSet.Contains(neighbour)) then 
                        do! explore neighbour // not tail recursive!
            } 
        
        trial {
            do! explore v1
            return visitedSet.Contains(v2)
        }
                    
    /// Return the connected components of graph.
    let connectedComponents (graph: Graph) : GraphResult<ResizeArray<ResizeArray<VertexId>>> =              
        let visitedSet = VisitedSet(graph)
        let mutable componentId = 0
        let componentGroups = new ResizeArray<ResizeArray<VertexId>>()
        
        let rec explore (v: VertexId): GraphResult<unit> = 
            visitedSet.Insert(v)
            componentGroups.[componentId].Add(v)
            trial {
                let! vertex = vertexFromId graph v    
                for neighbour in vertex.Neighbours do
                    if not (visitedSet.Contains(neighbour)) then 
                        do! explore neighbour 
            }

        trial {
            for v in verticesSeq graph do 
                if not (visitedSet.Contains(v.Identifier)) then
                    componentGroups.Add(new ResizeArray<VertexId>())
                    do! explore v.Identifier
                    componentId <- componentId + 1
            
            return componentGroups
        }

    /// Return a new graph with all edges reversed. All edges point from their edge destinations to their sources.
    /// An undirected graph is returned unchanged.
    let reverseDirectedGraph (graph: Graph) : Graph = 
        if graph.IsDirected then
                                           // Clear all weights and rebuild in the reverse copy 
            let reverseGraph = {graph with Weights = new Dictionary<_, _>(graph.Weights.Count)
                                           // Copy vertex Id but clear neighbours
                                           Vertices = [|for v in graph.Vertices do
                                                         yield {Identifier = v.Identifier; 
                                                                Neighbours = new ResizeArray<VertexId>()}|]}                                        
                                                                                                      
            for vertex in verticesSeq graph do                                                                     
                for neighbourVertexId in vertex.Neighbours do
                    if neighbourVertexId.Id < graph.VerticesCount then // In case the graph has some invalid entries
                        reverseGraph.Vertices.[neighbourVertexId.Id].Neighbours.Add(vertex.Identifier)
                        if graph.IsWeighted then
                            let w = graph.Weights.[(Source vertex.Identifier, Destination neighbourVertexId)]
                            reverseGraph.Weights.[(Source neighbourVertexId, Destination vertex.Identifier)] <- w
                                        
            reverseGraph
        else
            graph
          
    /// Is a graph directed and acyclic
    let isDAG (graph: Graph) : GraphResult<bool> = 
        
        // - Detect any back edges in the Depth First Search stack.
        //   There is a cycle in a graph only if there is a back edge present in the graph (self link or link to parent). 
        //   This involves checking whether a new node exists in the current dfs stack.
        // Note, another option would be to check if there are any strongly connected components with more than one vertex        
        let checkDAG graph =
            let visitedSet = VisitedSet(graph) 
            let dfsRecursionStackVertexIds: bool[] = Array.create (graph.VerticesCount + 1) false
            
            let rec explore (vertexId: VertexId): GraphResult<bool> =  
                if dfsRecursionStackVertexIds.[vertexId.Id] then 
                    ok true
                else 
                    visitThisThenExploreChildren vertexId
                
            and visitThisThenExploreChildren (vertexId: VertexId): GraphResult<bool> = 
                visitedSet.Insert(vertexId)
                dfsRecursionStackVertexIds.[vertexId.Id] <- true // this vertex is on the stack
                let mutable foundBackEdge = false 

                trial {
                    let! vertex = vertexFromId graph vertexId
                    
                    for neighbourVertexId in vertex.Neighbours do
                        if not foundBackEdge && not (visitedSet.Contains(neighbourVertexId)) then 
                            let! backEdgeExists = explore vertexId
                            foundBackEdge <- backEdgeExists
                            
                    dfsRecursionStackVertexIds.[vertexId.Id] <- false // this vertex has finished being on the stack                    
                    return foundBackEdge
                }
            
            trial {
                let mutable foundBackEdge = false
                for vertex in verticesSeq graph do
                    if not foundBackEdge && not (visitedSet.Contains(vertex.Identifier)) then
                        let! backEdgeExists = explore vertex.Identifier
                        foundBackEdge <- backEdgeExists

                return foundBackEdge
            }

        if graph.IsDirected then
            checkDAG graph
        else
            ok false

    /// Return an array that can be indexed by VertexId.Id where each element contains the depth first search
    /// pre and post order numbers. 0 is the first visited vertex. With each new node visited the number is incremented.
    let dfsPrePostOrderNumbers (graph: Graph) : GraphResult<(int * int)[]> = 
        let visitedSet = VisitedSet(graph)
        let visitOrderNumbers = Array.create (graph.VerticesCount + 1) (0, 0)
        let mutable visitNumber = 0

        let rec explore (vertexId: VertexId): GraphResult<unit> = 
            
            let preOrderVisitNumber = visitNumber
            visitedSet.Insert(vertexId)
            visitOrderNumbers.[vertexId.Id] <- (preOrderVisitNumber, 0)
            visitNumber <- visitNumber + 1

            trial {
                let! vertex = vertexFromId graph vertexId
                for neighbourVertexId in vertex.Neighbours do 
                    if not (visitedSet.Contains(neighbourVertexId)) then 
                        do! explore neighbourVertexId

                let postOrderVisitNumber = visitNumber
                visitOrderNumbers.[vertexId.Id] <- (preOrderVisitNumber, postOrderVisitNumber)
                visitNumber <- visitNumber + 1    
            }

        trial {
            for v in verticesSeq graph do
                if not (visitedSet.Contains(v.Identifier)) then
                    do! explore v.Identifier 

            return visitOrderNumbers
        }
        
    /// Return the strongly connected components of the graph. 
    /// Components are strongly connected when there is a cycle amongst them meaning that they can all reach each other somehow.
    let stronglyConnectedComponents (graph: Graph) : GraphResult<ResizeArray<ResizeArray<VertexId>>> = 
                
        let findComponents reverseGraph = 
            // The vertex with the largest dfs post order number is in a source component
            // To get the sink component we need to reverse the graph and find the largest
            // post order number
            // At that point exploring the graph (not reverse graph) in reverse post order
            // allows us to collect the strongly connected components
                        
            let visitedSet = VisitedSet(graph)
            let componentGroups = new ResizeArray<ResizeArray<VertexId>>()
                
            let rec explore (vertexId: VertexId) (currentComponentGroup: ResizeArray<VertexId>): GraphResult<unit> = 
                visitedSet.Insert(vertexId)
                currentComponentGroup.Add(vertexId)
                   
                trial {
                    let! vertex = vertexFromId graph vertexId
                    for neighbourVertexId in vertex.Neighbours do 
                        if not (visitedSet.Contains(neighbourVertexId)) then 
                            do! explore neighbourVertexId currentComponentGroup
                }                             
                
            trial {
                let! dfsOrderings = dfsPrePostOrderNumbers reverseGraph
                let reversePostOrderVertices = 
                    dfsOrderings
                    |> Stream.ofArray
                    |> Stream.mapi (fun index prePostOrderNums -> (VertexId index, prePostOrderNums))
                    |> Stream.skip 1 // ignore the 0 index vertex put in to make 1 based indexing easier                                                           
                    |> Stream.sortBy (fun (_, (_, post)) -> -post) // Negative/reverse post 
                    |> Stream.toArray

                for (vId, _) in reversePostOrderVertices do 
                    if not (visitedSet.Contains(vId)) then 
                        componentGroups.Add(new ResizeArray<VertexId>())
                        let newestComponentGroup = componentGroups.[componentGroups.Count - 1]
                        do! explore vId newestComponentGroup

                return componentGroups
            }

        in reverseDirectedGraph graph |> findComponents

    
    /// Return the topological ordering (source vertices before sink vertices) of a directed acyclic graph
    let topologicalOrdering (dag: Graph) : GraphResult<VertexId []> = 

        trial {
            // Read off the reverse post order numbers 
            let! dfsOrderings = dfsPrePostOrderNumbers dag
            let orderedVertexIds = 
                dfsOrderings
                |> Stream.ofArray
                |> Stream.mapi (fun index prePostOrderNums -> (VertexId index, prePostOrderNums))
                |> Stream.skip 1 // ignore the 0 index vertex put in to make 1 based indexing easier                                                           
                |> Stream.sortBy (fun (_, (_, post)) -> -post) // Negative/reverse post 
                |> Stream.map (fun (vId, _) -> vId)
                |> Stream.toArray
            return orderedVertexIds
        }
            

    /// Return a set of all the edges
    let edgesSet (graph: Graph) : GraphResult<Set<Edge>> = 
            
        /// The function that given a source vertex index and a destination vertex id returns
        /// (source index, destination index) pair        
        let edgeFrom : (VertexId -> VertexId -> Weight option -> Edge) = 
            if graph.IsDirected then
                (fun (vId: VertexId) (neighbourVertexId: VertexId) (w: Weight option) ->
                    match w with
                    | Some(weight) -> Edge(Source vId, Destination neighbourVertexId, weight)
                    | None -> Edge(Source vId, Destination neighbourVertexId))
            else
                // For undirected graphs we need to order the pairs so that the set
                // can remove duplicates - (1, 2) is really the same as (2, 1) when undirected
                let orderEdgeVertices (a, b) = 
                    if a <= b then (a, b) else (b, a)      
                              
                (fun (vId: VertexId) (neighbourVertexId: VertexId) (w: Weight option) ->
                    orderEdgeVertices (vId, neighbourVertexId)
                    |> 
                    // Source/Destination is arbitrary for undirected graphs, but needs to be consistently ordered
                    (fun (va, vb) -> 
                        match w with 
                        | Some(weight) -> Edge(Source va, Destination vb, weight)
                        | None -> Edge(Source va, Destination vb)))


        let allEdgesFrom (vertexIndex: int, neighbours: seq<VertexId>) : GraphResult<Stream<Edge>> =
            let vertexId = VertexId vertexIndex
            if graph.IsWeighted then                
                trial {                    
                    let! vertex = vertexFromId graph vertexId
                    let! neighbouringLinks = neighboursWithWeights graph vertex

                    let edgesToNeighbours : seq<Edge> =
                        seq { for neighbourId, weight in neighbouringLinks do
                              yield edgeFrom vertexId neighbourId (Some weight)}                    
                    let edgeStream = 
                        edgesToNeighbours |> Stream.cast
                    return edgeStream
                }
                
            else
                seq { for neighbourId in neighbours do 
                      yield edgeFrom vertexId neighbourId None }
                |> Stream.cast // seq to stream
                |> ok

        // Stream collect === flatmap : (T -> Stream<R>) -> Stream T -> Stream R
        // Result collect : seq<Result<T>> -> Result<T list>
        let (edgesFromNeighbours: Stream<GraphResult<Stream<Edge>>>) = 
                graph.Vertices
                |> Stream.ofArray
                |> Stream.mapi (fun index v -> (index, v.Neighbours)) // -> Stream<int * ResizeArray<VertexId>>)
                // Cannot flatmap as need to convert the Stream<GraphResult<Stream<Edge>>> into a single Result
                |> Stream.map allEdgesFrom

        let collectResults (es: Stream<GraphResult<Stream<Edge>>>) : GraphResult<Stream<Edge> list> = 
            es
            |> Stream.toSeq
            |> collect

        trial {
            let! (edges: Stream<Edge> list) = collectResults edgesFromNeighbours
            let (edgesSet: Set<Edge>) = 
                edges 
                |> Stream.ofList // -> Stream Stream Edge
                |> Stream.toSeq // -> Seq Stream Edge
                |> Stream.concat // -> Stream Edge
                |> Stream.toSeq // -> Seq Edge
                |> Set.ofSeq    
            
            return edgesSet
        }
          

    /// Run a breadth first search on an unweighted graph to find the shortest path tree to
    /// all other vertices from a source vertex. 
    let breadthFirstSearch (graph: Graph) (source: VertexId) : GraphResult<ShortestPaths> = 
        
        // Start of only knowing the distance to the source vertex. Every other vertex is unprocessed - None.
        let distances = Array.create (graph.VerticesCount + 1) None
        let pathTree = Array.create (graph.VerticesCount + 1) None
        distances.[source.Id] <- Some(Distance 0u)
        let q = new Queue<VertexId>(seq { yield source })  

        /// Increment a distance value
        let stepDistance (dist: Distance option): Distance option = 
            match dist with 
            | Some(d) -> Some (Distance <| d.Distance + 1u)
            | None -> failwith "Algorithm error, distance to previous point in path should not be None."
            
        trial {
            while q.Count > 0 do 
                let vId = q.Dequeue()
                let! vertex = vertexFromId graph vId
                for neighbourId in vertex.Neighbours do 
                    if distances.[neighbourId.Id].IsNone then
                        q.Enqueue(neighbourId)
                        let neighbourIndex = neighbourId.Id
                        let vIndex = vId.Id
                        distances.[neighbourIndex] <- stepDistance distances.[vIndex]
                        pathTree.[neighbourIndex] <- Some(vId)
            
            return         
                {Source = source
                 ShortestPathDistances = distances
                 ShortestPathTree = pathTree}
        }
     
    
    /// Return the shortest path from a source vertex defined by the breadth first search
    /// result data to a destination vertex.
    let shortestPath (pathData: ShortestPaths) (v: VertexId) : ResizeArray<VertexId> option = 

        if pathData.ShortestPathTree.[v.Id].IsSome then
            let path = new ResizeArray<VertexId>()
            let mutable currentPathStep = v
            while currentPathStep <> pathData.Source do 
                path.Add(currentPathStep)
                let pathIndex = currentPathStep.Id
                match pathData.ShortestPathTree.[pathIndex] with 
                | Some(nextStep) -> currentPathStep <- nextStep
                | None -> failwith "BFS algorithm error, reached a dead end in the path"
            
            path.Reverse()
            Some(path)
        else
            None

    /// Return true if the graph is bipartite. 
    /// A graph is bipartite if its vertices can be split into two groups such that each edge of the
    /// graph joins to vertices from the other group
    let isBipartite (graph: Graph) : GraphResult<bool> =
        
        // In other words, a graph is bipartite if its vertices can be colored with two colors
        // (say, black and white) such that the endpoints of each edge have different colors. 
        // Run BFS and store the colour of each vertex as we go along, changing it on each edge
        // traversal. If the colour is already set and does not match the expected colour coming
        // out of another vertex then it isn't bipartite.
        
        let twoColourings = Array.create (graph.VerticesCount + 1) Uncoloured
        let firstVertexIndex = 1
        twoColourings.[firstVertexIndex] <- Red
        let q = Queue<VertexId>(seq { yield VertexId firstVertexIndex})

        let oppositeColour colour = 
            match colour with 
            | Red -> Green
            | Green -> Red
            | Uncoloured -> Uncoloured

        trial {                        
            let mutable failedTwoColouring = false
            while q.Count > 0 && not failedTwoColouring do
                
                let vId = q.Dequeue()
                let vIndex = vId.Id
                let vColour = twoColourings.[vIndex]
                let requiredNeighboursColour = oppositeColour vColour
                
                let! vertex = vertexFromId graph vId
                
                for neighbourId in vertex.Neighbours do 
                    let neighbourIndex = neighbourId.Id
                    let neighbourColour = twoColourings.[neighbourIndex]
                    match neighbourColour with 
                    | Uncoloured ->
                        q.Enqueue(neighbourId)
                        twoColourings.[neighbourIndex] <- requiredNeighboursColour
                    | _ -> if neighbourColour <> requiredNeighboursColour then
                                failedTwoColouring <- true           

            return (not failedTwoColouring)
        }

    /// Calculate the shortest path from a source vertex to all other vertices on weighted graph 
    /// with non-negative weights. Uses the Djikstra algorithm.
    let nonNegativeWeightedSearch (graph: Graph) (source: VertexId) : GraphResult<ShortestPaths> = 
        
        let capacity = graph.VerticesCount + 1 // accounting for the 1-based indexing and unused entry[0] 
                
        let distances = Array.create capacity None // distance to all vertices from the source
        let pathTree = Array.create capacity None // parent vertices in shortest path tree for all vertexIds

        // The source is zero distance away, whilst all other vertices are considered infinite distance to begin with
        distances.[source.Id] <- Some(Distance 0u)

        // Use the distance values as the priority queue keys with min distance being highest priority
        let infiniteDistance = System.UInt32.MaxValue
        let distanceValue distanceOpt : Distance = 
            defaultArg distanceOpt (Distance infiniteDistance)

        let toShortestPathKey (index: int) (distance: Distance option) : ShortestPathPriorityKey = 
            ShortestPathPriorityKey(distanceValue distance, VertexId index)

        let shortestPathKeys = Stream.ofArray distances 
                               |> Stream.mapi (fun index distanceEntry -> toShortestPathKey index distanceEntry) 
                               |> Stream.toSeq
        let priorityQueue = DHeap.ofSeq Octonary // 8 entries fit in one 64 byte cache line
                                        MinKey 
                                        (Capacity <| uint64 capacity)
                                        shortestPathKeys
        trial {

            while not (DHeap.isEmpty priorityQueue) do

                // The vertex with the minimum distance from source is removed from the queue (and not put back)
                let! shortestPathKey = DHeap.extractHighestPriority priorityQueue |> heapToGraphResult
                let! vertex = vertexFromId graph shortestPathKey.Id

                // Look at all the edges linking out of the vertex
                let! neighbouringLinks = neighboursWithWeights graph vertex
                for neighbourId, edgeCostToNeighbour in neighbouringLinks do
                    // does this edge find a shorter path than currently known about for (source -> neighbour vertex)?
                    let distV = distances.[vertex.Identifier.Id] |> distanceValue
                    let distNeighbour = distances.[neighbourId.Id] |> distanceValue
                    let vertToNeighbourCost = Distance <| distV.Distance + (uint32 edgeCostToNeighbour.Value)

                    if distNeighbour > vertToNeighbourCost then
                        distances.[neighbourId.Id] <- Some vertToNeighbourCost
                        pathTree.[neighbourId.Id] <- Some vertex.Identifier
                        // At this point the Djikstra algorithm changes the priority queue to reprioritise
                        // neighbour vertex that has a new distance value
                        // As the "Priority Queues and Dijkstra�s Algorithm" paper http://www.cs.sunysb.edu/~rezaul/papers/TR-07-54.pdf
                        // shows, the cost of just adding a new entry with the changed priority is often less
                        // than the cost of making the priority queue more complicated to support the decreaseKey (change priority)
                        // operation.
                        // So, we just add a new entry in the priority queue, with higher priority than it had before
                        DHeap.insert priorityQueue (ShortestPathPriorityKey(vertToNeighbourCost, neighbourId))

            return {
                Source = source
                ShortestPathDistances = distances
                ShortestPathTree = pathTree
            }
        }
        

    /// Calculate the shortest path from a source vertex to all other vertices on a weighted graph
    /// that may have positive or negative weights. Uses the Bellman-Ford algorithm.
    let anyWeightsShortestPathSearch (graph: Graph) (source: VertexId) = 
        raise (NotImplementedException "")

    /// Return a failed GraphResult if not an undireced and weighted graph.
    let internal ensureUndirectedAndWeighted(graph: Graph) : GraphResult<unit> = 
        if graph.IsDirected then 
            fail (GraphInvalidTypeFailure Directed)
        else if not graph.IsWeighted then
            fail (GraphInvalidTypeFailure Unweighted)
        else
            ok ()

    /// Return the minimum spanning tree of a graph using Kruskal's algorithm    
    /// The minimum spanning tree is the subset of edges that connects all vertices together with
    /// the minimum total weight. Weights can be positive or negative.
    /// * Fails if the graph is unweighted and fails if the graph is directed.
    /// Complexity O(|E| log |V|). Simpler and faster for fairly sparse graph than Prim's algorithm 
    let minimumSpanningTreeKruskal (graph: Graph) : GraphResult<ResizeArray<Edge>> = 
        
        trial {
            do! ensureUndirectedAndWeighted graph

            // Each vertex in the graph starts out in its own singleton connected component subset
            let connectedComponentSets = DisjointSet.make (Size <| uint32 graph.VerticesCount + 1u) // 1-based index handling, sigh
            let mst = new ResizeArray<Edge>(graph.EdgesCount)

            // For all edges in the graph in increasing weight order                
            // Immutable F# Sets (AVL tree based) are sorted in natural increasing order. 
            let! edges = edgesSet graph
            let weightOrderedEdges = edges |> Seq.sortBy (fun edge -> edge.Weight)
            for edge in weightOrderedEdges do 
                let src = EntryId (uint32 edge.Source.VId.Id)
                let dst = EntryId (uint32 edge.Destination.VId.Id)
                let! areVerticesLinked = DisjointSet.inSameSubset connectedComponentSets src dst
                                            |> disjointToGraphResult   
                if not areVerticesLinked then
                    mst.Add(edge)
                    do! DisjointSet.union connectedComponentSets src dst
                        |> disjointToGraphResult

            return mst
        }

    /// Return the minimum spanning tree of a graph using Prim's algorithm
    /// The minimum spanning tree is the subset of edges that connects all vertices together with
    /// the minimum total weight. Weights can be positive or negative.
    /// * Fails if the graph is unweighted and fails if the graph is directed.
    /// Complexity O(|E| + log |V|). Better choice than Kruskal if the graph has lots of edges.
    let minimumSpanningTreePrim (graph: Graph) : GraphResult<ResizeArray<Edge>> = 
        
        trial {

            do! ensureUndirectedAndWeighted graph

            let mst = new ResizeArray<Edge>(graph.EdgesCount)

            let capacity = graph.VerticesCount + 1
            let edgePriorityQueue = DHeap.empty Quaternary
                                                MinKey 
                                                (Capacity <| uint64 capacity)

            // Lazy Prim version so we do not need a priority queue with a `decrease priority` operation
            let vertexIdsAddedToMST = VisitedSet(graph)

            let visit (vId: VertexId) : GraphResult<unit> = 
                vertexIdsAddedToMST.Insert(vId)
                trial {
                    let! vertex = vertexFromId graph vId
                    let! weightedNeighbours = neighboursWithWeights graph vertex
                    for neighbourId, weight in weightedNeighbours do
                        if not <| vertexIdsAddedToMST.Contains(neighbourId) then
                            let edge = Edge(Source vId, Destination neighbourId, weight)
                            // need to tweak natural ordering of pq to sort by weight
                            DHeap.insert edgePriorityQueue (weight, edge)
                
                }
            
            let arbitraryStartVertex = VertexId 1 // assumes the vertices are all connected somehow
            do! visit arbitraryStartVertex

            while not (DHeap.isEmpty edgePriorityQueue) do

                let! (_, edge) = DHeap.extractHighestPriority edgePriorityQueue |> heapToGraphResult
                let src, dst = edge.Source.VId, edge.Destination.VId

                let addedSrc = vertexIdsAddedToMST.Contains(src)
                let addedDst = vertexIdsAddedToMST.Contains(dst)
                if not (addedSrc && addedDst) then
                    mst.Add(edge)
                    if not addedSrc then
                        do! visit src
                    if not addedDst then
                        do! visit dst

            return mst
        } 