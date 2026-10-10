using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

// ============================================================================
// L-SYSTEM TREE GENERATOR  —  fully Burst-compatible
// ============================================================================
//
// SYMBOL REFERENCE — KEY RULE: + and - only yaw (spin around world-up).
// They do nothing visible if the turtle still points straight up.
// Always pitch (& or ^) BEFORE or AFTER a yaw to actually tilt off vertical.
// Typical branch pattern: [+&F] = yaw to compass direction, then pitch outward.
// ----------------
//   F   move forward + place Log blocks (tapered by branch depth)
//   f   move forward, no blocks placed
//   +   yaw   left   (+YawAngle degrees)
//   -   yaw   right  (-YawAngle degrees)
//   &   pitch down   (+PitchAngle degrees)
//   ^   pitch up     (-PitchAngle degrees)
//   \   roll  left   (+RollAngle degrees)
//   /   roll  right  (-RollAngle degrees)
//   |   180 yaw (U-turn)
//   [   push turtle state onto stack
//   ]   pop  turtle state + place foliage at tip
//   !   multiply current step length by StepLengthScalar
//   T   place foliage at current position (explicit)
//   ~   apply a small pitch (CurvatureAngle) each step — use repeated in rules to curve branches
//   !   manually apply branch depth step
//
// WORKFLOW
// --------
//   Set Axiom, Rules, and Iterations in LSystemTreeData.
//   The interpreter expands the axiom at runtime before walking it.
//   Once happy with a tree, freeze Iterations at its final value.
// ============================================================================

// ----------------------------------------------------------------------------
// A single production rule
// ----------------------------------------------------------------------------
public struct LSystemRule
{
    public byte Symbol;                      // the char this rule replaces
    public FixedString64Bytes Replacement;   // what it expands to
}

// ----------------------------------------------------------------------------
// Turtle state — stored in a NativeArray
// ----------------------------------------------------------------------------
public struct TurtleState
{
    public float3 Position;
    public quaternion Rotation;
    public float StepLength;
    public int BranchDepth;
    public float ManualDepth; // accumulated by !, used to manually increase Branch depth
    public bool SuppressFoliage;
}

// ----------------------------------------------------------------------------
// Tree data
// ----------------------------------------------------------------------------
public struct LSystemTreeData
{
    // L-system grammar
    public FixedString512Bytes Axiom;
    public LSystemRule Rule0;
    public LSystemRule Rule1;        // add Rule2 if needed
    public int RuleCount;    // how many rules are active
    public int Iterations;   // expansion depth

    // Turtle parameters
    public float StepLength;
    public float MinStepLength;
    public float StepLengthVariance; // max random addition to StepLength per spawn
    public float PitchAngle;
    public float YawAngle;
    public float RollAngle;
    public TaperCurveType TaperCurve;
    public float GravityFactor;
    public float AngleVariance;

    // Visual
    public int TrunkWidthAtBase;
    public int MinWidth;
    public ShapeStyle Foliage;
    public BlockType BodyBlock;
    public BlockType FoliageBlock;
    public int FoliageSize;
}
public enum TaperCurveType : byte
{
    Linear = 0,        // straight interpolation
    Square = 1,         // t² — slow start, fast finish (stays full longer, tapers hard near the tip)
    SquareRoot = 2,      // sqrt(t) — fast start, slow finish (tapers early, lingers thin for a while)
    Logarithmic = 3,     // log-shaped — very fast initial taper, flattens out early and stays close to min
    None = 4
}
// ----------------------------------------------------------------------------
// Ruleset factory
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

    // Rulesets are built from C# strings, which Burst cannot touch. Jobs read this table instead,
    // built once on the main thread and indexed by (int)DecorationType.Tree.
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

    // Template 
    private static LSystemTreeData Temp() => new LSystemTreeData
    {
        Axiom = new FixedString512Bytes("F"),
        Rule0 = new LSystemRule { Symbol = (byte)'F', Replacement = new FixedString64Bytes("F[&F][+&F][-&F][++&F]") },
        RuleCount = 1,
        Iterations = 3,

        StepLength = 3f,
        StepLengthVariance = 0,
        PitchAngle = 30f,   // tilt off vertical
        YawAngle = 90f,   // 4-fold spread: 0, 90, -90, 180
        RollAngle = 0f,
        GravityFactor = 0f,
        AngleVariance = 8f,
        TrunkWidthAtBase = 1,
        Foliage = ShapeStyle.None,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.PineLeaves,
    };


    private static LSystemTreeData RedFancy() => new LSystemTreeData
    {
        // This design is solved so we use the final string as the initial axiom without iteration to reduce runtime
        // 3 interations at 408 characters

        //Axiom = new FixedString64Bytes("F~F~F[&A][+&A][++&A]"),
        Axiom = new FixedString512Bytes("F~F~F[&~F~F[&~F[+&~F][-&~F]][+&~F[+&~F][-&~F]][-&~F[+&~F][-&~F]]~F~F[&~F[+&~F][-&~F]][+&~F[+&~F][-&~F]][-&~F[+&~F][-&~F]]~F~F[&B][+&B][-&B]A][+&~F~F[&~F[+&~F][-&~F]][+&~F[+&~F][-&~F]][-&~F[+&~F][-&~F]]~F~F[&~F[+&~F][-&~F]][+&~F[+&~F][-&~F]][-&~F[+&~F][-&~F]]~F~F[&B][+&B][-&B]A][++&~F~F[&~F[+&~F][-&~F]][+&~F[+&~F][-&~F]][-&~F[+&~F][-&~F]]~F~F[&~F[+&~F][-&~F]][+&~F[+&~F][-&~F]][-&~F[+&~F][-&~F]]~F~F[&B][+&B][-&B]A]"),
        Rule0 = new LSystemRule { Symbol = (byte)'A', Replacement = new FixedString64Bytes("~F~F[&B][+&B][-&B]A") },
        Rule1 = new LSystemRule { Symbol = (byte)'B', Replacement = new FixedString64Bytes("~F[+&~F][-&~F]") },
        RuleCount = 0,
        Iterations = 0,

        StepLength = 4f,
        PitchAngle = 38f,   // outward pitch on bough split
        YawAngle = 120f,  // 3-fold symmetry
        RollAngle = 0f,
        GravityFactor = 0.12f, // stronger droop so boughs arc downward at tips
        AngleVariance = 20f,   // high — irregular twisted character
        TrunkWidthAtBase = 4,
        Foliage = ShapeStyle.Cluster,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.RedLeaves,
    };

    private static LSystemTreeData Willow() => new LSystemTreeData
    {
        Axiom = new FixedString64Bytes("~F~|F[&A][+&A][++&A][-\\^A]"),
        Rule0 = new LSystemRule { Symbol = (byte)'A', Replacement = new FixedString64Bytes("~F[&B][+&B][-&B]A") },
        Rule1 = new LSystemRule { Symbol = (byte)'B', Replacement = new FixedString64Bytes("~F[+&~F][-&~F]") },
        RuleCount = 2,
        Iterations = 3,

        StepLength = 7f,
        PitchAngle = 38f,   // outward pitch on bough split
        YawAngle = 90f,  // 4-fold symmetry
        RollAngle = -35f,
        GravityFactor = 0.333f, // stronger droop so boughs arc downward at tips
        AngleVariance = 25f,   // high — irregular twisted character
        TrunkWidthAtBase = 4,
        Foliage = ShapeStyle.HangingHemisphere,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.WillowLeaves,
    }; 
    private static LSystemTreeData Tallow() => new LSystemTreeData
    {
        Axiom = new FixedString64Bytes("F[+~FF][-~FFF][++~FFFF]"),
        Rule0 = new LSystemRule { Symbol = (byte)'A', Replacement = new FixedString64Bytes("~F[&B][+&B][-&B]A") },
        RuleCount = 0,
        Iterations = 0,

        StepLength = 0f,
        StepLengthVariance = 5f,
        PitchAngle = 38f,   // outward pitch on bough split
        YawAngle = 90f,  // 4-fold symmetry
        RollAngle = -35f,
        GravityFactor = 0.333f, // stronger droop so boughs arc downward at tips
        AngleVariance = 15f,   // high — irregular twisted character
        TrunkWidthAtBase = 2,
        Foliage = ShapeStyle.HangingHemisphere,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.WillowLeaves,
    };
    // Palm — tall straight trunk, crown burst of drooping fronds at the top.
    // No branching on the trunk — just F repeated to build height, then a
    // ring of fronds via [ ] each pitched out and allowed to droop via gravity.
    // Fronds use PalmFrond foliage which traces a drooping line rather than a blob.
    private static LSystemTreeData Palm() => new LSystemTreeData
    {
        // Just a straight trunk — T places the crown foliage explicitly at the top
        Axiom = new FixedString512Bytes("FFFFT"),
        RuleCount = 0,
        Iterations = 0,

        StepLength = 4f,
        PitchAngle = 0f,
        YawAngle = 0f,
        RollAngle = 0f,
        GravityFactor = 0f,
        AngleVariance = 3f,   // slight trunk lean
        StepLengthVariance = 3f,
        TrunkWidthAtBase = 1,
        Foliage = ShapeStyle.PalmCrown,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.PineLeaves,
        FoliageSize = 6
    };
    private static LSystemTreeData SmallPalm() => new LSystemTreeData
    {
        // Just a straight trunk — T places the crown foliage explicitly at the top
        Axiom = new FixedString512Bytes("FT"),
        RuleCount = 0,
        Iterations = 0,

        StepLength = 2f,
        PitchAngle = 0f,
        YawAngle = 0f,
        RollAngle = 0f,
        GravityFactor = 0f,
        AngleVariance = 3f,   // slight trunk lean
        StepLengthVariance = 0f,
        TrunkWidthAtBase = 2,
        Foliage = ShapeStyle.PalmCrown,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.PineLeaves,
        FoliageSize = 2
    };
    private static LSystemTreeData NightBlossom() => new LSystemTreeData
    {
        Axiom = new FixedString64Bytes("FA"),
        Rule0 = new LSystemRule { Symbol = (byte)'A', Replacement = new FixedString64Bytes("+[&FA]F+[&FA]+[&FA]!FA") },
        //Rule1 = new LSystemRule { Symbol = (byte)'B', Replacement = new FixedString64Bytes("~F[+&~F][-&~F]") },
        RuleCount = 1,
        Iterations = 4,

        StepLength = 10f,
        PitchAngle = 45f,   // outward pitch on bough split
        YawAngle = 120f,  // 3-fold symmetry
        RollAngle = -35f,
        GravityFactor = 0f, // stronger droop so boughs arc downward at tips
        AngleVariance = 10f,   // high — irregular twisted character
        StepLengthVariance = 0f,
        TrunkWidthAtBase = 7,
        Foliage = ShapeStyle.PalmCrown,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.PineLeaves,
        FoliageSize = 4
    };
}

// ----------------------------------------------------------------------------
// Interpreter  —  Burst-safe, no heap allocations
// ----------------------------------------------------------------------------
[BurstCompile]
public static class LSystemInterpreter
{
    private const int MaxStackDepth = 128;

    public static void Interpret(
        in LSystemTreeData tree,
        int originX, int originY, int originZ,
        ref Unity.Mathematics.Random rng,
        ref BlockWriter applyBlock)
    {
        // Dry-run pass: find max [ ] and ! nesting depth for width/foliage tapering,
        // without drawing anything or building any string.
        float curDepth = 0f, maxDepth = 0f;
        var depthStack = new NativeArray<float>(MaxStackDepth, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
        int depthStackTop = -1;
        CountDepth(in tree, tree.Axiom, 0, ref curDepth, ref maxDepth, ref depthStack, ref depthStackTop);
        depthStack.Dispose();

        var stack = new NativeArray<TurtleState>(MaxStackDepth, Allocator.Temp,
                           NativeArrayOptions.UninitializedMemory);
        int stackTop = -1;

        var state = new TurtleState
        {
            Position = new float3(originX, originY, originZ),
            Rotation = quaternion.RotateY(rng.NextFloat() * math.PI * 2f),
            StepLength = tree.StepLength + rng.NextFloat() * tree.StepLengthVariance,
            BranchDepth = 0,
            ManualDepth = 0f,
            SuppressFoliage = false,
        };

        var ctx = new WalkContext
        {
            PitchRad = math.radians(tree.PitchAngle),
            YawRad = math.radians(tree.YawAngle),
            RollRad = math.radians(tree.RollAngle),
            VarRad = math.radians(tree.AngleVariance),
            MaxDepth = (int)maxDepth,
        };

        var branchHasChild = new NativeArray<bool>(MaxStackDepth, Allocator.Temp, NativeArrayOptions.ClearMemory);

        WalkString(in tree, tree.Axiom, 0, ref state, ref stack, ref branchHasChild, ref stackTop, in ctx, ref rng, ref applyBlock);
        stack.Dispose();
        branchHasChild.Dispose();
    }

    private struct WalkContext
    {
        public float PitchRad, YawRad, RollRad, VarRad, CurveRad;
        public int MaxDepth;
    }

    // Returns true (and the replacement) if `sym` has a matching rule AND we're
    // still within the iteration budget at this recursion depth. False means
    // treat `sym` as terminal — execute it directly rather than expanding it.
    private static bool ExpandSymbol(in LSystemTreeData tree, byte sym, int iterDepth, out FixedString64Bytes replacement)
    {
        if (iterDepth < tree.Iterations)
        {
            if (tree.RuleCount > 0 && tree.Rule0.Symbol == sym) { replacement = tree.Rule0.Replacement; return true; }
            if (tree.RuleCount > 1 && tree.Rule1.Symbol == sym) { replacement = tree.Rule1.Replacement; return true; }
        }
        replacement = default;
        return false;
    }

    // Walks the grammar recursively, substituting rule symbols with their
    // replacements up to tree.Iterations deep, and executing terminal symbols
    // as turtle commands — functionally identical to expanding the full string
    // first, but with recursion depth bounded by Iterations instead of an
    // exponentially-growing buffer.
    private static void WalkString<T>(
    in LSystemTreeData tree, in T symbols, int iterDepth,
    ref TurtleState state, ref NativeArray<TurtleState> stack, ref NativeArray<bool> branchHasChild, ref int stackTop,
    in WalkContext ctx, ref Unity.Mathematics.Random rng, ref BlockWriter applyBlock)
    where T : struct, INativeList<byte>, IUTF8Bytes
    {
        for (int i = 0; i < symbols.Length; i++)
        {
            byte sym = symbols[i];
            if (ExpandSymbol(in tree, sym, iterDepth, out var replacement))
            {
                // Every bracket currently open contains this expansion transitively —
                // none of them are genuine leaf tips anymore.
                for (int lvl = 0; lvl <= stackTop; lvl++)
                    branchHasChild[lvl] = true;

                WalkString(in tree, replacement, iterDepth + 1, ref state, ref stack, ref branchHasChild, ref stackTop, in ctx, ref rng, ref applyBlock);
            }
            else
            {
                ExecuteSymbol(in tree, (char)sym, ref state, ref stack,ref stackTop, in ctx, ref rng, ref branchHasChild, ref applyBlock);
            }
        }
    }

    private static void ExecuteSymbol(
        in LSystemTreeData tree, char sym,
        ref TurtleState state, ref NativeArray<TurtleState> stack, ref int stackTop,
        in WalkContext ctx, ref Unity.Mathematics.Random rng, ref NativeArray<bool> branchHasChild, ref BlockWriter applyBlock)
    {
        switch (sym)
        {
            case 'F':
                {
                    float3 dir = math.mul(state.Rotation, new float3(0f, 1f, 0f));
                    if (tree.GravityFactor > 0f)
                        dir = math.normalize(dir - new float3(0f, tree.GravityFactor, 0f));

                    float taperedLength;
                    int width;

                    if (tree.TaperCurve == TaperCurveType.None)
                    {
                        taperedLength = state.StepLength;
                        width = tree.TrunkWidthAtBase;
                    }
                    else
                    {
                        int effectiveDepth = state.BranchDepth + (int)state.ManualDepth;
                        float rawT = ctx.MaxDepth > 0 ? math.saturate((float)effectiveDepth / ctx.MaxDepth) : 0f;
                        float curvedT = ApplyCurve(rawT, tree.TaperCurve);

                        taperedLength = math.lerp(state.StepLength, tree.MinStepLength, curvedT);
                        width = math.max(1, (int)math.round(math.lerp(tree.TrunkWidthAtBase, tree.MinWidth, curvedT)));
                    }

                    float3 end = state.Position + dir * taperedLength;
                    DrawLogSegment(state.Position, end, width, tree.BodyBlock, ref applyBlock);
                    state.Position = end;
                    break;
                }

            case 'f':
                {
                    float3 dir = math.mul(state.Rotation, new float3(0f, 1f, 0f));
                    state.Position += dir * state.StepLength;
                    break;
                }

            case '+': state.Rotation = math.mul(state.Rotation, quaternion.RotateY(ctx.YawRad + Variance(ref rng, ctx.VarRad))); break;
            case '-': state.Rotation = math.mul(state.Rotation, quaternion.RotateY(-ctx.YawRad + Variance(ref rng, ctx.VarRad))); break;
            case '&': state.Rotation = math.mul(state.Rotation, quaternion.RotateZ(ctx.PitchRad + Variance(ref rng, ctx.VarRad))); break;
            case '^': state.Rotation = math.mul(state.Rotation, quaternion.RotateZ(-ctx.PitchRad + Variance(ref rng, ctx.VarRad))); break;
            case '\\': state.Rotation = math.mul(state.Rotation, quaternion.RotateX(ctx.RollRad + Variance(ref rng, ctx.VarRad))); break;
            case '/': state.Rotation = math.mul(state.Rotation, quaternion.RotateX(-ctx.RollRad + Variance(ref rng, ctx.VarRad))); break;
            case '|': state.Rotation = math.mul(state.Rotation, quaternion.RotateY(math.PI)); break;
            case '~': state.Rotation = math.mul(state.Rotation, quaternion.RotateZ(ctx.CurveRad + Variance(ref rng, ctx.VarRad * 0.5f))); break;

            case '[':
                if (stackTop < MaxStackDepth - 1)
                {
                    stack[++stackTop] = state;
                    branchHasChild[stackTop] = false; // assume leaf until proven otherwise
                    state.BranchDepth++;
                }
                break;

            case ']':
                if ((stackTop < 0 || !branchHasChild[stackTop]) && !state.SuppressFoliage)
                {
                    PlaceFoliageAt(tree.Foliage, tree.FoliageBlock, tree.FoliageSize, state.Position, state.BranchDepth, ctx.MaxDepth, ref rng, ref applyBlock);
                }
                if (stackTop >= 0)
                    state = stack[stackTop--];
                break;

            case '!':
                state.ManualDepth += 1;
                break;

            case 'T':
                PlaceFoliageAt(tree.Foliage, tree.FoliageBlock, tree.FoliageSize, state.Position, state.BranchDepth, ctx.MaxDepth, ref rng, ref applyBlock);
                break;

            case 'Q':
                state.SuppressFoliage = true;
                break;
        }
    }
    private static float ApplyCurve(float t, TaperCurveType curve)
    {
        switch (curve)
        {
            case TaperCurveType.Square: return t * t;
            case TaperCurveType.SquareRoot: return math.sqrt(t);
            case TaperCurveType.Logarithmic: return math.log10(1f + 9f * t); // 0?0, 1?1
            case TaperCurveType.Linear:
            default: return t;
        }
    }
    private static float Variance(ref Unity.Mathematics.Random rng, float maxRad)
        => maxRad <= 0f ? 0f : (rng.NextFloat() * 2f - 1f) * maxRad;

// Helpers
private static void PlaceFoliageAt(
        ShapeStyle style, BlockType foliageBlock, int size, float3 pos, int branchDepth, int maxDepth,
        ref Unity.Mathematics.Random rng, ref BlockWriter applyBlock)
    {
        var ip = new int3((int)math.round(pos.x), (int)math.round(pos.y), (int)math.round(pos.z));
        ShapePlacer.Place(style, foliageBlock, size, ip, branchDepth, maxDepth, ref rng, ref applyBlock);
    }

    private static void CountDepth<T>(
    in LSystemTreeData tree, in T symbols, int iterDepth,
    ref float curDepth, ref float maxDepth,
    ref NativeArray<float> depthStack, ref int depthStackTop)
    where T : struct, INativeList<byte>, IUTF8Bytes
    {
        for (int i = 0; i < symbols.Length; i++)
        {
            byte sym = symbols[i];
            if (ExpandSymbol(in tree, sym, iterDepth, out var replacement))
            {
                CountDepth(in tree, replacement, iterDepth + 1, ref curDepth, ref maxDepth, ref depthStack, ref depthStackTop);
            }
            else if (sym == (byte)'[')
            {
                if (depthStackTop < depthStack.Length - 1)
                    depthStack[++depthStackTop] = curDepth;   // snapshot — mirrors real state push
                curDepth += 1f;
                if (curDepth > maxDepth) maxDepth = curDepth;
            }
            else if (sym == (byte)']')
            {
                if (depthStackTop >= 0)
                    curDepth = depthStack[depthStackTop--];    // full restore — mirrors real state pop
            }
            else if (sym == (byte)'!')
            {
                curDepth += 1f;
                if (curDepth > maxDepth) maxDepth = curDepth;
            }
        }
    }

    private static void DrawLogSegment(float3 start, float3 end, int width, BlockType bodyBlock, ref BlockWriter applyBlock)
    {
        float3 dir = end - start;
        float len = math.length(dir);
        if (len < 0.001f) return;

        float3 axis = dir / len;
        float3 worldUp = math.abs(axis.y) < 0.99f ? new float3(0f, 1f, 0f) : new float3(1f, 0f, 0f);
        float3 right = math.normalize(math.cross(worldUp, axis));
        float3 up = math.cross(axis, right);

        int steps = (int)math.ceil(len) + 1;
        for (int i = 0; i <= steps; i++)
        {
            float t = steps > 0 ? i / (float)steps : 0f;
            float3 p = math.lerp(start, end, t);
            PlaceRoundSlice(p, right, up, width, bodyBlock, ref applyBlock);
        }
    }

    // Fills a circular cross-section centred on world position p,
    // lying in the plane defined by right and up (both perpendicular to the branch).
    // width = 1 single block.
    // width >= 2 circle of radius (width-1)/2 in the branch-perpendicular plane.
    private static void PlaceRoundSlice(
        float3 p, float3 right, float3 up, int width,
        BlockType bodyBlock, ref BlockWriter applyBlock)
    {
        if (width <= 1)
        {
            applyBlock.ApplyBlock((int)math.round(p.x), (int)math.round(p.y), (int)math.round(p.z), bodyBlock);
            return;
        }

        if (width == 2)
        {
            // 2x2 disc in the branch-perpendicular plane
            for (int dr = 0; dr <= 1; dr++)
                for (int du = 0; du <= 1; du++)
                {
                    float3 offset = right * dr + up * du;
                    applyBlock.ApplyBlock(
                        (int)math.round(p.x + offset.x),
                        (int)math.round(p.y + offset.y),
                        (int)math.round(p.z + offset.z),
                        bodyBlock);
                }
            return;
        }

        float r = (width - 1) * 0.5f;
        float rSq = r * r;
        int ext = (int)math.ceil(r);

        for (int dr = -ext; dr <= ext; dr++)
            for (int du = -ext; du <= ext; du++)
            {
                if ((float)dr * dr + (float)du * du > rSq + 0.01f) continue;
                float3 offset = right * dr + up * du;
                applyBlock.ApplyBlock(
                    (int)math.round(p.x + offset.x),
                    (int)math.round(p.y + offset.y),
                    (int)math.round(p.z + offset.z),
                    bodyBlock);
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