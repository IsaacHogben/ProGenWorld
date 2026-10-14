using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Ragdoll for the character, simulated against the voxel data (no Unity colliders): one particle
/// per joint, held together by distance constraints, colliding with blocks as spheres. It takes
/// over from the animation on a hard landing, on the ragdoll key, or when <see cref="Activate"/>
/// is called, and drives the bones from the particles. The character stays down until the player
/// gives a command (moves, jumps...), then gets up where it lies and blends back into the
/// animated pose.
///
/// Folded (the fold key): the legs snap up into the body and the arms tuck in, then the whole
/// bundle tumbles as one rigid piece. The fold key again unfolds at once, wherever it is.
/// </summary>
[DefaultExecutionOrder(60)] // after CharacterAnimator (50) poses the bones, before the camera (100)
[RequireComponent(typeof(CharacterAnimator))]
public class VoxelRagdoll : MonoBehaviour
{
    [Header("Trigger")]
    [Tooltip("Landing faster than this (m/s) knocks the character over. 15 = roughly an 11 block fall")]
    [SerializeField] private float knockdownImpactSpeed = 15f;
    [Tooltip("Go limp")]
    [SerializeField] private Key ragdollKey = Key.G;
    [Tooltip("Fold up (legs into the body, arms tucked) and tumble as one piece")]
    [SerializeField] private Key foldKey = Key.F;

    [Header("Fold")]
    [Tooltip("Seconds for the limbs to snap into the folded pose")]
    [SerializeField] private float foldTime = 0.18f;
    [Tooltip("Folded up the body slides on blocks almost freely: share of sliding speed lost per physics step in contact (0 = ice, 1 = sticks)")]
    [Range(0f, 1f)] [SerializeField] private float foldFriction = 0.01f;
    [Tooltip("...and bounces off them")]
    [Range(0f, 1f)] [SerializeField] private float foldBounce = 0.55f;

    [Header("Simulation")]
    [SerializeField] private float gravityScale = 1f;
    [Tooltip("Constraint passes per step; more = stiffer body")]
    [SerializeField] private int iterations = 10;
    [Tooltip("Sliding friction against blocks (0 = ice, 1 = sticks)")]
    [Range(0f, 1f)] [SerializeField] private float friction = 0.5f;
    [Range(0f, 1f)] [SerializeField] private float bounce = 0.15f;
    [Tooltip("Air drag (fraction of velocity lost per second)")]
    [SerializeField] private float drag = 0.05f;

    [Header("Recovery")]
    [Tooltip("Considered at rest when every joint moves slower than this (m/s)")]
    [SerializeField] private float restSpeed = 0.5f;
    [Tooltip("Seconds at rest before a command gets the character up")]
    [SerializeField] private float restTime = 0.3f;
    [SerializeField] private float minRagdollTime = 0.8f;
    [Tooltip("Still sliding or rolling after this long, a command gets the character up anyway")]
    [SerializeField] private float maxRagdollTime = 6f;
    [Tooltip("Seconds to blend from the fallen pose back into the animation")]
    [SerializeField] private float getUpTime = 0.8f;
    [Tooltip("Seconds to unfold back into the animation")]
    [SerializeField] private float unfoldTime = 0.45f;

    public bool IsActive => state != State.Off;
    public bool IsRagdolling => state == State.Ragdoll;
    public bool IsFolded => state == State.Ragdoll && folded;
    /// <summary>At rest (or down long enough) and waiting for a command to get up.</summary>
    public bool CanGetUp => state == State.Ragdoll && stateTime > minRagdollTime && (restTimer > restTime || stateTime > maxRagdollTime);
    public event Action Started;
    public event Action Recovered;

    enum State { Off, Ragdoll, GettingUp }

    // Particle indices
    const int Pelvis = 0, Chest = 1, Head = 2, LShoulder = 3, RShoulder = 4, LElbow = 5, RElbow = 6,
        LHand = 7, RHand = 8, LHip = 9, RHip = 10, LKnee = 11, RKnee = 12, LFoot = 13, RFoot = 14, Count = 15;

    static readonly float[] Radius = { 0.22f, 0.25f, 0.22f, 0.12f, 0.12f, 0.09f, 0.09f, 0.1f, 0.1f, 0.12f, 0.12f, 0.1f, 0.1f, 0.12f, 0.12f };

    struct Link
    {
        public int a, b;
        public float length;
        public bool minOnly; // only pushes apart (keeps limbs out of the body)
    }

    // A bone driven by the direction between two particles.
    struct DrivenBone
    {
        public Transform bone;
        public int from, to;
        public Quaternion startRotation;
        public Vector3 startDirection;
    }

    readonly Vector3[] pos = new Vector3[Count];
    readonly Vector3[] prev = new Vector3[Count];
    readonly Vector3[] contact = new Vector3[Count];
    Link[] links;
    DrivenBone[] driven;

    CharacterAnimator animator;
    VoxelCharacterMotor motor;
    PlayerController controller;
    State state;
    float stateTime;
    float restTimer;
    bool getUpRequested;
    bool unfoldRequested;
    bool folded;
    float foldT;
    readonly Vector3[] foldFrom = new Vector3[Count]; // torso frame, at the start of the fold
    readonly Vector3[] foldTo = new Vector3[Count];
    static readonly int[] FoldingJoints = { Head, LElbow, RElbow, LHand, RHand, LKnee, RKnee, LFoot, RFoot };

    Quaternion startTorso, startPelvisRotation, startChestRotation;
    Quaternion[] startEndRotation; // feet, hands follow their lower bone
    Transform[] snapshotBones;
    Quaternion[] snapshotLocal;
    Vector3 snapshotPelvisPosition;
    Quaternion snapshotPelvisRotation;

    void Awake()
    {
        animator = GetComponent<CharacterAnimator>();
        motor = GetComponent<VoxelCharacterMotor>();
        controller = GetComponent<PlayerController>();
    }

    void OnEnable()
    {
        if (motor != null) motor.Landed += OnLanded;
    }

    void OnDisable()
    {
        if (motor != null) motor.Landed -= OnLanded;
    }

    void OnLanded(float impact)
    {
        if (state == State.Off && impact >= knockdownImpactSpeed)
        {
            Vector3 v = motor.Velocity;
            Activate(new Vector3(v.x, -impact, v.z));
        }
    }

    void Update()
    {
        var keyboard = Keyboard.current;
        bool ragdollPressed = keyboard != null && keyboard[ragdollKey].wasPressedThisFrame;
        bool foldPressed = keyboard != null && keyboard[foldKey].wasPressedThisFrame;

        if (state == State.Off)
        {
            if (ragdollPressed || foldPressed)
            {
                if (motor != null) motor.NoClip = false;
                Activate(motor != null ? motor.Velocity : Vector3.zero, foldPressed);
            }
            return;
        }

        // Folded: the fold key again unfolds straight away, even mid-roll or in the air.
        if (state == State.Ragdoll && folded && foldPressed)
        {
            unfoldRequested = true;
            return;
        }

        // Down: any command (or the key again) gets up once the body has settled.
        if (state == State.Ragdoll && stateTime > 0.25f &&
            (ragdollPressed || foldPressed || (controller != null && controller.AnyCommandPressed())))
            getUpRequested = true;
    }

    /// <summary>
    /// Knocks the character over (or folds it up), starting from the current pose moving at
    /// <paramref name="velocity"/>.
    /// </summary>
    public void Activate(Vector3 velocity, bool fold = false)
    {
        var rig = animator.Rig;
        if (!rig.IsValid || rig.chest == null || rig.head == null || !rig.Legs[0].Valid || !rig.Legs[1].Valid || !rig.Arms[0].Valid || !rig.Arms[1].Valid)
            return;

        // Particles at the joints of the current pose.
        pos[Pelvis] = rig.pelvis.position;
        pos[Chest] = rig.chest.position;
        pos[Head] = rig.head.position + (rig.head.position - rig.chest.position).normalized * 0.25f;
        pos[LShoulder] = rig.Arms[0].Upper.position; pos[RShoulder] = rig.Arms[1].Upper.position;
        pos[LElbow] = rig.Arms[0].Lower.position; pos[RElbow] = rig.Arms[1].Lower.position;
        pos[LHand] = rig.Arms[0].End.position; pos[RHand] = rig.Arms[1].End.position;
        pos[LHip] = rig.Legs[0].Upper.position; pos[RHip] = rig.Legs[1].Upper.position;
        pos[LKnee] = rig.Legs[0].Lower.position; pos[RKnee] = rig.Legs[1].Lower.position;
        pos[LFoot] = rig.Legs[0].End.position; pos[RFoot] = rig.Legs[1].End.position;

        float dt = Time.fixedDeltaTime;
        for (int i = 0; i < Count; i++)
        {
            prev[i] = pos[i] - velocity * dt;
            contact[i] = Vector3.zero;
        }

        folded = fold;
        foldT = 0f;
        if (fold)
        {
            BuildTorsoLinks();
            PlanFold();
        }
        else
        {
            BuildLinks();
        }

        startTorso = TorsoFrame();
        startPelvisRotation = rig.pelvis.rotation;
        startChestRotation = rig.chest.rotation;
        driven = new[]
        {
            Drive(rig.head, Chest, Head),
            Drive(rig.Legs[0].Upper, LHip, LKnee), Drive(rig.Legs[0].Lower, LKnee, LFoot),
            Drive(rig.Legs[1].Upper, RHip, RKnee), Drive(rig.Legs[1].Lower, RKnee, RFoot),
            Drive(rig.Arms[0].Upper, LShoulder, LElbow), Drive(rig.Arms[0].Lower, LElbow, LHand),
            Drive(rig.Arms[1].Upper, RShoulder, RElbow), Drive(rig.Arms[1].Lower, RElbow, RHand),
        };
        startEndRotation = new[] { rig.Legs[0].End.rotation, rig.Legs[1].End.rotation, rig.Arms[0].End.rotation, rig.Arms[1].End.rotation };

        if (controller != null) controller.enabled = false;
        state = State.Ragdoll;
        stateTime = 0f;
        restTimer = 0f;
        getUpRequested = false;
        unfoldRequested = false;
        Started?.Invoke();
    }

    DrivenBone Drive(Transform bone, int from, int to) => new DrivenBone
    {
        bone = bone,
        from = from,
        to = to,
        startRotation = bone.rotation,
        startDirection = (pos[to] - pos[from]).normalized,
    };

    void BuildLinks()
    {
        var list = new System.Collections.Generic.List<Link>();
        void Rigid(int a, int b) => list.Add(new Link { a = a, b = b, length = Vector3.Distance(pos[a], pos[b]) });
        void Apart(int a, int b, float fraction) => list.Add(new Link { a = a, b = b, length = Vector3.Distance(pos[a], pos[b]) * fraction, minOnly = true });

        // Torso is one rigid piece.
        int[] torso = { Pelvis, Chest, LShoulder, RShoulder, LHip, RHip };
        for (int i = 0; i < torso.Length; i++)
            for (int j = i + 1; j < torso.Length; j++)
                Rigid(torso[i], torso[j]);
        Rigid(Head, Chest); Rigid(Head, LShoulder); Rigid(Head, RShoulder);

        // Limbs.
        Rigid(LHip, LKnee); Rigid(LKnee, LFoot); Rigid(RHip, RKnee); Rigid(RKnee, RFoot);
        Rigid(LShoulder, LElbow); Rigid(LElbow, LHand); Rigid(RShoulder, RElbow); Rigid(RElbow, RHand);

        // Keep limbs from folding through the body or each other.
        Apart(LKnee, Chest, 0.6f); Apart(RKnee, Chest, 0.6f);
        Apart(LFoot, Pelvis, 0.45f); Apart(RFoot, Pelvis, 0.45f);
        Apart(LFoot, LHip, 0.4f); Apart(RFoot, RHip, 0.4f);
        Apart(LHand, LShoulder, 0.35f); Apart(RHand, RShoulder, 0.35f);
        Apart(LHand, Pelvis, 0.3f); Apart(RHand, Pelvis, 0.3f);
        Apart(LKnee, RKnee, 0.5f); Apart(LFoot, RFoot, 0.4f);

        links = list.ToArray();
    }

    // While folding only the torso is simulated; the limbs are moved into place on it.
    void BuildTorsoLinks()
    {
        var list = new System.Collections.Generic.List<Link>();
        int[] torso = { Pelvis, Chest, LShoulder, RShoulder, LHip, RHip };
        for (int i = 0; i < torso.Length; i++)
            for (int j = i + 1; j < torso.Length; j++)
                list.Add(new Link { a = torso[i], b = torso[j], length = Vector3.Distance(pos[torso[i]], pos[torso[j]]) });
        links = list.ToArray();
    }

    // Folded up every joint is locked: one rigid piece.
    void BuildRigidLinks()
    {
        var list = new System.Collections.Generic.List<Link>();
        for (int i = 0; i < Count; i++)
            for (int j = i + 1; j < Count; j++)
                list.Add(new Link { a = i, b = j, length = Vector3.Distance(pos[i], pos[j]) });
        links = list.ToArray();
    }

    // Folded pose in the torso frame (x right, y up the spine, z forward): thighs pulled up in
    // front of the body with the shins folded flat under them, upper arms against the sides with
    // the forearms across the chest, head bowed.
    void PlanFold()
    {
        Quaternion inv = Quaternion.Inverse(TorsoFrame());
        Vector3 origin = pos[Pelvis];
        Vector3 Local(int i) => inv * (pos[i] - origin);
        foreach (int i in FoldingJoints)
            foldFrom[i] = Local(i);

        void Leg(int hip, int knee, int foot)
        {
            Vector3 h = Local(hip);
            float thigh = Vector3.Distance(pos[hip], pos[knee]), shin = Vector3.Distance(pos[knee], pos[foot]);
            foldTo[knee] = h + thigh * new Vector3(0f, 0.55f, 0.83f).normalized;
            foldTo[foot] = foldTo[knee] + shin * new Vector3(0f, -0.3f, -0.95f).normalized;
        }
        void Arm(int shoulder, int elbow, int hand)
        {
            Vector3 s = Local(shoulder);
            float side = Mathf.Sign(s.x);
            float upper = Vector3.Distance(pos[shoulder], pos[elbow]), fore = Vector3.Distance(pos[elbow], pos[hand]);
            foldTo[elbow] = s + upper * new Vector3(0.1f * side, -0.8f, 0.4f).normalized;
            foldTo[hand] = foldTo[elbow] + fore * new Vector3(-0.75f * side, 0.45f, 0.35f).normalized;
        }
        Leg(LHip, LKnee, LFoot);
        Leg(RHip, RKnee, RFoot);
        Arm(LShoulder, LElbow, LHand);
        Arm(RShoulder, RElbow, RHand);

        Vector3 chest = Local(Chest);
        foldTo[Head] = chest + Vector3.Distance(pos[Chest], pos[Head]) * new Vector3(0f, 0.6f, 0.8f).normalized;
    }

    // Limbs snap into the folded pose on the (falling) torso; once there, everything locks.
    void StepFold(float dt)
    {
        foldT = Mathf.Min(1f, foldT + dt / Mathf.Max(foldTime, 0.01f));
        Simulate(dt);

        Quaternion frame = TorsoFrame();
        Vector3 origin = pos[Pelvis];
        Vector3 carry = pos[Pelvis] - prev[Pelvis];
        float k = foldT * foldT * (3f - 2f * foldT);
        foreach (int i in FoldingJoints)
        {
            pos[i] = origin + frame * Vector3.Lerp(foldFrom[i], foldTo[i], k);
            prev[i] = pos[i] - carry;
            contact[i] = Vector3.zero;
        }

        if (foldT >= 1f)
            BuildRigidLinks();
    }

    // ----------------------------------------------------------------------------------
    // Simulation
    // ----------------------------------------------------------------------------------

    void FixedUpdate()
    {
        if (state != State.Ragdoll)
            return;

        float dt = Time.fixedDeltaTime;
        stateTime += dt;
        if (folded && foldT < 1f)
            StepFold(dt);
        else
            Simulate(dt);

        // The body (and the camera on it) follows the pelvis, still colliding normally.
        if (motor != null)
        {
            Vector3 to = pos[Pelvis] - motor.Position;
            to.y = 0f;
            motor.HorizontalVelocity = Vector3.ClampMagnitude(to / dt, 20f);
        }

        float fastest = 0f;
        for (int i = 0; i < Count; i++)
            fastest = Mathf.Max(fastest, (pos[i] - prev[i]).magnitude / dt);
        restTimer = fastest < restSpeed ? restTimer + dt : 0f;

        if ((getUpRequested && CanGetUp) || (unfoldRequested && foldT >= 1f))
            BeginGetUp();
    }

    void Simulate(float dt)
    {
        Vector3 gravity = Physics.gravity * gravityScale * dt * dt;
        float keep = Mathf.Clamp01(1f - drag * dt);
        for (int i = 0; i < Count; i++)
        {
            Vector3 v = (pos[i] - prev[i]) * keep;
            prev[i] = pos[i];
            pos[i] += v + gravity;
            contact[i] = Vector3.zero;
        }

        for (int it = 0; it < iterations; it++)
        {
            foreach (var l in links)
            {
                Vector3 d = pos[l.b] - pos[l.a];
                float dist = d.magnitude;
                if (dist < 1e-6f || (l.minOnly && dist >= l.length))
                    continue;
                Vector3 correction = d * ((dist - l.length) / dist * 0.5f);
                pos[l.a] += correction;
                pos[l.b] -= correction;
            }
            for (int i = 0; i < Count; i++)
                Collide(i);
        }

        // Friction and bounce once per step, for joints touching blocks. Folded up it is a hard,
        // slippery shell; slow contacts don't bounce so it can come to rest.
        float mu = folded ? foldFriction : friction;
        float restitution = folded ? foldBounce : bounce;
        for (int i = 0; i < Count; i++)
        {
            Vector3 n = contact[i];
            if (n == Vector3.zero) continue;
            n.Normalize();
            Vector3 v = pos[i] - prev[i];
            float vn = Vector3.Dot(v, n);
            Vector3 tangent = v - n * vn;
            if (vn < 0f) vn = vn < -dt ? -vn * restitution : 0f;
            prev[i] = pos[i] - (tangent * (1f - mu) + n * vn);
        }
    }

    // Pushes a joint sphere out of any blocks it overlaps.
    void Collide(int i)
    {
        float r = Radius[i];
        Vector3 p = pos[i];
        int3 lo = (int3)math.floor((float3)(p - Vector3.one * r));
        int3 hi = (int3)math.floor((float3)(p + Vector3.one * r));

        for (int x = lo.x; x <= hi.x; x++)
        for (int y = lo.y; y <= hi.y; y++)
        for (int z = lo.z; z <= hi.z; z++)
        {
            var v = new int3(x, y, z);
            if (!VoxelPhysics.IsSolid(v)) continue;

            Vector3 min = new Vector3(x, y, z), max = min + Vector3.one;
            Vector3 closest = Vector3.Max(min, Vector3.Min(p, max));
            Vector3 d = p - closest;
            float dist = d.magnitude;
            Vector3 n;
            float push;

            if (dist > 1e-5f)
            {
                if (dist >= r) continue;
                n = d / dist;
                push = r - dist;
            }
            else
            {
                // Centre inside the block: leave through the nearest face that opens to air.
                n = Vector3.up;
                push = float.MaxValue;
                TryFace(v, new int3(0, 1, 0), max.y - p.y, Vector3.up, ref n, ref push, r);
                TryFace(v, new int3(0, -1, 0), p.y - min.y, Vector3.down, ref n, ref push, r);
                TryFace(v, new int3(1, 0, 0), max.x - p.x, Vector3.right, ref n, ref push, r);
                TryFace(v, new int3(-1, 0, 0), p.x - min.x, Vector3.left, ref n, ref push, r);
                TryFace(v, new int3(0, 0, 1), max.z - p.z, Vector3.forward, ref n, ref push, r);
                TryFace(v, new int3(0, 0, -1), p.z - min.z, Vector3.back, ref n, ref push, r);
                if (push == float.MaxValue)
                {
                    n = Vector3.up;
                    push = max.y - p.y + r;
                }
            }

            p += n * push;
            contact[i] += n;
        }
        pos[i] = p;
    }

    static void TryFace(int3 v, int3 dir, float distance, Vector3 normal, ref Vector3 n, ref float push, float r)
    {
        if (VoxelPhysics.IsSolid(v + dir)) return;
        if (distance + r < push)
        {
            n = normal;
            push = distance + r;
        }
    }

    // ----------------------------------------------------------------------------------
    // Posing
    // ----------------------------------------------------------------------------------

    Quaternion TorsoFrame()
    {
        Vector3 up = (pos[Chest] - pos[Pelvis]).normalized;
        Vector3 right = pos[RHip] - pos[LHip];
        Vector3 forward = Vector3.Cross(right, up);
        if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
        return Quaternion.LookRotation(forward.normalized, up);
    }

    void LateUpdate()
    {
        if (state == State.Ragdoll)
            PoseFromParticles();
        else if (state == State.GettingUp)
            BlendToAnimation();
    }

    void PoseFromParticles()
    {
        var rig = animator.Rig;
        Quaternion torso = TorsoFrame() * Quaternion.Inverse(startTorso);

        rig.pelvis.SetPositionAndRotation(pos[Pelvis], torso * startPelvisRotation);
        rig.chest.rotation = torso * startChestRotation;

        // Each bone turns from its starting direction to where its joints are now, twisting with the torso.
        for (int i = 0; i < driven.Length; i++)
        {
            var d = driven[i];
            Vector3 now = pos[d.to] - pos[d.from];
            if (now.sqrMagnitude < 1e-8f) continue;
            Quaternion carried = torso * d.startRotation;
            d.bone.rotation = Quaternion.FromToRotation(torso * d.startDirection, now.normalized) * carried;
        }

        // Feet and hands keep their angle to the shin / forearm.
        Transform[] lower = { rig.Legs[0].Lower, rig.Legs[1].Lower, rig.Arms[0].Lower, rig.Arms[1].Lower };
        Transform[] end = { rig.Legs[0].End, rig.Legs[1].End, rig.Arms[0].End, rig.Arms[1].End };
        int[] lowerDriven = { 2, 4, 6, 8 };
        for (int i = 0; i < 4; i++)
            end[i].rotation = lower[i].rotation * Quaternion.Inverse(driven[lowerDriven[i]].startRotation) * startEndRotation[i];
    }

    // ----------------------------------------------------------------------------------
    // Getting up
    // ----------------------------------------------------------------------------------

    void BeginGetUp()
    {
        var rig = animator.Rig;

        // Stand where the pelvis lies, facing the way the head points.
        Vector3 up = pos[Chest] - pos[Pelvis];
        up.y = 0f;
        float yaw = up.sqrMagnitude > 0.01f ? Mathf.Atan2(up.x, up.z) * Mathf.Rad2Deg : transform.eulerAngles.y;

        // On the ground under the pelvis; unfolding in the air, the feet hang below the body and
        // it falls from there.
        Vector3 stand = pos[Pelvis];
        bool onGround = VoxelPhysics.TryGetGroundHeight(pos[Pelvis] + Vector3.up * 0.5f, 1.5f, 2.2f, out float ground);
        if (onGround)
            stand.y = ground;
        else
            stand.y -= rig.PelvisRestPosition.y * 0.85f;

        // Momentum of the whole body, kept when getting up mid-roll (unfolding early).
        Vector3 momentum = Vector3.zero;
        for (int i = 0; i < Count; i++) momentum += pos[i] - prev[i];
        momentum /= Count * Time.fixedDeltaTime;
        if (onGround && momentum.y < 0f) momentum.y = 0f;
        if (motor != null)
        {
            motor.HorizontalVelocity = Vector3.zero;
            motor.Teleport(stand);
        }
        else
        {
            transform.position = stand;
        }

        // Remember the fallen pose to blend out of.
        snapshotBones = new Transform[driven.Length + 5];
        for (int i = 0; i < driven.Length; i++) snapshotBones[i] = driven[i].bone;
        snapshotBones[driven.Length] = rig.chest;
        snapshotBones[driven.Length + 1] = rig.Legs[0].End;
        snapshotBones[driven.Length + 2] = rig.Legs[1].End;
        snapshotBones[driven.Length + 3] = rig.Arms[0].End;
        snapshotBones[driven.Length + 4] = rig.Arms[1].End;
        snapshotLocal = new Quaternion[snapshotBones.Length];
        for (int i = 0; i < snapshotBones.Length; i++) snapshotLocal[i] = snapshotBones[i].localRotation;
        snapshotPelvisPosition = rig.pelvis.position;
        snapshotPelvisRotation = rig.pelvis.rotation;

        if (controller != null)
        {
            controller.enabled = true;
            controller.SetFacing(yaw);
            controller.CarryMomentum(momentum);
        }
        else if (motor != null)
        {
            motor.SetVelocity(momentum);
        }
        animator.ResetLayers();

        state = State.GettingUp;
        stateTime = 0f;
    }

    void BlendToAnimation()
    {
        stateTime += Time.deltaTime;
        float t = Mathf.Clamp01(stateTime / Mathf.Max(folded ? unfoldTime : getUpTime, 0.01f));
        float w = t * t * (3f - 2f * t);

        // The animator has just posed the bones; ease from the fallen pose into it.
        var rig = animator.Rig;
        Vector3 pelvisPosition = Vector3.Lerp(snapshotPelvisPosition, rig.pelvis.position, w);
        Quaternion pelvisRotation = Quaternion.Slerp(snapshotPelvisRotation, rig.pelvis.rotation, w);
        Quaternion[] animated = new Quaternion[snapshotBones.Length];
        for (int i = 0; i < snapshotBones.Length; i++) animated[i] = snapshotBones[i].localRotation;

        rig.pelvis.SetPositionAndRotation(pelvisPosition, pelvisRotation);
        for (int i = 0; i < snapshotBones.Length; i++)
            snapshotBones[i].localRotation = Quaternion.Slerp(snapshotLocal[i], animated[i], w);

        if (t >= 1f)
        {
            state = State.Off;
            Recovered?.Invoke();
        }
    }
}
