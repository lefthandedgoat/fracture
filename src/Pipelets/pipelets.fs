﻿namespace Pipelets
    open System
    open System.Reflection
    open System.Collections.Concurrent
    open FSharp.Control
    
    type pipelet<'a,'b>(processor, router: seq<IPipeletInput<'b>> * 'b -> seq<IPipeletInput<'b>>, capacity, ?overflow, ?blockingTime) =
        let buffer = new BlockingCollection<'a>(capacity:int)
        let buffertwo = BlockingQueueAgent<_> capacity
        let routes = ref List.empty<IPipeletInput<'b>>
        let queuedOrRunning = ref false
        ///The blocking time for removing / adding an item to the blocking collection
        let blocktime =
            match blockingTime with
            | Some b -> b
            | None -> 250
        
        let consumerLoop = async {
            try
                while true do
                    let! result = async {
                                  let! takentwo = buffertwo.AsyncGet(blocktime)
                                  if takentwo.IsSome then
                                       return takentwo.Value |> processor |> Some
                                  else return None 
                                  }
                    if result.IsSome then
                        do result.Value |> Seq.iter (fun z -> 
                            (match !routes with 
                            | [] -> ()
                            | _ -> do router (!routes, z) |> Seq.iter (fun r -> (r.Insert z ))) )
                    else()
            with
            | _ as exc -> ()
        }

        member this.ClearRoutes = routes := []
        
        interface IPipeletInput<'a> with
            member this.Insert payload =
                Async.Start(async {try
                                        let! result = buffertwo.AsyncAdd(payload, blocktime)
                                        if result.IsSome then
                                            //begin consumer loop
                                            if not !queuedOrRunning then
                                                lock consumerLoop (fun() ->
                                                Async.Start(consumerLoop)
                                                queuedOrRunning := true)
                                            else()
                                        else if overflow.IsSome then payload |> overflow.Value
                                    with
                                    | _ as exc ->
                                        if overflow.IsSome then payload |> overflow.Value
                                    })
        
        interface IPipeletConnect<'b> with
            member this.Attach (stage) =
                let current = !routes
                routes := stage :: current
            member this.Detach (stage) =
                let current = !routes
                routes := List.filter (fun el -> el <> stage) current

        static member Attach (a:IPipeletConnect<_>) (b) = a.Attach b;b
        static member Detach (a: IPipeletConnect<_>) (b) = a.Detach b;a
        ///Connect operator
        static member (++>) (a:IPipeletConnect<_>, b) = a.Attach (b);b
        ///Detach operator
        static member (-->) (a:IPipeletConnect<_>, b) = a.Detach b;a
        ///Insert into leftoperator
        static member (<<--) (a:IPipeletInput<_>, b:'b) = a.Insert b
        ///Insert into right operator
        static member (-->>) (b,a:IPipeletInput<_>) = a.Insert b