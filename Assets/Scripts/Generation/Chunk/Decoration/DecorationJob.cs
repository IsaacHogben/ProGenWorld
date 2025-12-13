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
    [ReadOnly] public Unity.Mathematics.Random rng; // per-chunk seed for deterministic RNG
    // Full-res block array for this chunk
    public NativeArray<byte> blockIds;
    // Cross-chunk writes only
    public NativeList<PendingBlockWrite> pendingWrites;

    public void Execute()
    {
        for (int z = 0; z < chunkSize; z++)
            for (int y = 0; y < chunkSize; y++)
                for (int x = 0; x < chunkSize; x++)
                {
                    byte currentBlock = blockIds[GetIndex(z, y, x)];
                    byte aboveBlock = blockIds[GetIndex(z, y + 1, x)];
                    bool surfaceLayer = aboveBlock == (byte)BlockType.Air;
                    bool isAboveWaterLevel = GetWorldYValue(y) > -90;

                    if (surfaceLayer && isAboveWaterLevel && (currentBlock == (byte)BlockType.Dirt || currentBlock == (byte)BlockType.Grass))
                    {
                        if (rng.NextFloat() < 0.0045f)
                            MakeSmallPine(x, y, z);
                        else if (currentBlock == (byte)BlockType.Grass && rng.NextFloat() < 0.0016f)
                            MakeLargePine(x, y, z);
                    }
                }
    }

    private readonly int GetIndex(int z, int y, int x)
    {
        return x + y * indexSize + z * indexSize * indexSize;
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
        if (targetLocal.Equals(chunkCoord) && x != 0 && y != 0 && z != 0)
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
    private void MakeSmallPine(int x, int y, int z)
    {
        // Randomize dimensions for variation
        int trunkHeight = rng.NextInt(8, 30);
        int bareHeightFromBottom = trunkHeight / 4; // No leaves on lower trunk
        int foliageHeight = trunkHeight - bareHeightFromBottom;

        // Trunk
        for (int i = 0; i < trunkHeight; i++)
        {
            ApplyBlock(x, y + i, z, BlockType.Log);
        }

        // Calculate layer spacing to ensure good coverage
        int numLayers = foliageHeight / 2 + 2; // Enough layers to cover the foliage area
        int foliageStartY = y + bareHeightFromBottom;

        for (int layer = 0; layer < numLayers; layer++)
        {
            int layerY = foliageStartY + (layer * foliageHeight / numLayers);

            // Cone shape: radius decreases linearly from bottom to top
            float progress = (float)layer / (numLayers - 1); // 0.0 at bottom, 1.0 at top
            int maxRadius = 4;
            int radius = Mathf.Max(1, (int)(maxRadius * (1.0f - progress) + 0.5f));

            // Always place lower layers, occasionally skip middle/upper layers
            bool shouldPlace = true;
            if (layer > 2 && layer < numLayers - 1)
            {
                // 20% chance to skip a middle layer
                if (rng.NextFloat() < 0.2f)
                    shouldPlace = false;
            }

            if (shouldPlace)
            {
                MakePineLayer(x, layerY, z, radius);
            }
        }

        // Guaranteed tip
        ApplyBlock(x, y + trunkHeight, z, BlockType.Leaves);
        ApplyBlock(x, y + trunkHeight + 1, z, BlockType.Leaves);
    }

    private void MakeLargePine(int x, int y, int z)
    {
        z -= 1; // Put the trunk of the tree into the ground a bit.

        // Randomize dimensions for variation
        int trunkHeight = rng.NextInt(22, 46);
        int bareHeightFromBottom = trunkHeight / 4; // No leaves on lower trunk
        int foliageHeight = trunkHeight - bareHeightFromBottom;

        // 2x2 Trunk
        for (int i = 0; i < trunkHeight; i++)
        {
            ApplyBlock(x, y + i, z, BlockType.Log);
            ApplyBlock(x + 1, y + i, z, BlockType.Log);
            ApplyBlock(x, y + i, z + 1, BlockType.Log);
            ApplyBlock(x + 1, y + i, z + 1, BlockType.Log);
        }

        // Calculate layer spacing to ensure good coverage
        int numLayers = foliageHeight / 2 + 3; // Enough layers to cover the foliage area
        int foliageStartY = y + bareHeightFromBottom;
        int radiusVariation = 0;

        for (int layer = 0; layer < numLayers; layer++)
        {
            int layerY = foliageStartY + (layer * foliageHeight / numLayers);

            // Cone shape: radius decreases linearly from bottom to top
            float progress = (float)layer / (numLayers - 1); // 0.0 at bottom, 1.0 at top
            int maxRadius = 7;
            int radius = Mathf.Max(2, (int)(maxRadius * (1.0f - progress) + 0.5f));

            // Add slight variation to radius given the last variation wasnt negative
            if (radiusVariation >= 0)
                radiusVariation = rng.NextInt(-1, 2);
            else
                radiusVariation = 2;
            radius = Mathf.Max(2, radius + radiusVariation);

            // Always place lower layers, occasionally skip middle layers
            bool shouldPlace = true;
            if (layer > 3 && layer < numLayers - 2)
            {
                // 20% chance to skip a middle layer
                if (rng.NextFloat() < 0.2f)
                    shouldPlace = false;
            }

            if (shouldPlace)
            {
                MakePineLayerCentered(x, layerY, z, radius);

                // Occasionally add extra density to lower layers
                if (layer < numLayers / 2 && rng.NextFloat() > 0.7f && radius > 3)
                {
                    MakePineLayerCentered(x, layerY + 1, z, radius - 1);
                }
            }
        }

        // Guaranteed tip (centered on 2x2 trunk)
        int tipY = y + trunkHeight;
        ApplyBlock(x, tipY, z, BlockType.Leaves);
        ApplyBlock(x + 1, tipY, z, BlockType.Leaves);
        ApplyBlock(x, tipY, z + 1, BlockType.Leaves);
        ApplyBlock(x + 1, tipY, z + 1, BlockType.Leaves);
        ApplyBlock(x, tipY + 1, z, BlockType.Leaves);
        ApplyBlock(x + 1, tipY + 1, z + 1, BlockType.Leaves);
    }

    private void MakePineLayer(int cx, int cy, int cz, int radius)
    {
        int radiusSquared = radius * radius;

        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                int distSquared = dx * dx + dz * dz;

                if (distSquared <= radiusSquared)
                {
                    // Add slight randomness to edges for natural look
                    if (distSquared < radiusSquared * 0.7f || rng.NextFloat() > 0.3f)
                    {
                        ApplyBlock(cx + dx, cy, cz + dz, BlockType.Leaves);
                    }
                }
            }
        }
    }

    private void MakePineLayerCentered(int trunkX, int cy, int trunkZ, int radius)
    {
        // For a 2x2 trunk at (x, z), the center is at (x+0.5, z+0.5)
        float centerX = trunkX + 0.5f;
        float centerZ = trunkZ + 0.5f;
        int radiusSquared = radius * radius;

        for (int dx = -radius; dx <= radius + 1; dx++)
        {
            for (int dz = -radius; dz <= radius + 1; dz++)
            {
                // Calculate distance from center of 2x2 trunk
                float blockX = trunkX + dx;
                float blockZ = trunkZ + dz;
                float distX = blockX - centerX;
                float distZ = blockZ - centerZ;
                float distSquared = distX * distX + distZ * distZ;

                if (distSquared <= radiusSquared)
                {
                    // Add slight randomness to edges for natural look
                    if (distSquared < radiusSquared * 0.7f || rng.NextFloat() > 0.3f)
                    {
                        ApplyBlock(trunkX + dx, cy, trunkZ + dz, BlockType.Leaves);
                    }
                }
            }
        }
    }
    int GetWorldYValue(int y)
    {
        return y + chunkCoord.y * chunkSize;
    }
}