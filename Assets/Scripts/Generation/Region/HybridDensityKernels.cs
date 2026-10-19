using Unity.Burst;
using Unity.Mathematics;

/// <summary>
/// The per voxel loops of HybridDensity.Fill, compiled by Burst (called directly from the density
/// tasks; the FastNoise trees are sampled between them). Arrays are a chunk's (n + 1)^3 voxels,
/// x fastest then y then z, and per column data (n + 1)^2, x fastest.
/// </summary>
[BurstCompile]
public static unsafe class HybridDensityKernels
{
    /// <summary>The height surface's density in every voxel: densityScale * (y - height) * invNorm.</summary>
    [BurstCompile]
    public static void Surface(float* output, float* height, float* invNorm, int side, float originY, int sampleRes, float densityScale)
    {
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int col = x + z * side;
            float d0 = densityScale * (originY - height[col]) * invNorm[col];
            float dStep = densityScale * sampleRes * invNorm[col];
            int baseIndex = x + z * side * side;
            for (int y = 0; y < side; y++)
                output[baseIndex + y * side] = d0 + y * dStep;
        }
    }

    /// <summary>
    /// Sample positions of one tree: every band voxel of every column with weight there, in column
    /// order (Accumulate reads the results back in the same order). Returns the count.
    /// </summary>
    [BurstCompile]
    public static int Positions(float* xs, float* ys, float* zs, float* colWeight, int* bandLo, int* bandHi, float* height,
        int side, float originX, float originY, float originZ, int sampleRes, float f, float offset)
    {
        int k = 0;
        float yStep = sampleRes * f;
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int col = x + z * side;
            if (colWeight[col] == 0f)
                continue;
            float wx = (originX + x * sampleRes) * f, wz = (originZ + z * sampleRes) * f;
            float y0 = (originY - height[col] + offset) * f;
            for (int y = bandLo[col]; y <= bandHi[col]; y++)
            {
                xs[k] = wx;
                ys[k] = y0 + y * yStep;
                zs[k] = wz;
                k++;
            }
        }
        return k;
    }

    /// <summary>Adds one tree's samples (in Positions order) times its weight into detail.</summary>
    [BurstCompile]
    public static void Accumulate(float* detail, float* outs, float* colWeight, int* bandLo, int* bandHi, int side, float scale)
    {
        int k = 0;
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int col = x + z * side;
            float w = colWeight[col];
            if (w == 0f)
                continue;
            w *= scale;
            int baseIndex = x + z * side * side;
            for (int y = bandLo[col]; y <= bandHi[col]; y++)
                detail[baseIndex + y * side] += w * outs[k++];
        }
    }

    /// <summary>The 3D shapes replace the surface by their combined weight, inside each column's band.</summary>
    [BurstCompile]
    public static void Blend(float* output, float* detail, float* colWeightSum, int* bandLo, int* bandHi, int side)
    {
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int col = x + z * side;
            float w = math.min(1f, colWeightSum[col]);
            int baseIndex = x + z * side * side;
            for (int y = bandLo[col]; y <= bandHi[col]; y++)
            {
                int i = baseIndex + y * side;
                output[i] = (1f - w) * output[i] + detail[i];
            }
        }
    }

    /// <summary>
    /// Coarse sampling: marks the grid points (every step voxels, m = n / step + 1 per side) around
    /// every band voxel of the columns with weight, and writes their sample positions (each with y
    /// measured from its own column's surface). Returns the count; needed is m^3, cleared here.
    /// </summary>
    [BurstCompile]
    public static int CoarsePositions(byte* needed, float* xs, float* ys, float* zs, float* colWeight, int* bandLo, int* bandHi,
        float* height, int side, int step, float originX, float originY, float originZ, int sampleRes, float f, float offset)
    {
        int n = side - 1;
        int m = n / step + 1;
        for (int i = 0; i < m * m * m; i++)
            needed[i] = 0;

        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int col = x + z * side;
            if (colWeight[col] == 0f)
                continue;
            int x0 = x / step, x1 = (x + step - 1) / step;
            int z0 = z / step, z1 = (z + step - 1) / step;
            int gy0 = bandLo[col] / step, gy1 = (bandHi[col] + step - 1) / step;
            for (int gy = gy0; gy <= gy1; gy++)
            {
                needed[x0 + (gy + z0 * m) * m] = 1;
                needed[x1 + (gy + z0 * m) * m] = 1;
                needed[x0 + (gy + z1 * m) * m] = 1;
                needed[x1 + (gy + z1 * m) * m] = 1;
            }
        }

        int k = 0;
        for (int gz = 0; gz < m; gz++)
        for (int gy = 0; gy < m; gy++)
        for (int gx = 0; gx < m; gx++)
        {
            if (needed[gx + (gy + gz * m) * m] == 0)
                continue;
            int vx = gx * step, vy = gy * step, vz = gz * step;
            xs[k] = (originX + vx * sampleRes) * f;
            ys[k] = (originY + vy * sampleRes - height[vx + vz * side] + offset) * f;
            zs[k] = (originZ + vz * sampleRes) * f;
            k++;
        }
        return k;
    }

    /// <summary>
    /// Coarse sampling: spreads the grid samples (in CoarsePositions order) back onto the grid and
    /// adds the trilinear blend of them, times the tree's weight, into every band voxel.
    /// </summary>
    [BurstCompile]
    public static void CoarseAccumulate(float* detail, float* nodes, byte* needed, float* outs, float* colWeight,
        int* bandLo, int* bandHi, int side, int step, float scale)
    {
        int n = side - 1;
        int m = n / step + 1;
        int k = 0;
        for (int g = 0; g < m * m * m; g++)
            if (needed[g] != 0)
                nodes[g] = outs[k++];

        float inv = 1f / step;
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int col = x + z * side;
            float w = colWeight[col];
            if (w == 0f)
                continue;
            w *= scale;
            int x0 = x / step, z0 = z / step;
            int x1 = math.min(x0 + 1, m - 1), z1 = math.min(z0 + 1, m - 1);
            float fx = (x - x0 * step) * inv, fz = (z - z0 * step) * inv;
            int r00 = x0 + z0 * m * m, r10 = x1 + z0 * m * m, r01 = x0 + z1 * m * m, r11 = x1 + z1 * m * m;
            int baseIndex = x + z * side * side;
            for (int y = bandLo[col]; y <= bandHi[col]; y++)
            {
                int y0 = y / step;
                int y1 = math.min(y0 + 1, m - 1);
                float fy = (y - y0 * step) * inv;
                int a0 = y0 * m, a1 = y1 * m;
                float lower = math.lerp(math.lerp(nodes[r00 + a0], nodes[r10 + a0], fx), math.lerp(nodes[r01 + a0], nodes[r11 + a0], fx), fz);
                float upper = math.lerp(math.lerp(nodes[r00 + a1], nodes[r10 + a1], fx), math.lerp(nodes[r01 + a1], nodes[r11 + a1], fx), fz);
                detail[baseIndex + y * side] += w * math.lerp(lower, upper, fy);
            }
        }
    }
}
