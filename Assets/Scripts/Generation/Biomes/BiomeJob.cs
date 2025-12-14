using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public struct BiomeJob : IJobParallelFor
{
    public int3 chunkCoord;
    public int chunkSize;
    public int resolution;
    public uint seed;
    public float frequency;

    [WriteOnly]
    public NativeArray<BiomeHint> output;

    public void Execute(int index)
    {
        int side = resolution + 1;

        int x = index % side;
        int z = index / side;

        // Normalized [0..1] INCLUDING edges
        float fx = (float)x / resolution;
        float fz = (float)z / resolution;

        float worldX = chunkCoord.x * chunkSize + fx * chunkSize;
        float worldZ = chunkCoord.z * chunkSize + fz * chunkSize;

        float humidity = ValueNoise2D(worldX, worldZ, frequency, seed);

        byte primary, secondary;
        float blend;

        if (humidity < 0.5f)
        {
            primary = 0;
            secondary = 1;
            blend = humidity * 2f;
        }
        else
        {
            primary = 1;
            secondary = 0;
            blend = (humidity - 0.5f) * 2f;
        }

        output[index] = new BiomeHint
        {
            primary = primary,
            secondary = secondary,
            blend = (byte)math.clamp(blend * 255f, 0f, 255f)
        };
    }


    // --------------------------------------------------
    // Burst-safe Value Noise
    // --------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352d;
        x ^= x >> 15;
        x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Hash01(int x, int z, uint seed)
    {
        uint h =
            (uint)x * 374761393u ^
            (uint)z * 668265263u ^
            seed * 1442695041u;

        return (Hash(h) & 0x00FFFFFF) * (1f / 16777216f);
    }

    static float ValueNoise2D(float x, float z, float freq, uint seed)
    {
        x *= freq;
        z *= freq;

        int xi = (int)math.floor(x);
        int zi = (int)math.floor(z);

        float xf = x - xi;
        float zf = z - zi;

        float v00 = Hash01(xi, zi, seed);
        float v10 = Hash01(xi + 1, zi, seed);
        float v01 = Hash01(xi, zi + 1, seed);
        float v11 = Hash01(xi + 1, zi + 1, seed);

        float u = xf * xf * (3f - 2f * xf);
        float v = zf * zf * (3f - 2f * zf);

        return math.lerp(
            math.lerp(v00, v10, u),
            math.lerp(v01, v11, u),
            v
        );
    }
}
