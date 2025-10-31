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
    [ReadOnly] public NativeArray<BlockDatabase.BlockInfoUnmanaged> blocks;
    public int chunkSize;
    public MeshData meshData;
    public int vertexCount;

    public struct FMask
    {
        public byte Block;
        public Int16 Normal;
    }
    public void Execute()
    {
        var mask = new NativeArray<FMask>(chunkSize * chunkSize, Allocator.Temp);
        AGenerateMesh(mask);
        mask.Dispose();
    }

    bool IsSolid(byte block)
    {
        return blocks[block].isSolid;
    }

    byte GetBlock(int3 coord)
    {
        int i = coord.x + coord.y * (chunkSize + 1) + coord.z * (chunkSize + 1) * (chunkSize + 1);
        return blockArray[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool CompareMask(FMask a, FMask b) => a.Normal == b.Normal && a.Block == b.Block;

    public void AGenerateMesh(NativeArray<FMask> mask)
    {
        // Debug.Log($"Blocks array accessed. Num elements: {Blocks[99]}");
        int Lod = 1;
        // Sweep over each axis (X, Y, Z)
        for (int axis = 0; axis < 3; ++axis)
        {
            // 2 Perpendicular axes
            int axis1 = (axis + 1) % 3;
            int axis2 = (axis + 2) % 3;

            int mainAxisLimit = chunkSize / Lod;
            int axis1Limit = chunkSize / Lod;
            int axis2Limit = chunkSize / Lod;

            int3 deltaAxis1 = int3.zero;
            int3 deltaAxis2 = int3.zero;

            int3 chunkItr = int3.zero;
            int3 axisMask = int3.zero;

            axisMask[axis] = 1;

            // This change prevents overlapping faces on high lod chunks
            int lodModifiedi = Lod != 1 ? -1 : 0;

            // Check each slice of the chunk
            for (chunkItr[axis] = lodModifiedi; chunkItr[axis] < mainAxisLimit;)
            {
                int n = 0;

                // Compute Mask
                for (chunkItr[axis2] = 0; chunkItr[axis2] < axis2Limit; ++chunkItr[axis2])
                {
                    for (chunkItr[axis1] = 0; chunkItr[axis1] < axis1Limit; ++chunkItr[axis1])
                    {
                        var currentBlock = GetBlock(chunkItr);
                        var compareBlock = GetBlock(chunkItr + axisMask);
                        var currentBlockData = blocks[currentBlock];
                        var compareBlockData = blocks[compareBlock];

                        bool currentBlockOpaque = currentBlock != 0;
                        bool compareBlockOpaque = compareBlock != 0;


                        // Standard Block draw
                        if (currentBlockOpaque == compareBlockOpaque)
                        {
                            mask[n++] = new FMask { Normal = 0 };
                        }
                        else if (currentBlockOpaque)
                        {
                            mask[n++] = new FMask {Block = currentBlock, Normal = -1 };
                        }
                        else
                        {
                            mask[n++] = new FMask {Block = compareBlock, Normal = 1 };
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
        int3 normal = axisMask * mask.Normal;

        // Exit Quad creation if block data requires it.
        //if (mask.BlockData.DisplayFaces == EBlockDisplayType.OnlySides && normal.Z != 0)
            //return;

        // apply vertex colour here if able 
        float4 color = new float4(mask.Block, 0, 0, 1);

        // Append vertices
        meshData.vertices.Add(v1);
        meshData.vertices.Add(v2);
        meshData.vertices.Add(v3);
        meshData.vertices.Add(v4);

        // Append triangles
        meshData.triangles.Add(vertexCount);
        meshData.triangles.Add(vertexCount + 2 + mask.Normal);
        meshData.triangles.Add(vertexCount + 2 - mask.Normal);
        meshData.triangles.Add(vertexCount + 3);
        meshData.triangles.Add(vertexCount + 1 - mask.Normal);
        meshData.triangles.Add(vertexCount + 1 + mask.Normal);

        // Append normals
        for (int i = 0; i < 4; i++)
        {
            meshData.normals.Add(normal * -1);
        }

        // Append colors
        for (int i = 0; i < 4; i++)
        {
            meshData.colors.Add(color);
        }

        // Append UV coordinates
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
