using UnityEngine;

/// <summary>
/// Procedural leg IK for voxel character - works with SimpleVoxelController
/// </summary>
public class ProceduralLegIK : MonoBehaviour
{
    [Header("Leg Transforms")]
    [Tooltip("The left thigh bone (left_leg)")]
    [SerializeField] private Transform leftLeg;
    [Tooltip("The left shin bone (left_shin)")]
    [SerializeField] private Transform leftShin;
    [Tooltip("The left foot bone (left_foot)")]
    [SerializeField] private Transform leftFoot;
    [Tooltip("The right thigh bone (right_leg)")]
    [SerializeField] private Transform rightLeg;
    [Tooltip("The right shin bone (right_shin)")]
    [SerializeField] private Transform rightShin;
    [Tooltip("The right foot bone (right_foot)")]
    [SerializeField] private Transform rightFoot;

    [Header("Body Reference")]
    [Tooltip("The Body GameObject that rotates (parent of all model parts)")]
    [SerializeField] private Transform bodyTransform;
    [Tooltip("Left arm transform (optional, for arm swing)")]
    [SerializeField] private Transform leftArm;
    [Tooltip("Right arm transform (optional, for arm swing)")]
    [SerializeField] private Transform rightArm;

    [Header("General Settings")]
    [Tooltip("How far to the sides feet are placed (stance width)")]
    [SerializeField] private float footOffsetSide = 0.3f;
    [Tooltip("Height offset to raise foot above ground (adjust if feet sink into terrain)")]
    [SerializeField] private float footHeightOffset = 0.1f;

    [Header("Walking Settings")]
    [Tooltip("Distance foot must be from target before triggering a step")]
    [SerializeField] private float walkStepDistance = 0.5f;
    [Tooltip("How high the foot lifts during a step")]
    [SerializeField] private float walkStepHeight = 0.3f;
    [Tooltip("Time in seconds to complete one step")]
    [SerializeField] private float walkStepDuration = 0.3f;
    [Tooltip("How far ahead to place feet when walking")]
    [SerializeField] private float walkFootOffset = 0.5f;
    [Tooltip("Maximum angle to tilt foot forward during step (in degrees)")]
    [SerializeField] private float walkFootAngle = 25f;

    [Header("Turning Settings")]
    [Tooltip("Distance foot must be from target before triggering a step")]
    [SerializeField] private float turnStepDistance = 0.2f;
    [Tooltip("How high the foot lifts during a step")]
    [SerializeField] private float turnStepHeight = 0.2f;
    [Tooltip("Time in seconds to complete one step")]
    [SerializeField] private float turnStepDuration = 0.25f;
    [Tooltip("Maximum angle to tilt foot forward during step (in degrees)")]
    [SerializeField] private float turnFootAngle = 15f;

    [Header("Sprinting Settings")]
    [Tooltip("Distance foot must be from target before triggering a step")]
    [SerializeField] private float sprintStepDistance = 0.7f;
    [Tooltip("How high the foot lifts during a step")]
    [SerializeField] private float sprintStepHeight = 0.4f;
    [Tooltip("Time in seconds to complete one step")]
    [SerializeField] private float sprintStepDuration = 0.2f;
    [Tooltip("How far ahead to place feet when sprinting")]
    [SerializeField] private float sprintFootOffset = 0.8f;
    [Tooltip("Maximum angle to tilt foot forward during step (in degrees)")]
    [SerializeField] private float sprintFootAngle = 30f;

    [Header("Foot Angle Curve")]
    [Tooltip("Step progress when foot reaches maximum tilt (0-1, e.g. 0.3 = 30% through step)")]
    [SerializeField] private float tiltPeakProgress = 0.3f;
    [Tooltip("Step progress when foot starts returning to flat (0-1, e.g. 0.7 = 70% through step)")]
    [SerializeField] private float tiltReturnProgress = 0.7f;

    [Header("Airborne Pose")]
    [Tooltip("Default foot position when jumping/falling (relative to body in local space)")]
    [SerializeField] private Vector3 defaultFootOffset = new Vector3(0.3f, -0.5f, 0.2f);
    [Tooltip("Speed to lerp feet to default position when airborne")]
    [SerializeField] private float airborneTransitionSpeed = 10f;
    [Tooltip("Speed to lerp feet to ground when landing")]
    [SerializeField] private float landingTransitionSpeed = 15f;
    [Tooltip("Velocity threshold for full airborne pose (positive = going up)")]
    [SerializeField] private float maxUpwardVelocity = 5f;
    [Tooltip("Velocity threshold for captured pose (negative = falling)")]
    [SerializeField] private float maxDownwardVelocity = -5f;

    [Header("Sprint Tilt")]
    [Tooltip("How much to tilt body forward when sprinting (in degrees)")]
    [SerializeField] private float sprintTiltAngle = 10f;
    [Tooltip("How fast body tilts into/out of sprint")]
    [SerializeField] private float sprintTiltSpeed = 5f;

    [Header("Arm Swing")]
    [Tooltip("Enable arm swing animation")]
    [SerializeField] private bool enableArmSwing = true;
    [Tooltip("Maximum arm swing angle forward/back")]
    [SerializeField] private float armSwingAngle = 30f;
    [Tooltip("Arm swing speed multiplier")]
    [SerializeField] private float armSwingSpeed = 2f;

    [Header("Ground Detection")]
    [Tooltip("Maximum distance to raycast down when looking for ground")]
    [SerializeField] private float raycastDistance = 3f;
    [Tooltip("Height above character to start raycasting from")]
    [SerializeField] private float raycastStartHeight = 2f;
    [Tooltip("Additional distance to check below for step-down (1 = one block)")]
    [SerializeField] private float stepDownDistance = 1f;
    [Tooltip("Speed at which body adjusts height")]
    [SerializeField] private float bodyHeightAdjustSpeed = 5f;
    [Tooltip("Layer mask for terrain/ground detection")]
    [SerializeField] private LayerMask terrainLayer;

    // Target positions for feet (world space)
    private Vector3 leftFootTarget;
    private Vector3 rightFootTarget;

    // Current foot positions (world space, smoothed)
    private Vector3 leftFootCurrentPos;
    private Vector3 rightFootCurrentPos;

    // Foot rotation tracking
    private float leftFootAngle = 0f;
    private float rightFootAngle = 0f;

    // Body height adjustment
    private float targetBodyHeight = 0f;
    private float currentBodyHeight = 0f;
    private float bodyHeightOffset = 0f; // Starting height of body above root

    // Body tilt
    private float currentBodyTilt = 0f;

    // Arm swing
    private float armSwingTime = 0f;

    // Airborne tracking
    private bool wasJumping = false;
    private Vector3 leftFootJumpPos;
    private Vector3 rightFootJumpPos;
    private bool isLanding = false;

    // Stepping state
    private bool isLeftFootStepping = false;
    private bool isRightFootStepping = false;
    private float leftStepProgress = 0f;
    private float rightStepProgress = 0f;
    private Vector3 leftFootStartPos;
    private Vector3 rightFootStartPos;

    // Reference to movement controller
    private SimpleVoxelController controller;

    void Start()
    {
        controller = GetComponent<SimpleVoxelController>();

        // Store the initial body height offset
        if (bodyTransform != null)
        {
            bodyHeightOffset = bodyTransform.localPosition.y;
        }

        // Initialize foot positions
        if (leftFoot != null)
        {
            leftFootCurrentPos = leftFoot.position;
            leftFootTarget = leftFoot.position;
        }

        if (rightFoot != null)
        {
            rightFootCurrentPos = rightFoot.position;
            rightFootTarget = rightFoot.position;
        }
    }

    void LateUpdate()
    {
        // Check if jumping or falling
        bool isAirborne = controller != null && (controller.IsJumping || controller.State == SimpleVoxelController.MovementState.Falling);

        if (isAirborne)
        {
            // First frame of being airborne - capture current foot positions in local space
            if (!wasJumping)
            {
                if (bodyTransform != null)
                {
                    leftFootJumpPos = bodyTransform.InverseTransformPoint(leftFootCurrentPos);
                    rightFootJumpPos = bodyTransform.InverseTransformPoint(rightFootCurrentPos);
                }
                wasJumping = true;
                isLanding = false;
            }

            // Move feet to airborne positions
            UpdateAirbornePose();
            UpdateBodyHeight();
            UpdateBodyTilt();
            ApplyIK();
            return;
        }

        // Just landed - transition smoothly to ground
        if (wasJumping && !isAirborne)
        {
            isLanding = true;
            wasJumping = false;
        }

        // Handle landing transition
        if (isLanding)
        {
            UpdateFootTargets();

            // Smoothly lerp to ground positions
            leftFootCurrentPos = Vector3.Lerp(leftFootCurrentPos, leftFootTarget, Time.deltaTime * landingTransitionSpeed);
            rightFootCurrentPos = Vector3.Lerp(rightFootCurrentPos, rightFootTarget, Time.deltaTime * landingTransitionSpeed);

            // Check if close enough to ground to resume normal stepping
            float leftDist = Vector3.Distance(leftFootCurrentPos, leftFootTarget);
            float rightDist = Vector3.Distance(rightFootCurrentPos, rightFootTarget);

            // Exit landing when feet are close OR if moving (to prevent getting stuck)
            bool isMoving = controller != null && controller.CurrentSpeed > 0.1f;
            if ((leftDist < 0.1f && rightDist < 0.1f) || isMoving)
            {
                isLanding = false;
                // Snap feet to targets to ensure clean transition
                leftFootCurrentPos = leftFootTarget;
                rightFootCurrentPos = rightFootTarget;
            }

            UpdateBodyHeight();
            UpdateBodyTilt();
            ApplyIK();
            return;
        }

        UpdateFootTargets();
        HandleStepping();
        UpdateBodyHeight();
        UpdateBodyTilt();
        UpdateArmSwing();
        ApplyIK();
    }

    void UpdateFootTargets()
    {
        // Get movement state and adjust parameters
        float currentFootOffset = walkFootOffset;

        if (controller != null)
        {
            switch (controller.State)
            {
                case SimpleVoxelController.MovementState.Turning:
                    currentFootOffset = 0; // No forward offset when turning
                    break;
                case SimpleVoxelController.MovementState.Sprinting:
                    currentFootOffset = sprintFootOffset;
                    break;
            }
        }

        // Get movement direction from body rotation
        Vector3 rightDir = bodyTransform != null ? bodyTransform.right : transform.right;

        // Calculate where to check for ground
        Vector3 movementOffset = Vector3.zero;
        if (controller != null && controller.CurrentSpeed > 0.1f)
        {
            movementOffset = controller.MoveDirection * currentFootOffset;
        }

        // Left and right foot positions
        Vector3 leftCheckPos = transform.position + rightDir * footOffsetSide + movementOffset;
        Vector3 rightCheckPos = transform.position - rightDir * footOffsetSide + movementOffset;

        // Find ground at those positions
        leftFootTarget = GetGroundPosition(leftCheckPos);
        rightFootTarget = GetGroundPosition(rightCheckPos);
    }

    Vector3 GetGroundPosition(Vector3 worldPosition)
    {
        // Raycast from above to find ground, checking extra distance for step-down
        Vector3 rayStart = worldPosition + Vector3.up * raycastStartHeight;
        RaycastHit hit;

        float totalDistance = raycastDistance + stepDownDistance;

        if (Physics.Raycast(rayStart, Vector3.down, out hit, totalDistance, terrainLayer))
        {
            return hit.point + Vector3.up * footHeightOffset;
        }

        // No ground found - return default airborne position
        return GetDefaultFootPosition(worldPosition);
    }

    Vector3 GetDefaultFootPosition(Vector3 sidePosition)
    {
        // Calculate default foot position in world space based on body orientation
        if (bodyTransform == null) return sidePosition;

        // Determine if this is left or right foot based on position
        Vector3 rightDir = bodyTransform.right;
        float side = Vector3.Dot(sidePosition - transform.position, rightDir) > 0 ? 1f : -1f;

        // Create offset with proper side
        Vector3 offset = new Vector3(defaultFootOffset.x * side, defaultFootOffset.y, defaultFootOffset.z);

        // Transform from body local space to world space using TransformPoint
        return bodyTransform.TransformPoint(offset);
    }

    void UpdateAirbornePose()
    {
        if (bodyTransform == null || controller == null) return;

        // Get current vertical velocity
        float verticalVelocity = controller.GetComponent<Rigidbody>()?.linearVelocity.y ?? 0f;

        // Calculate blend factor based on velocity
        // +maxUpwardVelocity = 1.0 (full airborne pose)
        // 0 velocity = 0.5 (halfway)
        // -maxDownwardVelocity = 0.0 (captured pose)
        float blend = Mathf.InverseLerp(maxDownwardVelocity, maxUpwardVelocity, verticalVelocity);

        // Create airborne pose (full XYZ from defaultFootOffset)
        Vector3 leftAirbornePose = new Vector3(-defaultFootOffset.x, defaultFootOffset.y, defaultFootOffset.z);
        Vector3 rightAirbornePose = new Vector3(defaultFootOffset.x, defaultFootOffset.y, defaultFootOffset.z);

        // Blend between captured pose and airborne pose based on velocity
        Vector3 leftLocalPose = Vector3.Lerp(leftFootJumpPos, leftAirbornePose, blend);
        Vector3 rightLocalPose = Vector3.Lerp(rightFootJumpPos, rightAirbornePose, blend);

        // Transform to world space
        Vector3 leftTargetWorld = bodyTransform.TransformPoint(leftLocalPose);
        Vector3 rightTargetWorld = bodyTransform.TransformPoint(rightLocalPose);

        // Smoothly transition feet to these positions
        leftFootCurrentPos = Vector3.Lerp(leftFootCurrentPos, leftTargetWorld, Time.deltaTime * airborneTransitionSpeed);
        rightFootCurrentPos = Vector3.Lerp(rightFootCurrentPos, rightTargetWorld, Time.deltaTime * airborneTransitionSpeed);

        // Reset foot angles to flat
        leftFootAngle = Mathf.Lerp(leftFootAngle, 0f, Time.deltaTime * airborneTransitionSpeed);
        rightFootAngle = Mathf.Lerp(rightFootAngle, 0f, Time.deltaTime * airborneTransitionSpeed);

        // Reset stepping state
        isLeftFootStepping = false;
        isRightFootStepping = false;
    }

    void HandleStepping()
    {
        // Get current step parameters based on movement state
        float currentStepDist = walkStepDistance;
        float currentStepDur = walkStepDuration;
        float currentStepHeight = walkStepHeight;
        float currentFootAngle = walkFootAngle;

        if (controller != null)
        {
            switch (controller.State)
            {
                case SimpleVoxelController.MovementState.Turning:
                    currentStepDist = turnStepDistance;
                    currentStepDur = turnStepDuration;
                    currentStepHeight = turnStepHeight;
                    currentFootAngle = turnFootAngle;
                    break;
                case SimpleVoxelController.MovementState.Sprinting:
                    currentStepDist = sprintStepDistance;
                    currentStepDur = sprintStepDuration;
                    currentStepHeight = sprintStepHeight;
                    currentFootAngle = sprintFootAngle;
                    break;
            }
        }

        // Only allow one foot to step at a time
        if (isLeftFootStepping || isRightFootStepping)
        {
            // Continue current step
            if (isLeftFootStepping)
            {
                leftStepProgress += Time.deltaTime / currentStepDur;

                if (leftStepProgress >= 1f)
                {
                    leftStepProgress = 0f;
                    isLeftFootStepping = false;
                    leftFootCurrentPos = leftFootTarget;
                    leftFootAngle = 0f; // Ensure foot is flat when step ends
                }
                else
                {
                    leftFootCurrentPos = CalculateStepPosition(leftFootStartPos, leftFootTarget, leftStepProgress, currentStepHeight);
                    leftFootAngle = CalculateFootAngle(leftStepProgress, currentFootAngle);
                }
            }

            if (isRightFootStepping)
            {
                rightStepProgress += Time.deltaTime / currentStepDur;

                if (rightStepProgress >= 1f)
                {
                    rightStepProgress = 0f;
                    isRightFootStepping = false;
                    rightFootCurrentPos = rightFootTarget;
                    rightFootAngle = 0f; // Ensure foot is flat when step ends
                }
                else
                {
                    rightFootCurrentPos = CalculateStepPosition(rightFootStartPos, rightFootTarget, rightStepProgress, currentStepHeight);
                    rightFootAngle = CalculateFootAngle(rightStepProgress, currentFootAngle);
                }
            }
        }
        else
        {
            // Check if either foot needs to step
            float leftDistance = Vector3.Distance(leftFootCurrentPos, leftFootTarget);
            float rightDistance = Vector3.Distance(rightFootCurrentPos, rightFootTarget);

            // Trigger step for the foot that's furthest from its target
            if (leftDistance > currentStepDist && leftDistance > rightDistance)
            {
                isLeftFootStepping = true;
                leftFootStartPos = leftFootCurrentPos;
                leftStepProgress = 0f;
            }
            else if (rightDistance > currentStepDist)
            {
                isRightFootStepping = true;
                rightFootStartPos = rightFootCurrentPos;
                rightStepProgress = 0f;
            }
        }
    }

    float CalculateFootAngle(float progress, float maxAngle)
    {
        // Calculate foot tilt based on step progress
        if (progress < tiltPeakProgress)
        {
            // Lerp from 0 to max angle (0 to tiltPeakProgress)
            float t = progress / tiltPeakProgress;
            return Mathf.Lerp(0f, maxAngle, t);
        }
        else if (progress < tiltReturnProgress)
        {
            // Hold at max angle (tiltPeakProgress to tiltReturnProgress)
            return maxAngle;
        }
        else
        {
            // Lerp from max angle back to 0 (tiltReturnProgress to 1.0)
            float t = (progress - tiltReturnProgress) / (1f - tiltReturnProgress);
            return Mathf.Lerp(maxAngle, 0f, t);
        }
    }

    Vector3 CalculateStepPosition(Vector3 start, Vector3 end, float progress, float height)
    {
        // Linear interpolation between start and end
        Vector3 position = Vector3.Lerp(start, end, progress);

        // Add arc (parabola) for natural stepping motion
        float arc = Mathf.Sin(progress * Mathf.PI) * height;
        position.y += arc;

        return position;
    }

    void UpdateBodyHeight()
    {
        // Don't adjust body height when not grounded (jumping/falling)
        if (controller != null && !controller.IsGrounded)
        {
            // Smoothly return to original height
            currentBodyHeight = Mathf.Lerp(currentBodyHeight, 0f, Time.deltaTime * bodyHeightAdjustSpeed);

            if (bodyTransform != null)
            {
                Vector3 bodyPos = bodyTransform.localPosition;
                // Apply crouch offset from controller
                float crouchOffset = controller != null ? controller.CrouchOffset : 0f;
                bodyPos.y = bodyHeightOffset + currentBodyHeight + crouchOffset;
                bodyTransform.localPosition = bodyPos;
            }
            return;
        }

        // Find the lowest foot target position
        float lowestFootHeight = Mathf.Min(leftFootTarget.y, rightFootTarget.y);

        // Calculate how much to lower the body
        // The body should lower so the lowest foot can reach the ground
        targetBodyHeight = lowestFootHeight - transform.position.y;

        // Clamp the adjustment so we only move down (not up) and max by stepDownDistance
        targetBodyHeight = Mathf.Clamp(targetBodyHeight, -stepDownDistance, 0f);

        // Smoothly interpolate to target height
        currentBodyHeight = Mathf.Lerp(currentBodyHeight, targetBodyHeight, Time.deltaTime * bodyHeightAdjustSpeed);

        // Apply the height adjustment to the body transform (adding back the original offset)
        if (bodyTransform != null)
        {
            Vector3 bodyPos = bodyTransform.localPosition;
            // Apply both step-down adjustment AND crouch offset from controller
            float crouchOffset = controller != null ? controller.CrouchOffset : 0f;
            bodyPos.y = bodyHeightOffset + currentBodyHeight + crouchOffset;
            bodyTransform.localPosition = bodyPos;
        }
    }

    void UpdateBodyTilt()
    {
        if (bodyTransform == null) return;

        // Determine target tilt based on movement state
        float targetTilt = 0f;

        if (controller != null && controller.State == SimpleVoxelController.MovementState.Sprinting)
        {
            targetTilt = sprintTiltAngle;
        }

        // Smoothly interpolate to target tilt
        currentBodyTilt = Mathf.Lerp(currentBodyTilt, targetTilt, Time.deltaTime * sprintTiltSpeed);

        // Apply tilt to body rotation (preserve Y rotation)
        Vector3 currentEuler = bodyTransform.eulerAngles;
        bodyTransform.rotation = Quaternion.Euler(currentBodyTilt, currentEuler.y, 0);
    }

    void UpdateArmSwing()
    {
        if (!enableArmSwing || leftArm == null || rightArm == null || controller == null) return;

        // Only swing arms when moving on ground
        if (controller.State == SimpleVoxelController.MovementState.Jumping ||
            controller.State == SimpleVoxelController.MovementState.Falling)
        {
            // Reset arms to neutral when airborne
            leftArm.localRotation = Quaternion.Lerp(leftArm.localRotation, Quaternion.identity, Time.deltaTime * 5f);
            rightArm.localRotation = Quaternion.Lerp(rightArm.localRotation, Quaternion.identity, Time.deltaTime * 5f);
            return;
        }

        // Increment swing time based on movement speed
        float speedMultiplier = Mathf.Clamp01(controller.CurrentSpeed / 5f);
        armSwingTime += Time.deltaTime * armSwingSpeed * speedMultiplier;

        // Calculate swing angles (opposite to legs)
        float leftSwing = Mathf.Sin(armSwingTime) * armSwingAngle * speedMultiplier;
        float rightSwing = -leftSwing; // Opposite

        // Apply rotation around X axis (forward/back swing)
        leftArm.localRotation = Quaternion.Euler(leftSwing, 0, 0);
        rightArm.localRotation = Quaternion.Euler(rightSwing, 0, 0);
    }

    void ApplyIK()
    {
        // Apply 2-bone IK to left leg
        if (leftLeg != null && leftShin != null && leftFoot != null)
        {
            SolveLegIK(leftLeg, leftShin, leftFoot, leftFootCurrentPos, leftFootAngle, true);
        }

        // Apply 2-bone IK to right leg
        if (rightLeg != null && rightShin != null && rightFoot != null)
        {
            SolveLegIK(rightLeg, rightShin, rightFoot, rightFootCurrentPos, rightFootAngle, false);
        }
    }

    void SolveLegIK(Transform thigh, Transform shin, Transform foot, Vector3 targetPos, float footAngle, bool isLeftLeg)
    {
        // Get the positions
        Vector3 thighPos = thigh.position;
        Vector3 shinPos = shin.position;

        // Calculate bone lengths
        float thighLength = Vector3.Distance(thighPos, shinPos);
        float shinLength = Vector3.Distance(shinPos, foot.position);
        float totalLength = thighLength + shinLength;

        // Direction from thigh to target
        Vector3 toTarget = targetPos - thighPos;
        float targetDistance = toTarget.magnitude;

        // Clamp to prevent overextension
        targetDistance = Mathf.Min(targetDistance, totalLength * 0.99f);
        Vector3 targetDirection = toTarget.normalized;

        // Law of cosines to find angles
        float thighAngle = Mathf.Acos(Mathf.Clamp(
            (thighLength * thighLength + targetDistance * targetDistance - shinLength * shinLength) /
            (2f * thighLength * targetDistance), -1f, 1f));

        // Pole direction (which way the knee points)
        Vector3 poleDirection = bodyTransform.forward;

        // Calculate the axis perpendicular to both target direction and pole
        Vector3 bendAxis = Vector3.Cross(targetDirection, poleDirection).normalized;

        // If cross product is zero, use a default axis
        if (bendAxis.magnitude < 0.01f)
        {
            bendAxis = bodyTransform.right * (isLeftLeg ? -1 : 1);
        }

        // Calculate knee position
        Vector3 kneeDirection = Quaternion.AngleAxis(-thighAngle * Mathf.Rad2Deg, bendAxis) * targetDirection;
        Vector3 kneePos = thighPos + kneeDirection * thighLength;

        // Point thigh toward knee
        Vector3 thighDir = (kneePos - thighPos).normalized;
        thigh.rotation = Quaternion.LookRotation(bendAxis, -thighDir) * Quaternion.Euler(0, 90, 0);

        // Point shin toward foot target
        Vector3 shinDir = (targetPos - kneePos).normalized;
        shin.rotation = Quaternion.LookRotation(bendAxis, -shinDir) * Quaternion.Euler(0, 90, 0);

        // Apply foot rotation with tilt
        // Base rotation keeps foot level with ground facing body direction
        Quaternion baseRotation = Quaternion.Euler(0, bodyTransform.eulerAngles.y, 0);

        // Add the pitch/tilt rotation around the right axis of the body
        Quaternion tiltRotation = Quaternion.AngleAxis(-footAngle, bodyTransform.right);

        foot.rotation = tiltRotation * baseRotation;
    }
}