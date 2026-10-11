// BiomeNoise.cs
// Burst-compatible port of the part of FastNoiseLite the biome system uses: 2D OpenSimplex2 with
// FBm fractal. FastNoiseLite is a managed class and cannot run in a Burst job; this keeps the same
// constants, gradient table and float operations as FastNoiseLite.GetNoise(x, y) for these settings
// (NoiseType.OpenSimplex2, FractalType.FBm). Not bit-exact under Burst: within 50 chunks of the
// origin results differ by up to ~2e-5, growing with distance as float precision drops.

using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>2D OpenSimplex2 FBm noise settings, evaluated like FastNoiseLite.</summary>
public struct FbmSimplexNoise2D
{
    public int seed;
    public float frequency;
    public int octaves;
    public float lacunarity;
    public float gain;
    public float weightedStrength;
    public float fractalBounding;

    public static FbmSimplexNoise2D Create(int seed, float frequency, int octaves = 3, float lacunarity = 2f, float gain = 0.5f, float weightedStrength = 0f)
    {
        // FastNoiseLite.CalculateFractalBounding
        float g = gain < 0 ? -gain : gain;
        float amp = g;
        float ampFractal = 1.0f;
        for (int i = 1; i < octaves; i++)
        {
            ampFractal += amp;
            amp *= g;
        }

        return new FbmSimplexNoise2D
        {
            seed = seed,
            frequency = frequency,
            octaves = octaves,
            lacunarity = lacunarity,
            gain = gain,
            weightedStrength = weightedStrength,
            fractalBounding = 1 / ampFractal
        };
    }

    /// <summary>Noise in -1..1, as FastNoiseLite.GetNoise(x, y).</summary>
    public float GetNoise(float x, float y)
    {
        // TransformNoiseCoordinate: frequency, then OpenSimplex2 skew
        x *= frequency;
        y *= frequency;
        const float SQRT3 = (float)1.7320508075688772935274463415059;
        const float F2 = 0.5f * (SQRT3 - 1);
        float t = (x + y) * F2;
        x += t;
        y += t;

        // GenFractalFBm
        int s = seed;
        float sum = 0;
        float amp = fractalBounding;
        for (int i = 0; i < octaves; i++)
        {
            float noise = SingleSimplex(s++, x, y);
            sum += noise * amp;
            amp *= Lerp(1.0f, math.min(noise + 1, 2) * 0.5f, weightedStrength);

            x *= lacunarity;
            y *= lacunarity;
            amp *= gain;
        }
        return sum;
    }

    const int PrimeX = 501125321;
    const int PrimeY = 1136930381;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Lerp(float a, float b, float t) => a + t * (b - a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int FastFloor(float f) => f >= 0 ? (int)f : (int)f - 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float GradCoord(int seed, int xPrimed, int yPrimed, float xd, float yd)
    {
        int hash = seed ^ xPrimed ^ yPrimed;
        hash *= 0x27d4eb2d;
        hash ^= hash >> 15;
        hash &= 127 << 1;

        return xd * Gradients2D[hash] + yd * Gradients2D[hash | 1];
    }

    // FastNoiseLite.SingleSimplex (2D OpenSimplex2, skew already applied)
    static float SingleSimplex(int seed, float x, float y)
    {
        const float SQRT3 = 1.7320508075688772935274463415059f;
        const float G2 = (3 - SQRT3) / 6;

        int i = FastFloor(x);
        int j = FastFloor(y);
        float xi = x - i;
        float yi = y - j;

        float t = (xi + yi) * G2;
        float x0 = xi - t;
        float y0 = yi - t;

        i *= PrimeX;
        j *= PrimeY;

        float n0, n1, n2;

        float a = 0.5f - x0 * x0 - y0 * y0;
        if (a <= 0) n0 = 0;
        else n0 = (a * a) * (a * a) * GradCoord(seed, i, j, x0, y0);

        float c = (float)(2 * (1 - 2 * G2) * (1 / G2 - 2)) * t + ((float)(-2 * (1 - 2 * G2) * (1 - 2 * G2)) + a);
        if (c <= 0) n2 = 0;
        else
        {
            float x2 = x0 + (2 * G2 - 1);
            float y2 = y0 + (2 * G2 - 1);
            n2 = (c * c) * (c * c) * GradCoord(seed, i + PrimeX, j + PrimeY, x2, y2);
        }

        if (y0 > x0)
        {
            float x1 = x0 + G2;
            float y1 = y0 + (G2 - 1);
            float b = 0.5f - x1 * x1 - y1 * y1;
            if (b <= 0) n1 = 0;
            else n1 = (b * b) * (b * b) * GradCoord(seed, i, j + PrimeY, x1, y1);
        }
        else
        {
            float x1 = x0 + (G2 - 1);
            float y1 = y0 + G2;
            float b = 0.5f - x1 * x1 - y1 * y1;
            if (b <= 0) n1 = 0;
            else n1 = (b * b) * (b * b) * GradCoord(seed, i + PrimeX, j, x1, y1);
        }

        return (n0 + n1 + n2) * 99.83685446303647f;
    }

    // FastNoiseLite.Gradients2D
    static readonly float[] Gradients2D =
    {
         0.130526192220052f,  0.99144486137381f,   0.38268343236509f,   0.923879532511287f,  0.608761429008721f,  0.793353340291235f,  0.793353340291235f,  0.608761429008721f,
         0.923879532511287f,  0.38268343236509f,   0.99144486137381f,   0.130526192220051f,  0.99144486137381f,  -0.130526192220051f,  0.923879532511287f, -0.38268343236509f,
         0.793353340291235f, -0.60876142900872f,   0.608761429008721f, -0.793353340291235f,  0.38268343236509f,  -0.923879532511287f,  0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f,  -0.38268343236509f,  -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f,  -0.99144486137381f,  -0.130526192220052f, -0.99144486137381f,   0.130526192220051f, -0.923879532511287f,  0.38268343236509f,
        -0.793353340291235f,  0.608761429008721f, -0.608761429008721f,  0.793353340291235f, -0.38268343236509f,   0.923879532511287f, -0.130526192220052f,  0.99144486137381f,
         0.130526192220052f,  0.99144486137381f,   0.38268343236509f,   0.923879532511287f,  0.608761429008721f,  0.793353340291235f,  0.793353340291235f,  0.608761429008721f,
         0.923879532511287f,  0.38268343236509f,   0.99144486137381f,   0.130526192220051f,  0.99144486137381f,  -0.130526192220051f,  0.923879532511287f, -0.38268343236509f,
         0.793353340291235f, -0.60876142900872f,   0.608761429008721f, -0.793353340291235f,  0.38268343236509f,  -0.923879532511287f,  0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f,  -0.38268343236509f,  -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f,  -0.99144486137381f,  -0.130526192220052f, -0.99144486137381f,   0.130526192220051f, -0.923879532511287f,  0.38268343236509f,
        -0.793353340291235f,  0.608761429008721f, -0.608761429008721f,  0.793353340291235f, -0.38268343236509f,   0.923879532511287f, -0.130526192220052f,  0.99144486137381f,
         0.130526192220052f,  0.99144486137381f,   0.38268343236509f,   0.923879532511287f,  0.608761429008721f,  0.793353340291235f,  0.793353340291235f,  0.608761429008721f,
         0.923879532511287f,  0.38268343236509f,   0.99144486137381f,   0.130526192220051f,  0.99144486137381f,  -0.130526192220051f,  0.923879532511287f, -0.38268343236509f,
         0.793353340291235f, -0.60876142900872f,   0.608761429008721f, -0.793353340291235f,  0.38268343236509f,  -0.923879532511287f,  0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f,  -0.38268343236509f,  -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f,  -0.99144486137381f,  -0.130526192220052f, -0.99144486137381f,   0.130526192220051f, -0.923879532511287f,  0.38268343236509f,
        -0.793353340291235f,  0.608761429008721f, -0.608761429008721f,  0.793353340291235f, -0.38268343236509f,   0.923879532511287f, -0.130526192220052f,  0.99144486137381f,
         0.130526192220052f,  0.99144486137381f,   0.38268343236509f,   0.923879532511287f,  0.608761429008721f,  0.793353340291235f,  0.793353340291235f,  0.608761429008721f,
         0.923879532511287f,  0.38268343236509f,   0.99144486137381f,   0.130526192220051f,  0.99144486137381f,  -0.130526192220051f,  0.923879532511287f, -0.38268343236509f,
         0.793353340291235f, -0.60876142900872f,   0.608761429008721f, -0.793353340291235f,  0.38268343236509f,  -0.923879532511287f,  0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f,  -0.38268343236509f,  -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f,  -0.99144486137381f,  -0.130526192220052f, -0.99144486137381f,   0.130526192220051f, -0.923879532511287f,  0.38268343236509f,
        -0.793353340291235f,  0.608761429008721f, -0.608761429008721f,  0.793353340291235f, -0.38268343236509f,   0.923879532511287f, -0.130526192220052f,  0.99144486137381f,
         0.130526192220052f,  0.99144486137381f,   0.38268343236509f,   0.923879532511287f,  0.608761429008721f,  0.793353340291235f,  0.793353340291235f,  0.608761429008721f,
         0.923879532511287f,  0.38268343236509f,   0.99144486137381f,   0.130526192220051f,  0.99144486137381f,  -0.130526192220051f,  0.923879532511287f, -0.38268343236509f,
         0.793353340291235f, -0.60876142900872f,   0.608761429008721f, -0.793353340291235f,  0.38268343236509f,  -0.923879532511287f,  0.130526192220052f, -0.99144486137381f,
        -0.130526192220052f, -0.99144486137381f,  -0.38268343236509f,  -0.923879532511287f, -0.608761429008721f, -0.793353340291235f, -0.793353340291235f, -0.608761429008721f,
        -0.923879532511287f, -0.38268343236509f,  -0.99144486137381f,  -0.130526192220052f, -0.99144486137381f,   0.130526192220051f, -0.923879532511287f,  0.38268343236509f,
        -0.793353340291235f,  0.608761429008721f, -0.608761429008721f,  0.793353340291235f, -0.38268343236509f,   0.923879532511287f, -0.130526192220052f,  0.99144486137381f,
         0.38268343236509f,   0.923879532511287f,  0.923879532511287f,  0.38268343236509f,   0.923879532511287f, -0.38268343236509f,   0.38268343236509f,  -0.923879532511287f,
        -0.38268343236509f,  -0.923879532511287f, -0.923879532511287f, -0.38268343236509f,  -0.923879532511287f,  0.38268343236509f,  -0.38268343236509f,   0.923879532511287f,
    };
}

/// <summary>
/// Fills a chunk's terrain / humidity / temperature biome grids ((resolution + 1)^2, z major),
/// remapped to 0..1. Runs before BiomeJob, which reads the grids.
/// </summary>
[BurstCompile]
public struct BiomeNoiseJob : IJobParallelFor
{
    public int3 chunkCoord;
    public int chunkSize;
    public int resolution;

    public FbmSimplexNoise2D terrainNoise;
    public FbmSimplexNoise2D humidityNoise;
    public FbmSimplexNoise2D temperatureNoise;

    [WriteOnly] public NativeArray<float> terrainNoiseGrid;
    [WriteOnly] public NativeArray<float> humidityNoiseGrid;
    [WriteOnly] public NativeArray<float> temperatureNoiseGrid;

    public void Execute(int index)
    {
        int side = resolution + 1;
        int x = index % side;
        int z = index / side;

        float fx = (float)x / resolution;
        float fz = (float)z / resolution;
        float worldX = chunkCoord.x * chunkSize + fx * chunkSize;
        float worldZ = chunkCoord.z * chunkSize + fz * chunkSize;

        terrainNoiseGrid[index] = terrainNoise.GetNoise(worldX, worldZ) * 0.5f + 0.5f;
        humidityNoiseGrid[index] = humidityNoise.GetNoise(worldX, worldZ) * 0.5f + 0.5f;
        temperatureNoiseGrid[index] = temperatureNoise.GetNoise(worldX, worldZ) * 0.5f + 0.5f;
    }
}
