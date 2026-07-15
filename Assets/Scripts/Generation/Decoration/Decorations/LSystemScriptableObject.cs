using Unity.Collections;
using UnityEngine;

// ----------------------------------------------------------------------------
// Serializable rule — mirrors LSystemRule, but uses plain strings/chars so
// Unity's inspector and serializer can actually show/save it. Converted to
// FixedString64Bytes only at the point of use (ToTreeData).
// ----------------------------------------------------------------------------
[System.Serializable]
public struct LSystemRuleSO
{
    [Tooltip("The single character this rule replaces (e.g. 'A', 'F').")]
    public char Symbol;

    [Tooltip("What Symbol expands to. Keep under 63 characters — FixedString64Bytes capacity.")]
    [TextArea(1, 3)]
    public string Replacement;
}

// ----------------------------------------------------------------------------
// ScriptableObject wrapper for LSystemTreeData.
// Lets you author tree rulesets as assets instead of hardcoded factory
// methods in TreeRulesets. Call ToTreeData() to get the runtime struct
// the interpreter actually consumes.
// ----------------------------------------------------------------------------
[CreateAssetMenu(fileName = "NewLSystemTree", menuName = "Decoration Generation/LSystem Tree Data", order = 0)]
public class LSystemScriptableObject : ScriptableObject
{
    [Header("Grammar")]
    [Tooltip("Starting string. Keep under 511 characters — FixedString512Bytes capacity.")]
    [TextArea(1, 4)]
    public string Axiom = "F";

    public LSystemRuleSO Rule0;
    public LSystemRuleSO Rule1;

    [Tooltip("How many rules above are active: 0, 1, or 2.")]
    [Range(0, 2)]
    public int RuleCount = 1;

    [Tooltip("Expansion depth. 0 means the Axiom is used as-is (e.g. a pre-baked string).")]
    [Min(0)]
    public int Iterations = 1;

    [Header("Movement")]
    [Min(1)] public float StepLength = 5f;
    [Min(1)] public float MinStepLength = 1f;
    [Tooltip("Max random addition to StepLength per tree spawn.")]
    public float StepLengthVariance = 0f;

    [Header("Rotation")]
    [Tooltip("^ &")]
    public float PitchAngle = 30f;
    [Tooltip("+ -")]
    public float YawAngle = 90f;
    [Tooltip("\\ /")]
    public float RollAngle = 0f;
    [Tooltip("Degrees applied by each step, in a downwards direction, to simulate gravity.")]
    public float GravityFactor = 0f;
    [Tooltip("Random +/- jitter applied to every rotation command.")]
    public float AngleVariance = 0f;

    [Header("Taper")]
    [Tooltip("Linear = straight interpolation\nSquare = t² — slow start, fast finish (stays full longer, tapers hard near the tip)\nSquareRoot = sqrt(t) — fast start, slow finish (tapers early, lingers thin for a while)\nLogarithmic = log-shaped — very fast initial taper, flattens out early and stays close to min")]
    public TaperCurveType TaperCurve = TaperCurveType.Linear;

    [Header("Visual")]
    [Min(1)] public int TrunkWidthAtBase = 1;
    [Min(0)] public int MinWidth = 1;
    public ShapeStyle Foliage = ShapeStyle.None;
    public BlockType BodyBlock = BlockType.Log;
    public BlockType FoliageBlock = BlockType.PineLeaves;
    [Tooltip("Used to set foliage size for some tree types (e.g. Palm frond count/length).")]
    public int FoliageSize = 5;

    // ------------------------------------------------------------------
    // Converts the authored asset into the Burst-safe runtime struct.
    // ------------------------------------------------------------------
    public LSystemTreeData ToTreeData()
    {
        return new LSystemTreeData
        {
            Axiom = new FixedString512Bytes(Axiom ?? string.Empty),
            Rule0 = new LSystemRule
            {
                Symbol = (byte)Rule0.Symbol,
                Replacement = new FixedString64Bytes(Rule0.Replacement ?? string.Empty)
            },
            Rule1 = new LSystemRule
            {
                Symbol = (byte)Rule1.Symbol,
                Replacement = new FixedString64Bytes(Rule1.Replacement ?? string.Empty)
            },
            RuleCount = RuleCount,
            Iterations = Iterations,

            StepLength = StepLength,
            MinStepLength = MinStepLength,
            StepLengthVariance = StepLengthVariance,
            PitchAngle = PitchAngle,
            YawAngle = YawAngle,
            RollAngle = RollAngle,
            TaperCurve = TaperCurve,
            GravityFactor = GravityFactor,
            AngleVariance = AngleVariance,

            TrunkWidthAtBase = TrunkWidthAtBase,
            MinWidth = MinWidth,
            Foliage = Foliage,
            BodyBlock = BodyBlock,
            FoliageBlock = FoliageBlock,
            FoliageSize = FoliageSize,
        };
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (!string.IsNullOrEmpty(Rule0.Replacement) && Rule0.Replacement.Length > 63)
            Debug.LogWarning($"[{name}] Rule0.Replacement exceeds 63 chars — will be truncated by FixedString64Bytes.", this);
        if (!string.IsNullOrEmpty(Rule1.Replacement) && Rule1.Replacement.Length > 63)
            Debug.LogWarning($"[{name}] Rule1.Replacement exceeds 63 chars — will be truncated by FixedString64Bytes.", this);
        if (!string.IsNullOrEmpty(Axiom) && Axiom.Length > 511)
            Debug.LogWarning($"[{name}] Axiom exceeds 511 chars — will be truncated by FixedString512Bytes.", this);
    }
#endif
}