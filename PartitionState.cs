using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

class Program
{
    // Simulated "commit" coordinator per partition
    class PartitionState
    {
        public long NextExpectedOffset = 0; // next offset we must wait for (start at 0)
        public readonly ConcurrentDictionary<long, Task> InFlight = new();
        public readonly object CommitLock = new(); // protect commit advancement
        public long LastCommittedOffset = -1;
    }

    static async Task Main()
    {
        Console.WriteLine("ActionBlock comparison demo - 100 synthetic messages\n");

        // deterministic randomness for reproducibility
        var rng = new Random(12345);

        // Generate 100 synthetic messages with offsets 0..99 and random durations (ms)
        var messages = Enumerable.Range(0, 100)
            .Select(i => new { Offset = i, ProcessingMs = rng.Next(50, 2000) })
            .ToList();

        Console.WriteLine("Messages (offset -> ms) preview (first 10):");
        foreach (var m in messages.Take(10))
            Console.WriteLine($"  {m.Offset,3} -> {m.ProcessingMs} ms");
        Console.WriteLine();

        // Approach A: single ActionBlock with MaxDegreeOfParallelism = 5
        await RunSingleBlockApproach(messages, maxDegreeOfParallelism: 5);

        Console.WriteLine("\n--- pausing briefly before Approach B ---\n");
        await Task.Delay(1500);

        // Approach B: 5 ActionBlocks each MaxDegreeOfParallelism = 1, route by offset % 5
        await RunShardedBlockApproach(messages, shards: 5);

        Console.WriteLine("\nDemo complete.");
    }

    static async Task RunSingleBlockApproach(
        System.Collections.Generic.List<dynamic> messages,
        int maxDegreeOfParallelism)
    {
        Console.WriteLine("=== Approach A: ONE ActionBlock (MaxDegreeOfParallelism = " + maxDegreeOfParallelism + ") ===\n");

        var state = new PartitionState();
        var sw = Stopwatch.StartNew();

        var options = new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            BoundedCapacity = 100 // just an example
        };

        var block = new ActionBlock<dynamic>(async msg =>
        {
            var offset = (int)msg.Offset;
            Log("[A] Consumed", offset, sw);

            // Start processing
            var processingTask = ProcessMessageAsync(offset, (int)msg.ProcessingMs, "A", sw);

            // Register in-flight
            state.InFlight[offset] = processingTask;

            Log("[A] Started", offset, sw);

            try
            {
                await processingTask; // occupy a worker slot for duration
                Log("[A] Finished", offset, sw);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[A] Error processing {offset}: {ex}");
            }
            finally
            {
                TryAdvanceAndCommit(state, offset, sw, "A");
            }

        }, options);

        // Post all messages quickly to simulate a fast consumer
        foreach (var m in messages)
        {
            // Post will block when bounded capacity is full, providing backpressure
            await block.SendAsync(m);
        }

        // Mark complete and wait for all work to finish
        block.Complete();
        await block.Completion;

        sw.Stop();
        Console.WriteLine($"\n[A] Done. Last committed offset: {state.LastCommittedOffset}. Elapsed: {sw.ElapsedMilliseconds} ms");
    }

    static async Task RunShardedBlockApproach(
        System.Collections.Generic.List<dynamic> messages,
        int shards)
    {
        Console.WriteLine($"=== Approach B: {shards} ActionBlocks (each MaxDegreeOfParallelism = 1) ===\n");

        var state = new PartitionState();
        var sw = Stopwatch.StartNew();

        // Create N blocks (each single-worker)
        var blocks = Enumerable.Range(0, shards)
            .Select(i => new ActionBlock<dynamic>(async msg =>
            {
                var offset = (int)msg.Offset;
                Log($"[B][w{i}]", offset, sw);

                var processingTask = ProcessMessageAsync(offset, (int)msg.ProcessingMs, $"B-w{i}", sw);

                // register in-flight
                state.InFlight[offset] = processingTask;

                Log($"[B][w{i}] Started", offset, sw);

                try
                {
                    await processingTask; // single worker for this block
                    Log($"[B][w{i}] Finished", offset, sw);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[B][w{i}] Error processing {offset}: {ex}");
                }
                finally
                {
                    TryAdvanceAndCommit(state, offset, sw, $"B-w{i}");
                }
            }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, BoundedCapacity = 100 }))
            .ToArray();

        // Post messages to blocks by offset % shards (simple routing)
        foreach (var m in messages)
        {
            int shard = m.Offset % shards;
            // Post to chosen block (SendAsync to respect bounded capacity/backpressure)
            await blocks[shard].SendAsync(m);
            Log($"[B] Routed offset {m.Offset} -> w{shard}", m.Offset, sw, addMs: false);
        }

        // Complete all blocks and wait
        foreach (var b in blocks) b.Complete();
        await Task.WhenAll(blocks.Select(b => b.Completion));

        sw.Stop();
        Console.WriteLine($"\n[B] Done. Last committed offset: {state.LastCommittedOffset}. Elapsed: {sw.ElapsedMilliseconds} ms");
    }

    // Simulates actual processing (e.g., I/O). Logs start + end by caller.
    static async Task ProcessMessageAsync(int offset, int processingMs, string tag, Stopwatch sw)
    {
        // simulate asynchronous I/O
        await Task.Delay(processingMs);
    }

    // Advance NextExpectedOffset while contiguous offsets are completed; commit (simulate)
    static void TryAdvanceAndCommit(PartitionState state, int completedOffset, Stopwatch sw, string tag)
    {
        lock (state.CommitLock)
        {
            // If the completed task is recorded and truly completed, we'll let the loop handle it
            // Advance contiguous offsets
            while (true)
            {
                var next = state.NextExpectedOffset;

                if (!state.InFlight.TryGetValue(next, out var t))
                {
                    // No task registered yet for the next offset -> cannot advance
                    break;
                }

                if (!t.IsCompleted)
                {
                    // Next offset is still running
                    break;
                }

                // Next offset is completed, remove and move forward
                state.InFlight.TryRemove(next, out _);
                state.NextExpectedOffset = next + 1;
            }

            // Commit (simulate) the "next to read" - Kafka expects commit = next offset to read
            // So the highest processed offset is NextExpectedOffset - 1
            long committed = state.NextExpectedOffset - 1;
            if (committed > state.LastCommittedOffset)
            {
                state.LastCommittedOffset = committed;
                Console.WriteLine($"{Timestamp(sw)} [{tag}] COMMIT -> nextToRead={state.NextExpectedOffset} (committed up to {committed})");
            }
        }
    }

    static void Log(string prefix, int offset, Stopwatch sw, bool addMs = true)
    {
        Console.WriteLine($"{Timestamp(sw)} {prefix} offset={offset}" + (addMs ? $" (t={sw.ElapsedMilliseconds}ms)" : ""));
    }

    static string Timestamp(Stopwatch sw)
    {
        return $"[{sw.ElapsedMilliseconds,5}ms]";
    }
}
