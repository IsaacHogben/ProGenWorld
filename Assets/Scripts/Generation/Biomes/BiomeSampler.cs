using Unity.Mathematics;
using Unity.Collections;

public static class BiomeSampler
{
    /// <summary>
    /// Samples a biome hint from a biome grid given world position.
    /// </summary>
    public static BiomeHint SampleBiome(
    NativeArray<BiomeHint> grid,
    int resolution,
    int chunkSize,
    int localX,
    int localZ)
    {
        int side = resolution + 1;

        float u = localX / (float)chunkSize;
        float v = localZ / (float)chunkSize;

        float fx = u * resolution;
        float fz = v * resolution;

        int x = math.clamp((int)math.floor(fx), 0, resolution);
        int z = math.clamp((int)math.floor(fz), 0, resolution);

        return grid[x + z * side];
    }
}
