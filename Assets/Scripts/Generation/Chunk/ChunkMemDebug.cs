using UnityEngine;

public static class ChunkMemDebug
{
    public static int ActiveDensityArrays;
    public static int ActiveBlockIdArrays;
    public static int ActiveMeshDatas;

    public static int TotalDensityAlloc;
    public static int TotalBlockIdAlloc;
    public static int TotalMeshDataAlloc;

    private static float _lastLogTime;

    public static void LogIfNeeded()
    {
        if (Time.time - _lastLogTime < 2f) return;
        _lastLogTime = Time.time;

        Debug.Log(
            $"[ChunkMem] Density active={ActiveDensityArrays} totalAlloc={TotalDensityAlloc} | " +
            $"BlockIds active={ActiveBlockIdArrays} totalAlloc={TotalBlockIdAlloc} | " +
            $"MeshData active={ActiveMeshDatas} totalAlloc={TotalMeshDataAlloc}");
    }
}