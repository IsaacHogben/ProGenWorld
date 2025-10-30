//using FastNoise2;
using System.Diagnostics;
using Unity.Collections;
using Unity.Mathematics;

public class NoiseGenerator
{
    private FastNoise noise;

    public NoiseGenerator(int seed = 1337)
    {
        //noise = FastNoise.FromEncodedNodeTree("DQAGAAAAAAAAQBAAAAAAPwcAAJqZmT4AKVwPPwCPwvW+");
        //noise = FastNoise.FromEncodedNodeTree("EwCamRk/EQACAAAAAAAgQBAAexSuPhkAGQATAB+F6z8lAAAAgD/D9eg/AACAPwAAgD8RAAMAAABI4fo/EAAK12NADQADAAAAAAAAQBAAAAAAPwcAAJqZmT4AKVwPPwDXo3A/AM3MzD0AzcyMPwAUrgdAAQAAj8J1PQEEAAAAAABcj4I/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHsULj8AMzMzPwAAAAA/");
        //noise = new FastNoise("FractalFBm");
        noise = FastNoise.FromEncodedNodeTree("EwDNzMw+EQACAAAAAAAAQBAAexSuPhkAGQATAB+F6z8lAAAAgD/D9eg/AACAPwAAgD8RAAQAAACuRwFAEAAK12NADQADAAAAAAAAQBAAAAAAPwcAAJqZmT4AKVwPPwDXo3A/AM3MzD0AzcyMPwAUrgdAAQAAj8J1PQEEAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHsULj8AMzMzPwAAAAA/");
        //noise = FastNoise.FromEncodedNodeTree("IQAZABMArkfhPyUAAACAP8P16D8AAIA/AACAPxEABAAAAK5HAUAQAArXY0ANAAMAAAAAAABAEAAAAAA/BwAAmpmZPgApXA8/ANejcD8AzczMPQDNzIw/ABSuB0ABEwBcj0I/EAB7FK4+JwABAAAA//8BAACuR+FABAAAAAAAuB6FPwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACuR+G+");
    }

    public float[] FillDensity(int3 chunkCoord, int chunkSize, int voxelCount)
    {
        if (noise == null)
            throw new System.Exception("Noise not initialized");
        float[] noiseOut = new float[voxelCount];
        //int index = 0;

        int3 worldPos = (chunkCoord * chunkSize);
        //density[index++] = noise.GenSingle3D(worldPos.x, worldPos.y, worldPos.z, 1337);
        noise.GenUniformGrid3D(noiseOut, worldPos.x, worldPos.y, worldPos.z, chunkSize + 1, chunkSize + 1, chunkSize + 1, 0.009f, 1337);
        return noiseOut;
        /*for (int i = 0; i < voxelCount; i++) 
        {
            density[i] = noiseOut[i]; 
        }*/

    }
}
