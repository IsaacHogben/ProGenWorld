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
        int trunkHeight = rng.NextInt(10, 16);

        // Place trunk
        for (int i = 0; i < trunkHeight; i++)
        {
            applyBlock(x, y + i, z, BlockType.Log);
        }

        // Improved tip cluster - layer of leaves around trunk before the spire
        int tipY = y + trunkHeight;
        applyBlock(x + 1, tipY - 1, z, BlockType.Leaves);
        applyBlock(x - 1, tipY - 1, z, BlockType.Leaves);
        applyBlock(x, tipY - 1, z + 1, BlockType.Leaves);
        applyBlock(x, tipY - 1, z - 1, BlockType.Leaves);

        // Tip spire
        applyBlock(x, tipY, z, BlockType.Leaves);
        applyBlock(x, tipY + 1, z, BlockType.Leaves);
        applyBlock(x, tipY + 2, z, BlockType.Leaves);

        // Create branch whorls with spiral rotation
        int numWhorls = (int)(trunkHeight / 2);
        int startHeight = (int)(trunkHeight * 0.4f);
        float spiralRotation = 0f; // Accumulate rotation for spiral

        for (int whorl = 0; whorl < numWhorls; whorl++)
        {
            int whorlY = y + startHeight + (whorl * 2);
            if (whorlY >= y + trunkHeight) break;

            // Calculate branch length
            float heightProgress = (float)(whorlY - y - startHeight) / (trunkHeight - startHeight);
            int branchLength = 2 - (int)(3 * heightProgress);
            if (branchLength < 0) branchLength = 0;

            int numBranches = rng.NextInt(4, 6);
            for (int b = 0; b < numBranches; b++)
            {
                float angle = (b / (float)numBranches) * math.PI * 2f + spiralRotation;

                // 5% chance for branch length variation
                int finalBranchLength = branchLength;
                float lengthRoll = rng.NextFloat();
                if (lengthRoll < 0.05f)
                    finalBranchLength = branchLength + 1;
                else if (lengthRoll < 0.10f)
                    finalBranchLength = math.max(1, branchLength - 1);

                int dx = (int)math.round(math.cos(angle) * finalBranchLength);
                int dz = (int)math.round(math.sin(angle) * finalBranchLength);

                PlaceSmallBranch(x, whorlY, z, dx, dz, ref rng, applyBlock);
            }

            // Add spiral rotation for next whorl (about 30 degrees per layer)
            spiralRotation += math.PI / 6f;
        }
    }

    private static void PlaceSmallBranch(
        int trunkX, int trunkY, int trunkZ,
        int targetDx, int targetDz,
        ref Unity.Mathematics.Random rng,
        ApplyBlockDelegate applyBlock)
    {
        // Create branch extending from trunk
        int steps = math.max(math.abs(targetDx), math.abs(targetDz));

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            int bx = trunkX + (int)math.round(targetDx * t);
            int bz = trunkZ + (int)math.round(targetDz * t);
            int by = trunkY + (i > steps / 2 ? -1 : 0); // Slight droop at the end

            applyBlock(bx, by, bz, BlockType.Leaves);

            // Add leaves along and at end of branch
            if (i >= steps - 1 || rng.NextFloat() > 0.6f)
            {
                //applyBlock(bx, by + 1, bz, BlockType.Leaves);
                //applyBlock(bx, by - 1, bz, BlockType.Leaves);

                // End cluster
                if (i == steps)
                {
                    applyBlock(bx + 1, by, bz, BlockType.Leaves);
                    applyBlock(bx - 1, by, bz, BlockType.Leaves);
                    applyBlock(bx, by, bz + 1, BlockType.Leaves);
                    applyBlock(bx, by, bz - 1, BlockType.Leaves);
                }
            }
        }
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
        int startHeight = (int)(trunkHeight * 0.3f);
        float spiralRotation = 0f;

        for (int whorl = 0; whorl < numWhorls; whorl++)
        {
            int whorlY = y + startHeight + (whorl * 3);
            if (whorlY >= y + trunkHeight - 1) break;

            // Calculate branch length
            float heightProgress = (float)(whorlY - y - startHeight) / (trunkHeight - startHeight);
            int branchLength = 6 - (int)(4 * heightProgress);
            if (branchLength < 2) branchLength = 2;

            int numBranches = rng.NextInt(5, 9);
            for (int b = 0; b < numBranches; b++)
            {
                float angle = (b / (float)numBranches) * math.PI * 2f + spiralRotation;

                // 5% chance for branch length variation
                int finalBranchLength = branchLength;
                float lengthRoll = rng.NextFloat();
                if (lengthRoll < 0.05f)
                    finalBranchLength = branchLength + 1;
                else if (lengthRoll < 0.10f)
                    finalBranchLength = math.max(2, branchLength - 1);

                int dx = (int)math.round(math.cos(angle) * finalBranchLength);
                int dz = (int)math.round(math.sin(angle) * finalBranchLength);

                PlaceMediumBranch(x, whorlY, z, dx, dz, ref rng, applyBlock);
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

            // Leaves along branch
            if (i >= steps - 2 || (i > 2 && rng.NextFloat() > 0.5f))
            {
                applyBlock(bx, by + 1, bz, BlockType.Leaves);
                applyBlock(bx, by - 1, bz, BlockType.Leaves);

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

        // Tip cluster
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

            // Calculate branch length
            float heightProgress = (float)(whorlY - y - startHeight) / (trunkHeight - startHeight);
            int branchLength = 9 - (int)(6 * heightProgress);
            if (branchLength < 3) branchLength = 3;

            int numBranches = rng.NextInt(5, 12);
            for (int b = 0; b < numBranches; b++)
            {
                float angle = (b / (float)numBranches) * math.PI * 2f + spiralRotation;

                // 5% chance for branch length variation
                int finalBranchLength = branchLength;
                float lengthRoll = rng.NextFloat();
                if (lengthRoll < 0.05f)
                    finalBranchLength = branchLength + 1;
                else if (lengthRoll < 0.10f)
                    finalBranchLength = math.max(3, branchLength - 1);

                int dx = (int)math.round(math.cos(angle) * finalBranchLength);
                int dz = (int)math.round(math.sin(angle) * finalBranchLength);

                PlaceLargeBranch(x + 1, whorlY, z + 1, dx, dz, ref rng, applyBlock);
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

            // Leaves along branch
            if (i >= steps - 3 || (i > 3 && rng.NextFloat() > 0.4f))
            {
                applyBlock(bx, by + 1, bz, BlockType.Leaves);
                applyBlock(bx, by - 1, bz, BlockType.Leaves);
                applyBlock(bx, by + 2, bz, BlockType.Leaves);

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
                    applyBlock(bx, by + 3, bz, BlockType.Leaves);
                }
            }
        }
    }
}
