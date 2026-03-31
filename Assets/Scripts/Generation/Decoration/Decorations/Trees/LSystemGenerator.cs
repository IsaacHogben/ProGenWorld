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
    // Add your styles here, e.g.:
    // Pine  = 2,
    // Round = 3,
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
        }
    }

    // Hemisphere of leaves above the tip, with randomised downward-hanging strands.
    private static void PlaceHangingHemisphere(int3 p, BlockType foliageBlock, ref Unity.Mathematics.Random rng, ApplyBlockDelegate ab)
    {
        int x = p.x, y = p.y, z = p.z;
        const int R = 5;

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

    // private static void PlacePine(int3 p, ref Unity.Mathematics.Random rng, ApplyBlockDelegate ab)
    // {
    //     int x = p.x, y = p.y, z = p.z;
    //     ab(x + 1, y, z, BlockType.Leaves);
    //     ab(x - 1, y, z, BlockType.Leaves);
    //     ab(x, y, z + 1, BlockType.Leaves);
    //     ab(x, y, z - 1, BlockType.Leaves);
    //     ab(x, y + 1, z, foliageBlock);
    // }
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
        FoliageBlock = BlockType.Leaves,
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
        FoliageBlock = BlockType.Leaves,
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
            StepLength = tree.StepLength,
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
                    PlaceFoliageAt(tree.Foliage, tree.FoliageBlock, state.Position, state.BranchDepth, maxDepth, ref rng, applyBlock);
                    if (stackTop >= 0)
                        state = stack[stackTop--];
                    break;

                case '!':
                    state.StepLength *= tree.StepLengthScalar;
                    break;

                case 'T':
                    PlaceFoliageAt(tree.Foliage, tree.FoliageBlock, state.Position, state.BranchDepth, maxDepth, ref rng, applyBlock);
                    break;
            }
        }

        PlaceFoliageAt(tree.Foliage, tree.FoliageBlock, state.Position, 0, maxDepth, ref rng, applyBlock);
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

    // ?? Helpers ??????????????????????????????????????????????????????????

    private static void PlaceFoliageAt(
        FoliageStyle style, BlockType foliageBlock, float3 pos, int branchDepth, int maxDepth,
        ref Unity.Mathematics.Random rng, ApplyBlockDelegate applyBlock)
    {
        var ip = new int3((int)math.round(pos.x), (int)math.round(pos.y), (int)math.round(pos.z));
        FoliagePlacer.Place(style, foliageBlock, ip, branchDepth, maxDepth, ref rng, applyBlock);
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
        float depthRatio = maxDepth > 0 ? (float)branchDepth / maxDepth : 1f;
        int width = math.max(1, baseTrunkWidth - (int)math.round(depthRatio * (baseTrunkWidth - 1)));

        int steps = (int)math.ceil(math.length(end - start)) + 1;
        for (int i = 0; i <= steps; i++)
        {
            float t = steps > 0 ? i / (float)steps : 0f;
            float3 p = math.lerp(start, end, t);
            int bx = (int)math.round(p.x);
            int by = (int)math.round(p.y);
            int bz = (int)math.round(p.z);
            PlaceRoundSlice(bx, by, bz, width, bodyBlock, applyBlock);
        }
    }

    // Fills a circular cross-section centred on (cx, cy, cz).
    // width=1 ? single block.
    // width=2 ? 2x2 (no corners to clip at this scale).
    // width>=3 ? proper circle: each block is included only if its centre
    //            lies within the circle, clipping the four square corners.
    //
    // Block centres are at integer offsets from cx/cz.
    // For even widths the circle centre sits between blocks (offset 0.5);
    // for odd widths it sits on a block centre (offset 0).
    private static void PlaceRoundSlice(
        int cx, int cy, int cz, int width,
        BlockType bodyBlock, ApplyBlockDelegate applyBlock)
    {
        if (width <= 1)
        {
            applyBlock(cx, cy, cz, bodyBlock);
            return;
        }
        if (width == 2)
        {
            applyBlock(cx, cy, cz, bodyBlock);
            applyBlock(cx + 1, cy, cz, bodyBlock);
            applyBlock(cx, cy, cz + 1, bodyBlock);
            applyBlock(cx + 1, cy, cz + 1, bodyBlock);
            return;
        }

        // For width >= 3: circle radius is (width-1)/2 so it fits snugly inside
        // the width×width bounding box with corners clipped.
        float r = (width - 1) * 0.5f;
        float rSq = r * r;
        int ext = (int)math.ceil(r);

        for (int dx = -ext; dx <= ext; dx++)
            for (int dz = -ext; dz <= ext; dz++)
                if ((float)dx * dx + (float)dz * dz <= rSq + 0.01f)
                    applyBlock(cx + dx, cy, cz + dz, bodyBlock);
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