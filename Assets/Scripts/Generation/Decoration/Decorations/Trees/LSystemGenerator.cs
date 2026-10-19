using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

// ============================================================================
// L-SYSTEM TREE GENERATOR  -  fully Burst-compatible
// ============================================================================
//
// SYMBOL REFERENCE  (optional "(value)" after a symbol overrides its default)
// ----------------
//   F(x)    move forward x steps, placing body blocks          (x default 1)
//   f(x)    move forward x steps without placing blocks         (x default 1)
//   + -     spin around the branch's own heading by +/-YawAngle (+(deg) for an explicit angle)
//   & ^     tilt the heading by +/-PitchAngle
//   \ /     tilt the heading sideways by +/-RollAngle
//   ~(deg)  tilt by CurveAngle with half the usual jitter - repeat to curve a branch
//   |       spin 180 degrees around the heading
//   [ ]     push / pop turtle state; ] places foliage if the branch is a leaf tip
//   !(x)    advance the taper depth by x                         (x default 1)
//   *(x)    multiply the step length for the rest of this branch (x default 0.8)
//   T(x)    place foliage here, x scales its size                (x default 1)
//   Q       suppress tip foliage for the rest of this branch
//
// Any other character is a rule symbol if a rule exists for it (otherwise ignored).
// When it runs out of Iterations, a rule symbol that is also a turtle symbol
// (e.g. a rule for F) executes as that turtle symbol instead.
// Several rules for the same symbol are picked between randomly by Weight.
//
// + and - only spin around the heading, so they do nothing visible on a branch
// that has not been tilted. Typical branch pattern: [+&F] = spin, then tilt outward.
//
// The grammar is compiled once (LSystemCompiler) into a flat list of ops with
// angles and parameters already resolved, so the interpreter never parses text.
// ============================================================================

public enum LSystemOpCode : byte
{
    Nop,
    Rule,           // expandable symbol; falls back to Terminal once out of iterations
    Forward,        // F
    Move,           // f
    RotateY,        // + -   spin around heading
    RotateZ,        // & ^
    RotateX,        // \ /
    Curve,          // ~
    Turn180,        // |
    Push,           // [
    Pop,            // ]
    Taper,          // !
    ScaleStep,      // *
    Foliage,        // T
    SuppressFoliage // Q
}

public struct LSystemOp
{
    public LSystemOpCode Code;
    public LSystemOpCode Terminal; // Rule ops: what to execute once out of iterations
    public byte FirstRule;         // Rule ops: index of the symbol's first rule in Rules
    public byte RuleCount;         // Rule ops: number of alternatives
    public float Value;            // angle in radians, step multiplier, etc. (already resolved)
}

public struct LSystemRule
{
    public ushort Start;            // into Ops
    public ushort Length;
    public float CumulativeWeight;  // normalised within its symbol's group; last one is 1
}

// ----------------------------------------------------------------------------
// Turtle state
// ----------------------------------------------------------------------------
public struct TurtleState
{
    public float3 Position;
    public quaternion Rotation;
    public float StepLength;
    public float Path;          // F steps walked from the root, for length-based taper
    public int BranchDepth;
    public float ManualDepth;   // accumulated by !
    public bool SuppressFoliage;
}

// ----------------------------------------------------------------------------
// Tree data  -  build with LSystemCompiler.Compile, never by hand
// ----------------------------------------------------------------------------
public struct LSystemTreeData
{
    // Compiled grammar: Ops[0, AxiomLength) is the axiom, rule bodies follow.
    public FixedList4096Bytes<LSystemOp> Ops;
    public FixedList512Bytes<LSystemRule> Rules;
    public int AxiomLength;
    public int Iterations;
    public float DetailStartScale;      // above this scale, extra iterations are added...
    public float IterationsPerDoubling; // ...this many per doubling of scale (0 = off)
    public float DetailMaxDroopAngle;   // MaxDroopAngle for branches grown by those extra iterations
    public float DetailStepScale;       // segment length of each extra level relative to the one before

    // Turtle parameters (angles in degrees; the defaults are baked into Ops at compile time)
    public float StepLength;
    public float MinStepLength;
    public float StepLengthVariance; // max random addition to StepLength per spawn
    public float PitchAngle;
    public float YawAngle;
    public float RollAngle;
    public float CurveAngle;
    public float AngleVariance;
    public float Gravity;            // per-segment bend toward the ground (negative bends upward)
    public float WindStrength;       // per-segment bend toward the wind direction
    public float WindDirection;      // degrees around world up, 0 = +X
    public float WindDirectionVariance; // random +/- degrees per tree
    public float MaxDroopAngle;      // branches never point further below horizontal than this (>= 90 = off)
    public bool KeepAboveGround;     // level off branches that would dip below the tree's base height

    // Body
    public TaperCurveType TaperCurve;
    public TaperMode TaperBy;
    public float TrunkWidthAtBase;
    public float MinWidth;
    public BlockType BodyBlock;
    public BranchShapeMode BranchShape;

    // Foliage
    public ShapeStyle Foliage;
    public BlockType FoliageBlock;
    public BlockType FoliageBlock2;
    public float FoliageBlock2Chance;
    public float FoliageSize;          // rounded to whole blocks after scaling
    public float FoliageSizeVariance;

    // Blocks dropped onto the ground beneath each foliage placement (Air = off)
    public BlockType GroundScatterBlock;
    public float GroundScatterAmount;

    // Roots: wander outward from the trunk base, each block settled onto the terrain surface
    public int RootCount;
    public float RootLength;
    public float RootWidth;
    public int RootHeight;           // blocks stacked at the trunk end, tapering to 1
    public float RootWander;         // max heading change per block, degrees
    public BlockType RootBlock;      // Air = BodyBlock
}

public enum TaperCurveType : byte
{
    Linear = 0,        // straight interpolation
    Square = 1,        // t^2 - slow start, fast finish (stays full longer, tapers hard near the tip)
    SquareRoot = 2,    // sqrt(t) - fast start, slow finish (tapers early, lingers thin for a while)
    Logarithmic = 3,   // log-shaped - very fast initial taper, flattens out early and stays close to min
    None = 4
}

public enum BranchShapeMode : byte
{
    Mixed = 0,       // every disc shape: 1, 2x2, plus, 3x3, rounded 4x4, circle, 4x4... (smallest steps)
    WholeBlocks = 1, // whole-block widths: 1, 2x2, 3x3, rounded 4x4, disc...
    Circles = 2,     // block-centred discs only: 1, plus, 3x3, circle... (the original look)
}

public enum TaperMode : byte
{
    Depth = 0,   // by bracket nesting (+ !): constant along a segment, steps at each branch
    Length = 1,  // by distance from the root: smooth cones, branches start as thick as their parent
    Both = 2,    // whichever is thinner: smooth along limbs, and side branches start thinner
}

// ----------------------------------------------------------------------------
// Compiler  -  managed, runs once when trees are baked
// ----------------------------------------------------------------------------
public static class LSystemCompiler
{
    public static int OpCapacity => new FixedList4096Bytes<LSystemOp>().Capacity;
    public static int RuleCapacity => new FixedList512Bytes<LSystemRule>().Capacity;

    // Fills tree.Ops / Rules / AxiomLength from source text. The tree's angle fields must be set first.
    // Returns false (and a readable error) if the grammar does not parse or does not fit.
    public static bool Compile(ref LSystemTreeData tree, string axiom, IReadOnlyList<LSystemRuleSO> rules, out string error)
    {
        tree.Ops.Clear();
        tree.Rules.Clear();
        tree.AxiomLength = 0;
        var errors = new StringBuilder();

        // Group rules by symbol, keeping first-appearance order.
        var groups = new List<(char symbol, List<LSystemRuleSO> rules)>();
        var groupIndex = new Dictionary<char, int>();
        if (rules != null)
        {
            foreach (var rule in rules)
            {
                if (rule == null || rule.Symbol == '\0')
                    continue;
                if (!groupIndex.TryGetValue(rule.Symbol, out int g))
                {
                    g = groups.Count;
                    groupIndex[rule.Symbol] = g;
                    groups.Add((rule.Symbol, new List<LSystemRuleSO>()));
                }
                groups[g].rules.Add(rule);
            }
        }

        int ruleTotal = 0;
        var firstRule = new Dictionary<char, (int first, int count)>();
        foreach (var (symbol, list) in groups)
        {
            firstRule[symbol] = (ruleTotal, list.Count);
            ruleTotal += list.Count;
        }
        if (ruleTotal > RuleCapacity || ruleTotal > 255)
        {
            error = $"Too many rules ({ruleTotal}); the limit is {math.min(RuleCapacity, 255)}.";
            return false;
        }

        if (!Emit(ref tree, axiom ?? string.Empty, firstRule, "Axiom", errors))
        {
            error = errors.ToString();
            return false;
        }
        tree.AxiomLength = tree.Ops.Length;

        foreach (var (symbol, list) in groups)
        {
            float total = 0f;
            foreach (var r in list)
                total += math.max(0f, r.Weight);

            float cumulative = 0f;
            for (int i = 0; i < list.Count; i++)
            {
                int start = tree.Ops.Length;
                if (!Emit(ref tree, list[i].Replacement ?? string.Empty, firstRule, $"Rule '{symbol}'", errors))
                {
                    error = errors.ToString();
                    return false;
                }

                cumulative += total > 0f ? math.max(0f, list[i].Weight) / total : 1f / list.Count;
                tree.Rules.Add(new LSystemRule
                {
                    Start = (ushort)start,
                    Length = (ushort)(tree.Ops.Length - start),
                    CumulativeWeight = i == list.Count - 1 ? 1f : cumulative,
                });
            }
        }

        error = errors.Length > 0 ? errors.ToString() : null;
        return true;
    }

    static bool Emit(ref LSystemTreeData tree, string text, Dictionary<char, (int first, int count)> rules,
        string where, StringBuilder errors)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
                continue;

            float? param = null;
            if (i + 1 < text.Length && text[i + 1] == '(')
            {
                int close = text.IndexOf(')', i + 2);
                if (close < 0)
                {
                    errors.AppendLine($"{where}: missing ')' after '{c}' at {i}.");
                    return false;
                }
                string number = text.Substring(i + 2, close - i - 2);
                if (!float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                {
                    errors.AppendLine($"{where}: '{number}' after '{c}' is not a number.");
                    return false;
                }
                param = parsed;
                i = close;
            }

            var op = TerminalOp(in tree, c, param);
            if (rules.TryGetValue(c, out var group))
            {
                op.Terminal = op.Code;
                op.Code = LSystemOpCode.Rule;
                op.FirstRule = (byte)group.first;
                op.RuleCount = (byte)group.count;
            }
            else if (op.Code == LSystemOpCode.Nop)
            {
                continue; // unknown symbol with no rule does nothing - don't spend an op on it
            }

            if (tree.Ops.Length >= tree.Ops.Capacity)
            {
                errors.AppendLine($"Grammar is too long: more than {tree.Ops.Capacity} symbols in the axiom and rules combined.");
                return false;
            }
            tree.Ops.Add(op);
        }
        return true;
    }

    static LSystemOp TerminalOp(in LSystemTreeData tree, char c, float? param)
    {
        float Angle(float defaultDegrees, float sign) => math.radians(param ?? defaultDegrees) * sign;

        switch (c)
        {
            case 'F': return Op(LSystemOpCode.Forward, param ?? 1f);
            case 'f': return Op(LSystemOpCode.Move, param ?? 1f);
            case '+': return Op(LSystemOpCode.RotateY, Angle(tree.YawAngle, 1f));
            case '-': return Op(LSystemOpCode.RotateY, Angle(tree.YawAngle, -1f));
            case '&': return Op(LSystemOpCode.RotateZ, Angle(tree.PitchAngle, 1f));
            case '^': return Op(LSystemOpCode.RotateZ, Angle(tree.PitchAngle, -1f));
            case '\\': return Op(LSystemOpCode.RotateX, Angle(tree.RollAngle, 1f));
            case '/': return Op(LSystemOpCode.RotateX, Angle(tree.RollAngle, -1f));
            case '~': return Op(LSystemOpCode.Curve, Angle(tree.CurveAngle, 1f));
            case '|': return Op(LSystemOpCode.Turn180, math.PI);
            case '[': return Op(LSystemOpCode.Push, 0f);
            case ']': return Op(LSystemOpCode.Pop, 0f);
            case '!': return Op(LSystemOpCode.Taper, param ?? 1f);
            case '*': return Op(LSystemOpCode.ScaleStep, param ?? 0.8f);
            case 'T': return Op(LSystemOpCode.Foliage, param ?? 1f);
            case 'Q': return Op(LSystemOpCode.SuppressFoliage, 0f);
            default: return Op(LSystemOpCode.Nop, 0f);
        }
    }

    static LSystemOp Op(LSystemOpCode code, float value) => new LSystemOp { Code = code, Terminal = code, Value = value };

    // Upper bound on ops the interpreter will execute, taking the longest alternative of every
    // random rule. Saturates at `limit`.
    public static long WorstCaseOps(in LSystemTreeData tree, long limit = long.MaxValue)
    {
        var memo = new Dictionary<(int rule, int depth), long>();
        return Count(in tree, 0, tree.AxiomLength, 0, memo, limit);
    }

    static long Count(in LSystemTreeData tree, int start, int length, int depth, Dictionary<(int, int), long> memo, long limit)
    {
        long total = 0;
        for (int i = start; i < start + length; i++)
        {
            var op = tree.Ops[i];
            total++;
            if (op.Code == LSystemOpCode.Rule && depth < tree.Iterations)
            {
                long best = 0;
                for (int r = op.FirstRule; r < op.FirstRule + op.RuleCount; r++)
                {
                    if (!memo.TryGetValue((r, depth), out long n))
                    {
                        var rule = tree.Rules[r];
                        n = Count(in tree, rule.Start, rule.Length, depth + 1, memo, limit);
                        memo[(r, depth)] = n;
                    }
                    best = math.max(best, n);
                }
                total += best;
            }
            if (total >= limit)
                return limit;
        }
        return total;
    }
}

// ----------------------------------------------------------------------------
// Builtin rulesets (DecorationType.Tree)
// ----------------------------------------------------------------------------
public static class TreeRulesets
{
    public static LSystemTreeData Get(DecorationType.Tree treeType)
    {
        if (!TryBuild(treeType, out var tree))
            Debug.LogWarning($"[LSystemTreeGenerator] No ruleset for {treeType}.");
        return tree;
    }

    static bool TryBuild(DecorationType.Tree treeType, out LSystemTreeData tree)
    {
        switch (treeType)
        {
            case DecorationType.Tree.SmallPine: tree = Temp(); return true;
            case DecorationType.Tree.RedFancy: tree = RedFancy(); return true;
            case DecorationType.Tree.Willow: tree = Willow(); return true;
            case DecorationType.Tree.Tallow: tree = Tallow(); return true;
            case DecorationType.Tree.Palm: tree = Palm(); return true;
            case DecorationType.Tree.SmallPalm: tree = SmallPalm(); return true;
            case DecorationType.Tree.NightBlossom: tree = NightBlossom(); return true;
            default: tree = default; return false;
        }
    }

    // Jobs read this table, built once on the main thread and indexed by (int)DecorationType.Tree.
    static NativeArray<LSystemTreeData> nativeTable;

    public static NativeArray<LSystemTreeData> GetNativeTable()
    {
        if (nativeTable.IsCreated)
            return nativeTable;

        var values = (DecorationType.Tree[])System.Enum.GetValues(typeof(DecorationType.Tree));
        int count = 0;
        foreach (var v in values)
            count = math.max(count, (int)v + 1);

        nativeTable = new NativeArray<LSystemTreeData>(count, Allocator.Persistent);
        foreach (var v in values)
        {
            if (TryBuild(v, out var tree))
                nativeTable[(int)v] = tree;
        }

        Application.quitting -= DisposeNativeTable;
        Application.quitting += DisposeNativeTable;
        return nativeTable;
    }

    static void DisposeNativeTable()
    {
        if (nativeTable.IsCreated)
            nativeTable.Dispose();
    }

    static LSystemTreeData Build(LSystemTreeData settings, string axiom, params (char symbol, string replacement)[] rules)
    {
        var list = new List<LSystemRuleSO>();
        foreach (var (symbol, replacement) in rules)
            list.Add(new LSystemRuleSO { Symbol = symbol, Replacement = replacement, Weight = 1f });

        if (!LSystemCompiler.Compile(ref settings, axiom, list, out string error))
            Debug.LogError($"[TreeRulesets] {error}");
        return settings;
    }

    // Template
    private static LSystemTreeData Temp() => Build(new LSystemTreeData
    {
        Iterations = 3,
        StepLength = 3f,
        PitchAngle = 30f,   // tilt off vertical
        YawAngle = 90f,     // 4-fold spread: 0, 90, -90, 180
        AngleVariance = 8f,
        TrunkWidthAtBase = 1,
        Foliage = ShapeStyle.None,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.PineLeaves,
    }, "F", ('F', "F[&F][+&F][-&F][++&F]"));

    private static LSystemTreeData RedFancy() => Build(new LSystemTreeData
    {
        // This design is solved so we use the final string as the initial axiom without iteration to reduce runtime
        StepLength = 4f,
        PitchAngle = 38f,   // outward pitch on bough split
        YawAngle = 120f,    // 3-fold symmetry
        Gravity = 0.06f,    // boughs arc downward at tips
        AngleVariance = 20f,
        TrunkWidthAtBase = 4,
        Foliage = ShapeStyle.Cluster,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.RedLeaves,
    }, "F~F~F[&~F~F[&~F[+&~F][-&~F]][+&~F[+&~F][-&~F]][-&~F[+&~F][-&~F]]~F~F[&~F[+&~F][-&~F]][+&~F[+&~F][-&~F]][-&~F[+&~F][-&~F]]~F~F[&B][+&B][-&B]A][+&~F~F[&~F[+&~F][-&~F]][+&~F[+&~F][-&~F]][-&~F[+&~F][-&~F]]~F~F[&~F[+&~F][-&~F]][+&~F[+&~F][-&~F]][-&~F[+&~F][-&~F]]~F~F[&B][+&B][-&B]A][++&~F~F[&~F[+&~F][-&~F]][+&~F[+&~F][-&~F]][-&~F[+&~F][-&~F]]~F~F[&~F[+&~F][-&~F]][+&~F[+&~F][-&~F]][-&~F[+&~F][-&~F]]~F~F[&B][+&B][-&B]A]");

    private static LSystemTreeData Willow() => Build(new LSystemTreeData
    {
        Iterations = 3,
        StepLength = 7f,
        PitchAngle = 38f,
        YawAngle = 90f,
        RollAngle = -35f,
        Gravity = 0.2f,      // boughs droop
        AngleVariance = 25f,
        TrunkWidthAtBase = 4,
        Foliage = ShapeStyle.HangingHemisphere,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.WillowLeaves,
    }, "~F~|F[&A][+&A][++&A][-\\^A]", ('A', "~F[&B][+&B][-&B]A"), ('B', "~F[+&~F][-&~F]"));

    private static LSystemTreeData Tallow() => Build(new LSystemTreeData
    {
        StepLength = 0f,
        StepLengthVariance = 5f,
        PitchAngle = 38f,
        YawAngle = 90f,
        RollAngle = -35f,
        Gravity = 0.2f,
        AngleVariance = 15f,
        TrunkWidthAtBase = 2,
        Foliage = ShapeStyle.HangingHemisphere,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.WillowLeaves,
    }, "F[+~FF][-~FFF][++~FFFF]");

    // Palm - tall straight trunk, T places the crown foliage explicitly at the top.
    private static LSystemTreeData Palm() => Build(new LSystemTreeData
    {
        StepLength = 4f,
        AngleVariance = 3f,   // slight trunk lean
        StepLengthVariance = 3f,
        TrunkWidthAtBase = 1,
        Foliage = ShapeStyle.PalmCrown,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.PineLeaves,
        FoliageSize = 6
    }, "FFFFT");

    private static LSystemTreeData SmallPalm() => Build(new LSystemTreeData
    {
        StepLength = 2f,
        AngleVariance = 3f,
        TrunkWidthAtBase = 2,
        Foliage = ShapeStyle.PalmCrown,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.PineLeaves,
        FoliageSize = 2
    }, "FT");

    private static LSystemTreeData NightBlossom() => Build(new LSystemTreeData
    {
        Iterations = 4,
        StepLength = 10f,
        PitchAngle = 45f,
        YawAngle = 120f,
        RollAngle = -35f,
        AngleVariance = 10f,
        TrunkWidthAtBase = 7,
        Foliage = ShapeStyle.PalmCrown,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.PineLeaves,
        FoliageSize = 4
    }, "FA", ('A', "+[&FA]F+[&FA]+[&FA]!FA"));
}

// ----------------------------------------------------------------------------
// Block output for a single tree: drops duplicate positions (overlapping foliage,
// segment joints) before they reach the BlockWriter, and mixes the secondary
// foliage block in by position hash.
// ----------------------------------------------------------------------------
public struct TreeBlockWriter
{
    public BlockWriter Writer;
    public NativeHashSet<int3> Written;
    public BlockType MixFrom;
    public BlockType MixTo;
    public uint MixThreshold;   // 0 = no mixing

    public void ApplyBlock(int x, int y, int z, BlockType block)
    {
        var p = new int3(x, y, z);
        if (!Written.Add(p))
            return;
        if (MixThreshold != 0 && block == MixFrom && math.hash(p) < MixThreshold)
            block = MixTo;
        Writer.ApplyBlock(x, y, z, block, PendingWriteMode.ReplaceAir);
    }

    public void DropToSurface(int x, int y, int z, BlockType block)
    {
        if (Written.Add(new int3(x, y, z)))
            Writer.ApplyBlock(x, y, z, block, PendingWriteMode.DropToSurface);
    }

    // Foliage with faked lighting: light (0..1) scales the chance of the secondary foliage block,
    // so shapes can put it on their sunlit side instead of scattering it evenly.
    public void ApplyLit(int x, int y, int z, BlockType block, float light)
    {
        var p = new int3(x, y, z);
        if (!Written.Add(p))
            return;
        if (MixThreshold != 0 && block == MixFrom && math.hash(p) < (uint)(math.saturate(light) * MixThreshold))
            block = MixTo;
        Writer.ApplyBlock(x, y, z, block, PendingWriteMode.ReplaceAir);
    }
}

// ----------------------------------------------------------------------------
// Interpreter  -  Burst-safe, no managed allocations
// ----------------------------------------------------------------------------
[BurstCompile]
public static class LSystemInterpreter
{
    private const int MaxStackDepth = 128;

    // Hard stop for runaway grammars so a bad asset can't stall a decoration job.
    public const int MaxExecutedOps = 2_000_000;

    struct Walker
    {
        public TurtleState State;
        public NativeArray<TurtleState> Stack;
        public NativeArray<bool> BranchHasChild;
        public int StackTop;
        public Unity.Mathematics.Random Rng;
        public Unity.Mathematics.Random RuleRng;
        public float VarRad;
        public float MaxDepth;
        public float MaxPath;
        public int OriginY;
        public float3 Wind;
        public float PartialChance;   // chance a symbol at depth == Iterations still expands
        public int BaseIterations;    // iterations before extra detail; deeper ops are detail
        public int DetailLevel;       // extra-detail level of the op being executed (0 = base tree)
        // Extra detail draws from its own streams so the base tree's choices and angles are
        // identical with or without it.
        public Unity.Mathematics.Random DetailRuleRng;
        public Unity.Mathematics.Random DetailRng;
        public int Budget;
        public TreeBlockWriter Out;
    }

    struct Counter
    {
        public NativeArray<float3> Stack; // x = depth, y = manual depth, z = path
        public int StackTop;
        public float Depth, Manual, Path;
        public float MaxDepth, MaxPath;
        public Unity.Mathematics.Random RuleRng;
        public float PartialChance;
        public int Budget;
    }

    public static void Interpret(
        in LSystemTreeData tree,
        int originX, int originY, int originZ,
        ref Unity.Mathematics.Random rng,
        ref BlockWriter applyBlock)
        => Interpret(in tree, originX, originY, originZ, 1f, ref rng, ref applyBlock);

    // scale multiplies every length and width (steps, branch widths, foliage, roots); angles are
    // untouched, so one design keeps its shape at any size. Above DetailStartScale the tree also
    // gains IterationsPerDoubling extra iterations per doubling of scale - fractional amounts expand
    // that share of the deepest symbols - with step lengths shrunk so the extra levels add branches
    // and foliage clusters rather than reach.
    public static void Interpret(
        in LSystemTreeData source,
        int originX, int originY, int originZ,
        float scale,
        ref Unity.Mathematics.Random rng,
        ref BlockWriter applyBlock)
    {
        if (source.AxiomLength == 0)
            return;

        if (scale <= 0f)
            scale = 1f;
        var tree = source;
        ApplyScale(ref tree, scale);

        // Rule choices come from their own stream so the dry run below makes exactly the same ones.
        uint ruleSeed = rng.NextUInt() | 1u;
        var ruleRng = new Unity.Mathematics.Random(ruleSeed);

        float windAngle = tree.WindDirection;
        if (tree.WindStrength != 0f && tree.WindDirectionVariance > 0f)
            windAngle += (rng.NextFloat() * 2f - 1f) * tree.WindDirectionVariance;
        windAngle = math.radians(windAngle);

        float extra = ExtraIterations(in tree, scale);

        // Dry run over the base tree: max nesting depth and max path length, for tapering. Extra
        // detail is left out, so trunk and limbs taper exactly as they would without it and the
        // detail grows on past the tips at minimum width.
        var counter = new Counter
        {
            Stack = new NativeArray<float3>(MaxStackDepth, Allocator.Temp, NativeArrayOptions.UninitializedMemory),
            StackTop = -1,
            RuleRng = ruleRng,
            Budget = MaxExecutedOps,
        };
        Count(in tree, 0, tree.AxiomLength, 0, ref counter);
        counter.Stack.Dispose();

        tree.Iterations += (int)extra;
        float partialChance = extra - (int)extra;

        var w = new Walker
        {
            State = new TurtleState
            {
                Position = new float3(originX, originY, originZ),
                Rotation = quaternion.RotateY(rng.NextFloat() * math.PI * 2f),
                StepLength = tree.StepLength + rng.NextFloat() * tree.StepLengthVariance,
            },
            Stack = new NativeArray<TurtleState>(MaxStackDepth, Allocator.Temp, NativeArrayOptions.UninitializedMemory),
            BranchHasChild = new NativeArray<bool>(MaxStackDepth, Allocator.Temp, NativeArrayOptions.ClearMemory),
            StackTop = -1,
            Rng = rng,
            RuleRng = ruleRng,
            VarRad = math.radians(tree.AngleVariance),
            MaxDepth = (int)counter.MaxDepth,
            MaxPath = counter.MaxPath,
            OriginY = originY,
            Wind = new float3(math.cos(windAngle), 0f, math.sin(windAngle)),
            PartialChance = partialChance,
            BaseIterations = source.Iterations,
            DetailRuleRng = new Unity.Mathematics.Random(math.hash(new uint2(ruleSeed, 0x9E3779B9u)) | 1u),
            DetailRng = new Unity.Mathematics.Random(math.hash(new uint2(ruleSeed, 0x85EBCA6Bu)) | 1u),
            Budget = MaxExecutedOps,
            Out = new TreeBlockWriter
            {
                Writer = applyBlock,
                Written = new NativeHashSet<int3>(4096, Allocator.Temp),
                MixFrom = tree.FoliageBlock,
                MixTo = tree.FoliageBlock2,
                MixThreshold = tree.FoliageBlock2 != BlockType.Air && tree.FoliageBlock2Chance > 0f
                    ? (uint)(math.saturate(tree.FoliageBlock2Chance) * (double)uint.MaxValue) : 0u,
            },
        };

        Walk(in tree, 0, tree.AxiomLength, 0, ref w);
        if (tree.RootCount > 0 && tree.RootLength > 0f)
            GrowRoots(in tree, new float3(originX, originY, originZ), ref w);

        rng = w.Rng;
        w.Stack.Dispose();
        w.BranchHasChild.Dispose();
        w.Out.Written.Dispose();
    }

    // Extra iterations a tree gets at this scale (capped at 4).
    public static float ExtraIterations(in LSystemTreeData tree, float scale)
        => tree.IterationsPerDoubling > 0f && tree.DetailStartScale > 0f && scale > tree.DetailStartScale
            ? math.min(math.log2(scale / tree.DetailStartScale) * tree.IterationsPerDoubling, 4f)
            : 0f;

    static void ApplyScale(ref LSystemTreeData tree, float scale)
    {
        tree.StepLength *= scale;
        tree.MinStepLength *= scale;
        tree.StepLengthVariance *= scale;
        tree.TrunkWidthAtBase *= scale;
        tree.MinWidth *= scale;
        tree.FoliageSize = math.max(1f, math.round(tree.FoliageSize * scale));
        tree.FoliageSizeVariance = math.round(tree.FoliageSizeVariance * scale);
        tree.RootLength *= scale;
        tree.RootWidth = math.max(1f, tree.RootWidth * scale);
        tree.RootHeight = math.max(1, (int)math.round(tree.RootHeight * scale));
    }

    // Expands while within Iterations; one level deeper, expands with PartialChance. Rolls on the rule
    // stream only in that last case, identically in Walk and Count, so the dry run stays in sync.
    static bool ShouldExpand(in LSystemTreeData tree, int iterDepth, float partialChance, ref Unity.Mathematics.Random ruleRng)
    {
        if (iterDepth < tree.Iterations)
            return true;
        return iterDepth == tree.Iterations && partialChance > 0f && ruleRng.NextFloat() < partialChance;
    }

    static int ChooseRule(in LSystemTreeData tree, in LSystemOp op, ref Unity.Mathematics.Random ruleRng)
    {
        if (op.RuleCount == 1)
            return op.FirstRule;
        float r = ruleRng.NextFloat();
        int last = op.FirstRule + op.RuleCount - 1;
        for (int i = op.FirstRule; i < last; i++)
            if (r < tree.Rules[i].CumulativeWeight)
                return i;
        return last;
    }

    // Walks the grammar recursively, substituting rule symbols up to tree.Iterations deep and
    // executing everything else as turtle commands - equivalent to expanding the full string
    // first, without the exponentially growing buffer.
    static void Walk(in LSystemTreeData tree, int start, int length, int iterDepth, ref Walker w)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            if (--w.Budget < 0)
                return;

            var op = tree.Ops[i];
            if (op.Code == LSystemOpCode.Rule)
            {
                ref var ruleRng = ref (iterDepth >= w.BaseIterations ? ref w.DetailRuleRng : ref w.RuleRng);
                if (ShouldExpand(in tree, iterDepth, w.PartialChance, ref ruleRng))
                {
                    // Every open bracket now contains an expansion, so none of them is a leaf tip.
                    // Marked levels always form a prefix of the stack, so stop at the first marked one.
                    for (int lvl = w.StackTop; lvl >= 0 && !w.BranchHasChild[lvl]; lvl--)
                        w.BranchHasChild[lvl] = true;

                    var rule = tree.Rules[ChooseRule(in tree, in op, ref ruleRng)];
                    Walk(in tree, rule.Start, rule.Length, iterDepth + 1, ref w);
                    continue;
                }
                op.Code = op.Terminal;
            }

            w.DetailLevel = math.max(0, iterDepth - w.BaseIterations);
            if (w.DetailLevel == 0)
            {
                Execute(in tree, in op, ref w);
            }
            else
            {
                var baseRng = w.Rng;
                w.Rng = w.DetailRng;
                Execute(in tree, in op, ref w);
                w.DetailRng = w.Rng;
                w.Rng = baseRng;
            }
        }
    }

    static void Execute(in LSystemTreeData tree, in LSystemOp op, ref Walker w)
    {
        ref var s = ref w.State;
        switch (op.Code)
        {
            case LSystemOpCode.Forward:
                Forward(in tree, op.Value, ref w);
                break;

            case LSystemOpCode.Move:
                s.Position += math.mul(s.Rotation, math.up()) * s.StepLength * op.Value;
                s.Path += op.Value;
                break;

            case LSystemOpCode.RotateY: s.Rotation = math.mul(s.Rotation, quaternion.RotateY(op.Value + Variance(ref w.Rng, w.VarRad))); break;
            case LSystemOpCode.RotateZ: s.Rotation = math.mul(s.Rotation, quaternion.RotateZ(op.Value + Variance(ref w.Rng, w.VarRad))); break;
            case LSystemOpCode.RotateX: s.Rotation = math.mul(s.Rotation, quaternion.RotateX(op.Value + Variance(ref w.Rng, w.VarRad))); break;
            case LSystemOpCode.Curve: s.Rotation = math.mul(s.Rotation, quaternion.RotateZ(op.Value + Variance(ref w.Rng, w.VarRad * 0.5f))); break;
            case LSystemOpCode.Turn180: s.Rotation = math.mul(s.Rotation, quaternion.RotateY(op.Value)); break;

            case LSystemOpCode.Push:
                if (w.StackTop < MaxStackDepth - 1)
                {
                    w.Stack[++w.StackTop] = s;
                    w.BranchHasChild[w.StackTop] = false; // assume leaf until proven otherwise
                    s.BranchDepth++;
                }
                break;

            case LSystemOpCode.Pop:
                if ((w.StackTop < 0 || !w.BranchHasChild[w.StackTop]) && !s.SuppressFoliage)
                    PlaceFoliage(in tree, s.Position, 1f, ref w);
                if (w.StackTop >= 0)
                    s = w.Stack[w.StackTop--];
                break;

            case LSystemOpCode.Taper: s.ManualDepth += op.Value; break;
            case LSystemOpCode.ScaleStep: s.StepLength *= op.Value; break;
            case LSystemOpCode.Foliage: PlaceFoliage(in tree, s.Position, op.Value, ref w); break;
            case LSystemOpCode.SuppressFoliage: s.SuppressFoliage = true; break;
        }
    }

    static void Forward(in LSystemTreeData tree, float steps, ref Walker w)
    {
        ref var s = ref w.State;
        float maxDroop = w.DetailLevel > 0 ? math.min(tree.MaxDroopAngle, tree.DetailMaxDroopAngle) : tree.MaxDroopAngle;
        if (maxDroop < 90f)
            s.Rotation = LimitDroop(s.Rotation, -math.sin(math.radians(maxDroop)));
        float3 heading = math.mul(s.Rotation, math.up());

        float length, width0, width1;
        if (tree.TaperCurve == TaperCurveType.None)
        {
            length = s.StepLength * steps;
            width0 = width1 = tree.TrunkWidthAtBase;
        }
        else
        {
            float depthT = w.MaxDepth > 0f ? math.saturate((s.BranchDepth + (int)s.ManualDepth) / w.MaxDepth) : 0f;
            float t0 = TaperProgress(tree.TaperBy, depthT, s.Path, w.MaxPath);
            float t1 = TaperProgress(tree.TaperBy, depthT, s.Path + steps, w.MaxPath);
            float c0 = ApplyCurve(t0, tree.TaperCurve);
            float c1 = ApplyCurve(t1, tree.TaperCurve);

            length = math.lerp(s.StepLength, tree.MinStepLength, c0) * steps;
            width0 = math.lerp(tree.TrunkWidthAtBase, tree.MinWidth, c0);
            width1 = math.lerp(tree.TrunkWidthAtBase, tree.MinWidth, c1);
        }
        if (w.DetailLevel > 0)
            length *= math.pow(tree.DetailStepScale, w.DetailLevel);

        // Level off rather than grow into the ground the tree stands on.
        if (tree.KeepAboveGround && heading.y < 0f && s.Position.y + heading.y * length < w.OriginY + 1f)
        {
            s.Rotation = LimitDroop(s.Rotation, 0f);
            heading = math.mul(s.Rotation, math.up());
        }

        float3 end = s.Position + heading * length;
        DrawSegment(s.Position, end, width0, width1, tree.BranchShape, tree.BodyBlock, ref w.Out);
        s.Position = end;
        s.Path += steps;

        // Tropism: bend the heading toward the ground / the wind, most strongly when at right
        // angles to it. Accumulates segment after segment, so branches arc rather than just tilt.
        if (tree.Gravity != 0f)
            s.Rotation = Bend(s.Rotation, new float3(0f, -1f, 0f), tree.Gravity * steps);
        if (tree.WindStrength != 0f)
            s.Rotation = Bend(s.Rotation, w.Wind, tree.WindStrength * steps);
    }

    // Tilts the heading up just enough that its y component is at least minY.
    static quaternion LimitDroop(quaternion rotation, float minY)
    {
        float3 heading = math.mul(rotation, math.up());
        if (heading.y >= minY)
            return rotation;
        float3 axis = math.cross(heading, math.up());
        float len = math.length(axis);
        axis = len > 1e-4f ? axis / len : math.mul(rotation, math.right()); // straight down: any horizontal axis
        float delta = math.asin(math.clamp(minY, -1f, 1f)) - math.asin(math.clamp(heading.y, -1f, 1f));
        return math.mul(quaternion.AxisAngle(axis, delta), rotation);
    }

    static quaternion Bend(quaternion rotation, float3 toward, float strength)
    {
        float3 axis = math.cross(math.mul(rotation, math.up()), toward);
        float sin = math.length(axis);
        return sin > 1e-4f ? math.mul(quaternion.AxisAngle(axis / sin, strength * sin), rotation) : rotation;
    }

    // Roots spread from the trunk base in plan view. Every block is a DropToSurface write that
    // settles onto the terrain when applied, so roots follow the ground even in chunks this job
    // can't see. Stacking several drops per column makes them flare up into the trunk.
    static void GrowRoots(in LSystemTreeData tree, float3 origin, ref Walker w)
    {
        BlockType block = tree.RootBlock != BlockType.Air ? tree.RootBlock : tree.BodyBlock;
        float wander = math.radians(tree.RootWander);
        float startRadius = math.max(0f, tree.TrunkWidthAtBase * 0.5f - 1f);
        int dropFrom = (int)origin.y + 4; // above the petals' start height, so the two never dedupe each other

        for (int r = 0; r < tree.RootCount; r++)
        {
            float angle = (r + w.Rng.NextFloat() * 0.7f) / tree.RootCount * math.PI * 2f;
            float length = tree.RootLength * (0.6f + 0.4f * w.Rng.NextFloat());
            float2 pos = origin.xz + new float2(math.cos(angle), math.sin(angle)) * startRadius;

            for (float travelled = 0f; travelled < length; travelled += 1f)
            {
                float t = travelled / length;
                float radius = math.max(0f, math.lerp(tree.RootWidth, 1f, t) * 0.5f - 0.35f);
                int height = math.max(1, (int)math.round(math.lerp(tree.RootHeight, 1f, t)));
                int ext = (int)math.ceil(radius);

                for (int dx = -ext; dx <= ext; dx++)
                    for (int dz = -ext; dz <= ext; dz++)
                    {
                        if (dx * dx + dz * dz > radius * radius + 0.01f && (dx != 0 || dz != 0))
                            continue;
                        int x = (int)math.round(pos.x) + dx, z = (int)math.round(pos.y) + dz; // pos is (x, z)
                        // Each layer has its own start height so it isn't deduplicated away;
                        // applied in order, each one lands on the one before.
                        for (int k = 0; k < height; k++)
                            w.Out.DropToSurface(x, dropFrom + k, z, block);
                    }

                angle += (w.Rng.NextFloat() * 2f - 1f) * wander;
                pos += new float2(math.cos(angle), math.sin(angle));
            }
        }
    }

    static float TaperProgress(TaperMode mode, float depthT, float path, float maxPath)
    {
        float lengthT = maxPath > 0f ? math.saturate(path / maxPath) : 0f;
        switch (mode)
        {
            case TaperMode.Length: return lengthT;
            case TaperMode.Both: return math.max(depthT, lengthT);
            default: return depthT;
        }
    }

    static float ApplyCurve(float t, TaperCurveType curve)
    {
        switch (curve)
        {
            case TaperCurveType.Square: return t * t;
            case TaperCurveType.SquareRoot: return math.sqrt(t);
            case TaperCurveType.Logarithmic: return math.log10(1f + 9f * t); // 0->0, 1->1
            case TaperCurveType.Linear:
            default: return t;
        }
    }

    static float Variance(ref Unity.Mathematics.Random rng, float maxRad)
        => maxRad <= 0f ? 0f : (rng.NextFloat() * 2f - 1f) * maxRad;

    static void PlaceFoliage(in LSystemTreeData tree, float3 pos, float sizeScale, ref Walker w)
    {
        if (tree.Foliage == ShapeStyle.None)
            return;

        int size = (int)tree.FoliageSize;
        int variance = (int)tree.FoliageSizeVariance;
        if (variance > 0)
            size += w.Rng.NextInt(-variance, variance + 1);
        size = math.max(1, (int)math.round(size * sizeScale));

        var ip = new int3((int)math.round(pos.x), (int)math.round(pos.y), (int)math.round(pos.z));
        ShapePlacer.Place(tree.Foliage, tree.FoliageBlock, size, ip, ref w.Rng, ref w.Out);

        if (tree.GroundScatterBlock == BlockType.Air || tree.GroundScatterAmount <= 0f)
            return;

        int count = (int)tree.GroundScatterAmount;
        if (w.Rng.NextFloat() < tree.GroundScatterAmount - count)
            count++;
        float radius = size * 0.6f + 1f;
        for (int i = 0; i < count; i++)
        {
            float angle = w.Rng.NextFloat() * math.PI * 2f;
            float r = math.sqrt(w.Rng.NextFloat()) * radius;
            w.Out.DropToSurface(
                ip.x + (int)math.round(math.cos(angle) * r),
                w.OriginY + 2,
                ip.z + (int)math.round(math.sin(angle) * r),
                tree.GroundScatterBlock);
        }
    }

    // Voxelises a tapered capsule from a to b, one plane slice at a time along the segment's major
    // axis, so each block is visited once and the rounded ends join consecutive segments at bends.
    // Each slice is a disc around the branch axis, centred on a block (odd shapes: 1, plus, 3x3...)
    // or on a block corner (even shapes: 2x2, rounded 4x4...), as BranchShapeMode decides.
    // WidthBias keeps width values meaning the same thickness as before: anything under ~2.2 is
    // a single block.
    const float WidthBias = 0.7f;

    static void DrawSegment(float3 a, float3 b, float widthA, float widthB, BranchShapeMode shape, BlockType block, ref TreeBlockWriter output)
    {
        float3 d = b - a;
        float lenSq = math.lengthsq(d);
        if (lenSq < 1e-6f)
            return;

        float rMax = math.max(widthA, widthB) * 0.5f + 0.5f;

        float3 ad = math.abs(d);
        int m = ad.x >= ad.y && ad.x >= ad.z ? 0 : (ad.y >= ad.z ? 1 : 2);
        int u = (m + 1) % 3, v = (m + 2) % 3;
        float cosM = ad[m] / math.sqrt(lenSq);
        int extent = (int)math.ceil(rMax / cosM + 0.5f);

        int cStart = (int)math.floor(math.min(a[m], b[m]) - rMax);
        int cEnd = (int)math.ceil(math.max(a[m], b[m]) + rMax);
        for (int c = cStart; c <= cEnd; c++)
        {
            float tPlane = (c - a[m]) / d[m];
            bool onSegment = tPlane >= 0f && tPlane <= 1f;
            float tc = math.saturate(tPlane);
            float3 center = a + d * tc;
            SliceShape(math.lerp(widthA, widthB, tc), shape, out float r, out bool even);

            // Snap the axis in this slice to a block centre (odd) or corner (even) and measure from
            // there, so every slice of a given width has exactly the same symmetric cross-section.
            float su = even ? math.floor(center[u]) + 0.5f : math.round(center[u]);
            float sv = even ? math.floor(center[v]) + 0.5f : math.round(center[v]);
            float3 shift = default;
            shift[u] = su - center[u];
            shift[v] = sv - center[v];
            int cu = (int)math.floor(su), cv = (int)math.floor(sv);

            for (int du = -extent; du <= extent + 1; du++)
            {
                for (int dv = -extent; dv <= extent + 1; dv++)
                {
                    int3 p = default;
                    p[m] = c;
                    p[u] = cu + du;
                    p[v] = cv + dv;

                    // The block on the axis is always placed so thin branches stay connected.
                    bool place = onSegment && !even && du == 0 && dv == 0;
                    if (!place)
                    {
                        float3 pf = (float3)p - shift;
                        float t = math.saturate(math.dot(pf - a, d) / lenSq);
                        place = math.distancesq(pf, a + d * t) <= r * r + 0.01f;
                    }
                    if (place)
                        output.ApplyBlock(p.x, p.y, p.z, block);
                }
            }
        }
    }

    // Cross-section for a branch width: disc radius r around a block centre (even = false) or a
    // block corner (even = true).
    static void SliceShape(float width, BranchShapeMode shape, out float r, out bool even)
    {
        if (shape == BranchShapeMode.WholeBlocks)
        {
            int blocks = math.max(1, (int)math.round(width - WidthBias));
            even = (blocks & 1) == 0;
            r = blocks * 0.5f - 0.05f;
            return;
        }

        r = math.max(0f, (width - WidthBias) * 0.5f);
        even = false;
        if (shape == BranchShapeMode.Mixed)
        {
            // Whichever centring gives a block count closer to a true circle of this radius.
            int corner = CornerDiscCount(r);
            if (corner >= 4)
            {
                float target = math.PI * r * r;
                even = math.abs(corner - target) < math.abs(CentredDiscCount(r) - target);
            }
        }
    }

    // Blocks within r (same tolerance as DrawSegment) of a block centre / of a block corner.
    static int CentredDiscCount(float r)
    {
        float r2 = r * r + 0.01f;
        int n = 0;
        for (int i = (int)math.floor(math.sqrt(r2)); i >= 0; i--)
            n += (i == 0 ? 1 : 2) * (2 * (int)math.floor(math.sqrt(r2 - i * i)) + 1);
        return n;
    }

    static int CornerDiscCount(float r)
    {
        float r2 = r * r + 0.01f;
        int n = 0;
        for (float x = 0.5f; x * x <= r2; x += 1f)
            n += 4 * (int)math.floor(math.sqrt(r2 - x * x) + 0.5f);
        return n;
    }

    // ------------------------------------------------------------------
    // Dry run - mirrors Walk/Execute's stack and rule choices, without drawing
    // ------------------------------------------------------------------
    static void Count(in LSystemTreeData tree, int start, int length, int iterDepth, ref Counter c)
    {
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            if (--c.Budget < 0)
                return;

            var op = tree.Ops[i];
            if (op.Code == LSystemOpCode.Rule)
            {
                if (ShouldExpand(in tree, iterDepth, c.PartialChance, ref c.RuleRng))
                {
                    var rule = tree.Rules[ChooseRule(in tree, in op, ref c.RuleRng)];
                    Count(in tree, rule.Start, rule.Length, iterDepth + 1, ref c);
                    continue;
                }
                op.Code = op.Terminal;
            }

            switch (op.Code)
            {
                case LSystemOpCode.Forward:
                case LSystemOpCode.Move:
                    c.Path += op.Value;
                    c.MaxPath = math.max(c.MaxPath, c.Path);
                    break;
                case LSystemOpCode.Push:
                    if (c.StackTop < MaxStackDepth - 1)
                    {
                        c.Stack[++c.StackTop] = new float3(c.Depth, c.Manual, c.Path);
                        c.Depth += 1f;
                        c.MaxDepth = math.max(c.MaxDepth, c.Depth + (int)c.Manual);
                    }
                    break;
                case LSystemOpCode.Pop:
                    if (c.StackTop >= 0)
                    {
                        var saved = c.Stack[c.StackTop--];
                        c.Depth = saved.x;
                        c.Manual = saved.y;
                        c.Path = saved.z;
                    }
                    break;
                case LSystemOpCode.Taper:
                    c.Manual += op.Value;
                    c.MaxDepth = math.max(c.MaxDepth, c.Depth + (int)c.Manual);
                    break;
            }
        }
    }
}

// ----------------------------------------------------------------------------
// Public entry point
// ----------------------------------------------------------------------------
[BurstCompile]
public static class LSystemTreeGenerator
{
    public static void Generate(
        DecorationType.Tree treeType,
        int x, int y, int z,
        ref Unity.Mathematics.Random rng,
        ref BlockWriter writer,
        NativeArray<LSystemTreeData> rulesets)
    {
        LSystemTreeData tree = rulesets[(int)treeType];
        LSystemInterpreter.Interpret(in tree, x, y, z, ref rng, ref writer);
    }
}
