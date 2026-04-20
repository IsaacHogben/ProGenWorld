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
//
// WORKFLOW
// --------
//   Set Axiom, Rules, and Iterations in LSystemTreeData.
//   The interpreter expands the axiom at runtime before walking it.
//   Once happy with a tree, freeze Iterations at its final value.
// ============================================================================


// ----------------------------------------------------------------------------
// Foliage style enum
// ----------------------------------------------------------------------------
public enum FoliageStyle : byte
{
    None = 0,
    HangingHemisphere = 1,
    Cluster = 2,
    PalmCrown = 3
}

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
    public float StepLengthVariance; // max random addition to StepLength per spawn
    public float PitchAngle;
    public float YawAngle;
    public float RollAngle;
    public float StepLengthScalar;
    public float GravityFactor;
    public float AngleVariance;
    public float CurvatureAngle;    // degrees applied by ~ (gentle per-step bend)

    // Visual
    public int TrunkWidthAtBase;
    public FoliageStyle Foliage;
    public BlockType BodyBlock;    // block used for trunk and branches
    public BlockType FoliageBlock; // block used for all leaf placements
    public int FoliageSize;        // used to set foliage size of some tree types 
}

// ----------------------------------------------------------------------------
// Foliage placer  — Burst-safe)
// ----------------------------------------------------------------------------
[BurstCompile]
public static class FoliagePlacer
{
    [BurstCompile]
    public static void Place(
        FoliageStyle style,
        BlockType foliageBlock,
        int size,
        int3 pos,
        int branchDepth,
        int maxDepth,
        ref Unity.Mathematics.Random rng,
        ApplyBlockDelegate applyBlock)
    {
        switch (style)
        {
            case FoliageStyle.HangingHemisphere: PlaceHangingHemisphere(pos, foliageBlock, ref rng, applyBlock); break;
            case FoliageStyle.Cluster: PlaceCluster(pos, foliageBlock, ref rng, applyBlock); break;
            case FoliageStyle.PalmCrown: PlacePalmCrown(size, pos, foliageBlock, ref rng, applyBlock); break;
        }
    }

    // Hemisphere of leaves above the tip, with randomised downward-hanging strands.
    private static void PlaceHangingHemisphere(int3 p, BlockType foliageBlock, ref Unity.Mathematics.Random rng, ApplyBlockDelegate ab)
    {
        int x = p.x, y = p.y, z = p.z;
        const int R = 3;

        // Upper hemisphere (dy >= 0)
        for (int dx = -R; dx <= R; dx++)
            for (int dy = 0; dy <= R; dy++)
                for (int dz = -R; dz <= R; dz++)
                {
                    float dist = math.sqrt(dx * dx + dy * dy + dz * dz);
                    if (dist <= R + 0.5f && rng.NextFloat() > 0.15f)
                        ab(x + dx, y + dy, z + dz, foliageBlock);
                }

        // Hanging strands — random lengths dropping below the tip
        for (int dx = -R; dx <= R; dx++)
            for (int dz = -R; dz <= R; dz++)
            {
                // Only hang from the outer edge of the hemisphere base
                float edgeDist = math.sqrt(dx * dx + dz * dz);
                if (edgeDist > R - 0.5f && edgeDist <= R + 0.5f && rng.NextFloat() > 0.4f)
                {
                    int hangLen = rng.NextInt(1, 5);
                    for (int dy = -1; dy >= -hangLen; dy--)
                        ab(x + dx, y + dy, z + dz, foliageBlock);
                }
            }
    }

    // Sparse wispy cluster for Japanese maple tips — a few leaves spread wide and thin.
    // Radius is small so many clusters together build the canopy rather than one big blob.
    private static void PlaceCluster(int3 p, BlockType foliageBlock, ref Unity.Mathematics.Random rng, ApplyBlockDelegate ab)
    {
        int x = p.x, y = p.y, z = p.z;

        // Core — small filled ball radius 2 at the tip
        for (int dx = -2; dx <= 2; dx++)
            for (int dy = -1; dy <= 2; dy++)
                for (int dz = -2; dz <= 2; dz++)
                    if (dx * dx + dy * dy + dz * dz <= 5 && rng.NextFloat() > 0.2f)
                        ab(x + dx, y + dy, z + dz, foliageBlock);

        // Sparse radiating fingers — 5-8 directions, each 2-6 blocks long
        int arms = rng.NextInt(5, 9);
        for (int a = 0; a < arms; a++)
        {
            float angle = (a / (float)arms) * math.PI * 2f + rng.NextFloat() * 0.7f;
            int len = rng.NextInt(2, 7);
            int armDx = (int)math.round(math.cos(angle) * len);
            int armDz = (int)math.round(math.sin(angle) * len);
            int armDy = rng.NextInt(-1, 3); // slight vertical scatter

            // Fill every block along the arm — no gaps so no floating leaves
            for (int i = 1; i <= len; i++)
            {
                float t = i / (float)len;
                int bx = x + (int)math.round(armDx * t);
                int bz = z + (int)math.round(armDz * t);
                int by = y + armDy;
                ab(bx, by, bz, foliageBlock);
                // Optional second layer on the lower half of each arm for volume
                if (t < 0.5f && rng.NextFloat() > 0.4f)
                    ab(bx, by + 1, bz, foliageBlock);
            }
        }
    }

    private static void PlacePalmCrown(int size, int3 p, BlockType foliageBlock,
    ref Unity.Mathematics.Random rng, ApplyBlockDelegate ab)
    {
        int x = p.x, y = p.y, z = p.z;

        // Tight central tuft
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = 0; dy <= 4; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    if (rng.NextFloat() > 0.1f)
                        ab(x + dx, y + dy, z + dz, foliageBlock);

        int numFronds = rng.NextInt(size + 3, size + 10);
        for (int f = 0; f < numFronds; f++)
        {
            float angle = (f / (float)numFronds) * math.PI * 2f + rng.NextFloat() * 0.3f;
            float cosA = math.cos(angle);
            float sinA = math.sin(angle);
            int frondLen = rng.NextInt(size, size + 5);

            // Starting direction from pitch angle
            float frondPitch = rng.NextFloat() * math.PI * 0.5f + math.PI * 0.25f;

            // Initial step direction vector
            float3 dir = new float3(cosA * math.sin(frondPitch),
                                    -math.cos(frondPitch),
                                    sinA * math.sin(frondPitch));

            float perpCos = math.cos(angle + math.PI * 0.5f);
            float perpSin = math.sin(angle + math.PI * 0.5f);

            float3 pos = new float3(x, y + 1, z);

            for (int i = 1; i <= frondLen; i++)
            {
                float t = i / (float)frondLen;

                // Bend direction downward each step — same curve regardless of starting angle
                dir.y -= 0.18f;
                dir = math.normalize(dir);

                pos += dir;

                int fx = (int)math.round(pos.x);
                int fy = (int)math.round(pos.y);
                int fz = (int)math.round(pos.z);

                ab(fx, fy, fz, foliageBlock);
                ab(fx, fy + 1, fz, foliageBlock);

                float leafWidth = 1.5f * math.sin(t * math.PI);
                int leafExt = (int)math.round(leafWidth);
                for (int l = -leafExt; l <= leafExt; l++)
                {
                    int lx = fx + (int)math.round(perpCos * l);
                    int lz = fz + (int)math.round(perpSin * l);
                    ab(lx, fy, lz, foliageBlock);
                    if (t < 0.7f && rng.NextFloat() > 0.2f)
                        ab(lx, fy + 1, lz, foliageBlock);
                }

                if (t > 0.5f && rng.NextFloat() > 0.5f)
                    ab(fx, fy - 1, fz, foliageBlock);
            }
        }
    }
}

// ----------------------------------------------------------------------------
// Ruleset factory
// ----------------------------------------------------------------------------
public static class TreeRulesets
{
    public static LSystemTreeData Get(DecorationType.Tree treeType)
    {
        switch (treeType)
        {
            case DecorationType.Tree.SmallPine: return Temp();
            case DecorationType.Tree.RedFancy: return RedFancy();
            case DecorationType.Tree.Willow: return Willow();
            case DecorationType.Tree.Tallow: return Tallow();
            case DecorationType.Tree.Palm: return Palm();
            case DecorationType.Tree.SmallPalm: return SmallPalm();

            default:
                Debug.LogWarning($"[LSystemTreeGenerator] No ruleset for {treeType}.");
                return default;
        }
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
        StepLengthScalar = 0.65f,
        GravityFactor = 0f,
        AngleVariance = 8f,
        CurvatureAngle = 0f,
        TrunkWidthAtBase = 1,
        Foliage = FoliageStyle.None,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.PineLeaves,
    };


    // Axiom encodes the trunk split manually so the two main boughs diverge strongly.
    // Rule A: main bough segments — subdivide and spread horizontally.
    // Rule B: secondary branches — shorter, more upward, terminate quickly.
    //
    //   Trunk rises 2 steps then immediately splits into two boughs via [ ] brackets.
    //   Each bough pitches outward (&) then yaws for asymmetry (+/-).
    //   B branches off A for the finer canopy layer.
    //
    // Trunk rises (FF) then splits into 3 major boughs. Each bough is pre-yawed to a
    // compass direction (+, -, ++) THEN pitched strongly outward (&) so it actually
    // travels away from the trunk. Sub-branches (B) yaw to a side then pitch up (^)
    // for the upward-curving tip silhouette visible in the reference.
    // 
    // Curvature comes from ~ applied per step: each generation the branch accumulates
    // a small additional bend, giving the arc visible in the reference image.
    //
    // Trunk: F~F~F — three steps with a bend between each, so it leans slightly.
    // Split into 3 boughs (0°, 120°, 240° yaw) each pitched strongly outward.
    // A: main bough — two curved steps, then sprout sub-branches, recurse with ~
    //    so each iteration adds more arc to the bough.
    // B: tip — short curved arm terminating in foliage.
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
        StepLengthScalar = 0.58f,
        GravityFactor = 0.12f, // stronger droop so boughs arc downward at tips
        AngleVariance = 20f,   // high — irregular twisted character
        CurvatureAngle = 10f,   // per-~ bend; accumulates across iterations for arc
        TrunkWidthAtBase = 4,
        Foliage = FoliageStyle.Cluster,
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
        StepLengthScalar = 0.8333333f,
        GravityFactor = 0.333f, // stronger droop so boughs arc downward at tips
        AngleVariance = 25f,   // high — irregular twisted character
        CurvatureAngle = 10f,   // per-~ bend; accumulates across iterations for arc
        TrunkWidthAtBase = 4,
        Foliage = FoliageStyle.HangingHemisphere,
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
        StepLengthScalar = 1f,
        GravityFactor = 0.333f, // stronger droop so boughs arc downward at tips
        AngleVariance = 15f,   // high — irregular twisted character
        CurvatureAngle = 10f,   // per-~ bend; accumulates across iterations for arc
        TrunkWidthAtBase = 2,
        Foliage = FoliageStyle.HangingHemisphere,
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
        StepLengthScalar = 1f,
        GravityFactor = 0f,
        AngleVariance = 3f,   // slight trunk lean
        CurvatureAngle = 2f,   // very gentle curve so trunk isn't perfectly straight
        StepLengthVariance = 3f,
        TrunkWidthAtBase = 1,
        Foliage = FoliageStyle.PalmCrown,
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
        StepLengthScalar = 1f,
        GravityFactor = 0f,
        AngleVariance = 3f,   // slight trunk lean
        CurvatureAngle = 2f,   // very gentle curve so trunk isn't perfectly straight
        StepLengthVariance = 0f,
        TrunkWidthAtBase = 2,
        Foliage = FoliageStyle.PalmCrown,
        BodyBlock = BlockType.Log,
        FoliageBlock = BlockType.PineLeaves,
        FoliageSize = 2
    };
}

// ----------------------------------------------------------------------------
// Interpreter  —  Burst-safe, no heap allocations
// ----------------------------------------------------------------------------
[BurstCompile]
public static class LSystemInterpreter
{
    private const int MaxStackDepth = 128;
    private const int MaxStringBytes = 509;  // FixedString512Bytes actual capacity (UTF8 header uses 3 bytes)

    [BurstCompile]
    public static void Interpret(
        in LSystemTreeData tree,
        int originX, int originY, int originZ,
        ref Unity.Mathematics.Random rng,
        ApplyBlockDelegate applyBlock)
    {
        // Expand axiom ? final L-string
        FixedString512Bytes lString = default;
        Expand(in tree, ref lString);

        // Turtle stack
        var stack = new NativeArray<TurtleState>(MaxStackDepth, Allocator.Temp,
                           NativeArrayOptions.UninitializedMemory);
        int stackTop = -1;

        var state = new TurtleState
        {
            Position = new float3(originX, originY, originZ),
            Rotation = quaternion.RotateY(rng.NextFloat() * math.PI * 2f),
            StepLength = tree.StepLength + rng.NextFloat() * tree.StepLengthVariance,
            BranchDepth = 0,
        };

        float pitchRad = math.radians(tree.PitchAngle);
        float yawRad = math.radians(tree.YawAngle);
        float rollRad = math.radians(tree.RollAngle);
        float varRad = math.radians(tree.AngleVariance);
        float curveRad = math.radians(tree.CurvatureAngle);
        int maxDepth = CountMaxDepth(in lString);

        for (int i = 0; i < lString.Length; i++)
        {
            char sym = (char)lString[i];
            switch (sym)
            {
                case 'F':
                    {
                        float3 dir = math.mul(state.Rotation, new float3(0f, 1f, 0f));
                        if (tree.GravityFactor > 0f)
                            dir = math.normalize(dir - new float3(0f, tree.GravityFactor, 0f));
                        float3 end = state.Position + dir * state.StepLength;
                        DrawLogSegment(state.Position, end,
                            tree.TrunkWidthAtBase, state.BranchDepth, maxDepth,
                            tree.BodyBlock, applyBlock);
                        state.Position = end;
                        break;
                    }

                case 'f':
                    {
                        float3 dir = math.mul(state.Rotation, new float3(0f, 1f, 0f));
                        state.Position += dir * state.StepLength;
                        break;
                    }

                case '+': state.Rotation = math.mul(state.Rotation, quaternion.RotateY(yawRad + Variance(ref rng, varRad))); break;
                case '-': state.Rotation = math.mul(state.Rotation, quaternion.RotateY(-yawRad + Variance(ref rng, varRad))); break;
                // Unity Y-up: pitch tilts the branch into XZ (horizontal spread) ? RotateZ
                case '&': state.Rotation = math.mul(state.Rotation, quaternion.RotateZ(pitchRad + Variance(ref rng, varRad))); break;
                case '^': state.Rotation = math.mul(state.Rotation, quaternion.RotateZ(-pitchRad + Variance(ref rng, varRad))); break;
                // Roll twists around the branch's own axis ? RotateX
                case '\\': state.Rotation = math.mul(state.Rotation, quaternion.RotateX(rollRad + Variance(ref rng, varRad))); break;
                case '/': state.Rotation = math.mul(state.Rotation, quaternion.RotateX(-rollRad + Variance(ref rng, varRad))); break;
                case '|': state.Rotation = math.mul(state.Rotation, quaternion.RotateY(math.PI)); break;
                // Gentle incremental bend — repeat in rules to curve a branch over multiple steps
                case '~': state.Rotation = math.mul(state.Rotation, quaternion.RotateZ(curveRad + Variance(ref rng, varRad * 0.5f))); break;

                case '[':
                    if (stackTop < MaxStackDepth - 1)
                    {
                        stack[++stackTop] = state;
                        state.BranchDepth++;
                    }
                    break;

                case ']':
                    PlaceFoliageAt(tree.Foliage, tree.FoliageBlock, tree.FoliageSize, state.Position, state.BranchDepth, maxDepth, ref rng, applyBlock);
                    if (stackTop >= 0)
                        state = stack[stackTop--];
                    break;

                case '!':
                    state.StepLength *= tree.StepLengthScalar;
                    break;

                case 'T':
                    PlaceFoliageAt(tree.Foliage, tree.FoliageBlock, tree.FoliageSize, state.Position, state.BranchDepth, maxDepth, ref rng, applyBlock);
                    break;
            }
        }

        stack.Dispose();
    }

    // Expands tree.Axiom according to tree.Rule0..N for tree.Iterations passes.
    private static void Expand(in LSystemTreeData tree, ref FixedString512Bytes result)
    {
        result = new FixedString512Bytes();
        result.Append(tree.Axiom);

        var buffer = new FixedString512Bytes();

        for (int iter = 0; iter < tree.Iterations; iter++)
        {
            buffer = new FixedString512Bytes();

            for (int i = 0; i < result.Length; i++)
            {
                byte sym = result[i];
                bool matched = false;

                // Check each active rule — guard capacity BEFORE appending
                if (tree.RuleCount > 0 && tree.Rule0.Symbol == sym)
                {
                    if (buffer.Length + tree.Rule0.Replacement.Length > MaxStringBytes) break;
                    buffer.Append(tree.Rule0.Replacement);
                    matched = true;
                }
                // Add more rules here as needed:
                else if (tree.RuleCount > 1 && tree.Rule1.Symbol == sym)
                {
                    if (buffer.Length + tree.Rule1.Replacement.Length > MaxStringBytes) break;
                    buffer.Append(tree.Rule1.Replacement);
                    matched = true;
                }

                if (!matched)
                {
                    if (buffer.Length >= MaxStringBytes) break;
                    buffer.Add(sym);
                }
            }

            result = buffer;
        }
    }

    // Helpers

    private static void PlaceFoliageAt(
        FoliageStyle style, BlockType foliageBlock, int size, float3 pos, int branchDepth, int maxDepth,
        ref Unity.Mathematics.Random rng, ApplyBlockDelegate applyBlock)
    {
        var ip = new int3((int)math.round(pos.x), (int)math.round(pos.y), (int)math.round(pos.z));
        FoliagePlacer.Place(style, foliageBlock, size, ip, branchDepth, maxDepth, ref rng, applyBlock);
    }

    private static float Variance(ref Unity.Mathematics.Random rng, float maxRad)
        => maxRad <= 0f ? 0f : (rng.NextFloat() * 2f - 1f) * maxRad;

    private static int CountMaxDepth(in FixedString512Bytes s)
    {
        int depth = 0, max = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = (char)s[i];
            if (c == '[') { if (++depth > max) max = depth; }
            else if (c == ']') depth--;
        }
        return max;
    }

    private static void DrawLogSegment(
       float3 start, float3 end,
       int baseTrunkWidth, int branchDepth, int maxDepth,
       BlockType bodyBlock,
       ApplyBlockDelegate applyBlock)
    {
        // For deeper trees, width reduces toward tips but never below 1.
        float depthRatio = maxDepth > 0 ? (float)branchDepth / maxDepth : 0f;
        int width = math.max(1, baseTrunkWidth - (int)math.round(depthRatio * (baseTrunkWidth - 1)));

        float3 dir = end - start;
        float len = math.length(dir);
        if (len < 0.001f) return;
        float3 axis = dir / len;  // normalised branch direction

        // Build two axes perpendicular to the branch direction.
        // We pick an arbitrary up vector and cross to get the disc plane.
        float3 worldUp = math.abs(axis.y) < 0.99f ? new float3(0f, 1f, 0f) : new float3(1f, 0f, 0f);
        float3 right = math.normalize(math.cross(worldUp, axis));
        float3 up = math.cross(axis, right);

        int steps = (int)math.ceil(len) + 1;
        for (int i = 0; i <= steps; i++)
        {
            float t = steps > 0 ? i / (float)steps : 0f;
            float3 p = math.lerp(start, end, t);
            PlaceRoundSlice(p, right, up, width, bodyBlock, applyBlock);
        }
    }

    // Fills a circular cross-section centred on world position p,
    // lying in the plane defined by right and up (both perpendicular to the branch).
    // width = 1 single block.
    // width >= 2 circle of radius (width-1)/2 in the branch-perpendicular plane.
    private static void PlaceRoundSlice(
        float3 p, float3 right, float3 up, int width,
        BlockType bodyBlock, ApplyBlockDelegate applyBlock)
    {
        if (width <= 1)
        {
            applyBlock((int)math.round(p.x), (int)math.round(p.y), (int)math.round(p.z), bodyBlock);
            return;
        }

        if (width == 2)
        {
            // 2x2 disc in the branch-perpendicular plane
            for (int dr = 0; dr <= 1; dr++)
                for (int du = 0; du <= 1; du++)
                {
                    float3 offset = right * dr + up * du;
                    applyBlock(
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
                applyBlock(
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
    [BurstCompile]
    public static void Generate(
        DecorationType.Tree treeType,
        int x, int y, int z,
        ref Unity.Mathematics.Random rng,
        ref BlockWriter writer)
    {
        LSystemTreeData tree = TreeRulesets.Get(treeType);
        LSystemInterpreter.Interpret(in tree, x, y, z, ref rng, writer.ApplyBlock);
    }
}