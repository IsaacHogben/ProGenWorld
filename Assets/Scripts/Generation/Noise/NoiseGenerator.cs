//using FastNoise2;
using Unity.Mathematics;
using UnityEngine.Profiling;

public class NoiseGenerator
{
    private FastNoise noise;
    private int seed;

    public NoiseGenerator(int inSeed)
    {
        seed = inSeed;
        // delve style
        //noise = FastNoise.FromEncodedNodeTree("GgABHQAEAAAAAABI4bo/AAAAAAAAAAAAAAAAzcyMPwAAAAAAAAAAAAAAAAABGgABJQAAAIA/AADAPwAAgD8AAIA/DQAEAAAAH4XrPwcAAAAAAD8AAAAAAAEQAArXA0AeAAQAAAAAAAAA8EEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAK1yM9");
        // Hills + Cubes
        //noise = FastNoise.FromEncodedNodeTree("HgAEAAAAAAB7FO4/AAAAAAAAAAAAAAAAAAAAvwAAAAAAAAAAARsAEQACAAAAAAAgQBAAAAAAQBkAEwDD9Sg/DQAEAAAAAAAgQAkAAGZmJj8AAAAAPwEEAAAAAAAAAEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAM3MTD4AMzMzPwAfhWtAARoAARMAKVwvQAwABAAAAK5HYT7//wMAAEjhej8A7FG4vg==");
        //noise = new FastNoise("FractalFBm");
        // Nice Hills
        //noise = FastNoise.FromEncodedNodeTree("EwDNzMw+EQACAAAAAAAAQBAAexSuPhkAGQATAB+F6z8lAAAAgD/D9eg/AACAPwAAgD8RAAQAAACuRwFAEAAK12NADQADAAAAAAAAQBAAAAAAPwcAAJqZmT4AKVwPPwDXo3A/AM3MzD0AzcyMPwAUrgdAAQAAj8J1PQEEAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHsULj8AMzMzPwAAAAA/");
        noise = FastNoise.FromEncodedNodeTree("BAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==");

    }

    /// <summary>
    /// Generates a density field for a chunk at a given LOD, then applies
    /// biome-driven shaping as a post-process.
    /// 
    /// Pipeline:
    /// 1) Generate base 3D noise in one bulk call (FastNoise)
    /// 2) Apply biome modulation in a single linear pass
    /// 
    /// Important:
    /// - Biomes DECORATE the density; they do not redefine world scale.
    /// - Biome modulation must be low-frequency and low-amplitude.
    /// - This function must remain deterministic and side-effect free.
    /// </summary>
    public float[] FillDensity(
        int3 chunkCoord,
        int chunkSize,
        float frequency,
        int sampleRes,
        BiomeData biome)
    {
        if (noise == null)
            throw new System.Exception("Noise not initialized");

        // --------------------------------------------------
        // 1. Compute density grid dimensions for this LOD
        // --------------------------------------------------
        // sampleRes > 1 means fewer samples per axis (lower detail)
        int scaledSize = chunkSize / sampleRes;

        // +1 because density grids are sampled on voxel corners
        int side = scaledSize + 1;
        int voxelCount = side * side * side;

        float[] density = new float[voxelCount];

        // World-space origin of this chunk, scaled for the LOD.
        // Dividing by sampleRes ensures all LODs sample the SAME
        // underlying noise field, just at different resolutions.
        int3 worldPos = (chunkCoord * chunkSize) / sampleRes;

        // Scale frequency so noise stretches over the same world area
        // regardless of LOD resolution.
        float relativeFrequency = frequency / 100f * sampleRes;

        // --------------------------------------------------
        // 2. Generate base terrain noise (bulk SIMD-friendly call)
        // --------------------------------------------------
        // FastNoise fills the entire density grid in one go.
        // This is significantly faster than sampling per-voxel.
        noise.GenUniformGrid3D(
            density,
            worldPos.x,
            worldPos.y,
            worldPos.z,
            side,
            side,
            side,
            relativeFrequency,
            seed
        );

        // --------------------------------------------------
        // 3. Apply biome modulation (post-process)
        // --------------------------------------------------
        // This pass:
        // - Does NOT change the noise field structure
        // - Only biases and scales it based on biome intent
        // - Runs once, linearly, cache-friendly
        ApplyBiomeToDensity(
            density,
            chunkCoord,
            chunkSize,
            sampleRes,
            biome
        );

        return density;
    }

    /// <summary>
    /// Applies biome-based shaping to an existing density buffer.
    ///
    /// Design goals:
    /// - Biomes shape terrain, but do not introduce hard boundaries
    /// - All effects must fade out naturally at biome borders
    ///
    /// This function operates column-wise (XZ),
    /// applying the same biome influence across Y.
    /// May later also change with Y value.
    /// </summary>
    static void ApplyBiomeToDensity(
        float[] density,
        int3 chunkCoord,
        int chunkSize,
        int sampleRes,
        BiomeData biome)
    {
        // --------------------------------------------------
        // Density grid dimensions (LOD-dependent)
        // --------------------------------------------------
        int scaledSize = chunkSize / sampleRes;
        int side = scaledSize + 1;

        // --------------------------------------------------
        // Biome grid dimensions (column-based, +1 resolution)
        // --------------------------------------------------
        int biomeRes = biome.resolution;
        int biomeSide = biomeRes + 1;

        // --------------------------------------------------
        // Iterate column-by-column (XZ)
        // --------------------------------------------------
        // Biomes are fundamentally 2D.
        // We sample biome intent once per column and
        // apply it uniformly across Y.
        for (int z = 0; z < side; z++)
        {
            // Normalized local Z position within the chunk [0..1]
            float lz = (float)z / scaledSize;

            // Map to biome grid coordinate
            int bz = (int)math.round(lz * biomeRes);
            bz = math.min(bz, biomeRes);

            for (int x = 0; x < side; x++)
            {
                // Normalized local X position within the chunk [0..1]
                float lx = (float)x / scaledSize;

                // Map to biome grid coordinate
                int bx = (int)math.round(lx * biomeRes);
                bx = math.min(bx, biomeRes);

                // Fetch biome intent for this column
                BiomeHint hint = biome.grid[bx + bz * biomeSide];

                // Convert blend byte to [0..1] weight
                float blend = hint.blend * (1f / 255f);

                // --------------------------------------------------
                // Interpret biome intent
                // --------------------------------------------------
                // These values are intentionally small.
                // Biomes should influence terrain character,
                // not break global continuity.
                //
                // Example mapping:
                // biome 0 = plains
                // biome 1 = hills
                float heightBias =
                    hint.primary == 1 ? 0.5f :
                    hint.secondary == 1 ? 0.5f * blend :
                    0f;

                float amplitude =
                    hint.primary == 1 ? 2f :
                    hint.secondary == 1 ? math.lerp(1f, 2f, blend) :
                    1f;

                // --------------------------------------------------
                // Apply to all Y samples in this column
                // --------------------------------------------------
                for (int y = 0; y < side; y++)
                {
                    int idx = x + y * side + z * side * side;

                    float d = density[idx];

                    // Scale first, then bias.
                    // Order matters: scaling preserves shape,
                    // bias shifts elevation.
                    d = d * amplitude + heightBias;

                    density[idx] = d;
                }
            }
        }
    }


}
