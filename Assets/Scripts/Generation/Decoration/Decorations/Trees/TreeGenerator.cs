using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

// Function pointer delegate for block placement
public delegate void ApplyBlockDelegate(int x, int y, int z, BlockType block);

[BurstCompile]
public static class TreeGenerator
{
    // ========================================================================
    // MAIN TREE GENERATION ENTRY POINT
    // ========================================================================

    public static void Generate(
        DecorationType.Tree treeType,
        int x, int y, int z,
        ref Unity.Mathematics.Random rng,
        ref BlockWriter writer)
    {
        switch (treeType)
        {
            case DecorationType.Tree.SmallPine:
            case DecorationType.Tree.MediumPine:
            case DecorationType.Tree.LargePine:
                PineGenerator.Generate(treeType, x, y, z, ref rng, ref writer);
                break;

            case DecorationType.Tree.RedFancy:
                LSystemTreeGenerator.Generate(treeType, x, y, z, ref rng, ref writer);
                break;


        }
    }
}