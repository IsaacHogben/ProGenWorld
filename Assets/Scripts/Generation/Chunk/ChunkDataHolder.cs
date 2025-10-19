using UnityEngine;

public class ChunkDataHolder : MonoBehaviour
{
    public ChunkData data;
    void OnDestroy()
    {
        if (data.density.IsCreated)
            data.density.Dispose();
    }
}

