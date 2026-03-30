using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

[BurstCompile]
public struct BlockWriter
{
    public int3 chunkCoord;
    public int chunkSize;
    public int indexSize;
    public NativeArray<byte> blockIds;
    public NativeList<PendingBlockWrite> pendingWrites;

    public void ApplyBlock(int x, int y, int z, BlockType block)
    {
        byte blockId = (byte)block;
        int3 localPos = new int3(x, y, z);
        int3 worldPos = chunkCoord * chunkSize + localPos;

        int3 targetChunk = new int3(
            (int)math.floor(worldPos.x / (float)chunkSize),
            (int)math.floor(worldPos.y / (float)chunkSize),
            (int)math.floor(worldPos.z / (float)chunkSize)
        );

        int3 targetLocal = new int3(
            worldPos.x - targetChunk.x * chunkSize,
            worldPos.y - targetChunk.y * chunkSize,
            worldPos.z - targetChunk.z * chunkSize
        );

        if (targetLocal.Equals(chunkCoord) && x != 0 && y != 0 && z != 0)
        {
            int index = x + y * indexSize + z * indexSize * indexSize;
            blockIds[index] = blockId;
        }
        else
        {
            pendingWrites.Add(new PendingBlockWrite
            {
                targetChunk = targetChunk,
                localPos = targetLocal,
                blockId = blockId,
                mode = PendingWriteMode.ReplaceAir,
                isMirror = false,
            });
        }
    }
}