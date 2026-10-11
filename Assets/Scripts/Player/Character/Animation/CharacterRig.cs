using System;
using UnityEngine;

/// <summary>
/// Bones of a jointed (non-skinned) character and how to pose them. Rest data is captured from
/// the model's import pose, and every rotation is applied relative to it, so the solver does not
/// depend on how the bones' local axes happen to be oriented.
///
/// "Facing frame" = rotated by the character's facing yaw, x right, y up, z forward, origin at the
/// feet pivot (the character root).
/// </summary>
[Serializable]
public class CharacterRig
{
    [Tooltip("Model node rotated to face the way the character faces")]
    public Transform modelRoot;
    [Tooltip("Hips/torso node; the legs and chest hang off it")]
    public Transform pelvis;
    public Transform chest;
    public Transform head;

    [Header("Legs")]
    public Transform leftThigh;
    public Transform leftShin;
    public Transform leftFoot;
    public Transform rightThigh;
    public Transform rightShin;
    public Transform rightFoot;

    [Header("Knees")]
    [Tooltip("How far (degrees) a knee may turn outward to stay out of terrain (high steps)")]
    public float maxKneeSplay = 70f;
    [Tooltip("How far (degrees) a knee may turn inward instead, when the outside is blocked")]
    public float maxKneeInward = 30f;
    [Tooltip("How fast the knees turn out / back in (degrees per second)")]
    public float kneeSplaySpeed = 900f;
    public float kneeReturnSpeed = 150f;
    [Tooltip("Thickness of the legs used when keeping them out of terrain")]
    public float legRadius = 0.13f;

    [Header("Arms")]
    public Transform leftUpperArm;
    public Transform leftForearm;
    public Transform leftHand;
    public Transform rightUpperArm;
    public Transform rightForearm;
    public Transform rightHand;

    /// <summary>A two-bone chain (leg or arm) with its rest pose.</summary>
    public class Limb
    {
        public Transform Upper, Lower, End;
        public float UpperLength, LowerLength;
        public float Length => UpperLength + LowerLength;

        // Rest pose in the facing frame.
        public Vector3 RestUpperPosition;   // relative to the character root
        public Vector3 RestEndPosition;     // relative to the character root
        public Vector3 RestUpperFromPelvis; // upper joint relative to the pelvis
        public Quaternion RestUpperRotation, RestLowerRotation, RestEndRotation;
        public Vector3 RestUpperDirection, RestLowerDirection, RestBendNormal;
        public Quaternion RestUpperLocal, RestLowerLocal;

        public bool Valid => Upper != null && Lower != null && End != null;

        /// <summary>Current turn of the bend plane (degrees, + outward / - inward), used to keep knees out of terrain.</summary>
        public float Splay;
    }

    public readonly Limb[] Legs = { new Limb(), new Limb() };
    public readonly Limb[] Arms = { new Limb(), new Limb() };

    Quaternion modelRootRest;
    Vector3 pelvisRestPosition;
    Quaternion pelvisRestRotation;
    Quaternion chestRestLocal, headRestLocal;

    /// <summary>Optional terrain test for knee avoidance (true = point is inside something solid).</summary>
    public Func<Vector3, bool> IsBlocked;

    public bool IsValid { get; private set; }
    /// <summary>Height of the foot bones above the ground in the rest pose.</summary>
    public float SoleHeight { get; private set; }
    public Vector3 PelvisRestPosition => pelvisRestPosition;

    /// <summary>Fills empty references by bone name (body, chest, head, left_leg, left_shin*, left_foot*, left_arm...).</summary>
    public void AutoBind(Transform root)
    {
        if (pelvis == null) pelvis = Find(root, "body");
        // The node above the pelvis turns the model (it may be renamed in the prefab).
        if (modelRoot == null && pelvis != null && pelvis.parent != root) modelRoot = pelvis.parent;
        if (chest == null) chest = Find(root, "chest");
        if (head == null) head = Find(root, "head");

        if (leftThigh == null) leftThigh = Find(root, "left_leg");
        if (rightThigh == null) rightThigh = Find(root, "right_leg");
        if (leftShin == null) leftShin = FindContains(leftThigh, "shin");
        if (rightShin == null) rightShin = FindContains(rightThigh, "shin");
        if (leftFoot == null) leftFoot = FindContains(leftShin, "foot");
        if (rightFoot == null) rightFoot = FindContains(rightShin, "foot");

        if (leftUpperArm == null) leftUpperArm = Find(root, "left_arm");
        if (rightUpperArm == null) rightUpperArm = Find(root, "right_arm");
        if (leftForearm == null) leftForearm = FindContains(leftUpperArm, "forearm");
        if (rightForearm == null) rightForearm = FindContains(rightUpperArm, "forearm");
        if (leftHand == null) leftHand = FindContains(leftForearm, "hand");
        if (rightHand == null) rightHand = FindContains(rightForearm, "hand");
    }

    /// <summary>Records the current (import) pose as the rest pose. Call before anything animates the model.</summary>
    public void CaptureRest(Transform character)
    {
        IsValid = modelRoot != null && pelvis != null;
        if (!IsValid)
            return;

        Quaternion frame = Quaternion.Euler(0f, character.eulerAngles.y, 0f);
        Quaternion inv = Quaternion.Inverse(frame);
        Vector3 origin = character.position;
        Vector3 Pos(Transform t) => inv * (t.position - origin);
        Quaternion Rot(Transform t) => inv * t.rotation;

        modelRootRest = Rot(modelRoot);
        pelvisRestPosition = Pos(pelvis);
        pelvisRestRotation = Rot(pelvis);
        if (chest != null) chestRestLocal = chest.localRotation;
        if (head != null) headRestLocal = head.localRotation;

        CaptureLimb(Legs[0], leftThigh, leftShin, leftFoot, Vector3.forward);
        CaptureLimb(Legs[1], rightThigh, rightShin, rightFoot, Vector3.forward);
        CaptureLimb(Arms[0], leftUpperArm, leftForearm, leftHand, Vector3.back);
        CaptureLimb(Arms[1], rightUpperArm, rightForearm, rightHand, Vector3.back);

        // Index 0 is always the character's left (-x), whatever the bones are called.
        SortBySide(Legs);
        SortBySide(Arms);

        float sole = 0f;
        int n = 0;
        foreach (var leg in Legs)
            if (leg.Valid) { sole += leg.RestEndPosition.y; n++; }
        SoleHeight = n > 0 ? sole / n : 0f;

        void CaptureLimb(Limb limb, Transform upper, Transform lower, Transform end, Vector3 pole)
        {
            limb.Upper = upper;
            limb.Lower = lower;
            limb.End = end;
            if (!limb.Valid)
                return;

            Vector3 a = Pos(upper), b = Pos(lower), c = Pos(end);
            limb.UpperLength = Vector3.Distance(a, b);
            limb.LowerLength = Vector3.Distance(b, c);
            limb.RestUpperPosition = a;
            limb.RestEndPosition = c;
            limb.RestUpperFromPelvis = a - pelvisRestPosition;
            limb.RestUpperRotation = Rot(upper);
            limb.RestLowerRotation = Rot(lower);
            limb.RestEndRotation = Rot(end);
            limb.RestUpperDirection = (b - a).normalized;
            limb.RestLowerDirection = (c - b).normalized;
            limb.RestBendNormal = BendNormal((c - a).normalized, pole);
            limb.RestUpperLocal = upper.localRotation;
            limb.RestLowerLocal = lower.localRotation;
        }
    }

    static void SortBySide(Limb[] pair)
    {
        if (pair[0].Valid && pair[1].Valid && pair[0].RestUpperPosition.x > pair[1].RestUpperPosition.x)
        {
            var leftData = pair[1];
            pair[1] = pair[0];
            pair[0] = leftData;
        }
    }

    /// <summary>Poses the bones. <paramref name="character"/> is the root the pose is relative to (the feet pivot).</summary>
    public void Apply(CharacterPose pose, Transform character, float deltaTime)
    {
        if (!IsValid)
            return;

        Vector3 origin = character.position;
        Quaternion facing = Quaternion.Euler(0f, pose.FacingYaw, 0f);

        modelRoot.rotation = facing * modelRootRest;

        pelvis.SetPositionAndRotation(
            origin + facing * (pelvisRestPosition + pose.PelvisOffset),
            facing * pose.PelvisRotation * pelvisRestRotation);

        if (chest != null) ApplyAdditive(chest, chestRestLocal, facing, pose.ChestRotation);
        if (head != null) ApplyAdditive(head, headRestLocal, facing, pose.HeadRotation);

        Vector3 knee = facing * pose.PelvisRotation * pose.KneeDirection;
        for (int i = 0; i < 2; i++)
        {
            var leg = Legs[i];
            if (!leg.Valid)
                continue;

            var foot = pose.Feet[i];
            UpdateKneeSplay(leg, facing, foot.Position, knee, deltaTime);
            SolveLimb(leg, facing, foot.Position, knee, foot.Weight, leg.Splay);

            Quaternion footRotation = Quaternion.Euler(0f, foot.Yaw, 0f) * Quaternion.AngleAxis(foot.Pitch, Vector3.right) * leg.RestEndRotation;
            Quaternion followRotation = leg.Lower.rotation * Quaternion.Inverse(facing * leg.RestLowerRotation) * (facing * leg.RestEndRotation);
            leg.End.rotation = Quaternion.Slerp(followRotation, footRotation, foot.Weight);
        }

        Vector3 elbowPole = facing * Vector3.back;
        for (int i = 0; i < 2; i++)
        {
            var arm = Arms[i];
            if (!arm.Valid)
                continue;

            var a = pose.Arms[i];
            ApplyAdditive(arm.Upper, arm.RestUpperLocal, facing, a.Shoulder);
            ApplyAdditive(arm.Lower, arm.RestLowerLocal, facing, a.Elbow);
            if (a.HandWeight > 0f)
                SolveLimb(arm, facing, a.HandTarget, elbowPole, a.HandWeight);
        }
    }

    // Rest local rotation, then a rotation about the joint expressed in the facing frame.
    static void ApplyAdditive(Transform bone, Quaternion restLocal, Quaternion facing, Quaternion additive)
    {
        Quaternion fk = bone.parent != null ? bone.parent.rotation * restLocal : restLocal;
        bone.rotation = facing * additive * Quaternion.Inverse(facing) * fk;
    }

    // Turns the knee outward while it (or the thigh/shin) would be inside terrain - like taking
    // a very high step - and lets it come back in once clear.
    void UpdateKneeSplay(Limb leg, Quaternion facing, Vector3 target, Vector3 pole, float deltaTime)
    {
        // Smallest turn that clears: outward first, inward (less far) when the outside is blocked.
        // Positive = outward, negative = inward.
        float wanted = 0f;
        if (IsBlocked != null && maxKneeSplay > 0f && LegBlocked(leg, facing, target, pole, 0f))
        {
            wanted = maxKneeSplay;
            for (float s = 10f; s <= maxKneeSplay; s += 10f)
            {
                if (!LegBlocked(leg, facing, target, pole, s)) { wanted = s; break; }
                if (s <= maxKneeInward && !LegBlocked(leg, facing, target, pole, -s)) { wanted = -s; break; }
            }
        }
        float speed = Mathf.Abs(wanted) > Mathf.Abs(leg.Splay) ? kneeSplaySpeed : kneeReturnSpeed;
        leg.Splay = Mathf.MoveTowards(leg.Splay, wanted, speed * Mathf.Max(deltaTime, 0f));
    }

    bool LegBlocked(Limb leg, Quaternion facing, Vector3 target, Vector3 pole, float splay)
    {
        Plan(leg, facing, target, pole, splay, out Vector3 root, out Vector3 joint, out Vector3 end, out Vector3 bend, out _, out _);

        // Sample the thigh and shin as thick segments: centre line plus the front (knee side)
        // and the forward-facing surface.
        Vector3 front = bend * legRadius;
        Vector3 ahead = facing * Vector3.forward * legRadius;
        for (int k = 0; k <= 6; k++)
        {
            float f = k / 6f;
            Vector3 p = k <= 3 ? Vector3.Lerp(root, joint, Mathf.Max(0.3f, f * 2f)) : Vector3.Lerp(joint, end, (f - 0.5f) * 2f * 0.85f);
            if (IsBlocked(p) || IsBlocked(p + front) || IsBlocked(p + ahead))
                return true;
        }
        return false;
    }

    // Solves where the middle joint goes: bent toward the pole, with the bend plane turned toward
    // the limb's outer side by splay degrees.
    static void Plan(Limb limb, Quaternion facing, Vector3 target, Vector3 pole, float splay,
        out Vector3 root, out Vector3 joint, out Vector3 end, out Vector3 bend, out Vector3 dir, out float distance)
    {
        root = limb.Upper.position;
        Vector3 toTarget = target - root;
        distance = toTarget.magnitude;
        dir = distance > 1e-5f ? toTarget / distance : facing * Vector3.down;

        float a = limb.UpperLength, b = limb.LowerLength;
        distance = Mathf.Clamp(distance, Mathf.Abs(a - b) + 1e-3f, (a + b) * 0.9999f);

        // Bend plane from the pole, falling back to "up" when the pole lines up with the limb.
        bend = pole - Vector3.Dot(pole, dir) * dir;
        if (bend.sqrMagnitude < 1e-4f)
            bend = facing * Vector3.up - Vector3.Dot(facing * Vector3.up, dir) * dir;
        bend.Normalize();

        if (Mathf.Abs(splay) > 0.01f)
        {
            Vector3 outward = facing * (limb.RestUpperPosition.x < 0f ? Vector3.left : Vector3.right);
            Vector3 side = Vector3.Cross(dir, bend);
            if (Vector3.Dot(side, outward) < 0f) side = -side;
            float r = splay * Mathf.Deg2Rad; // negative turns inward
            bend = (bend * Mathf.Cos(r) + side.normalized * Mathf.Sin(r)).normalized;
        }

        float cosAngle = Mathf.Clamp((a * a + distance * distance - b * b) / (2f * a * distance), -1f, 1f);
        float angle = Mathf.Acos(cosAngle);
        joint = root + (dir * Mathf.Cos(angle) + bend * Mathf.Sin(angle)) * a;
        end = root + dir * distance;
    }

    /// <summary>Two-bone IK: bends the limb toward <paramref name="pole"/> so its end reaches the target, blended with the current pose by weight.</summary>
    static void SolveLimb(Limb limb, Quaternion facing, Vector3 target, Vector3 pole, float weight, float splay = 0f)
    {
        if (weight <= 0f)
            return;

        Plan(limb, facing, target, pole, splay, out Vector3 root, out Vector3 joint, out Vector3 end, out Vector3 bend, out Vector3 dir, out _);
        Vector3 jointDir = (joint - root).normalized;
        Vector3 normal = BendNormal(dir, bend);

        Quaternion restFrame = Quaternion.Inverse(Quaternion.LookRotation(facing * limb.RestUpperDirection, facing * limb.RestBendNormal));
        Quaternion upper = Quaternion.LookRotation(jointDir, normal) * restFrame * (facing * limb.RestUpperRotation);

        restFrame = Quaternion.Inverse(Quaternion.LookRotation(facing * limb.RestLowerDirection, facing * limb.RestBendNormal));
        Quaternion lower = Quaternion.LookRotation((end - joint).normalized, normal) * restFrame * (facing * limb.RestLowerRotation);

        if (weight >= 1f)
        {
            limb.Upper.rotation = upper;
            limb.Lower.rotation = lower;
        }
        else
        {
            limb.Upper.rotation = Quaternion.Slerp(limb.Upper.rotation, upper, weight);
            limb.Lower.rotation = Quaternion.Slerp(limb.Lower.rotation, lower, weight);
        }
    }

    static Vector3 BendNormal(Vector3 limbDir, Vector3 pole)
    {
        Vector3 n = Vector3.Cross(limbDir, pole);
        return n.sqrMagnitude > 1e-8f ? n.normalized : Vector3.right;
    }

    static Transform Find(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        foreach (Transform child in root)
        {
            var found = Find(child, name);
            if (found != null) return found;
        }
        return null;
    }

    static Transform FindContains(Transform root, string part)
    {
        if (root == null) return null;
        foreach (Transform child in root)
        {
            if (child.name.Contains(part)) return child;
            var found = FindContains(child, part);
            if (found != null) return found;
        }
        return null;
    }
}
