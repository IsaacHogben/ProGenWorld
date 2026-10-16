using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Runs streams over one chunk's blocks: streams arriving from neighbouring chunks first, then
/// (when spawnSources is set) the chunk's own new streams.
///
/// A stream looks ahead: it tries points on a ring around it (searchRadius out, then twice that)
/// and picks one that is lower or level, reachable by a straight cut no deeper than maxCut, that
/// drops the most and follows the stream's meandering heading best. It then cuts a channel along
/// that line (water one block below the banks, wider as the stream grows, waterfalls where the
/// ground drops away) and looks again from there. With nowhere to go it floods the low ground
/// around it (or digs a round pond), or just ends when no pond fits. Where it, or its flood,
/// leaves the chunk, a StreamHead goes to the next chunk.
/// </summary>
[BurstCompile]
public struct StreamJob : IJob
{
    public int3 chunkCoord;
    public int chunkSize;
    public int indexSize;    // chunkSize + 1
    public int waterLevel;
    public uint seed;
    public byte spawnSources;

    public NativeArray<byte> blockIds;
    public NativeList<PendingBlockWrite> pendingWrites;

    [ReadOnly] public NativeArray<BiomeHint> biomeHints;
    public int biomeResolution;
    [ReadOnly] public NativeArray<StreamBiomeData> biomes;
    [ReadOnly] public NativeArray<StreamHead> incoming;
    public NativeList<StreamHead> outgoing;
    public NativeArray<byte> visited;   // chunkSize^2, kept per chunk: 1 = this stream's channel or pond, 2 = water it widened into, 3 = earlier streams' water
    public NativeList<StreamEnd> ends;  // where and why streams ended here

    const int Blocked = int.MaxValue;   // ground above the search window (a wall)
    const int Below = int.MinValue;     // ground below this chunk
    const int Directions = 16;          // ring points tried per look

    static readonly int2[] Dirs = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

    struct Run
    {
        public uint id;
        public int x, z;        // local column
        public int level;       // local y of the water
        public int length;
        public int budget;
        public int2 dir;        // last single step (for widening)
        public float2 heading;  // last run's direction (no turning back)
        public float drift;     // slowly wandering direction (radians)
        public StreamBiomeData s;
        public Random rng;
    }

    enum Outcome : byte { Reaches, Joins, Falls, Leaves, Fails }

    public void Execute()
    {
        var cells = new NativeList<int2>(64, Allocator.Temp);
        var handled = new NativeArray<byte>(incoming.Length, Allocator.Temp);
        for (int i = 0; i < incoming.Length; i++)
        {
            if (handled[i] != 0 || incoming[i].fill == 2)
                continue;
            Settle();
            if (incoming[i].fill == 1)
                FillSeeds(i, handled);
            else
                Continue(incoming[i], cells);
        }

        Settle();
        if (spawnSources != 0)
            Sources(cells);

        // Lowerings last, so they also reach ponds this chunk's own streams just made again
        for (int i = 0; i < incoming.Length; i++)
            if (incoming[i].fill == 2)
                Lower(incoming[i]);
        handled.Dispose();
        cells.Dispose();
    }

    // Water left by earlier streams (and runs) becomes settled water (3): the next stream can
    // join it, where its own channel (1) would stop it
    void Settle()
    {
        for (int i = 0; i < visited.Length; i++)
            if (visited[i] != 0)
                visited[i] = 3;
    }

    // ------------------------------------------------------------------------------------
    // Blocks
    // ------------------------------------------------------------------------------------

    int Index(int x, int y, int z) => x + y * indexSize + z * indexSize * indexSize;

    bool Solid(int x, int y, int z)
    {
        byte b = blockIds[Index(x, y, z)];
        return b != (byte)BlockType.Air && b != (byte)BlockType.Water;
    }

    bool Water(int x, int y, int z) => blockIds[Index(x, y, z)] == (byte)BlockType.Water;

    bool Own(int x, int z) => (uint)x < (uint)chunkSize && (uint)z < (uint)chunkSize;

    /// <summary>
    /// Top of the ground in a column (x, z in 0..chunkSize, the last being the overlap with the
    /// next chunk), searched down from maxUp above level. Blocked if the ground is higher,
    /// Below if the column is open down through the whole chunk.
    /// </summary>
    int Ground(int x, int z, int level, int maxUp, out bool water)
    {
        water = false;
        int top = level + maxUp + 1;
        if (top > chunkSize)
            top = chunkSize;
        if (top < 0)
            return Below;
        if (Solid(x, top, z))
            return Blocked;
        for (int y = top; y >= 0; y--)
        {
            if (Water(x, y, z))
            {
                water = true;
                return y;
            }
            if (Solid(x, y, z))
                return y;
        }
        return Below;
    }

    void End(in Run run, StreamEndReason reason)
    {
        ends.Add(new StreamEnd
        {
            id = run.id,
            position = chunkCoord * chunkSize + new int3(run.x, run.level, run.z),
            length = run.length,
            reason = reason
        });
    }

    // Stream water, with the biome's sediment under it and its subsurface block beside it
    void SetWater(int x, int y, int z, PendingWriteMode mode, in StreamBiomeData s)
    {
        Set(x, y, z, BlockType.Water, mode);
        Set(x, y - 1, z, (BlockType)s.sedimentBlock, PendingWriteMode.ReplaceSolid);
        for (int k = 0; k < 4; k++)
            Set(x + Dirs[k].x, y, z + Dirs[k].y, (BlockType)s.subsurfaceBlock, PendingWriteMode.ReplaceSolid);
    }

    void Set(int x, int y, int z, BlockType block, PendingWriteMode mode)
    {
        var writer = new BlockWriter
        {
            chunkCoord = chunkCoord,
            chunkSize = chunkSize,
            indexSize = indexSize,
            blockIds = blockIds,
            pendingWrites = pendingWrites
        };
        writer.ApplyBlock(x, y, z, block, mode);
    }

    // ------------------------------------------------------------------------------------
    // Starting and continuing
    // ------------------------------------------------------------------------------------

    void Sources(NativeList<int2> cells)
    {
        int c = chunkSize / 2;
        var biome = SampleBiome(c, c);
        if (biome.primaryBiome >= biomes.Length)
            return;
        var s = biomes[biome.primaryBiome];
        if (s.enabled == 0)
            return;

        uint hash = math.hash(new int4(chunkCoord, (int)seed)) | 1u;
        var rng = new Random(hash);
        if (rng.NextFloat() >= s.chance)
            return;

        int x = rng.NextInt(2, chunkSize - 2), z = rng.NextInt(2, chunkSize - 2);

        // The ground must be in this chunk (the chunk holding the surface starts the stream)
        int ground = -1;
        for (int y = chunkSize - 1; y >= 0; y--)
        {
            if (Water(x, y, z))
                return;
            if (Solid(x, y, z))
            {
                ground = y;
                break;
            }
        }
        if (ground < 0 || Solid(x, ground + 1, z))
            return;
        if (chunkCoord.y * chunkSize + ground < waterLevel + s.minHeightAboveWater)
            return;

        var run = new Run
        {
            id = hash,
            x = x,
            z = z,
            level = ground,
            budget = s.carveBudget,
            s = s,
            rng = rng
        };
        run.drift = rng.NextFloat(0f, 2f * math.PI);
        Enter(ref run, x, z, ground);
        Walk(ref run, cells);
    }

    void Continue(StreamHead head, NativeList<int2> cells)
    {
        int lx = head.column.x - chunkCoord.x * chunkSize;
        int lz = head.column.y - chunkCoord.z * chunkSize;
        if (!Own(lx, lz))
            return;

        var run = new Run
        {
            id = head.id,
            x = lx,
            z = lz,
            level = head.level - chunkCoord.y * chunkSize,
            length = head.length,
            budget = head.budget,
            dir = head.dir,
            s = head.settings,
            rng = new Random(head.id ^ (uint)(head.length * 0x9E3779B9) | 1u)
        };
        if (run.length >= run.s.maxLength)
        {
            End(run, StreamEndReason.MaxLength);
            return;
        }
        if (visited[lx + lz * chunkSize] == 1)
        {
            End(run, StreamEndReason.EntryVisited);
            return;
        }
        run.heading = math.normalizesafe((float2)head.dir);
        run.drift = math.atan2(head.dir.y, head.dir.x);

        int g;
        bool water;
        if (run.level >= chunkSize)
        {
            // Falling in from the chunk above: this chunk's top layer is shared with that chunk
            // and holds the stream's own falling water, so the search starts below it
            g = Below;
            water = false;
            for (int y = chunkSize - 1; y >= 0; y--)
            {
                if (Water(lx, y, lz)) { water = true; g = y; break; }
                if (Solid(lx, y, lz)) { g = y; break; }
            }
        }
        else
            g = Ground(lx, lz, run.level, run.s.maxCut + 1, out water);
        if (water)
        {
            End(run, StreamEndReason.EntryWater);
            return;
        }
        if (g == Blocked)
        {
            End(run, StreamEndReason.EntryTooHigh);
            return;
        }
        if (g == Below)
        {
            FallThrough(ref run, lx, lz);
            return;
        }
        int rise = g - run.s.sink - run.level;
        if (rise > 0 && (rise > run.s.maxCut || rise > run.budget))
        {
            End(run, StreamEndReason.EntryTooHigh);
            return;
        }

        Enter(ref run, lx, lz, g);
        Walk(ref run, cells);
    }

    // ------------------------------------------------------------------------------------
    // Cutting the channel
    // ------------------------------------------------------------------------------------

    // Moves the stream into column (x, z) whose ground is g. The water replaces the top ground
    // block (or sits one below the banks with Cut Below Banks): higher ground is cut down to the
    // stream's water; where the ground drops by more than a step, the air between the old and new
    // water fills (a waterfall)
    void Enter(ref Run run, int x, int z, int g)
    {
        int target = g - run.s.sink;
        if (target > run.level)
        {
            for (int y = run.level + 1; y <= g; y++)
                Set(x, y, z, BlockType.Air, PendingWriteMode.Replace);
            run.budget -= target - run.level;
        }
        else
        {
            int old = run.level;
            run.level = target;
            if (target < g)
                Set(x, g, z, BlockType.Air, PendingWriteMode.Replace);
            if (old - target >= 2)
                for (int y = target + 1, top = math.min(old, chunkSize - 1); y <= top; y++)
                    SetWater(x, y, z, PendingWriteMode.Replace, run.s);
        }

        SetWater(x, run.level, z, PendingWriteMode.Replace, run.s);
        visited[x + z * chunkSize] = 1;
        run.x = x;
        run.z = z;
        run.length++;
        Widen(ref run, x, z);
    }

    // A wider stream: the columns beside the centre (across its direction) are cut to its water,
    // where their ground is at most a few blocks higher and not below the water
    void Widen(ref Run run, int x, int z)
    {
        int width = math.min(run.s.maxWidth, 1 + run.length / run.s.widenEvery);
        if (width <= 1)
            return;
        int2 side = new int2(-run.dir.y, run.dir.x);
        if (side.Equals(int2.zero))
            side = new int2(1, 0);
        for (int k = 1; k < width; k++)
        {
            int offset = (k + 1) / 2 * ((k & 1) == 1 ? 1 : -1);   // +1, -1, +2, -2 ...
            int sx = x + side.x * offset, sz = z + side.y * offset;
            if (!Own(sx, sz))
                continue;
            int gs = Ground(sx, sz, run.level, 3, out bool water);
            if (water || gs == Blocked || gs == Below || gs - run.s.sink < run.level)
                continue;
            for (int y = run.level + 1; y <= gs; y++)
                Set(sx, y, sz, BlockType.Air, PendingWriteMode.Replace);
            SetWater(sx, run.level, sz, PendingWriteMode.Replace, run.s);
            if (visited[sx + sz * chunkSize] == 0)
                visited[sx + sz * chunkSize] = 2;
        }
    }

    // The ground is below this chunk: water fills this column down through the chunk (a
    // waterfall) and the stream carries on in the chunk below
    void FallThrough(ref Run run, int x, int z)
    {
        if (Own(x, z))
            for (int y = math.min(run.level, chunkSize - 1); y >= 0; y--)
                SetWater(x, y, z, PendingWriteMode.ReplaceAir, run.s);
        HandOff(ref run, x, z, -1);
    }

    void HandOff(ref Run run, int x, int z, int dy)
    {
        int3 world = chunkCoord * chunkSize + new int3(x, run.level, z);
        outgoing.Add(new StreamHead
        {
            id = run.id,
            column = world.xz,
            level = world.y,
            length = run.length,
            budget = run.budget,
            dir = run.dir,
            chunk = new int3(FloorDiv(world.x, chunkSize), chunkCoord.y + dy, FloorDiv(world.z, chunkSize)),
            settings = run.s
        });
    }

    static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    // ------------------------------------------------------------------------------------
    // Looking ahead
    // ------------------------------------------------------------------------------------

    // The 4-connected columns of a straight line from (x0, z0) to (x1, z1), start excluded
    static void Line(int x0, int z0, int x1, int z1, NativeList<int2> cells)
    {
        cells.Clear();
        int2 d = new int2(x1 - x0, z1 - z0);
        int sx = d.x > 0 ? 1 : -1, sz = d.y > 0 ? 1 : -1;
        float2 fd = d;
        float tDeltaX = d.x != 0 ? 1f / math.abs(fd.x) : float.MaxValue;
        float tDeltaZ = d.y != 0 ? 1f / math.abs(fd.y) : float.MaxValue;
        float tMaxX = d.x != 0 ? 0.5f * tDeltaX : float.MaxValue;
        float tMaxZ = d.y != 0 ? 0.5f * tDeltaZ : float.MaxValue;
        int x = x0, z = z0;
        for (int n = math.abs(d.x) + math.abs(d.y); n > 0; n--)
        {
            if (tMaxX < tMaxZ) { x += sx; tMaxX += tDeltaX; }
            else { z += sz; tMaxZ += tDeltaZ; }
            cells.Add(new int2(x, z));
        }
    }

    // Follows a line from the stream's column: the water keeps to the lowest ground so far, and
    // ground above it must be cut (no deeper than maxCut, within the budget). Stops early where it
    // meets water, falls out of the chunk's bottom, or reaches a column outside this chunk.
    Outcome Trace(in Run run, NativeList<int2> cells, out int endLevel, out int cut, out int steps)
    {
        int level = run.level;
        cut = 0;
        steps = 0;
        endLevel = level;
        for (int i = 0; i < cells.Length; i++)
        {
            int2 c = cells[i];
            steps = i + 1;
            if (!Own(c.x, c.y))
                return Outcome.Leaves;   // the next chunk decides where it goes from there
            byte v = visited[c.x + c.y * chunkSize];
            if (v == 1)
                return Outcome.Fails;      // its own channel
            if (v == 2)
                continue;                  // its own widened water beside the channel: passes through
            int g = Ground(c.x, c.y, level, run.s.maxCut + 1, out bool water);
            if (water)
                return g <= level + 1 ? Outcome.Joins : Outcome.Fails;
            if (g == Blocked)
                return Outcome.Fails;
            if (g == Below)
                return Outcome.Falls;
            int need = g - run.s.sink - level;
            if (need > 0)
            {
                if (need > run.s.maxCut)
                    return Outcome.Fails;
                cut += need;
                if (cut > run.budget)
                    return Outcome.Fails;
            }
            else
                level = g - run.s.sink;
            endLevel = level;
        }
        return Outcome.Reaches;
    }

    void Walk(ref Run run, NativeList<int2> cells)
    {
        for (int guard = 0; guard < run.s.maxLength; guard++)
        {
            if (run.length >= run.s.maxLength)
            {
                End(run, StreamEndReason.MaxLength);
                return;
            }

            // Meandering heading: a slow drift plus a regular side-to-side swing
            run.drift += (run.rng.NextFloat() * 2f - 1f) * 0.3f * run.s.windiness;
            float phase = (run.id & 0xFFFF) * (2f * math.PI / 65536f);
            float swing = 1.1f * run.s.windiness * math.sin(run.length * (2f * math.PI / 40f) + phase);
            float2 heading = new float2(math.cos(run.drift + swing), math.sin(run.drift + swing));

            int2 best = default;
            float bestScore = float.MaxValue;
            Outcome bestOutcome = Outcome.Fails;
            int bestEnd = run.level;
            float2 bestDir = default;

            for (int pass = 1; pass <= 2 && bestOutcome == Outcome.Fails; pass++)
            {
                int radius = run.s.searchRadius * pass;
                float turn = run.rng.NextFloat() * (2f * math.PI / Directions);
                for (int k = 0; k < Directions; k++)
                {
                    float a = turn + k * (2f * math.PI / Directions);
                    float2 dir = new float2(math.cos(a), math.sin(a));
                    if (!run.heading.Equals(float2.zero) && math.dot(dir, run.heading) < -0.2f)
                        continue;   // no turning back
                    int tx = run.x + (int)math.round(dir.x * radius);
                    int tz = run.z + (int)math.round(dir.y * radius);
                    Line(run.x, run.z, tx, tz, cells);
                    var outcome = Trace(run, cells, out int end, out int cut, out int steps);
                    if (outcome == Outcome.Fails)
                        continue;

                    // Lower is better, then following the heading, then less cutting; meeting
                    // water and leaving the chunk are fine places to go
                    float drop = run.level - end;
                    float score = -drop * (1f - 0.6f * run.s.windiness) / radius * 8f
                                  - 1.5f * run.s.windiness * math.dot(dir, heading)
                                  + 0.15f * cut + run.rng.NextFloat() * 0.05f;
                    if (outcome == Outcome.Joins || outcome == Outcome.Falls)
                        score -= 2f;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = new int2(tx, tz);
                        bestOutcome = outcome;
                        bestEnd = end;
                        bestDir = dir;
                    }
                }
            }

            if (bestOutcome == Outcome.Fails)
            {
                var reason = StreamEndReason.NoPath;
                if (run.s.pondStyle == (byte)PondStyle.Fill && Fill(ref run))
                    reason = StreamEndReason.Filled;
                else if (Pond(ref run, cells))
                    reason = StreamEndReason.Pond;
                End(run, reason);
                return;
            }

            // Cut the channel along the chosen line
            run.heading = bestDir;
            Line(run.x, run.z, best.x, best.y, cells);
            for (int i = 0; i < cells.Length; i++)
            {
                int2 c = cells[i];
                run.dir = c - new int2(run.x, run.z);
                if (!Own(c.x, c.y))
                {
                    HandOff(ref run, c.x, c.y, 0);
                    return;
                }
                if (visited[c.x + c.y * chunkSize] == 2)
                {
                    // Its own widened water: already cut, just move along it
                    visited[c.x + c.y * chunkSize] = 1;
                    run.x = c.x;
                    run.z = c.y;
                    run.length++;
                    continue;
                }
                int g = Ground(c.x, c.y, run.level, run.s.maxCut + 1, out bool water);
                if (water)
                {
                    End(run, StreamEndReason.JoinedWater);
                    return;
                }
                if (g == Below)
                {
                    FallThrough(ref run, c.x, c.y);
                    return;
                }
                if (g == Blocked || g - run.s.sink - run.level > run.s.maxCut)
                {
                    End(run, StreamEndReason.CutTooDeep);
                    return;
                }
                Enter(ref run, c.x, c.y, g);
                if (run.length >= run.s.maxLength)
                {
                    End(run, StreamEndReason.MaxLength);
                    return;
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------
    // Ponds
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Floods the low ground around the stream's end. The water rises a block at a time from the
    /// stream's level (up to fillHeight) while it holds no more than the stream's volume, which
    /// grows with the stream's length from minFillVolume to maxFillVolume, and stops below a level
    /// where it would spill (fall away, or meet lower water). It fails, leaving a dug pond to try,
    /// when it cannot hold at least minFillVolume. Where the flood reaches the chunk's edge, fill
    /// seeds carry it on into the next chunk, which lowers the pond if its side cannot hold it.
    /// </summary>
    bool Fill(ref Run run)
    {
        float t = math.saturate(run.length / (float)math.max(1, run.s.maxLength));
        int target = (int)math.lerp(run.s.minFillVolume, run.s.maxFillVolume, t);

        int n = chunkSize * chunkSize;
        var state = new NativeArray<byte>(n, Allocator.Temp);
        var ground = new NativeArray<int>(n, Allocator.Temp);
        var queue = new NativeList<int>(64, Allocator.Temp);
        var crossings = new NativeList<int4>(16, Allocator.Temp);

        // The highest level that holds, preferring one that stays inside this chunk (past its
        // edge the ground is unseen, and the next chunk may have to lower the pond)
        int start = run.x + run.z * chunkSize;
        int best = -1, inside = -1;
        for (int level = run.level + 1, top = run.level + run.s.fillHeight; level <= top; level++)
        {
            Begin(state, queue, crossings);
            state[start] = 1;
            ground[start] = run.level;   // the stream's own water
            queue.Add(start);
            if (!Flood(level, target, state, ground, queue, crossings, out int volume))
                break;
            if (volume < run.s.minFillVolume)
                continue;
            best = level;
            if (crossings.Length == 0)
                inside = level;
        }
        if (inside >= 0)
            best = inside;

        bool done = best >= 0;
        if (done)
        {
            Begin(state, queue, crossings);
            state[start] = 1;
            ground[start] = run.level;
            queue.Add(start);
            Flood(best, target, state, ground, queue, crossings, out int volume);
            var pond = new StreamHead
            {
                id = run.id,
                origin = chunkCoord.xz * chunkSize + new int2(run.x, run.z),
                floor = chunkCoord.y * chunkSize + run.level,
                budget = target - volume,
                settings = run.s
            };
            Flooded(pond, best, ground, queue, crossings);
        }

        state.Dispose();
        ground.Dispose();
        queue.Dispose();
        crossings.Dispose();
        return done;
    }

    /// <summary>
    /// A pond's flood arriving from a neighbouring chunk (all its seeds here at once): its water
    /// stands at the seeds' level there. This chunk floods its low ground from the seed columns at
    /// the highest level, from there down, that its side holds (no spill, no more than the
    /// stream's water left), and if that is lower, sends a lowering back to the pond.
    /// </summary>
    void FillSeeds(int first, NativeArray<byte> handled)
    {
        var h0 = incoming[first];
        int level = h0.level - chunkCoord.y * chunkSize;
        int floor = math.max(h0.floor - chunkCoord.y * chunkSize, -1);

        int n = chunkSize * chunkSize;
        var state = new NativeArray<byte>(n, Allocator.Temp);
        var ground = new NativeArray<int>(n, Allocator.Temp);
        var queue = new NativeList<int>(64, Allocator.Temp);
        var crossings = new NativeList<int4>(16, Allocator.Temp);
        var starts = new NativeList<int2>(16, Allocator.Temp);   // column, its ground (Below: spills there)
        var seeds = new NativeList<int>(16, Allocator.Temp);

        for (int j = first; j < incoming.Length; j++)
        {
            var h = incoming[j];
            if (handled[j] != 0 || h.fill != 1 || h.id != h0.id || h.level != h0.level)
                continue;
            handled[j] = 1;
            int x = h.column.x - chunkCoord.x * chunkSize, z = h.column.y - chunkCoord.z * chunkSize;
            if (!Own(x, z))
                continue;
            seeds.Add(j);
            int c = x + z * chunkSize;
            int g = Ground(x, z, level, 0, out bool water);
            if (g == Blocked || g >= level)
                continue;   // shore
            if (water && visited[c] == 0)
                g = Below;  // lower water that is not a stream's: the pond would spill into it
            starts.Add(new int2(c, g));
        }

        // The highest level this side holds
        int chosen = floor, volume = 0;
        for (int L = level; L > floor; L--)
        {
            Begin(state, queue, crossings);
            bool spills = false;
            for (int i = 0; i < starts.Length; i++)
            {
                int c = starts[i].x, g = starts[i].y;
                if (g >= L || state[c] != 0)
                    continue;
                if (g == Below)
                {
                    spills = true;
                    break;
                }
                state[c] = 1;
                ground[c] = g;
                queue.Add(c);
            }
            if (spills || !Flood(L, h0.budget, state, ground, queue, crossings, out volume))
                continue;
            chosen = L;
            break;
        }

        if (chosen > floor && queue.Length > 0)
        {
            var pond = h0;
            pond.budget = h0.budget - volume;
            Flooded(pond, chosen, ground, queue, crossings);
        }

        if (chosen < level)
        {
            // Lower the pond on the other side to match
            for (int i = 0; i < seeds.Length; i++)
            {
                var h = incoming[seeds[i]];
                int2 back = h.column - h.dir;
                var lower = h;
                lower.fill = 2;
                lower.column = back;
                lower.budget = h.level;   // the level it is lowered from
                lower.level = chunkCoord.y * chunkSize + chosen;
                lower.chunk = new int3(FloorDiv(back.x, chunkSize), chunkCoord.y, FloorDiv(back.y, chunkSize));
                outgoing.Add(lower);
            }
            ends.Add(new StreamEnd
            {
                id = h0.id,
                position = new int3(h0.column.x, h0.level, h0.column.y),
                length = level - chosen,
                reason = StreamEndReason.FillLowered
            });
        }

        state.Dispose();
        ground.Dispose();
        queue.Dispose();
        crossings.Dispose();
        starts.Dispose();
        seeds.Dispose();
    }

    /// <summary>
    /// A pond lowered by a neighbouring chunk: its water here (connected at the old level, from
    /// the given column) drains down to the new level, and the lowering passes on to the other
    /// chunks the pond reaches. Ground left dry gets the biome's surface block back.
    /// </summary>
    void Lower(StreamHead head)
    {
        int x = head.column.x - chunkCoord.x * chunkSize, z = head.column.y - chunkCoord.z * chunkSize;
        int from = head.budget - chunkCoord.y * chunkSize;
        int to = head.level - chunkCoord.y * chunkSize;
        if (!Own(x, z) || from < 0 || from >= chunkSize || to >= from || !Water(x, from, z))
            return;

        int n = chunkSize * chunkSize;
        var seen = new NativeArray<byte>(n, Allocator.Temp);
        var queue = new NativeList<int>(64, Allocator.Temp);
        int start = x + z * chunkSize;
        seen[start] = 1;
        queue.Add(start);
        for (int i = 0; i < queue.Length; i++)
        {
            int c = queue[i];
            int cx = c % chunkSize, cz = c / chunkSize;
            for (int y = from; y > to && y >= 0; y--)
                if (Water(cx, y, cz))
                    Set(cx, y, cz, BlockType.Air, PendingWriteMode.Replace);
            Regrass(cx, cz, from, to, head.settings);
            for (int k = 0; k < 4; k++)
            {
                int nx = cx + Dirs[k].x, nz = cz + Dirs[k].y;
                if (Own(nx, nz))
                    Regrass(nx, nz, from, to, head.settings);
                if (!Own(nx, nz))
                {
                    int2 world = chunkCoord.xz * chunkSize + new int2(nx, nz);
                    var next = head;
                    next.column = world;
                    next.dir = Dirs[k];
                    next.chunk = new int3(FloorDiv(world.x, chunkSize), chunkCoord.y, FloorDiv(world.y, chunkSize));
                    outgoing.Add(next);
                    continue;
                }
                int ni = nx + nz * chunkSize;
                if (seen[ni] != 0 || !Water(nx, from, nz))
                    continue;
                seen[ni] = 1;
                queue.Add(ni);
            }
        }
        seen.Dispose();
        queue.Dispose();
    }

    // The top block of a column, if it lies between the drained levels and is open to the air
    // now: sediment or subsurface left by the water becomes the surface block again
    void Regrass(int x, int z, int from, int to, in StreamBiomeData s)
    {
        for (int y = math.min(from, chunkSize - 1); y > to && y >= 0; y--)
        {
            byte b = blockIds[Index(x, y, z)];
            if (b == (byte)BlockType.Air)
                continue;
            if ((b == s.sedimentBlock || b == s.subsurfaceBlock) && blockIds[Index(x, y + 1, z)] == (byte)BlockType.Air)
                Set(x, y, z, (BlockType)s.surfaceBlock, PendingWriteMode.Replace);
            return;
        }
        // The top block may be just below the new level, now dry
        if (to >= 0 && to < chunkSize)
        {
            byte b = blockIds[Index(x, to, z)];
            if ((b == s.sedimentBlock || b == s.subsurfaceBlock) && blockIds[Index(x, to + 1, z)] == (byte)BlockType.Air)
                Set(x, to, z, (BlockType)s.surfaceBlock, PendingWriteMode.Replace);
        }
    }

    void Begin(NativeArray<byte> state, NativeList<int> queue, NativeList<int4> crossings)
    {
        for (int i = 0; i < state.Length; i++)
            state[i] = 0;
        queue.Clear();
        crossings.Clear();
    }

    /// <summary>
    /// Floods from the queued columns at a water level over every connected column whose ground is
    /// below it (state 1, all left in queue; 2 = shore). Columns past the chunk's edge are listed
    /// in crossings (x, z and the step out). False if the water spills (falls away, or meets lower
    /// water that is not a stream's) or holds more than maxVolume.
    /// </summary>
    bool Flood(int level, int maxVolume, NativeArray<byte> state, NativeArray<int> ground,
        NativeList<int> queue, NativeList<int4> crossings, out int volume)
    {
        volume = 0;
        for (int i = 0; i < queue.Length; i++)
            volume += level - ground[queue[i]];
        if (volume > maxVolume)
            return false;

        for (int head = 0; head < queue.Length; head++)
        {
            int c = queue[head];
            int cx = c % chunkSize, cz = c / chunkSize;
            for (int k = 0; k < 4; k++)
            {
                int nx = cx + Dirs[k].x, nz = cz + Dirs[k].y;
                if (!Own(nx, nz))
                {
                    crossings.Add(new int4(nx, nz, Dirs[k].x, Dirs[k].y));   // the next chunk floods on from there
                    continue;
                }
                int ni = nx + nz * chunkSize;
                if (state[ni] != 0)
                    continue;
                int g = Ground(nx, nz, level, 0, out bool water);
                bool own = visited[ni] != 0;   // a stream's channel or pond here: fills like low ground
                if (g == Below || (water && !own && g < level))
                    return false;   // falls away, or meets lower water
                if (g == Blocked || g >= level || (water && !own))
                {
                    state[ni] = 2;   // shore
                    continue;
                }
                if (volume + level - g > maxVolume)
                    return false;   // more water than the stream brings
                state[ni] = 1;
                ground[ni] = g;
                volume += level - g;
                queue.Add(ni);
            }
        }
        return true;
    }

    // Writes a flood's water and sends fill seeds on across the chunk's edges (pond: the pond's
    // id, origin, floor, water left and settings)
    void Flooded(in StreamHead pond, int level, NativeArray<int> ground, NativeList<int> queue, NativeList<int4> crossings)
    {
        for (int i = 0; i < queue.Length; i++)
        {
            int c = queue[i];
            int cx = c % chunkSize, cz = c / chunkSize;
            for (int y = ground[c] + 1; y <= level; y++)
                SetWater(cx, y, cz, PendingWriteMode.ReplaceAir, pond.settings);
            visited[c] = 1;
        }
        for (int i = 0; i < crossings.Length; i++)
        {
            int3 world = chunkCoord * chunkSize + new int3(crossings[i].x, level, crossings[i].y);
            outgoing.Add(new StreamHead
            {
                id = pond.id,
                fill = 1,
                origin = pond.origin,
                floor = pond.floor,
                column = world.xz,
                dir = crossings[i].zw,
                level = world.y,
                budget = math.max(pond.budget, 0),
                chunk = new int3(FloorDiv(world.x, chunkSize), chunkCoord.y, FloorDiv(world.z, chunkSize)),
                settings = pond.settings
            });
        }
    }

    // Wavy round outline: distance relative to the pond's radius
    static float PondDistance(float2 d, float phase)
    {
        float a = math.atan2(d.y, d.x);
        float wave = 1f + 0.15f * math.sin(3f * a + phase) + 0.07f * math.sin(5f * a - phase);
        return math.length(d) / wave;
    }

    /// <summary>
    /// Ends the stream in a round pond (pondRadius plus a little, so at least about 10 blocks
    /// across) dug as a shallow bowl, its water no higher than the ground just around it. When a
    /// pond that size does not fit (the chunk's edge, the ground falling away) the stream just ends.
    /// </summary>
    bool Pond(ref Run run, NativeList<int2> cells)
    {
        int r = run.s.pondRadius + run.rng.NextInt(0, 3);
        float reach = r * 1.25f + 1.5f;   // the wavy outline plus a ring around it
        int span = (int)math.ceil(reach);

        // Near the chunk's edge the pond moves inwards (it can only see this chunk's ground) and
        // a level channel joins it to the stream's end
        int lo = span + 2, hi = chunkSize - 1 - span - 2;
        if (lo > hi)
            return false;
        int cx = math.clamp(run.x, lo, hi), cz = math.clamp(run.z, lo, hi);
        if (math.abs(cx - run.x) + math.abs(cz - run.z) > 2 * r)
            return false;
        if (cx != run.x || cz != run.z)
        {
            Line(run.x, run.z, cx, cz, cells);
            for (int i = 0; i < cells.Length; i++)
            {
                int2 c = cells[i];
                if (visited[c.x + c.y * chunkSize] != 0)
                    continue;
                int g = Ground(c.x, c.y, run.level, run.s.maxCut + 1, out bool water);
                if (water || g == Blocked || g == Below || g - run.s.sink < run.level - 1 || g - run.s.sink - run.level > run.s.maxCut)
                    return false;   // the way in is blocked or falls away: no pond
                run.dir = c - new int2(run.x, run.z);
                int keep = run.level;
                Enter(ref run, c.x, c.y, math.max(g, keep + run.s.sink));
                run.level = keep;
            }
        }
        float phase = run.rng.NextFloat(0f, 2f * math.PI);

        // Water level: the stream's, or lower if the ring around the pond is lower
        int level = run.level;
        for (int dz = -span; dz <= span; dz++)
        for (int dx = -span; dx <= span; dx++)
        {
            float d = PondDistance(new float2(dx, dz), phase);
            if (d <= r || d > r + 1.5f)
                continue;
            int g = Ground(cx + dx, cz + dz, run.level, 8, out bool water);
            if (g == Below)
                return false;
            if (g == Blocked || water)
                continue;
            level = math.min(level, g);
        }
        if (level < run.level - 4)
            return false;   // the ground falls away: no pond here

        for (int dz = -span; dz <= span; dz++)
        for (int dx = -span; dx <= span; dx++)
        {
            float d = PondDistance(new float2(dx, dz), phase);
            if (d > r)
                continue;
            int x = cx + dx, z = cz + dz;
            int g = Ground(x, z, level, 8, out bool water);
            if (water || g == Blocked || g == Below)
                continue;
            int bottom = level - (d < r * 0.6f ? 2 : 1);
            for (int y = level + 1; y <= g; y++)
                Set(x, y, z, BlockType.Air, PendingWriteMode.Replace);
            for (int y = math.max(bottom, 0); y <= level; y++)
                SetWater(x, y, z, PendingWriteMode.Replace, run.s);
            visited[x + z * chunkSize] = 1;
        }
        return true;
    }

    BiomeHint SampleBiome(int localX, int localZ)
    {
        int side = biomeResolution + 1;
        int x = math.clamp(localX * biomeResolution / chunkSize, 0, biomeResolution);
        int z = math.clamp(localZ * biomeResolution / chunkSize, 0, biomeResolution);
        return biomeHints[x + z * side];
    }
}
