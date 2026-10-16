using System;
using Unity.Mathematics;
using UnityEngine;

public enum PondStyle : byte
{
    [Tooltip("Water rises over the low ground around the stream's end until it holds the stream's volume, held in by the ground.")]
    Fill,
    [Tooltip("A round bowl is dug at the stream's end.")]
    Dig
}

/// <summary>
/// Streams as decoration: a stream starts on high ground and runs downhill over the real blocks,
/// looking a few blocks ahead for lower or level ground and cutting a channel there through
/// anything in the way. With nowhere lower to go it ends in a pond: water flooding the low ground
/// around it, or a dug round bowl. At a chunk edge it hands its state
/// to the next chunk.
/// </summary>
[Serializable]
public class StreamSettings
{
    [Tooltip("Spawn streams in this biome.")]
    public bool enabled = false;

    [Tooltip("Chance per chunk (64 x 64 blocks) that a stream starts in it.")]
    [Range(0f, 1f)] public float chancePerChunk = 0.1f;

    [Tooltip("Streams only start this many blocks or more above the water level.")]
    [Min(0)] public int minHeightAboveWater = 20;

    [Tooltip("Longest a stream runs, in blocks. Also the furthest a stream can reach from where it started, so new chunks only change chunks this close to them.")]
    [Range(16, 2000)] public int maxLength = 300;

    [Tooltip("How far ahead (blocks) a stream looks for lower ground; if nothing is found it looks twice as far.")]
    [Range(3, 24)] public int searchRadius = 8;

    [Tooltip("Deepest cut in blocks a stream makes through higher ground on its way to lower ground.")]
    [Range(0, 8)] public int maxCut = 4;

    [Tooltip("Sink the water one block below the banks (a cut channel) instead of replacing the top ground block.")]
    public bool cutBelowBanks = false;

    [Tooltip("Total blocks of cutting (summed over columns) a stream may do before it can only go downhill.")]
    [Range(0, 200)] public int carveBudget = 40;

    [Tooltip("How a stream that finds nowhere lower to go ends: flooding the low ground around it, or digging a round pond.")]
    public PondStyle pondStyle = PondStyle.Fill;

    [Tooltip("Fill: highest the water rises above where the stream ended, in blocks.")]
    [Range(1, 16)] public int fillHeight = 6;

    [Tooltip("Fill: blocks of water a stream that just started brings. The water rises a block at a time while it holds no more than the stream's volume; if it cannot hold at least this, a pond is dug instead.")]
    [Range(10, 5000)] public int minFillVolume = 60;

    [Tooltip("Fill: blocks of water a stream of full length (Max Length) brings; streams in between bring in between.")]
    [Range(10, 20000)] public int maxFillVolume = 2000;

    [Tooltip("Dig: radius of the pond (plus up to 2). No pond when there is no room.")]
    [Range(3, 12)] public int pondRadius = 5;

    [Tooltip("How much a stream wanders: 0 = always the steepest way down, 1 = winds a lot across gentle ground (it never runs uphill).")]
    [Range(0f, 1f)] public float windiness = 0.6f;

    [Tooltip("Widest a stream grows, in blocks.")]
    [Range(1, 7)] public int maxWidth = 3;

    [Tooltip("Blocks a stream runs before it grows a block wider.")]
    [Range(8, 500)] public int widenEvery = 40;
}

/// <summary>Job safe copy of a biome's StreamSettings, indexed by biome ID.</summary>
public struct StreamBiomeData
{
    public byte enabled;
    public float chance;
    public int minHeightAboveWater;
    public int maxLength;
    public int searchRadius;
    public int maxCut;
    public int sink;   // 1 = water one block below the banks
    public int carveBudget;
    public int pondRadius;
    public byte pondStyle;
    public int fillHeight;
    public int minFillVolume;
    public int maxFillVolume;
    public float windiness;
    public int maxWidth;
    public int widenEvery;
    public byte sedimentBlock;     // under the water
    public byte subsurfaceBlock;   // beside the water
    public byte surfaceBlock;      // put back where a lowered pond leaves ground dry

    public static StreamBiomeData From(StreamSettings s, byte surfaceBlock, byte sedimentBlock, byte subsurfaceBlock) => new StreamBiomeData
    {
        surfaceBlock = surfaceBlock,
        sedimentBlock = sedimentBlock,
        subsurfaceBlock = subsurfaceBlock,
        enabled = (byte)(s != null && s.enabled ? 1 : 0),
        chance = s?.chancePerChunk ?? 0f,
        minHeightAboveWater = s?.minHeightAboveWater ?? 0,
        maxLength = s?.maxLength ?? 0,
        searchRadius = math.max(3, s?.searchRadius ?? 8),
        maxCut = s?.maxCut ?? 0,
        sink = s != null && s.cutBelowBanks ? 1 : 0,
        carveBudget = s?.carveBudget ?? 0,
        pondRadius = math.max(3, s?.pondRadius ?? 5),
        pondStyle = (byte)(s?.pondStyle ?? PondStyle.Fill),
        fillHeight = math.max(1, s?.fillHeight ?? 6),
        minFillVolume = math.max(1, s?.minFillVolume ?? 60),
        maxFillVolume = math.max(s?.minFillVolume ?? 60, s?.maxFillVolume ?? 2000),
        windiness = s?.windiness ?? 0f,
        maxWidth = s?.maxWidth ?? 1,
        widenEvery = math.max(1, s?.widenEvery ?? 40)
    };
}

public enum StreamEndReason : byte
{
    MaxLength,       // ran its full length
    JoinedWater,     // ran into water (another stream, a pond, a lake, the sea)
    Pond,            // nowhere lower to go: ended in a dug pond
    Filled,          // nowhere lower to go: flooded the low ground around its end
    NoPath,          // nowhere lower to go and no room for a pond
    EntryTooHigh,    // arrived from a neighbouring chunk on ground higher than it can cut
    EntryWater,      // arrived from a neighbouring chunk onto water
    EntryVisited,    // arrived on a column it already has water in
    CutTooDeep,      // the ground ahead rose too high while cutting its channel
    FillLowered      // a pond's flood into a neighbouring chunk had to lower the pond (logged only; length = blocks lowered)
}

/// <summary>Where and why a stream ended (logged with ChunkManager > Log Stream Ends).</summary>
public struct StreamEnd
{
    public uint id;
    public int3 position;   // world block
    public int length;
    public StreamEndReason reason;
}

/// <summary>
/// A stream crossing into another chunk: where it arrives and what it carries. Everything is in
/// world blocks, so the receiving chunk continues exactly where the sender stopped. A fill seed
/// instead carries a pond's flood across the edge: the receiving chunk floods its own low ground
/// from that column at the same water level.
/// </summary>
public struct StreamHead
{
    public uint id;          // the stream (hash of where it started)
    public byte fill;        // 1 = a fill seed: level is the pond's water, budget its water left;
                             // 2 = a lowering: the pond's water at budget drains down to level
    public int2 origin;      // fill: world x, z where the pond started
    public int floor;        // fill: world y of the stream's water where the pond started (it never drains below)
    public int2 column;      // world x, z of the column it continues at
    public int level;        // world y of its water
    public int length;       // blocks run so far
    public int budget;       // carve budget left
    public int2 dir;         // last step
    public int3 chunk;       // chunk it continues in
    public StreamBiomeData settings;   // of the biome it started in, all the way

    /// <summary>Same stream arriving at the same place again (e.g. resent by a regenerated chunk).</summary>
    public long Key => fill == 0
        ? ((long)id << 24) ^ (uint)length
        : ((long)id << 24) ^ ((long)fill << 61) ^ ((long)(uint)level << 32) ^ (uint)(column.x * 73856093 ^ column.y * 19349663);
}
