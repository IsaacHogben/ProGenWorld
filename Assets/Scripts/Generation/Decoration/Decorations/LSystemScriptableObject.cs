using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

// ----------------------------------------------------------------------------
// One production rule. Several rules for the same Symbol are chosen between
// randomly, in proportion to Weight, each time the symbol expands.
// ----------------------------------------------------------------------------
[System.Serializable]
public class LSystemRuleSO
{
    [Tooltip("The single character this rule replaces (e.g. 'A', 'F').")]
    public char Symbol;

    [Tooltip("What Symbol expands to.")]
    [TextArea(1, 3)]
    public string Replacement;

    [Tooltip("Relative chance of this rule when several rules share a Symbol.")]
    [Min(0f)]
    public float Weight = 1f;
}

// ----------------------------------------------------------------------------
// Authoring asset for an L-system tree. ToTreeData() compiles it into the
// Burst-safe runtime struct. See LSystemGenerator.cs for the symbol reference.
// ----------------------------------------------------------------------------
[CreateAssetMenu(fileName = "NewLSystemTree", menuName = "Decoration Generation/LSystem Tree Data", order = 0)]
public class LSystemScriptableObject : ScriptableObject
{
    [Header("Grammar")]
    [Tooltip("Starting string. Symbols: F f + - & ^ \\ / ~ | [ ] ! * T Q, most take an optional (value).")]
    [TextArea(1, 4)]
    public string Axiom = "F";

    [Tooltip("Production rules. Add several with the same Symbol to pick between them randomly by Weight.")]
    public List<LSystemRuleSO> Rules = new List<LSystemRuleSO>();

    [Tooltip("Expansion depth. 0 means the Axiom is used as-is.")]
    [Min(0)]
    public int Iterations = 1;

    [Header("Detail By Size")]
    [Tooltip("Trees spawned larger than this scale gain extra iterations, so big trees get more branches and foliage clusters instead of just bigger ones.")]
    [Min(0.05f)] public float DetailStartScale = 1f;
    [Tooltip("Extra iterations per doubling of scale above DetailStartScale (fractions expand that share of branch tips). The trunk and limbs stay exactly as in the plain scaled tree; the extra levels branch on from the tips. 0 = off.")]
    [Min(0f)] public float IterationsPerDoubling = 0f;
    [Tooltip("Max Droop Angle for branches grown by the extra iterations, so added detail doesn't sag toward the ground. 90 = same as Max Droop Angle.")]
    [Range(0f, 90f)] public float DetailMaxDroopAngle = 90f;
    [Tooltip("Segment length of each extra level relative to the level before, so detail fills in around the tips rather than adding reach.")]
    [Range(0.1f, 1f)] public float DetailStepScale = 0.5f;

    [Header("Movement")]
    [Min(0f)] public float StepLength = 5f;
    [Min(0f)] public float MinStepLength = 1f;
    [Tooltip("Max random addition to StepLength per tree spawn.")]
    public float StepLengthVariance = 0f;

    [Header("Rotation")]
    [Tooltip("& ^")]
    public float PitchAngle = 30f;
    [Tooltip("+ -  (spin around the branch's own heading)")]
    public float YawAngle = 90f;
    [Tooltip("\\ /")]
    public float RollAngle = 0f;
    [Tooltip("~  Tilt per ~ symbol; repeat ~F to curve a branch.")]
    public float CurveAngle = 0f;
    [Tooltip("Random +/- jitter applied to every rotation command.")]
    public float AngleVariance = 0f;
    [Tooltip("Bend toward the ground after each F, strongest on horizontal branches. Builds up along a branch so limbs arc. Negative values bend upward.")]
    [FormerlySerializedAs("GravityFactor")]
    public float Gravity = 0f;
    [Tooltip("Branches never point further below horizontal than this. 90 = no limit.")]
    [Range(0f, 90f)] public float MaxDroopAngle = 90f;
    [Tooltip("Level off branches that would dip below the height of the tree's base, instead of growing into the ground.")]
    public bool KeepAboveGround = false;

    [Header("Wind")]
    [Tooltip("Bend toward the wind after each F, like Gravity but sideways. Builds up along branches for a windswept lean.")]
    public float WindStrength = 0f;
    [Tooltip("Direction the wind blows toward, in degrees around world up (0 = +X). The same for every tree, so a whole biome leans together.")]
    public float WindDirection = 0f;
    [Tooltip("Random +/- degrees added to WindDirection per tree.")]
    [Min(0f)] public float WindDirectionVariance = 0f;

    [Header("Body")]
    [Tooltip("Linear = straight interpolation\nSquare = t^2 - stays full longer, tapers hard near the tips\nSquareRoot = sqrt(t) - tapers early, lingers thin\nLogarithmic = very fast initial taper\nNone = no taper")]
    public TaperCurveType TaperCurve = TaperCurveType.Linear;
    [Tooltip("Depth = by branch nesting (steps at each branch)\nLength = by distance from the root (smooth cones)\nBoth = whichever is thinner (smooth limbs, thinner side branches)")]
    public TaperMode TaperBy = TaperMode.Both;
    [Min(0.5f)] public float TrunkWidthAtBase = 1f;
    [Min(0f)] public float MinWidth = 1f;
    public BlockType BodyBlock = BlockType.Log;
    [Tooltip("Branch cross-sections as width changes.\nMixed = every shape, smallest steps (1, 2x2, plus, 3x3, rounded 4x4...)\nWhole Blocks = 1, 2x2, 3x3, rounded 4x4...\nCircles = 1, plus, 3x3, circle... (original look)")]
    public BranchShapeMode BranchShape = BranchShapeMode.Mixed;

    [Header("Foliage")]
    public ShapeStyle Foliage = ShapeStyle.None;
    public BlockType FoliageBlock = BlockType.PineLeaves;
    [Tooltip("Foliage diameter in blocks (some styles, e.g. PalmCrown, use it differently). Rounded after scaling.")]
    [Min(1f)] public float FoliageSize = 5f;
    [Tooltip("Random +/- added to FoliageSize per placement. Rounded after scaling.")]
    [Min(0f)] public float FoliageSizeVariance = 0f;
    [Tooltip("Optional second foliage block mixed through the first.")]
    public BlockType FoliageBlock2 = BlockType.Air;
    [Range(0f, 1f)] public float FoliageBlock2Chance = 0f;

    [Header("Ground Scatter")]
    [Tooltip("Block dropped onto the ground beneath the foliage, e.g. fallen petals. Air = off.")]
    public BlockType GroundScatterBlock = BlockType.Air;
    [Tooltip("Average blocks dropped per foliage placement.")]
    [Min(0f)] public float GroundScatterAmount = 0f;

    [Header("Roots")]
    [Tooltip("Roots spreading from the trunk base. Each block settles onto the terrain, so roots follow the ground.")]
    [Min(0)] public int RootCount = 0;
    [Tooltip("Length of each root in blocks (randomly 60-100%).")]
    [Min(0f)] public float RootLength = 12f;
    [Tooltip("Width at the trunk end, tapering to 1.")]
    [Min(1f)] public float RootWidth = 3f;
    [Tooltip("Blocks stacked at the trunk end, tapering to 1 - makes roots flare up into the trunk.")]
    [Min(1)] public int RootHeight = 3;
    [Tooltip("Max change of direction per block, in degrees.")]
    [Min(0f)] public float RootWander = 20f;
    [Tooltip("Air = use BodyBlock.")]
    public BlockType RootBlock = BlockType.Air;

    // ------------------------------------------------------------------
    // Compiles the authored asset into the Burst-safe runtime struct.
    // ------------------------------------------------------------------
    public LSystemTreeData ToTreeData()
    {
        var data = ToTreeData(out string error);
        if (error != null)
            Debug.LogError($"[{name}] {error}", this);
        else if (LSystemCompiler.WorstCaseOps(in data, LSystemInterpreter.MaxExecutedOps + 1L) > LSystemInterpreter.MaxExecutedOps)
            Debug.LogWarning($"[{name}] Can expand past {LSystemInterpreter.MaxExecutedOps:N0} symbols and will be cut short. Lower Iterations.", this);
        return data;
    }

    public LSystemTreeData ToTreeData(out string error)
    {
        var data = new LSystemTreeData
        {
            Iterations = Iterations,
            DetailStartScale = DetailStartScale,
            IterationsPerDoubling = IterationsPerDoubling,
            DetailMaxDroopAngle = DetailMaxDroopAngle,
            DetailStepScale = DetailStepScale,

            StepLength = StepLength,
            MinStepLength = MinStepLength,
            StepLengthVariance = StepLengthVariance,
            PitchAngle = PitchAngle,
            YawAngle = YawAngle,
            RollAngle = RollAngle,
            CurveAngle = CurveAngle,
            AngleVariance = AngleVariance,
            Gravity = Gravity,
            MaxDroopAngle = MaxDroopAngle,
            KeepAboveGround = KeepAboveGround,
            WindStrength = WindStrength,
            WindDirection = WindDirection,
            WindDirectionVariance = WindDirectionVariance,

            TaperCurve = TaperCurve,
            TaperBy = TaperBy,
            TrunkWidthAtBase = TrunkWidthAtBase,
            MinWidth = MinWidth,
            BodyBlock = BodyBlock,
            BranchShape = BranchShape,

            Foliage = Foliage,
            FoliageBlock = FoliageBlock,
            FoliageSize = FoliageSize,
            FoliageSizeVariance = FoliageSizeVariance,
            FoliageBlock2 = FoliageBlock2,
            FoliageBlock2Chance = FoliageBlock2Chance,

            GroundScatterBlock = GroundScatterBlock,
            GroundScatterAmount = GroundScatterAmount,

            RootCount = RootCount,
            RootLength = RootLength,
            RootWidth = RootWidth,
            RootHeight = RootHeight,
            RootWander = RootWander,
            RootBlock = RootBlock,
        };

        if (!LSystemCompiler.Compile(ref data, Axiom, Rules, out error))
            data.AxiomLength = 0; // nothing will be drawn
        return data;
    }
}
