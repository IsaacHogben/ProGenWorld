public struct BiomeHint
{
    public byte primary;
    public byte secondary;
    public byte blend;
}

public struct BiomeData
{
    public int resolution;
    public Unity.Collections.NativeArray<BiomeHint> grid;
}
