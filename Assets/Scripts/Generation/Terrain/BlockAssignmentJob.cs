using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

public class BlockAssignmentJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float> density;

    [WriteOnly] public NativeArray<byte> blockIds;



    public void Execute(int i)
    {

    }
}
