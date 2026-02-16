using System;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;

[BurstCompile]
public static class PineGenerator
{
    // ========================================================================
    // MAIN PINE GENERATION ENTRY POINT
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
                MakeSmallPine(x, y, z, ref rng, applyBlock);
                break;

            case DecorationType.Tree.MediumPine:
                MakeMediumPine(x, y, z, ref rng, applyBlock);
                break;

            case DecorationType.Tree.LargePine:
                MakeLargePine(x, y, z, ref rng, applyBlock);
                break;
        }
    }

    // ========================================================================
    // SMALL PINE TREE (1x1 trunk)
    // ========================================================================

    private static void MakeSmallPine(
        int x, int y, int z,
        ref Unity.Mathematics.Random rng,
        ApplyBlockDelegate applyBlock)
    {
        int trunkHeight = rng.NextInt(8, 16);

        // Place trunk
        for (int i = 0; i < trunkHeight; i++)
        {
            applyBlock(x, y + i, z, BlockType.Log);
        }

        // Simple tip cluster
        int tipY = y + trunkHeight;
        applyBlock(x + 1, tipY - 1, z, BlockType.Leaves);
        applyBlock(x - 1, tipY - 1, z, BlockType.Leaves);
        applyBlock(x, tipY - 1, z + 1, BlockType.Leaves);
        applyBlock(x, tipY - 1, z - 1, BlockType.Leaves);

        applyBlock(x, tipY, z, BlockType.Leaves);
        applyBlock(x, tipY + 1, z, BlockType.Leaves);
        applyBlock(x, tipY + 2, z, BlockType.Leaves);

        // Simple branch layers - just 4 cardinal directions
        int numWhorls = trunkHeight / 2;
        int startHeight = (int)(trunkHeight * 0.3f);

        for (int whorl = 0; whorl < numWhorls; whorl++)
        {
            int whorlY = y + startHeight + (whorl * 2);
            if (whorlY >= y + trunkHeight - 1) break;

            // Simple branch length calculation
            int branchLength = 2 - whorl / 2;
            if (branchLength < 1) branchLength = 1;

            // Place simple branches in cardinal directions with connected leaves
            for (int i = 1; i <= branchLength; i++)
            {
                PlaceSimpleBranch(x + i, whorlY, z, applyBlock);
                PlaceSimpleBranch(x - i, whorlY, z, applyBlock);
                PlaceSimpleBranch(x, whorlY, z + i, applyBlock);
                PlaceSimpleBranch(x, whorlY, z - i, applyBlock);
            }
        }
    }

    private static void PlaceSimpleBranch(
        int bx, int by, int bz,
        ApplyBlockDelegate applyBlock)
    {
        // Place leaf at the end
        applyBlock(bx, by, bz, BlockType.Leaves);
    }

    // ========================================================================
    // MEDIUM PINE TREE (2x2 trunk)
    // ========================================================================

    private static void MakeMediumPine(
        int x, int y, int z,
        ref Unity.Mathematics.Random rng,
        ApplyBlockDelegate applyBlock)
    {
        z -= 1;
        int trunkHeight = rng.NextInt(22, 36);

        // Place 2x2 trunk
        for (int i = 0; i < trunkHeight; i++)
        {
            applyBlock(x, y + i, z, BlockType.Log);
            applyBlock(x + 1, y + i, z, BlockType.Log);
            applyBlock(x, y + i, z + 1, BlockType.Log);
            applyBlock(x + 1, y + i, z + 1, BlockType.Log);
        }

        // Improved tip cluster - layer of leaves around trunk before the spire
        int tipY = y + trunkHeight;
        for (int dx = -1; dx <= 2; dx++)
        {
            for (int dz = -1; dz <= 2; dz++)
            {
                if ((dx == -1 || dx == 2) && (dz == -1 || dz == 2))
                    continue; // Skip corners
                if (dx >= 0 && dx <= 1 && dz >= 0 && dz <= 1)
                    continue; // Skip trunk area
                applyBlock(x + dx, tipY - 1, z + dz, BlockType.Leaves);
            }
        }

        // Tip spire
        for (int tx = 0; tx < 2; tx++)
        {
            for (int tz = 0; tz < 2; tz++)
            {
                applyBlock(x + tx, tipY, z + tz, BlockType.Leaves);
                applyBlock(x + tx, tipY + 1, z + tz, BlockType.Leaves);
            }
        }
        applyBlock(x, tipY + 2, z, BlockType.Leaves);
        applyBlock(x, tipY + 3, z, BlockType.Leaves);

        // Create branch whorls with spiral rotation
        int numWhorls = (int)(trunkHeight / 3);
        int startHeight = (int)(trunkHeight * 0.2f);
        float spiralRotation = 0f;

        for (int whorl = 0; whorl < numWhorls; whorl++)
        {
            int whorlY = y + startHeight + (whorl * 3);
            if (whorlY >= y + trunkHeight - 1) break;

            // Calculate branch length - LONG at top, SHORT at bottom
            float heightProgress = (float)(whorlY - y - startHeight) / (trunkHeight - startHeight);
            int branchLength = 6 - (int)(4 * heightProgress);
            if (branchLength < 2) branchLength = 2;

            // Number of branches increases from top to bottom
            // Top (progress=1): 4-6 branches, Bottom (progress=0): 7-10 branches
            float invertedProgress = 1.0f - heightProgress; // Invert so bottom = 1, top = 0
            int minBranches = 4 + (int)(3 * invertedProgress); // 4 at top -> 7 at bottom
            int maxBranches = 6 + (int)(4 * invertedProgress); // 6 at top -> 10 at bottom
            int numBranches = rng.NextInt(minBranches, maxBranches + 1);

            for (int b = 0; b < numBranches; b++)
            {
                float angle = (b / (float)numBranches) * math.PI * 2f + spiralRotation;

                // 10% chance for small angle variation
                float angleRoll = rng.NextFloat();
                if (angleRoll < 0.10f)
                {
                    angle += (rng.NextFloat() - 0.5f) * 0.4f; // ±0.2 radians (~11 degrees)
                }

                // 15% chance for branch length variation (increased from 5%)
                int finalBranchLength = branchLength;
                float lengthRoll = rng.NextFloat();
                if (lengthRoll < 0.075f)
                    finalBranchLength = branchLength + 1;
                else if (lengthRoll < 0.15f)
                    finalBranchLength = math.max(2, branchLength - 1);

                // 5% chance for branch to start one block lower
                int branchY = whorlY;
                if (rng.NextFloat() < 0.05f)
                    branchY -= 1;

                int dx = (int)math.round(math.cos(angle) * finalBranchLength);
                int dz = (int)math.round(math.sin(angle) * finalBranchLength);

                PlaceMediumBranch(x, branchY, z, dx, dz, ref rng, applyBlock);
            }

            // Add spiral rotation for next whorl
            spiralRotation += math.PI / 6f;
        }
    }

    private static void PlaceMediumBranch(
        int trunkX, int trunkY, int trunkZ,
        int targetDx, int targetDz,
        ref Unity.Mathematics.Random rng,
        ApplyBlockDelegate applyBlock)
    {
        int steps = math.max(math.abs(targetDx), math.abs(targetDz));

        // Start from edge of 2x2 trunk
        int startX = trunkX + (targetDx > 0 ? 1 : 0);
        int startZ = trunkZ + (targetDz > 0 ? 1 : 0);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            int bx = startX + (int)math.round(targetDx * t);
            int bz = startZ + (int)math.round(targetDz * t);
            int by = trunkY + (i > steps * 0.6f ? -1 : 0);

            applyBlock(bx, by, bz, BlockType.Log);

            // Leaves on the sides of branch (perpendicular to branch direction)
            if (i >= steps - 2 || (i > 2 && rng.NextFloat() > 0.5f))
            {
                // Calculate perpendicular directions
                int perpDx = targetDz != 0 ? 1 : 0;
                int perpDz = targetDx != 0 ? 1 : 0;

                applyBlock(bx + perpDx, by, bz + perpDz, BlockType.Leaves);
                applyBlock(bx - perpDx, by, bz - perpDz, BlockType.Leaves);

                if (i == steps)
                {
                    // Larger end cluster
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            applyBlock(bx + dx, by, bz + dz, BlockType.Leaves);
                        }
                    }
                }
            }
        }
    }

    // ========================================================================
    // LARGE PINE TREE (3x3 trunk)
    // ========================================================================

    private static void MakeLargePine(
        int x, int y, int z,
        ref Unity.Mathematics.Random rng,
        ApplyBlockDelegate applyBlock)
    {
        z -= 1;
        x -= 1;
        int trunkHeight = rng.NextInt(46, 70);

        // Place 3x3 trunk
        for (int i = 0; i < trunkHeight; i++)
        {
            for (int tx = 0; tx < 3; tx++)
            {
                for (int tz = 0; tz < 3; tz++)
                {
                    applyBlock(x + tx, y + i, z + tz, BlockType.Log);
                }
            }
        }

        // Improved tip cluster - layer of leaves around trunk before the spire
        int tipY = y + trunkHeight;
        for (int dx = -1; dx <= 3; dx++)
        {
            for (int dz = -1; dz <= 3; dz++)
            {
                if ((dx == -1 || dx == 3) && (dz == -1 || dz == 3))
                    continue; // Skip corners
                if (dx >= 0 && dx <= 2 && dz >= 0 && dz <= 2)
                    continue; // Skip trunk area
                applyBlock(x + dx, tipY - 1, z + dz, BlockType.Leaves);
            }
        }

        // Tip spire
        for (int tx = 0; tx < 3; tx++)
        {
            for (int tz = 0; tz < 3; tz++)
            {
                applyBlock(x + tx, tipY, z + tz, BlockType.Leaves);
                applyBlock(x + tx, tipY + 1, z + tz, BlockType.Leaves);
            }
        }
        applyBlock(x + 1, tipY + 2, z + 1, BlockType.Leaves);
        applyBlock(x + 1, tipY + 3, z + 1, BlockType.Leaves);
        applyBlock(x + 1, tipY + 4, z + 1, BlockType.Leaves);

        // Create branch whorls with spiral rotation
        int numWhorls = (int)(trunkHeight / 4);
        int startHeight = (int)(trunkHeight * 0.2f);
        float spiralRotation = 0f;

        for (int whorl = 0; whorl < numWhorls; whorl++)
        {
            int whorlY = y + startHeight + (whorl * 4);
            if (whorlY >= y + trunkHeight - 1) break;

            // Calculate branch length - LONG at top, SHORT at bottom
            float heightProgress = (float)(whorlY - y - startHeight) / (trunkHeight - startHeight);
            int branchLength = 9 - (int)(6 * heightProgress);
            if (branchLength < 3) branchLength = 3;

            // Number of branches increases from top to bottom
            // Top (progress=1): 5-7 branches, Bottom (progress=0): 10-13 branches
            float invertedProgress = 1.0f - heightProgress; // Invert so bottom = 1, top = 0
            int minBranches = 5 + (int)(5 * invertedProgress); // 5 at top -> 10 at bottom
            int maxBranches = 7 + (int)(6 * invertedProgress); // 7 at top -> 13 at bottom
            int numBranches = rng.NextInt(minBranches, maxBranches + 1);

            for (int b = 0; b < numBranches; b++)
            {
                float angle = (b / (float)numBranches) * math.PI * 2f + spiralRotation;

                // 10% chance for small angle variation
                float angleRoll = rng.NextFloat();
                if (angleRoll < 0.10f)
                {
                    angle += (rng.NextFloat() - 0.5f) * 0.4f; // ±0.2 radians (~11 degrees)
                }

                // 15% chance for branch length variation (increased from 5%)
                int finalBranchLength = branchLength;
                float lengthRoll = rng.NextFloat();
                if (lengthRoll < 0.075f)
                    finalBranchLength = branchLength + 1;
                else if (lengthRoll < 0.15f)
                    finalBranchLength = math.max(3, branchLength - 1);

                // 5% chance for branch to start one block lower
                int branchY = whorlY;
                if (rng.NextFloat() < 0.05f)
                    branchY -= 1;

                int dx = (int)math.round(math.cos(angle) * finalBranchLength);
                int dz = (int)math.round(math.sin(angle) * finalBranchLength);

                PlaceLargeBranch(x + 1, branchY, z + 1, dx, dz, ref rng, applyBlock);
            }

            // Add spiral rotation for next whorl
            spiralRotation += math.PI / 6f;
        }
    }

    private static void PlaceLargeBranch(
        int trunkX, int trunkY, int trunkZ,
        int targetDx, int targetDz,
        ref Unity.Mathematics.Random rng,
        ApplyBlockDelegate applyBlock)
    {
        int steps = math.max(math.abs(targetDx), math.abs(targetDz));

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            int bx = trunkX + (int)math.round(targetDx * t);
            int bz = trunkZ + (int)math.round(targetDz * t);
            int by = trunkY + (i > steps * 0.5f ? -1 : 0) + (i > steps * 0.75f ? -1 : 0);

            // Thicker branch base
            applyBlock(bx, by, bz, BlockType.Log);
            if (i <= 2)
            {
                applyBlock(bx, by + 1, bz, BlockType.Log);
            }

            // Leaves on the SIDES of branch (perpendicular to branch direction)
            if (i >= steps - 3 || (i > 3 && rng.NextFloat() > 0.4f))
            {
                // Calculate perpendicular directions
                int perpDx = targetDz != 0 ? 1 : 0;
                int perpDz = targetDx != 0 ? 1 : 0;

                applyBlock(bx + perpDx, by, bz + perpDz, BlockType.Leaves);
                applyBlock(bx - perpDx, by, bz - perpDz, BlockType.Leaves);

                if (i == steps)
                {
                    // Large end cluster
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            applyBlock(bx + dx, by, bz + dz, BlockType.Leaves);
                            if (math.abs(dx) + math.abs(dz) <= 1)
                            {
                                applyBlock(bx + dx, by + 1, bz + dz, BlockType.Leaves);
                            }
                        }
                    }
                }
            }
        }
    }
}