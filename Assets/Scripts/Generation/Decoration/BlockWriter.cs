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
        => ApplyBlock(x, y, z, block, PendingWriteMode.ReplaceAir);

    // x, y, z are relative to this chunk and may fall outside it.
    public void ApplyBlock(int x, int y, int z, BlockType block, PendingWriteMode mode)
    {
        byte blockId = (byte)block;
        int3 worldPos = chunkCoord * chunkSize + new int3(x, y, z);
        int3 targetChunk = FloorDiv(worldPos, chunkSize);
        int3 targetLocal = worldPos - targetChunk * chunkSize;

        // Blocks inside this chunk are written straight into its array. Those on local 0 also go
        // to WriteSystem, which mirrors them into the neighbouring chunk's overlap layer.
        if ((mode == PendingWriteMode.ReplaceAir || mode == PendingWriteMode.Replace || mode == PendingWriteMode.ReplaceSolid)
            && targetChunk.Equals(chunkCoord))
        {
            int index = targetLocal.x + targetLocal.y * indexSize + targetLocal.z * indexSize * indexSize;
            byte current = blockIds[index];
            bool write = mode == PendingWriteMode.Replace
                         || (mode == PendingWriteMode.ReplaceAir && current == (byte)BlockType.Air)
                         || (mode == PendingWriteMode.ReplaceSolid && current != (byte)BlockType.Air && current != (byte)BlockType.Water);
            if (write)
                blockIds[index] = blockId;
            if (targetLocal.x != 0 && targetLocal.y != 0 && targetLocal.z != 0)
                return;
            if (!write)
                return;   // nothing changed, nothing to mirror
            mode = PendingWriteMode.Replace;   // already decided here: WriteSystem just writes and mirrors it
        }

        // Drops inside this chunk can settle right here, since its blocks are available.
        if (mode == PendingWriteMode.DropToSurface && targetChunk.Equals(chunkCoord))
        {
            if (!TryFindSurface(blockIds, indexSize, chunkSize, ref targetLocal))
                return; // WriteSystem would search the same column and fail too
            if (targetLocal.x != 0 && targetLocal.y != 0 && targetLocal.z != 0)
            {
                int index = targetLocal.x + targetLocal.y * indexSize + targetLocal.z * indexSize * indexSize;
                blockIds[index] = blockId; // TryFindSurface only returns air positions
                return;
            }
            mode = PendingWriteMode.ReplaceAir; // on the shared face: let WriteSystem write and mirror it
        }

        pendingWrites.Add(new PendingBlockWrite
        {
            targetChunk = targetChunk,
            localPos = targetLocal,
            blockId = blockId,
            mode = mode,
            isMirror = false,
        });
    }

    // Moves p to the first air block resting on a non-air block in its column: downwards if p is in
    // air, upwards if it starts inside terrain. Searches only this chunk's own layers (y < chunkSize).
    // Shared with WriteSystem so both sides settle blocks identically.
    public static bool TryFindSurface(NativeArray<byte> blockIds, int indexSize, int chunkSize, ref int3 p)
    {
        const int maxSearch = 16;
        int column = p.x + p.z * indexSize * indexSize;
        int top = chunkSize - 1;
        if (p.y > top)
            return false;

        if (blockIds[column + p.y * indexSize] == 0)
        {
            for (int y = p.y, n = 0; y >= 1 && n < maxSearch; y--, n++)
            {
                if (blockIds[column + (y - 1) * indexSize] != 0)
                {
                    p.y = y;
                    return true;
                }
            }
            return false;
        }

        for (int y = p.y + 1, n = 0; y <= top && n < maxSearch; y++, n++)
        {
            if (blockIds[column + y * indexSize] == 0)
            {
                p.y = y;
                return true;
            }
        }
        return false;
    }

    static int3 FloorDiv(int3 a, int b)
    {
        int3 q = a / b;
        return q - math.select(int3.zero, new int3(1), (a % b != 0) & (a < 0));
    }
}
