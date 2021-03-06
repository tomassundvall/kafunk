﻿#r "bin/release/fsharp.control.asyncseq.dll"
#r "bin/Release/kafunk.dll"
#time "on"

open FSharp.Control
open Kafunk
open System
open System.Diagnostics
open System.Threading

let Log = Log.create __SOURCE_FILE__

let argiDefault i def = fsi.CommandLineArgs |> Seq.tryItem i |> Option.getOr def

let host = argiDefault 1 "localhost"
let topic = argiDefault 2 "absurd-topic"
let N = argiDefault 3 "1000000" |> Int64.Parse
let batchSize = argiDefault 4 "500" |> Int32.Parse
let messageSize = argiDefault 5 "100" |> Int32.Parse
let parallelism = argiDefault 6 "100" |> Int32.Parse

let volumeMB = (N * int64 messageSize) / int64 1000000
let payload = Array.zeroCreate messageSize
let batchCount = int (N / int64 batchSize)

Log.info "producer_run_starting|host=%s topic=%s messages=%i batch_size=%i batch_count=%i message_size=%i parallelism=%i MB=%i" host topic N batchSize batchCount messageSize parallelism volumeMB

let conn = Kafka.connHost host

let producerCfg =
  ProducerConfig.create (topic, Partitioner.roundRobin, requiredAcks=RequiredAcks.Local)

let producer =
  Producer.createAsync conn producerCfg
  |> Async.RunSynchronously

let sw = Stopwatch.StartNew()

let mutable completed = 0L

Async.Start (async {
  while true do
    do! Async.Sleep (1000 * 5)
    let completed = completed
    let mb = (int64 completed * int64 messageSize) / int64 1000000
    Log.info "completed=%i elapsed_sec=%f MB=%i" completed sw.Elapsed.TotalSeconds mb })

try

  let errMonitor = 
    FlowMonitor.escalateOnThreshold
      100
      (TimeSpan.FromSeconds 1.0)
      (Exn.ofSeq)

  Seq.init batchCount id
  |> Seq.map (fun is -> async {
    try
      let msgs = Array.init batchSize (fun i -> ProducerMessage.ofBytes payload)
      let! prodRes = Producer.produce producer msgs
      Interlocked.Add(&completed, int64 batchSize) |> ignore
      return ()
    with ex ->
      Log.error "%O" ex
      errMonitor ex
      return () })
  |> Async.ParallelThrottledIgnore parallelism
  |> Async.RunSynchronously
with ex ->
  Log.error "%O" ex

sw.Stop ()

let missing = N - completed
let ratePerSec = float completed / sw.Elapsed.TotalSeconds

Log.info "producer_run_completed|messages=%i missing=%i batch_size=%i message_size=%i parallelism=%i elapsed_sec=%f rate_per_sec=%f MB=%i" completed missing batchSize messageSize parallelism sw.Elapsed.TotalSeconds ratePerSec volumeMB

Thread.Sleep 2000