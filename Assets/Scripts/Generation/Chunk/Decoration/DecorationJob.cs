using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Collections.AllocatorManager;

[BurstCompile]
public struct DecorationJob : IJob
{
    [ReadOnly] public int3 chunkCoord;
    [ReadOnly] public int chunkSize;
    [ReadOnly] public int indexSize;
    [ReadOnly] public LODLevel lod;
    [ReadOnly] public uint seed; // per-chunk seed for deterministic RNG

    // Full-res block array for this chunk
    public NativeArray<byte> blockIds;

    // Cross-chunk writes only
    public NativeList<PendingBlockWrite> pendingWrites;

    public void Execute()
    {
        var rng = new Unity.Mathematics.Random(seed);

        for (int z = 0; z < chunkSize; z++)
            for (int y = 0; y < chunkSize; y++)
                for (int x = 0; x < chunkSize; x++)
                {
                    int index = x + y * indexSize + z * indexSize * indexSize;

                    byte currentBlock = blockIds[index];

                    if (currentBlock == (byte)BlockType.Grass && rng.NextFloat() < 0.002f)
                        MakeTestPineTree(x, y, z); 
                    /*else if (currentBlock == (byte)BlockType.Grass && rng.NextFloat() < 0.000001f)
                        MakeTestSpiralTower(x, y, z);*/
                }
    }

    void ApplyBlock(int x, int y, int z, BlockType block)
    {
        byte blockId = (byte)block;
        int index = x + y * indexSize + z * indexSize * indexSize;
        int3 localPos = new int3(x, y, z);
        int3 worldPos = chunkCoord * chunkSize + localPos;

        // Compute which chunk this block belongs to
        int3 targetChunk = new int3(
            (int)math.floor(worldPos.x / (float)chunkSize),
            (int)math.floor(worldPos.y / (float)chunkSize),
            (int)math.floor(worldPos.z / (float)chunkSize)
        );

        // Compute localPos inside that target chunk
        int3 targetLocal = new int3(
            worldPos.x - targetChunk.x * chunkSize,
            worldPos.y - targetChunk.y * chunkSize,
            worldPos.z - targetChunk.z * chunkSize
        );

        // Writes imediatly if change does not affect bordering chunks, else, uses the pending writes system
        // This may be changed to only use pending writes system
        if (targetLocal.Equals(chunkCoord) && x!=0 && y!=0 && z!=0)
        {
            blockIds[index] = blockId;
        }
        else
            // Cross-chunk: emit pending write for main thread to route later
            pendingWrites.Add(new PendingBlockWrite
            {
                targetChunk = targetChunk,
                localPos = targetLocal,
                blockId = blockId,
                mode = PendingWriteMode.ReplaceAir,
                isMirror = false,
            });
    }
    private void MakeTestSpiralTower(int x, int y, int z)
    {
        int height = 160;         // tall
        int radius = 6;           // outer spiral radius
        int coreRadius = 2;       // thick central shaft
        float turns = 5f;         // number of spiral rotations

        // --- central solid shaft ---
        for (int yy = y; yy < y + height; yy++)
            for (int dx = -coreRadius; dx <= coreRadius; dx++)
                for (int dz = -coreRadius; dz <= coreRadius; dz++)
                    if (dx * dx + dz * dz <= coreRadius * coreRadius)
                        ApplyBlock(x + dx, yy, z + dz, BlockType.Stone);

        // --- spiral ramp wrapping around ---
        for (int i = 0; i < height; i++)
        {
            float t = (float)i / height;
            float angle = t * turns * math.PI * 2f;

            int sx = x + (int)(math.cos(angle) * radius);
            int sz = z + (int)(math.sin(angle) * radius);
            int sy = y + i;

            // Thick walkway: 3x3 around the spiral center point
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    ApplyBlock(sx + dx, sy, sz + dz, BlockType.Dirt);
        }

        // --- top platform ---
        int top = y + height;
        int topRadius = 8;
        for (int dx = -topRadius; dx <= topRadius; dx++)
            for (int dz = -topRadius; dz <= topRadius; dz++)
                if (dx * dx + dz * dz <= topRadius * topRadius)
                    ApplyBlock(x + dx, top, z + dz, BlockType.Grass);

        // --- central beacon on top ---
        for (int yy = 0; yy < 10; yy++)
            ApplyBlock(x, top + yy, z, BlockType.Stone);
    }
    private void MakeTestPineTree(int x, int y, int z)
    {
        // --- Trunk ---
        int trunkHeight = 18;
        for (int i = 0; i < trunkHeight; i++)
        {
            ApplyBlock(x, y + i, z, BlockType.Log);
        }

        // --- Layers of decreasing radius ---     
        int maxRadius = 4;           // bottom foliage
        int layers = 4;              // number of "rings"
        int layerStartY = y + trunkHeight - (layers * 2);
        int radius = maxRadius;

        for (int ly = 0; ly < layers; ly++)
        {
            int cy = layerStartY + ly * 2;
            MakePineLayer(x, cy, z, radius);
            radius = Mathf.Max(1, radius - 1);  // taper
        }

        // --- Tip ---
        ApplyBlock(x, layerStartY + layers * 2, z, BlockType.Leaves);
    }
    private void MakePineLayer(int cx, int cy, int cz, int r)
    {
        // Simple diamond / circle hybrid
        for (int x = -r; x <= r; x++)
        {
            for (int z = -r; z <= r; z++)
            {
                if (x * x + z * z <= r * r)  // circular-ish
                {
                    ApplyBlock(cx + x, cy, cz + z, BlockType.Leaves);
                }
            }
        }
    }
}


