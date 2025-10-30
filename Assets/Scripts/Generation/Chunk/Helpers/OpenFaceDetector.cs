using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;

public class OpenFaceDetector : System.IDisposable
{
    private NativeArray<BlockDatabase.BlockInfoUnmanaged> nativeBlockDatabase;
    private readonly ComputeShader shader;
    private readonly int kernel;

    public OpenFaceDetector(ref NativeArray<BlockDatabase.BlockInfoUnmanaged> nativeBlockDatabase)
    {
        // Auto-load compute shader from Resources/OpenFaceDetect.compute
        shader = Resources.Load<ComputeShader>("OpenFaceDetect");
        if (shader == null)
        {
            Debug.LogError("[OpenFaceDetector] Failed to load compute shader 'OpenFaceDetect.compute'. " +
                           "Ensure it is located in a Resources folder.");
            return;
        }
        kernel = shader.FindKernel("CSMain");
        this.nativeBlockDatabase = nativeBlockDatabase;
    }

    public OpenFaces DetectGPU(NativeArray<byte> blockIds, int chunkSize)
    {
        if (!blockIds.IsCreated || shader == null)
            return OpenFaces.None;
        int voxelCount = blockIds.Length;

        ComputeBuffer blocksBuf = new ComputeBuffer(voxelCount, sizeof(uint));
        ComputeBuffer resultBuf = new ComputeBuffer(1, sizeof(uint));

        // Convert byte IDs to uints efficiently
        NativeArray<uint> temp = new NativeArray<uint>(voxelCount, Allocator.Temp);
        for (int i = 0; i < voxelCount; i++)
            temp[i] = blockIds[i];
        blocksBuf.SetData(temp);
        temp.Dispose();

        shader.SetBuffer(kernel, "blockIds", blocksBuf);
        shader.SetBuffer(kernel, "result", resultBuf);
        shader.SetInt("chunkSize", chunkSize);

        shader.Dispatch(kernel, 1, 1, 1);

        uint[] outFlags = new uint[1];
        resultBuf.GetData(outFlags);

        blocksBuf.Dispose();
        resultBuf.Dispose();

        return (OpenFaces)outFlags[0];
    }

    public void Dispose()
    {
        // Nothing persistent to clean up (buffers are transient)
    }
}
