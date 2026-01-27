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
        ApplyBlockDelegate applyBlock)
    {
        switch (treeType)
        {
            case DecorationType.Tree.SmallPine:
            case DecorationType.Tree.MediumPine:
            case DecorationType.Tree.LargePine:
                PineGenerator.Generate(treeType, x, y, z, ref rng, applyBlock);
                break;

            case DecorationType.Tree.Oak:
                // MakeOak(x, y, z, ref rng, applyBlock);
                break;

            case DecorationType.Tree.Birch:
                // MakeBirch(x, y, z, ref rng, applyBlock);
                break;

            case DecorationType.Tree.DeadTree:
                // MakeDeadTree(x, y, z, ref rng, applyBlock);
                break;

            case DecorationType.Tree.AlienSpire:
                // MakeAlienSpire(x, y, z, ref rng, applyBlock);
                break;
        }
    }
}