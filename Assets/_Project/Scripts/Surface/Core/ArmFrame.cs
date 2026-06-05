using UnityEngine;

namespace Surface.Core
{
    /// <summary>
    /// Resolves the wrist/elbow bones each frame and builds the forearm coordinate frame
    /// (Axis = wrist->elbow, AxisRight = camera-fixed lateral, AxisUp = surface normal), plus
    /// temporally-smoothed pronation and screen-orientation angles. ForearmDepthSurface calls
    /// TryUpdate() once per LateUpdate, then reads the public properties for that frame.
    ///
    /// WHY camera-fixed AxisRight and linear (not cylindrical) UV: a bone-fixed frame spins the
    /// texture 180° when the wrist pronates; instead AxisRight is anchored to the camera so the
    /// viewport stays upright, and pronation is added as a separate U scroll offset. Cylindrical
    /// (angle-around-axis) UV was rejected because the forearm's varying radius makes angle-based U
    /// swim; linear projection onto a fixed-width window is stable.
    /// </summary>
    public class ArmFrame
    {
        // ------------------------------------------------------------------
        // BONE INDICES (IOBT body skeleton, left arm)
        // Indices into OVRSkeleton.Bones as defined by the IOBT rig.
        // ------------------------------------------------------------------
        private const int WristBoneIndex = 19;
        private const int ElbowBoneIndex = 11;

        // ------------------------------------------------------------------
        // REFERENCES
        // _bodySkeleton and _centerEyeAnchor are set at construction.
        // _cam is resolved lazily on the first valid TryUpdate call because
        // the Camera component may not exist at construction time.
        // ------------------------------------------------------------------
        private readonly OVRSkeleton _bodySkeleton;
        private readonly Transform _centerEyeAnchor;
        private Camera _cam;

        // ------------------------------------------------------------------
        // CONFIGURATION
        // ------------------------------------------------------------------
        /// <summary> When true, forces Orientation toward 0 (portrait) so the display never flips. </summary>
        public bool _lockOrientation;

        // ------------------------------------------------------------------
        // TEMPORAL SMOOTHING STATE (survives across frames, damps bone-tracking jitter)
        // The Lerp alphas (0.15, 0.10) are frame-rate dependent — tuned for the Quest 3's 90 Hz.
        // ------------------------------------------------------------------
        private float _smoothedPronation;
        private float _orientationAngle;

        // ------------------------------------------------------------------
        // PUBLIC PROPERTIES (valid only after TryUpdate returns true)
        // ------------------------------------------------------------------
        public Vector3    WristPos      { get; private set; }
        public Vector3    ElbowPos      { get; private set; }
        // Unit vector wrist->elbow — the primary arm axis.
        public Vector3    Axis          { get; private set; }
        // Camera-fixed lateral axis (see class summary).
        public Vector3    AxisRight     { get; private set; }
        // Camera-facing arm surface normal; completes the right-handed frame.
        public Vector3    AxisUp        { get; private set; }
        // Forearm twist in radians, clamped [0, PI]: 0 = palm-down, PI = palm-up. MeshGenerator adds
        // it as a U scroll offset so wrist rotation scrolls the display rather than spinning it.
        public float      Pronation     { get; private set; }
        // Portrait/landscape angle: 0 = arm vertical, -PI/2 = arm horizontal. Applied as a UV rotation.
        public float      Orientation   { get; private set; }
        public Camera     Cam           { get; private set; }

        /// <summary>
        /// Stores references for later use. Camera is resolved lazily on the first TryUpdate call.
        /// </summary>
        public ArmFrame(OVRSkeleton bodySkeleton, Transform centerEyeAnchor, bool lockOrientation)
        {
            _bodySkeleton    = bodySkeleton;
            _centerEyeAnchor = centerEyeAnchor;
            _lockOrientation = lockOrientation;
        }

        /// <summary>
        /// Resolves bones, validates tracking, computes the full arm coordinate
        /// frame, and smooths pronation/orientation. Call once per LateUpdate.
        /// Returns false if tracking is unavailable or bones are invalid; the caller
        /// should hold the last valid frame rather than updating downstream state.
        /// </summary>
        public bool TryUpdate()
        {
            // 1. VALIDATION — fail fast if critical references are missing.
            if (_bodySkeleton == null || _centerEyeAnchor == null) return false;

            // Lazy camera resolution: defer GetComponent until the first valid frame
            // and assign the public Cam property only once.
            if (_cam == null)
            {
                _cam = _centerEyeAnchor.GetComponent<Camera>();
                if (_cam == null) return false;
                Cam = _cam;
            }

            var bones = _bodySkeleton.Bones;
            // OVRSkeleton.Bones may be empty during tracking initialization.
            if (bones == null || bones.Count <= Mathf.Max(WristBoneIndex, ElbowBoneIndex)) return false;

            Transform wrist = bones[WristBoneIndex].Transform;
            Transform elbow = bones[ElbowBoneIndex].Transform;
            if (wrist == null || elbow == null) return false;

            // 2. AXIS — build the primary wrist->elbow direction.
            Vector3 wristPos = wrist.position;
            Vector3 elbowPos = elbow.position;
            Vector3 delta    = elbowPos - wristPos;
            // Reject degenerate poses where bones are coincident.
            // sqrMagnitude < 0.001 corresponds to ≈3 cm, well below any real forearm length.
            if (delta.sqrMagnitude < 0.001f) return false;

            Vector3 axis = delta.normalized;
            WristPos      = wristPos;
            ElbowPos      = elbowPos;
            Axis          = axis;

            // 3. ORTHOGONAL FRAME (camera-facing) — AxisRight anchored to the camera, not the bone
            // (see class summary).
            Vector3 armMid   = (wristPos + elbowPos) * 0.5f;
            Vector3 toCamera = (_cam.transform.position - armMid).normalized;

            // Cross(axis, toCamera) lies flat across the arm as seen from the headset,
            // regardless of forearm rotation. Guard: if the arm points almost directly at
            // the camera the cross product is near-zero and normalization would produce NaN.
            Vector3 axisRightRaw = Vector3.Cross(axis, toCamera);
            if (axisRightRaw.sqrMagnitude < 0.001f) return false;
            AxisRight = axisRightRaw.normalized;

            // Cross(AxisRight, axis) completes the right-handed orthonormal frame.
            // The result is the arm's surface normal pointing generally toward the camera.
            AxisUp = Vector3.Cross(AxisRight, axis).normalized;

            // 4. PRONATION (signed forearm twist from a palm-down reference). In the IOBT rig
            // wrist.up faces the palm, so Cross(axis, -wrist.up) is the wrist's lateral direction
            // projected perpendicular to the axis; palmDownRef is that same vector in the palm-down
            // pose (dorsal faces worldUp). The angle between them is gravity-anchored: 0 palm-down, PI palm-up.
            Vector3 boneRightRaw = Vector3.Cross(axis, -wrist.up);
            Vector3 palmDownRef  = Vector3.Cross(axis, Vector3.up);
            if (boneRightRaw.sqrMagnitude >= 0.001f && palmDownRef.sqrMagnitude >= 0.001f)
            {
                Vector3 boneRight = boneRightRaw.normalized;
                palmDownRef       = palmDownRef.normalized;

                float cos          = Vector3.Dot(boneRight, palmDownRef);
                float sin          = Vector3.Dot(Vector3.Cross(boneRight, palmDownRef), axis);
                float rawPronation = Mathf.Atan2(sin, cos);
                _smoothedPronation = Mathf.LerpAngle(
                    _smoothedPronation * Mathf.Rad2Deg,
                    rawPronation       * Mathf.Rad2Deg,
                    0.15f) * Mathf.Deg2Rad;
            }
            Pronation = Mathf.Clamp(_smoothedPronation, 0f, Mathf.PI);

            // 5. SCREEN ORIENTATION SNAPPING — project the arm axis onto the camera's screen axes;
            // |Dot(axis, cam.right)| > |Dot(axis, cam.up)| means the arm looks horizontal (landscape).
            float screenX = Vector3.Dot(axis, _cam.transform.right);
            float screenY = Vector3.Dot(axis, _cam.transform.up);
            // Landscape -> rotate the UV frame -PI/2 so content reads left-to-right; portrait -> 0.
            // lockOrientation forces portrait. Lerp (not LerpAngle): range [0,-PI/2], no wraparound.
            float target = (!_lockOrientation && Mathf.Abs(screenX) > Mathf.Abs(screenY)) ? -Mathf.PI * 0.5f : 0f;
            _orientationAngle = Mathf.Lerp(_orientationAngle, target, 0.1f);
            Orientation = _orientationAngle;

            return true;
        }
    }
}
