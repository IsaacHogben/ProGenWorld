using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public sealed class BiomeSystem : MonoBehaviour
{
    [Header("Biome Settings")]
    [Tooltip("Biome samples per axis per chunk (e.g. 8, 16, 32)")]
    public int biomeResolution = 32;
    public float frequency = 0.0012f;

    int seed = 1337;
    public void SetSeed(int value) => seed = value;

    struct PendingBiome
    {
        public JobHandle handle;
        public NativeArray<BiomeHint> grid;
    }

    // Pending biome jobs
    readonly Dictionary<int3, PendingBiome> pending = new();

    // --------------------------------------------------
    // Events
    // --------------------------------------------------

    /// <summary>
    /// Fired when a biome job completes.
    /// Ownership of BiomeData.grid transfers to listener.
    /// </summary>
    public System.Action<int3, BiomeData> OnBiomeCompleted;

    // --------------------------------------------------
    // Public API
    // --------------------------------------------------

    public void RequestBiome(int3 chunkCoord, int chunkSize)
    {
        if (pending.ContainsKey(chunkCoord))
            return;

        int side = biomeResolution + 1;
        int count = side * side;

        var grid = new NativeArray<BiomeHint>(
            count,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory
        );

        var job = new BiomeJob
        {
            chunkCoord = chunkCoord,
            chunkSize = chunkSize,
            resolution = biomeResolution,
            seed = (uint)seed,
            frequency = frequency,
            output = grid
        };

        JobHandle handle = job.Schedule(count, 64);

        pending.Add(chunkCoord, new PendingBiome
        {
            handle = handle,
            grid = grid
        });
    }

    public void CancelBiome(int3 chunkCoord)
    {
        if (!pending.TryGetValue(chunkCoord, out var p))
            return;

        p.handle.Complete();

        if (p.grid.IsCreated)
            p.grid.Dispose();

        pending.Remove(chunkCoord);
    }

    static readonly List<int3> tmpKeys = new List<int3>(128);
    public void Updater()
    {
        if (pending.Count == 0)
            return;
        tmpKeys.Clear();
        tmpKeys.AddRange(pending.Keys);

        for (int i = 0; i < tmpKeys.Count; i++)
        {
            int3 coord = tmpKeys[i];
            var p = pending[coord];

            if (!p.handle.IsCompleted)
                continue;

            p.handle.Complete();

            var biome = new BiomeData
            {
                resolution = biomeResolution,
                grid = p.grid
            };

            pending.Remove(coord);

            // Emit completion
            OnBiomeCompleted?.Invoke(coord, biome);
        }
    }

    void OnDestroy()
    {
        foreach (var kv in pending)
        {
            kv.Value.handle.Complete();
            if (kv.Value.grid.IsCreated)
                kv.Value.grid.Dispose();
        }
        pending.Clear();
    }
}
