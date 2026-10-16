// RiverNetwork.cs
// Rivers and lakes of the region generator. Drainage nodes sit on a jittered grid; each node
// drains to its steepest lower neighbour, a node with no lower neighbour is a basin (a lake when
// enough rain reaches it) and a node under the water level is open water. Every node gathers the
// rain of the nodes upstream of it within a fixed radius, and a river runs from a node to its
// downstream neighbour once that rainfall passes a threshold. All of this only depends on nodes
// near the one being computed, so rivers continue seamlessly across planning tiles.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>A straight piece of river: water surface and widths at both ends.</summary>
public struct RiverEdge
{
    public float2 a, b;
    public float waterA, waterB;
    public float halfWidth;
    public float depth;     // channel depth below the water at the centre line
    public float valley;    // width of the carved valley beyond the channel
    public float flatten;   // distance beyond the channel over which hills flatten into the floodplain
}

/// <summary>A lake around a basin node: flat water at level, bowl shaped bed.</summary>
public struct RiverLake
{
    public float2 centre;
    public float level;
    public float radius;
    public float depth;
    public float shore;     // width of the raised shore

    public float Flatten => shore * 2f;   // hills flatten towards the shore over this distance

    /// <summary>Distance outside the (wavy) shoreline; negative inside the lake.</summary>
    public float Outside(float2 p)
    {
        float2 d = p - centre;
        float len = math.length(d);
        float a = math.atan2(d.y, d.x);
        float phase = math.frac(centre.x * 0.0137f + centre.y * 0.0071f) * 6.2831853f;
        float wave = 1f + 0.18f * math.sin(3f * a + phase) + 0.08f * math.sin(7f * a - 2f * phase);
        return len - radius * wave;
    }
}

/// <summary>Rivers and lakes near an area, bucketed for quick lookup in column jobs.</summary>
public struct RiverQuery : IDisposable
{
    public float2 origin;
    public float bucketSize;
    public int2 buckets;
    public NativeArray<RiverEdge> edges;
    public NativeArray<RiverLake> lakes;
    public NativeArray<int2> bucketRanges;   // (start, count) into items
    public NativeArray<int> items;           // >= 0: edge index, < 0: lake index ~i

    public void Dispose()
    {
        if (edges.IsCreated) edges.Dispose();
        if (lakes.IsCreated) lakes.Dispose();
        if (bucketRanges.IsCreated) bucketRanges.Dispose();
        if (items.IsCreated) items.Dispose();
    }

    public JobHandle Dispose(JobHandle dependsOn)
    {
        var h = edges.Dispose(dependsOn);
        h = JobHandle.CombineDependencies(h, lakes.Dispose(dependsOn));
        h = JobHandle.CombineDependencies(h, bucketRanges.Dispose(dependsOn));
        return JobHandle.CombineDependencies(h, items.Dispose(dependsOn));
    }
}

/// <summary>Burst side: carves a column for the rivers and lakes around it.</summary>
public static class RiverMath
{
    /// <summary>
    /// How much of the hills remain at a point (1 = all, 0 = flat floodplain): rivers and lakes
    /// were planned on the ground without hills, so hills flatten out towards them.
    /// </summary>
    public static float HillFactor(float2 pos, in RiverQuery q)
    {
        if (q.items.Length == 0)
            return 1f;
        int2 b = math.clamp((int2)math.floor((pos - q.origin) / q.bucketSize), 0, q.buckets - 1);
        int2 range = q.bucketRanges[b.x + b.y * q.buckets.x];
        float factor = 1f;
        for (int k = range.x; k < range.x + range.y; k++)
        {
            int item = q.items[k];
            float x, width;
            if (item >= 0)
            {
                var e = q.edges[item];
                x = SegmentDistance(pos, e.a, e.b, out _) - e.halfWidth;
                width = e.flatten;
            }
            else
            {
                var l = q.lakes[~item];
                x = l.Outside(pos);
                width = l.Flatten;
            }
            factor = math.min(factor, math.smoothstep(0f, 1f, x / math.max(1f, width)));
        }
        return factor;
    }

    /// <summary>
    /// Lowers the height into river channels, valleys and lake beds (after raising banks and
    /// shores so water cannot spill), and returns the water surface and how close water is.
    /// </summary>
    public static void Apply(float2 pos, in RiverQuery q, float bankWidth, ref float height,
        ref float waterY, out float nearWater, out bool bank)
    {
        nearWater = 0f;
        bank = false;
        if (q.items.Length == 0)
            return;

        int2 b = math.clamp((int2)math.floor((pos - q.origin) / q.bucketSize), 0, q.buckets - 1);
        int2 range = q.bucketRanges[b.x + b.y * q.buckets.x];
        if (range.y == 0)
            return;

        // Pass 1: raise river banks and lake shores to just above their water
        for (int k = range.x; k < range.x + range.y; k++)
        {
            int item = q.items[k];
            if (item >= 0)
            {
                var e = q.edges[item];
                float d = SegmentDistance(pos, e.a, e.b, out float t);
                float x = d - e.halfWidth;
                if (x < 0f || x > e.valley)
                    continue;
                float wl = math.lerp(e.waterA, e.waterB, t);
                height = math.max(height, wl + 1f - math.max(0f, x - 2f) * 0.5f);
            }
            else
            {
                var l = q.lakes[~item];
                float x = l.Outside(pos);
                if (x < 0f || x > l.shore)
                    continue;
                float raised = math.max(height, l.level + 1f + x * 0.15f);
                height = math.lerp(raised, height, math.smoothstep(0.5f, 1f, x / l.shore));
            }
        }

        // Pass 2: carve channels, valleys and lake beds
        for (int k = range.x; k < range.x + range.y; k++)
        {
            int item = q.items[k];
            if (item >= 0)
            {
                var e = q.edges[item];
                float d = SegmentDistance(pos, e.a, e.b, out float t);
                float x = d - e.halfWidth;
                if (x > e.valley)
                    continue;
                float wl = math.lerp(e.waterA, e.waterB, t);
                if (x < 0f)
                {
                    float u = d / e.halfWidth;
                    height = math.min(height, wl - 0.5f - (e.depth - 0.5f) * math.sqrt(math.max(0f, 1f - u * u)));
                    waterY = math.max(waterY, wl);
                    nearWater = 1f;
                }
                else
                {
                    float f = math.smoothstep(0f, 1f, x / math.max(1f, e.valley));
                    float carved = math.min(height, wl + 1f + x * 0.2f);
                    height = math.lerp(carved, height, f);
                    nearWater = math.max(nearWater, 1f - f);
                    if (x < bankWidth && height <= wl + 2.5f)
                        bank = true;
                }
            }
            else
            {
                var l = q.lakes[~item];
                float x = l.Outside(pos);
                if (x > l.shore)
                    continue;
                if (x < 0f)
                {
                    float u = math.saturate(1f + x / l.radius);
                    height = math.min(height, l.level - 1f - (l.depth - 1f) * (1f - u * u));
                    waterY = math.max(waterY, l.level);
                    nearWater = 1f;
                }
                else
                {
                    nearWater = math.max(nearWater, 1f - x / l.shore);
                    if (x < bankWidth && height <= l.level + 2.5f)
                        bank = true;
                }
            }
        }
    }

    public static float SegmentDistance(float2 p, float2 a, float2 b, out float t)
    {
        float2 ab = b - a;
        float len2 = math.lengthsq(ab);
        t = len2 > 1e-6f ? math.saturate(math.dot(p - a, ab) / len2) : 0f;
        return math.distance(p, a + ab * t);
    }
}

/// <summary>Height and rainfall of every drainage node of a tile and its apron.</summary>
[BurstCompile]
struct RiverNodeJob : IJobParallelFor
{
    public int2 firstNode;   // global node index of grid entry 0
    public int side;
    public float spacing;
    public uint seed;
    public RegionParams rp;
    [ReadOnly] public NativeArray<BiomeShapeData> shapes;
    [ReadOnly] public NativeArray<SubBiomeData> subs;
    [ReadOnly] public NativeArray<ClimateGridCell> climateCells;
    [ReadOnly] public NativeArray<byte> cellBiomes;
    [ReadOnly] public NativeArray<float> rarity;

    [WriteOnly] public NativeArray<float2> positions;
    [WriteOnly] public NativeArray<float> heights;
    [WriteOnly] public NativeArray<float> rain;

    public void Execute(int index)
    {
        int2 node = firstNode + new int2(index % side, index / side);
        float2 pos = RiverNetwork.NodePosition(node, spacing, seed);
        var rw = RegionMath.Weights(pos, rp, shapes, climateCells, cellBiomes, rarity);
        // Drainage follows the ground without hills (hills flatten towards rivers afterwards)
        var s = RegionMath.Sample(pos, rw, rp, shapes, subs, 0f);
        positions[index] = pos;
        heights[index] = s.height;

        float humidity = math.saturate(0.5f + 0.5f * rp.climateContrast * rp.humidity.GetNoise(pos.x, pos.y));
        float biomeRain = rw.w0 * shapes[rw.b0].rainfall;
        if (rw.w1 > 0f) biomeRain += rw.w1 * shapes[rw.b1].rainfall;
        if (rw.w2 > 0f) biomeRain += rw.w2 * shapes[rw.b2].rainfall;
        rain[index] = math.lerp(0.3f, 1.5f, humidity) * biomeRain;
    }
}

/// <summary>Plans river tiles in the background and hands out the rivers near an area.</summary>
public sealed class RiverNetwork : IDisposable
{
    readonly RegionGenSettingsSO settings;
    readonly RegionSystem regions;
    readonly BiomeDataManager biomeDataManager;
    readonly float waterLevel;
    readonly float spacing;
    readonly int tileNodes;
    readonly int radius;     // catchment radius in nodes
    readonly int apron;      // extra nodes planned around a tile
    readonly uint seed;
    readonly FbmSimplexNoise2D meander;

    public readonly float MaxInfluence;   // furthest a tile's rivers and lakes reach past its nodes

    sealed class Tile
    {
        public RiverEdge[] edges;
        public RiverLake[] lakes;
    }

    sealed class Pending
    {
        public JobHandle handle;
        public NativeArray<float2> positions;
        public NativeArray<float> heights, rain;
        public Task<Tile> task;
    }

    readonly Dictionary<int2, Tile> tiles = new();
    readonly Dictionary<int2, Pending> pending = new();
    readonly List<int2> tmpKeys = new();

    public bool Enabled => settings.rivers;
    public float TileSize => tileNodes * spacing;

    public RiverNetwork(RegionGenSettingsSO settings, RegionSystem regions, BiomeDataManager biomeDataManager, int seed)
    {
        this.settings = settings;
        this.regions = regions;
        this.biomeDataManager = biomeDataManager;
        waterLevel = biomeDataManager.GetWaterLevel();
        spacing = settings.riverNodeSpacing;
        tileNodes = settings.riverTileNodes;
        radius = settings.riverCatchmentRadius;
        apron = radius + 4;
        this.seed = (uint)seed ^ 0x2545F491u;
        meander = FbmSimplexNoise2D.Create(seed + 7501, 1f / (spacing * 1.3f), 2);

        float maxWidth = settings.riverMaxWidth;
        float riverReach = maxWidth * 0.5f + 2f * (settings.valleyBase + settings.valleyPerWidth * maxWidth);
        float lakeReach = 1.3f * settings.lakeMaxRadius * spacing + 2f * settings.lakeShore;
        // Edges belong to their upstream node's tile and bend up to meander * spacing aside
        MaxInfluence = spacing * (1.5f + settings.riverMeander) + math.max(riverReach, lakeReach);
    }

    public static float2 NodePosition(int2 node, float spacing, uint seed)
    {
        uint h = RegionMath.Hash(node, seed);
        float2 r = new float2(h & 0xFFFF, h >> 16) * (1f / 65535f) - 0.5f;
        return ((float2)node + 0.5f + r * 0.6f) * spacing;
    }

    int2 TileOf(float2 world) => (int2)math.floor(world / TileSize);

    void TilesFor(float2 min, float2 max, List<int2> output)
    {
        output.Clear();
        int2 lo = TileOf(min - MaxInfluence), hi = TileOf(max + MaxInfluence);
        for (int z = lo.y; z <= hi.y; z++)
        for (int x = lo.x; x <= hi.x; x++)
            output.Add(new int2(x, z));
    }

    readonly List<int2> tmpTiles = new();

    /// <summary>True when every tile with rivers near the area is planned; starts planning the rest.</summary>
    public bool EnsureArea(float2 min, float2 max)
    {
        if (!Enabled)
            return true;
        TilesFor(min, max, tmpTiles);
        bool ready = true;
        foreach (var t in tmpTiles)
        {
            if (tiles.ContainsKey(t))
                continue;
            ready = false;
            if (!pending.ContainsKey(t))
                pending.Add(t, Start(t));
        }
        return ready;
    }

    /// <summary>Plans every tile near the area now (editor tools).</summary>
    public void CompleteArea(float2 min, float2 max)
    {
        if (!Enabled)
            return;
        EnsureArea(min, max);
        TilesFor(min, max, tmpTiles);
        foreach (var t in tmpTiles)
        {
            if (!pending.TryGetValue(t, out var p))
                continue;
            p.handle.Complete();
            if (p.task == null)
                p.task = StartTask(t, p);
            tiles[t] = p.task.Result;
            pending.Remove(t);
        }
    }

    public void Update()
    {
        if (pending.Count == 0)
            return;
        tmpKeys.Clear();
        tmpKeys.AddRange(pending.Keys);
        foreach (var t in tmpKeys)
        {
            var p = pending[t];
            if (p.task == null)
            {
                if (!p.handle.IsCompleted)
                    continue;
                p.handle.Complete();
                p.task = StartTask(t, p);
                continue;
            }
            if (!p.task.IsCompleted)
                continue;
            if (p.task.IsFaulted)
            {
                Debug.LogError($"[Rivers] Tile {t} failed: {p.task.Exception?.GetBaseException()}");
                tiles[t] = new Tile { edges = Array.Empty<RiverEdge>(), lakes = Array.Empty<RiverLake>() };
            }
            else
                tiles[t] = p.task.Result;
            pending.Remove(t);
        }
    }

    Pending Start(int2 tile)
    {
        int side = tileNodes + 2 * apron;
        int count = side * side;
        var p = new Pending
        {
            positions = new NativeArray<float2>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            heights = new NativeArray<float>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            rain = new NativeArray<float>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory)
        };
        p.handle = new RiverNodeJob
        {
            firstNode = tile * tileNodes - apron,
            side = side,
            spacing = spacing,
            seed = seed,
            rp = regions.Params,
            shapes = regions.Shapes,
            subs = regions.SubBiomes,
            climateCells = biomeDataManager.GetClimateGridCells(),
            cellBiomes = biomeDataManager.GetClimateCellBiomes(),
            rarity = biomeDataManager.GetBiomeRarityWeights(),
            positions = p.positions,
            heights = p.heights,
            rain = p.rain
        }.Schedule(count, 32);
        return p;
    }

    Task<Tile> StartTask(int2 tile, Pending p)
    {
        var pos = p.positions.ToArray();
        var h = p.heights.ToArray();
        var rain = p.rain.ToArray();
        p.positions.Dispose();
        p.heights.Dispose();
        p.rain.Dispose();
        return Task.Run(() => Plan(pos, h, rain));
    }

    // ------------------------------------------------------------------------------------
    // Worker thread planning
    // ------------------------------------------------------------------------------------

    const int Pit = -1, Sea = -2, Unknown = -3;

    static readonly int2[] Neighbours =
    {
        new(-1, -1), new(0, -1), new(1, -1),
        new(-1, 0),              new(1, 0),
        new(-1, 1),  new(0, 1),  new(1, 1)
    };

    Tile Plan(float2[] pos, float[] h, float[] rain)
    {
        int side = tileNodes + 2 * apron;
        int Index(int x, int z) => x + z * side;

        // Where each node drains
        var down = new int[pos.Length];
        for (int i = 0; i < down.Length; i++)
            down[i] = Unknown;
        for (int z = 1; z < side - 1; z++)
        for (int x = 1; x < side - 1; x++)
        {
            int i = Index(x, z);
            if (h[i] < waterLevel)
            {
                down[i] = Sea;
                continue;
            }
            int best = Pit;
            float bestSlope = 0f;
            foreach (var n in Neighbours)
            {
                int j = Index(x + n.x, z + n.y);
                float slope = (h[i] - h[j]) / math.distance(pos[i], pos[j]);
                if (slope > bestSlope)
                {
                    bestSlope = slope;
                    best = j;
                }
            }
            down[i] = best;
        }

        // Rain gathered from upstream within the catchment radius (only needs nodes near each
        // node, so a node gets the same value from every tile that plans it)
        var flow = new float[pos.Length];
        var stack = new Stack<int>();
        int lo = apron - 2, hi = apron + tileNodes + 2;
        for (int z = lo; z < hi; z++)
        for (int x = lo; x < hi; x++)
        {
            int start = Index(x, z);
            float total = 0f;
            stack.Clear();
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                total += rain[i];
                int ix = i % side, iz = i / side;
                foreach (var n in Neighbours)
                {
                    int jx = ix + n.x, jz = iz + n.y;
                    if (math.abs(jx - x) > radius || math.abs(jz - z) > radius)
                        continue;
                    int j = Index(jx, jz);
                    if (down[j] == i)
                        stack.Push(j);
                }
            }
            flow[start] = total;
        }

        float LakeLevel(int i)
        {
            int ix = i % side, iz = i / side;
            float level = float.MaxValue;
            foreach (var n in Neighbours)
                level = math.min(level, h[Index(ix + n.x, iz + n.y)]);
            return level;
        }

        bool IsLake(int i) => down[i] == Pit && flow[i] >= settings.lakeMinRainfall && LakeLevel(i) > waterLevel;

        // Water surface at a node (falls along every river: a node is above its downstream one)
        float Water(int i)
        {
            if (down[i] == Sea) return waterLevel;
            if (down[i] == Pit) return IsLake(i) ? LakeLevel(i) : h[i];
            int d = down[i];
            float below = down[d] == Sea ? waterLevel : down[d] == Pit ? (IsLake(d) ? LakeLevel(d) : h[d]) : h[d] - 1f;
            return math.max(h[i] - 1f, below);
        }

        float Width(float f) => math.clamp(settings.riverMinWidth + settings.riverWidthScale * math.sqrt(math.max(0f, f - settings.riverThreshold)),
            settings.riverMinWidth, settings.riverMaxWidth);

        // The largest river flowing into a node (smooths the bend through it)
        int MainUpstream(int i)
        {
            int ix = i % side, iz = i / side, best = -1;
            float bestFlow = settings.riverThreshold;
            foreach (var n in Neighbours)
            {
                int jx = ix + n.x, jz = iz + n.y;
                if (jx < lo || jz < lo || jx >= hi || jz >= hi)
                    continue;
                int j = Index(jx, jz);
                if (down[j] == i && flow[j] >= bestFlow)
                {
                    bestFlow = flow[j];
                    best = j;
                }
            }
            return best;
        }

        var edges = new List<RiverEdge>();
        var lakes = new List<RiverLake>();
        float LakeRadius(int i) => math.clamp(settings.lakeSize * math.sqrt(flow[i]) * spacing, spacing * 0.15f, settings.lakeMaxRadius * spacing);
        const int Pieces = 6;
        var pts = new float2[Pieces + 1];
        var wls = new float[Pieces + 1];

        for (int z = apron; z < apron + tileNodes; z++)
        for (int x = apron; x < apron + tileNodes; x++)
        {
            int a = Index(x, z);

            if (IsLake(a))
            {
                float r = LakeRadius(a);
                lakes.Add(new RiverLake
                {
                    centre = pos[a],
                    level = LakeLevel(a),
                    radius = r,
                    depth = math.clamp(r * 0.08f, 2f, 16f),
                    shore = settings.lakeShore
                });
            }

            int b = down[a];
            if (b < 0 || flow[a] < settings.riverThreshold)
                continue;

            float width = Width(flow[a]);
            float wa = Water(a), wb = Water(b);

            // Catmull-Rom through the upstream river, this edge and the downstream one
            int up = MainUpstream(a);
            float2 p0 = up >= 0 ? pos[up] : 2f * pos[a] - pos[b];
            float2 p3 = down[b] >= 0 ? pos[down[b]] : 2f * pos[b] - pos[a];
            float2 p1 = pos[a], p2 = pos[b];
            float2 dir = math.normalizesafe(p2 - p1);
            float2 perp = new float2(-dir.y, dir.x);
            for (int k = 0; k <= Pieces; k++)
            {
                float t = k / (float)Pieces;
                float t2 = t * t, t3 = t2 * t;
                float2 p = 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
                if (k > 0 && k < Pieces)
                {
                    // Wander aside, most in the middle of the edge, so neighbouring edges still meet
                    float m = meander.GetNoise(p.x, p.y) * settings.riverMeander * spacing * math.sin(t * math.PI);
                    p += perp * m;
                }
                pts[k] = p;
                wls[k] = math.lerp(wa, wb, t);
            }

            // Into a lake: reach the lake's level where the river meets the shore (shores wave out
            // to 1.26 radius), not at the lake's centre, or the river runs out over the lake on a
            // raised bank
            if (down[b] == Pit && IsLake(b))
            {
                float shore = LakeRadius(b) * 1.3f;
                int entry = Pieces;
                for (int k = 0; k <= Pieces; k++)
                    if (math.distance(pts[k], pos[b]) <= shore)
                    {
                        entry = k;
                        break;
                    }
                for (int k = 0; k <= Pieces; k++)
                    wls[k] = k >= entry ? wb : math.lerp(wa, wb, entry > 0 ? k / (float)entry : 1f);
            }

            float depth = 1f + width * settings.riverDepthPerWidth;
            float valley = settings.valleyBase + settings.valleyPerWidth * width;
            for (int k = 0; k < Pieces; k++)
                edges.Add(new RiverEdge
                {
                    a = pts[k],
                    b = pts[k + 1],
                    waterA = wls[k],
                    waterB = wls[k + 1],
                    halfWidth = width * 0.5f,
                    depth = depth,
                    valley = valley,
                    flatten = valley * 2f
                });
        }

        return new Tile { edges = edges.ToArray(), lakes = lakes.ToArray() };
    }

    // ------------------------------------------------------------------------------------
    // Queries
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Rivers and lakes that reach into the area (min / max are x, z world), bucketed. Empty when
    /// rivers are off or the area's tiles are not planned (check EnsureArea first).
    /// </summary>
    public RiverQuery Query(float2 min, float2 max, Allocator allocator)
    {
        var edges = new List<RiverEdge>();
        var lakes = new List<RiverLake>();
        if (Enabled)
        {
            TilesFor(min, max, tmpTiles);
            foreach (var t in tmpTiles)
            {
                if (!tiles.TryGetValue(t, out var tile))
                    continue;
                foreach (var e in tile.edges)
                {
                    float r = e.halfWidth + e.flatten;
                    if (math.all(math.min(e.a, e.b) - r <= max) && math.all(math.max(e.a, e.b) + r >= min))
                        edges.Add(e);
                }
                foreach (var l in tile.lakes)
                {
                    float r = l.radius * 1.3f + l.Flatten;   // shores wave out to 1.26 radius
                    if (math.all(l.centre - r <= max) && math.all(l.centre + r >= min))
                        lakes.Add(l);
                }
            }
        }

        float2 size = math.max(max - min, 1f);
        float bucketSize = math.max(32f, math.cmax(size) / 32f);
        int2 buckets = math.max(1, (int2)math.ceil(size / bucketSize));
        var lists = new List<int>[buckets.x * buckets.y];

        void Insert(int item, float2 lo, float2 hi)
        {
            int2 b0 = math.clamp((int2)math.floor((lo - min) / bucketSize), 0, buckets - 1);
            int2 b1 = math.clamp((int2)math.floor((hi - min) / bucketSize), 0, buckets - 1);
            if (math.any(hi < min) || math.any(lo > max))
                return;
            for (int z = b0.y; z <= b1.y; z++)
            for (int x = b0.x; x <= b1.x; x++)
                (lists[x + z * buckets.x] ??= new List<int>()).Add(item);
        }

        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            float r = e.halfWidth + e.flatten;
            Insert(i, math.min(e.a, e.b) - r, math.max(e.a, e.b) + r);
        }
        for (int i = 0; i < lakes.Count; i++)
        {
            var l = lakes[i];
            float r = l.radius * 1.3f + l.Flatten;   // shores wave out to 1.26 radius
            Insert(~i, l.centre - r, l.centre + r);
        }

        var ranges = new NativeArray<int2>(lists.Length, allocator);
        int total = 0;
        foreach (var list in lists)
            total += list?.Count ?? 0;
        var items = new NativeArray<int>(total, allocator);
        int at = 0;
        for (int i = 0; i < lists.Length; i++)
        {
            int n = lists[i]?.Count ?? 0;
            ranges[i] = new int2(at, n);
            for (int k = 0; k < n; k++)
                items[at + k] = lists[i][k];
            at += n;
        }

        var edgeArray = new NativeArray<RiverEdge>(edges.Count, allocator);
        for (int i = 0; i < edges.Count; i++) edgeArray[i] = edges[i];
        var lakeArray = new NativeArray<RiverLake>(lakes.Count, allocator);
        for (int i = 0; i < lakes.Count; i++) lakeArray[i] = lakes[i];

        return new RiverQuery
        {
            origin = min,
            bucketSize = bucketSize,
            buckets = buckets,
            edges = edgeArray,
            lakes = lakeArray,
            bucketRanges = ranges,
            items = items
        };
    }

    public void Dispose()
    {
        foreach (var p in pending.Values)
        {
            p.handle.Complete();
            if (p.positions.IsCreated) p.positions.Dispose();
            if (p.heights.IsCreated) p.heights.Dispose();
            if (p.rain.IsCreated) p.rain.Dispose();
        }
        pending.Clear();
        tiles.Clear();
    }
}
