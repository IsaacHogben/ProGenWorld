using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>How a chunk's full resolution block array is kept in memory while it is loaded.</summary>
public enum BlockStorage
{
    Full,        // plain array, ready to read
    Compressed   // run-length encoded while idle; expanded again whenever something needs it
}

/// <summary>
/// Owns every loaded chunk's full resolution block array ((chunkSize + 1)^3 bytes). Arrays of
/// chunks that may be compressed are run-length encoded once they have been idle for a while, and
/// expanded transparently by TryGetFull (writes, repacks, physics). The data stays exact: block
/// writes, decorations and LOD changes behave the same whichever way a chunk is stored.
/// </summary>
public sealed class ChunkBlockStore : IDisposable
{
    public enum CompressCheck { Yes, NotNow, Never }

    readonly int arrayLength;
    readonly Dictionary<int3, NativeArray<byte>> full = new();
    readonly Dictionary<int3, NativeArray<byte>> compressed = new();
    readonly Dictionary<int3, float> lastUse = new();

    // Compression candidates, oldest first. Entries go stale when a chunk is used again,
    // compressed or removed; they are re-checked when they reach the front.
    readonly Queue<(int3 coord, float time)> candidates = new();
    readonly HashSet<int3> queued = new();

    NativeList<byte> encodeBuffer;
    long compressedBytes;

    public ChunkBlockStore(int chunkSize)
    {
        int s = chunkSize + 1;
        arrayLength = s * s * s;
        encodeBuffer = new NativeList<byte>(arrayLength / 4, Allocator.Persistent);
    }

    public int FullCount => full.Count;
    public int CompressedCount => compressed.Count;
    public long FullBytes => (long)full.Count * arrayLength;
    public long CompressedBytes => compressedBytes;
    public int ArrayLength => arrayLength;

    public bool Has(int3 coord) => full.ContainsKey(coord) || compressed.ContainsKey(coord);
    public bool IsCompressed(int3 coord) => compressed.ContainsKey(coord);

    /// <summary>Stores freshly generated blocks (takes ownership), replacing anything stored before.</summary>
    public void SetFull(int3 coord, NativeArray<byte> blocks, float now)
    {
        Remove(coord);
        full[coord] = blocks;
        lastUse[coord] = now;
    }

    /// <summary>
    /// The chunk's block array, expanded first if it is compressed. The array stays expanded
    /// until the chunk has been idle again for the compression delay.
    /// </summary>
    public bool TryGetFull(int3 coord, float now, out NativeArray<byte> blocks)
    {
        if (full.TryGetValue(coord, out blocks))
        {
            lastUse[coord] = now;
            return true;
        }

        if (!compressed.Remove(coord, out var packed))
        {
            blocks = default;
            return false;
        }

        blocks = new NativeArray<byte>(arrayLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        new DecodeJob { input = packed, output = blocks }.Run();
        compressedBytes -= packed.Length;
        packed.Dispose();

        full[coord] = blocks;
        lastUse[coord] = now;
        MarkCandidate(coord, now);
        return true;
    }

    /// <summary>Marks a chunk as worth compressing once it has been idle for the delay.</summary>
    public void MarkCandidate(int3 coord, float now)
    {
        if (full.ContainsKey(coord) && queued.Add(coord))
            candidates.Enqueue((coord, now));
    }

    public void Remove(int3 coord)
    {
        if (full.Remove(coord, out var a) && a.IsCreated)
            a.Dispose();
        if (compressed.Remove(coord, out var c) && c.IsCreated)
        {
            compressedBytes -= c.Length;
            c.Dispose();
        }
        lastUse.Remove(coord);
        queued.Remove(coord);
    }

    /// <summary>
    /// Compresses chunks that have been idle for at least idleSeconds, within a main thread time
    /// budget. check decides per chunk: compress now, retry later (busy), or drop the candidate
    /// (its LOD keeps full arrays).
    /// </summary>
    public void CompressIdle(float now, float idleSeconds, float budgetMs, Func<int3, CompressCheck> check)
    {
        if (candidates.Count == 0)
            return;

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long budgetTicks = (long)(budgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        int requeued = 0;

        while (candidates.Count > requeued)
        {
            var (coord, time) = candidates.Peek();
            if (now - time < idleSeconds)
                break;
            candidates.Dequeue();

            if (!full.TryGetValue(coord, out var blocks))
            {
                queued.Remove(coord);
                continue;
            }

            // Used since it was queued: wait for the delay from its last use.
            float used = lastUse.TryGetValue(coord, out float t) ? t : time;
            if (now - used < idleSeconds)
            {
                candidates.Enqueue((coord, used));
                requeued++;
                continue;
            }

            var verdict = check(coord);
            if (verdict == CompressCheck.Never)
            {
                queued.Remove(coord);
                continue;
            }
            if (verdict == CompressCheck.NotNow)
            {
                candidates.Enqueue((coord, now));
                requeued++;
                continue;
            }

            Compress(coord, blocks);
            queued.Remove(coord);

            if (System.Diagnostics.Stopwatch.GetTimestamp() - start > budgetTicks)
                break;
        }
    }

    void Compress(int3 coord, NativeArray<byte> blocks)
    {
        new EncodeJob { input = blocks, output = encodeBuffer }.Run();

        var packed = new NativeArray<byte>(encodeBuffer.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        NativeArray<byte>.Copy(encodeBuffer.AsArray(), packed, encodeBuffer.Length);
        compressedBytes += packed.Length;

        full.Remove(coord);
        blocks.Dispose();
        compressed[coord] = packed;
    }

    public void Dispose()
    {
        foreach (var a in full.Values) if (a.IsCreated) a.Dispose();
        foreach (var c in compressed.Values) if (c.IsCreated) c.Dispose();
        full.Clear();
        compressed.Clear();
        lastUse.Clear();
        candidates.Clear();
        queued.Clear();
        compressedBytes = 0;
        if (encodeBuffer.IsCreated) encodeBuffer.Dispose();
    }

    // Runs of (block id, length) in array order. Terrain is layered vertically and the array is
    // x-major, so runs along x through stone, dirt and air layers are long; any order decodes back
    // exactly, this one just compresses well and streams linearly.
    [BurstCompile]
    struct EncodeJob : IJob
    {
        [ReadOnly] public NativeArray<byte> input;
        public NativeList<byte> output;

        public void Execute()
        {
            output.Clear();
            int n = input.Length;
            int i = 0;
            while (i < n)
            {
                byte id = input[i];
                int run = 1;
                while (i + run < n && run < 255 && input[i + run] == id)
                    run++;
                output.Add(id);
                output.Add((byte)run);
                i += run;
            }
        }
    }

    [BurstCompile]
    struct DecodeJob : IJob
    {
        [ReadOnly] public NativeArray<byte> input;
        [WriteOnly] public NativeArray<byte> output;

        public void Execute()
        {
            int o = 0;
            for (int i = 0; i + 1 < input.Length; i += 2)
            {
                byte id = input[i];
                int run = input[i + 1];
                for (int k = 0; k < run; k++)
                    output[o++] = id;
            }
        }
    }
}
