using UnityEngine;

namespace Surface.Core
{
    /// <summary>
    /// Resolves body skeleton bones each frame, computes the forearm coordinate frame,
    /// and temporally smooths pronation and screen-orientation angles.
    ///
    /// DESIGN — why not bone rotation, and why linear not cylindrical UV?
    /// Bone-fixed UV spins with the forearm: pronating palm-down to palm-up rotates
    /// the texture 180°, flipping text mid-motion. Camera-fixed AxisRight keeps the
    /// viewport upright; pronation is measured separately and added as a U scroll offset
    /// so wrist rotation reveals new content rather than spinning the image in place.
    /// Cylindrical UV (angle around the arm axis) was considered but rejected: the
    /// forearm's radius varies from wrist to elbow, so angle-based U swims as the
    /// visible surface patch shifts. Linear projection onto a fixed-width window is
    /// stable regardless of the arm's irregular geometry.
    ///
    /// The coordinate frame produces three axes:
    ///   Axis      — wrist->elbow unit vector (primary arm direction).
    ///   AxisRight — camera-fixed lateral axis; the horizontal edge of the stable viewport.
    ///   AxisUp    — camera-facing arm surface normal; completes the frame.
    ///
    /// ForearmDepthSurface calls TryUpdate() once per LateUpdate and then reads
    /// the public properties for the remainder of that frame.
    ///
    /// Consumers: ForearmDepthSurface (direct), DepthReadback, SurfaceExtractor, MeshGenerator.
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
        /// <summary>
        /// When true, suppresses the portrait to landscape UV rotation: Orientation is driven toward
        /// 0 regardless of arm orientation, so the display never flips 90°. Toggling it back to
        /// false lets the orientation snapping resume.
        /// </summary>
        public bool _lockOrientation;

        // ------------------------------------------------------------------
        // TEMPORAL SMOOTHING STATE
        // Survive across frames. Values are exponentially smoothed each frame
        // to reduce jitter from noisy bone tracking.
        // Note: the Lerp alphas (0.15 and 0.10) are frame-rate dependent.
        // At the Quest 3's fixed 90 Hz they produce consistent results;
        // behavior will differ at other refresh rates.
        // ------------------------------------------------------------------
        private float _smoothedPronation;
        private float _orientationAngle;

        // ------------------------------------------------------------------
        // PUBLIC PROPERTIES (valid only after TryUpdate returns true)
        // ------------------------------------------------------------------
        public Vector3    WristPos      { get; private set; }
        public Vector3    ElbowPos      { get; private set; }
        // Unit vector pointing wrist->elbow — the primary arm axis.
        public Vector3    Axis          { get; private set; }
        // Camera-fixed lateral axis. Anchoring this to the camera rather than the bone
        // means the UV projection never rotates when the user pronates their wrist —
        // the viewport stays upright and wrist rotation scrolls the texture instead.
        public Vector3    AxisRight     { get; private set; }
        // Camera-facing arm surface normal; completes the right-handed frame with Axis and AxisRight.
        public Vector3    AxisUp        { get; private set; }
        // Forearm twist in radians: 0 = palm-down, PI = palm-up.
        // Added as a U scroll offset in MeshGenerator: rotating the wrist shifts which
        // column of the texture is visible, scrolling the display rather than spinning it.
        // Clamped to [0, PI] — values past palm-down in either direction are held at 0.
        public float      Pronation     { get; private set; }
        // Smoothed portrait/landscape angle: 0 = arm vertical (portrait), -PI/2 = arm horizontal (landscape).
        // Applied as a 2D UV rotation in MeshGenerator so display content is always readable.
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

            // 3. ORTHOGONAL FRAME (camera-facing)
            // AxisRight is anchored to the camera, not the bone, to prevent texture spin
            // on pronation. UV projection onto this axis is linear (not cylindrical) because
            // the forearm's varying radius makes angle-based U unstable. See class summary.
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

            // 4. PRONATION (signed forearm twist from palm-down reference)
            // In the IOBT rig wrist.up points toward the palm, so -wrist.up is the dorsal
            // (back-of-hand) direction. Cross(axis, -wrist.up) projects that onto the plane
            // perpendicular to the arm axis, giving boneRight (the wrist's lateral direction).
            // palmDownRef is that same projection when the arm is in the palm-down pose
            // (dorsal faces worldUp), so the angle between them is 0 at palm-down and pi at
            // palm-up — gravity-anchored, consistent across portrait and landscape orientations.
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

            // 5. SCREEN ORIENTATION SNAPPING (portrait vs. landscape)
            //
            // Project the arm axis onto the camera's horizontal and vertical screen axes
            // to determine whether the arm appears more horizontal or vertical.
            // Dot(axis, cam.right) ≈ ±1 -> arm is horizontal (landscape).
            // Dot(axis, cam.up)    ≈ ±1 -> arm is vertical (portrait).
            float screenX = Vector3.Dot(axis, _cam.transform.right);
            float screenY = Vector3.Dot(axis, _cam.transform.up);
            // Portrait  (arm vertical):   target = 0     — no UV rotation needed.
            // Landscape (arm horizontal): target = -PI/2 — rotate UV frame 90° so the
            // display content reads left-to-right across the horizontal arm.
            // LockOrientation forces the portrait target so the display never flips.
            float target = (!_lockOrientation && Mathf.Abs(screenX) > Mathf.Abs(screenY)) ? -Mathf.PI * 0.5f : 0f;
            // Lerp (not LerpAngle) — values are in radians, range is [0, -PI/2], no wraparound needed.
            _orientationAngle = Mathf.Lerp(_orientationAngle, target, 0.1f);
            Orientation = _orientationAngle;

            return true;
        }
    }
}
