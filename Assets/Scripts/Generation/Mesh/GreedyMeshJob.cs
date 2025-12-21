using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;
using static Unity.Collections.AllocatorManager;
using static UnityEngine.EventSystems.EventTrigger;

[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low, OptimizeFor = OptimizeFor.Performance)]
public struct GreedyMeshJob : IJob
{
    [NonSerialized] [ReadOnly] public NativeArray<byte> blockArray;
    [ReadOnly] public NativeArray<BlockDatabase.BlockInfoUnmanaged> blockDb;
    public int chunkSize;
    public int blockSize;
    public MeshData meshData;
    public int vertexCount;
    public bool isWaterMesh;

    public struct FMask
    {
        public byte Block;
        public Int16 Normal;
    }
    public void Execute()
    {
        var mask = new NativeArray<FMask>(chunkSize * chunkSize, Allocator.Temp);
        GenerateMesh(mask);
        mask.Dispose();
    }

    bool IsSolid(byte block)
    {
        return blockDb[block].isSolid;
    }

    byte GetBlock(int3 coord)
    {
        int i = coord.x + coord.y * (chunkSize + 1) + coord.z * (chunkSize + 1) * (chunkSize + 1);
        return blockArray[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool CompareMask(FMask a, FMask b) => a.Normal == b.Normal && a.Block == b.Block;

    byte SampleLOD(int3 coord)
{
    int s = meshData.meshRes;
    if (s == 1)
        return GetBlock(coord);

    int3 basePos = new int3(coord.x * s, coord.y * s, coord.z * s);

    int3 limit = new int3(s, s, s);

    // When sampling at chunk face (coord==0), reduce one layer from that axis
    if (coord.x == 0) limit.x -= 1;
    if (coord.y == 0) limit.y -= 1;
    if (coord.z == 0) limit.z -= 1;

    byte best = 0;

    for (int dx = 0; dx < limit.x; dx++)
        for (int dy = 0; dy < limit.y; dy++)
            for (int dz = 0; dz < limit.z; dz++)
            {
                int xi = basePos.x + dx;
                int yi = basePos.y + dy;
                int zi = basePos.z + dz;

                if (xi <= chunkSize && yi <= chunkSize && zi <= chunkSize)
                {
                    byte v = GetBlock(new int3(xi, yi, zi));
                    if (v > best)
                        best = v;
                }
            }

    return best;
}

    public void GenerateMesh(NativeArray<FMask> mask)
    {
        // Sweep over each axis (X, Y, Z)
        for (int axis = 0; axis < 3; ++axis)
        {
            // 2 Perpendicular axes
            int axis1 = (axis + 1) % 3;
            int axis2 = (axis + 2) % 3;

            int mainAxisLimit = chunkSize / meshData.meshRes;
            int axis1Limit = chunkSize / meshData.meshRes;
            int axis2Limit = chunkSize / meshData.meshRes;

            int3 deltaAxis1 = int3.zero;
            int3 deltaAxis2 = int3.zero;

            int3 chunkItr = int3.zero;
            int3 axisMask = int3.zero;

            axisMask[axis] = 1;

            // Check each slice of the chunk
            for (chunkItr[axis] = 0; chunkItr[axis] < mainAxisLimit;)
            {
                int n = 0;

                // Compute Mask
                for (chunkItr[axis2] = 0; chunkItr[axis2] < axis2Limit; ++chunkItr[axis2])
                {
                    for (chunkItr[axis1] = 0; chunkItr[axis1] < axis1Limit; ++chunkItr[axis1])
                    {
                        var currentBlock = SampleLOD(chunkItr);
                        var compareBlock = SampleLOD(chunkItr + axisMask);

                        var currentBlockData = blockDb[currentBlock];
                        var compareBlockData = blockDb[compareBlock];

                        BlockVisibility currentVis = blockDb[currentBlock].visibility;
                        BlockVisibility compareVis = blockDb[compareBlock].visibility;

                        bool shouldDrawFace = false;
                        byte faceBlock = 0;
                        short faceNormal = 0;

                        // Invisible (air) cases
                        if (currentVis == BlockVisibility.Invisible && compareVis == BlockVisibility.Invisible)
                        {
                            // Air to air - no face
                            shouldDrawFace = false;
                        }
                        else if (currentVis == BlockVisibility.Invisible)
                        {
                            // Air to something visible - draw the visible block's face
                            shouldDrawFace = true;
                            faceBlock = compareBlock;
                            faceNormal = 1;
                        }
                        else if (compareVis == BlockVisibility.Invisible)
                        {
                            // Something visible to air - draw this block's face
                            shouldDrawFace = true;
                            faceBlock = currentBlock;
                            faceNormal = -1;
                        }
                        // STACKED LOGIC - Both stacked
                        else if (currentVis == BlockVisibility.Stacked && compareVis == BlockVisibility.Stacked)
                        {
                            // Both stacked - draw BOTH faces (normal = 2 signals double-sided)
                            if (meshData.lod == LODLevel.Near)
                            {
                                shouldDrawFace = true;
                                faceBlock = currentBlock;  // Could be either, doesn't matter much
                                faceNormal = 2;  // Special case: render both sides
                            }
                            else
                            {   // Do nothing at farther lod
                                shouldDrawFace = false;
                                faceBlock = currentBlock;
                                faceNormal = 0;
                            }
                        }
                        // STACKED LOGIC - Current is stacked, compare is not
                        else if (currentVis == BlockVisibility.Stacked)
                        {
                            // Stacked block next to solid/translucent - show the solid/translucent face
                            shouldDrawFace = true;
                            faceBlock = compareBlock;
                            faceNormal = 1;
                        }
                        // STACKED LOGIC - Compare is stacked, current is not
                        else if (compareVis == BlockVisibility.Stacked)
                        {
                            // Solid/translucent next to stacked - show the solid/translucent face
                            shouldDrawFace = true;
                            faceBlock = currentBlock;
                            faceNormal = -1;
                        }
                        // Standard opaque/translucent logic
                        else if (currentVis == BlockVisibility.Opaque && compareVis == BlockVisibility.Opaque)
                        {
                            // Both opaque - no face (fully culled)
                            shouldDrawFace = false;
                        }
                        else if (currentVis == BlockVisibility.Opaque)
                        {
                            // Opaque to translucent - draw opaque face
                            shouldDrawFace = true;
                            faceBlock = currentBlock;
                            faceNormal = -1;
                        }
                        else if (compareVis == BlockVisibility.Opaque)
                        {
                            // Translucent to opaque - draw opaque face
                            shouldDrawFace = true;
                            faceBlock = compareBlock;
                            faceNormal = 1;
                        }
                        else if (currentVis == BlockVisibility.Translucent && compareVis == BlockVisibility.Translucent)
                        {
                            // Both translucent - only draw if different types
                            if (currentBlock != compareBlock)
                            {
                                shouldDrawFace = true;
                                faceBlock = currentBlock;
                                faceNormal = -1;
                            }
                            else
                            {
                                shouldDrawFace = false;
                            }
                        }

                        if (shouldDrawFace)
                        {
                            bool isWaterBlock = faceBlock == (byte)BlockType.Water; // Do our current mesh type check, currently only seperates water into its own mesh
                                                                                    // Handled by a request that will start another MeshJob.
                            if (isWaterBlock)
                                meshData.requestWaterMesh.Value = true;             // Flag for that request.

                            // Only add face if it matches the mesh type we're building
                            if (isWaterBlock == isWaterMesh)
                            {
                                mask[n++] = new FMask { Block = faceBlock, Normal = faceNormal };
                            }
                            else
                            {
                                mask[n++] = new FMask { Normal = 0 };
                            }
                        }
                        else
                        {
                            mask[n++] = new FMask { Normal = 0 };
                        }
                    }
                }

                ++chunkItr[axis];
                n = 0;

                // Generate Mesh From Mask
                for (int j = 0; j < axis2Limit; ++j)
                {
                    for (int i = 0; i < axis1Limit;)
                    {
                        if (mask[n].Normal != 0)
                        {
                            var currentMask = mask[n];
                            chunkItr[axis1] = i;
                            chunkItr[axis2] = j;

                            int width = 1;
                            int height = 1;

                            if (true)//currentMask.BlockData.GreedyMesh) // check for all other mesh types
                            {
                                for (width = 1; i + width < axis1Limit && CompareMask(mask[n + width], currentMask); ++width)
                                {
                                }
                                bool done = false;

                                for (height = 1; j + height < axis2Limit; ++height)
                                {
                                    for (int k = 0; k < width; ++k)
                                    {
                                        if (CompareMask(mask[n + k + height * axis1Limit], currentMask)) continue;

                                        done = true;
                                        break;
                                    }

                                    if (done) break;
                                }
                            }

                            deltaAxis1[axis1] = width;
                            deltaAxis2[axis2] = height;

                            CreateQuad(meshData, ref vertexCount, currentMask, axisMask,
                                width, height,
                                new int3(chunkItr),
                                new int3(chunkItr) + deltaAxis1,
                                new int3(chunkItr) + deltaAxis2,
                                new int3(chunkItr) + deltaAxis1 + deltaAxis2);

                            // Reset deltas
                            deltaAxis1 = int3.zero;
                            deltaAxis2 = int3.zero;

                            for (int l = 0; l < height; ++l)
                            {
                                for (int k = 0; k < width; ++k)
                                {
                                    mask[n + k + l * axis1Limit] = new FMask { Normal = 0 };
                                }
                            }

                            i += width;
                            n += width;
                        }
                        else
                        {
                            i++;
                            n++;
                        }
                    }
                }
            }
        }
    }

    public void CreateQuad(MeshData meshData, ref int vertexCount, FMask mask, int3 axisMask, int width, int height, int3 v1, int3 v2, int3 v3, int3 v4)
    {
        if (mask.Normal == 2)
        {
            // Create both faces with greedy-merged dimensions
            CreateDoubleSidedQuad(meshData, ref vertexCount, mask, axisMask, width, height, v1, v2, v3, v4);
        }
        else
        {
            // Original single-sided logic
            CreateSingleSidedQuad(meshData, ref vertexCount, mask, axisMask, width, height, v1, v2, v3, v4);
        }
    }

    private void CreateDoubleSidedQuad(MeshData meshData, ref int vertexCount, FMask mask, int3 axisMask,
        int width, int height, int3 v1, int3 v2, int3 v3, int3 v4)
    {
        float4 color = new float4(mask.Block, 0, 0, 1);
        float3 s = new float3(blockSize);

        // FRONT FACE
        int3 normalFront = axisMask * -1;

        meshData.vertices.Add(v1 * s);
        meshData.vertices.Add(v2 * s);
        meshData.vertices.Add(v3 * s);
        meshData.vertices.Add(v4 * s);

        meshData.triangles.Add(vertexCount);
        meshData.triangles.Add(vertexCount + 1);
        meshData.triangles.Add(vertexCount + 3);
        meshData.triangles.Add(vertexCount + 3);
        meshData.triangles.Add(vertexCount + 2);
        meshData.triangles.Add(vertexCount);

        for (int i = 0; i < 4; i++)
        {
            meshData.normals.Add(normalFront);
            meshData.colors.Add(color);
        }

        // UVs for front
        if (axisMask.x != 0)
        {
            meshData.UV0s.Add(new float2(width, height));
            meshData.UV0s.Add(new float2(0, height));
            meshData.UV0s.Add(new float2(width, 0));
            meshData.UV0s.Add(new float2(0, 0));
        }
        else
        {
            meshData.UV0s.Add(new float2(height, width));
            meshData.UV0s.Add(new float2(height, 0));
            meshData.UV0s.Add(new float2(0, width));
            meshData.UV0s.Add(new float2(0, 0));
        }

        vertexCount += 4;

        // BACK FACE (flipped winding)
        int3 normalBack = axisMask * 1;

        meshData.vertices.Add(v1 * s);
        meshData.vertices.Add(v2 * s);
        meshData.vertices.Add(v3 * s);
        meshData.vertices.Add(v4 * s);

        meshData.triangles.Add(vertexCount);
        meshData.triangles.Add(vertexCount + 2);
        meshData.triangles.Add(vertexCount + 3);
        meshData.triangles.Add(vertexCount + 3);
        meshData.triangles.Add(vertexCount + 1);
        meshData.triangles.Add(vertexCount);

        for (int i = 0; i < 4; i++)
        {
            meshData.normals.Add(normalBack);
            meshData.colors.Add(color);
        }

        // Same UVs for back
        if (axisMask.x != 0)
        {
            meshData.UV0s.Add(new float2(width, height));
            meshData.UV0s.Add(new float2(0, height));
            meshData.UV0s.Add(new float2(width, 0));
            meshData.UV0s.Add(new float2(0, 0));
        }
        else
        {
            meshData.UV0s.Add(new float2(height, width));
            meshData.UV0s.Add(new float2(height, 0));
            meshData.UV0s.Add(new float2(0, width));
            meshData.UV0s.Add(new float2(0, 0));
        }

        vertexCount += 4;
    }

    private void CreateSingleSidedQuad(MeshData meshData, ref int vertexCount, FMask mask, int3 axisMask,
        int width, int height, int3 v1, int3 v2, int3 v3, int3 v4)
    {
        // Your original CreateQuad code here
        int3 normal = axisMask * mask.Normal;
        float4 color = new float4(mask.Block, 0, 0, 1);
        float3 s = new float3(blockSize);

        meshData.vertices.Add(v1 * s);
        meshData.vertices.Add(v2 * s);
        meshData.vertices.Add(v3 * s);
        meshData.vertices.Add(v4 * s);

        meshData.triangles.Add(vertexCount);
        meshData.triangles.Add(vertexCount + 2 + mask.Normal);
        meshData.triangles.Add(vertexCount + 2 - mask.Normal);
        meshData.triangles.Add(vertexCount + 3);
        meshData.triangles.Add(vertexCount + 1 - mask.Normal);
        meshData.triangles.Add(vertexCount + 1 + mask.Normal);

        for (int i = 0; i < 4; i++)
        {
            meshData.normals.Add(normal * -1);
            meshData.colors.Add(color);
        }

        if (normal.x == 1 || normal.x == -1)
        {
            meshData.UV0s.Add(new float2(width, height));
            meshData.UV0s.Add(new float2(0, height));
            meshData.UV0s.Add(new float2(width, 0));
            meshData.UV0s.Add(new float2(0, 0));
        }
        else
        {
            meshData.UV0s.Add(new float2(height, width));
            meshData.UV0s.Add(new float2(height, 0));
            meshData.UV0s.Add(new float2(0, width));
            meshData.UV0s.Add(new float2(0, 0));
        }

        vertexCount += 4;
    }
}
