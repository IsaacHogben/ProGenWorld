//using FastNoise2;
using Unity.Mathematics;

public class NoiseGenerator
{
    private FastNoise noise;
    private int seed;

    public NoiseGenerator(int inSeed)
    {
        seed = inSeed;
        //noise = FastNoise.FromEncodedNodeTree("DQAGAAAAAAAAQBAAAAAAPwcAAJqZmT4AKVwPPwCPwvW+");
        noise = FastNoise.FromEncodedNodeTree("HgAEAAAAAAB7FO4/AAAAAAAAAAAAAAAAAAAAvwAAAAAAAAAAARsAEQACAAAAAAAgQBAAAAAAQBkAEwDD9Sg/DQAEAAAAAAAgQAkAAGZmJj8AAAAAPwEEAAAAAAAAAEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAM3MTD4AMzMzPwAfhWtAARoAARMAKVwvQAwABAAAAK5HYT7//wMAAEjhej8A7FG4vg==");
        //noise = new FastNoise("FractalFBm");
        //noise = FastNoise.FromEncodedNodeTree("EwDNzMw+EQACAAAAAAAAQBAAexSuPhkAGQATAB+F6z8lAAAAgD/D9eg/AACAPwAAgD8RAAQAAACuRwFAEAAK12NADQADAAAAAAAAQBAAAAAAPwcAAJqZmT4AKVwPPwDXo3A/AM3MzD0AzcyMPwAUrgdAAQAAj8J1PQEEAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHsULj8AMzMzPwAAAAA/");
        //noise = FastNoise.FromEncodedNodeTree("IQAZABMArkfhPyUAAACAP8P16D8AAIA/AACAPxEABAAAAK5HAUAQAArXY0ANAAMAAAAAAABAEAAAAAA/BwAAmpmZPgApXA8/ANejcD8AzczMPQDNzIw/ABSuB0ABEwBcj0I/EAB7FK4+JwABAAAA//8BAACuR+FABAAAAAAAuB6FPwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACuR+G+");
    }

    public float[] FillDensity(int3 chunkCoord, int chunkSize, float frequency, int stride)
    {
        if (noise == null)
            throw new System.Exception("Noise not initialized");

        // each LOD has fewer samples per axis
        int scaledSize = (chunkSize / stride);
        int scaledVoxelCount = (scaledSize + 1) * (scaledSize + 1) * (scaledSize + 1);

        float[] noiseOut = new float[scaledVoxelCount];
        int3 worldPos = (chunkCoord * chunkSize) / stride;

        // scale frequency up to stretch the same area of the noise field
        // this keeps world alignment consistent between LODs
        float relativeFrequency = frequency/100 * stride;

        noise.GenUniformGrid3D(
            noiseOut,
            worldPos.x,
            worldPos.y,
            worldPos.z,
            scaledSize + 1,
            scaledSize + 1,
            scaledSize + 1,
            relativeFrequency,
            seed
        );
        return noiseOut;
    }
}
