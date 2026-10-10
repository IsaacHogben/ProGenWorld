/// <summary>
/// Runtime state of a loaded, non-empty chunk. Chunks no longer own GameObjects, meshes or
/// colliders: their voxels live on the GPU (VoxelWorldRenderer) and collision reads the block
/// arrays directly (VoxelPhysics).
/// </summary>
public class Chunk
{
    public LODLevel lod;
}
