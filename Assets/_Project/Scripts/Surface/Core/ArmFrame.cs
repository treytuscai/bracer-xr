using UnityEngine;

namespace Surface.Core
{
    /// <summary>
    /// Resolves body skeleton bones, computes the forearm coordinate frame,
    /// and temporally smooths pronation/orientation each frame.
    ///
    /// Extracts everything ForearmDepthSurface.SetupBasis() used to do,
    /// plus the 13 fields it required. ForearmDepthSurface calls TryUpdate()
    /// once per LateUpdate and reads properties for the rest of the frame.
    ///
    /// Consumers: BoundingBox, SurfaceExtractor, MeshGenerator, public API.
    /// </summary>
    public class ArmFrame
    {
        // ------------------------------------------------------------------
        // BONE INDICES (IOBT body skeleton)
        // ------------------------------------------------------------------
        private static readonly int WristBoneIndex = 19;
        private static readonly int ElbowBoneIndex = 11;
        private static readonly Vector3 WristUpLocal = Vector3.up;

        // ------------------------------------------------------------------
        // REFERENCES (set once at construction)
        // ------------------------------------------------------------------
        private readonly OVRSkeleton _bodySkeleton;
        private readonly Transform _centerEyeAnchor;
        private Camera _cam;

        // ------------------------------------------------------------------
        // RESOLVED BONES (updated each frame)
        // ------------------------------------------------------------------
        private Transform _wrist;
        private Transform _elbow;

        // ------------------------------------------------------------------
        // TEMPORAL STATE (smoothed across frames)
        // ------------------------------------------------------------------
        private float _rawPronation;
        private float _smoothedPronation;
        private float _orientationAngle;

        // ------------------------------------------------------------------
        // PUBLIC (read after TryUpdate returns true)
        // ------------------------------------------------------------------
        public Vector3 WristPos    { get; private set; }
        public Vector3 ElbowPos    { get; private set; }
        public float BoneLength { get; private set; }
        public Vector3 Axis        { get; private set; }
        public Vector3 AxisRight   { get; private set; }
        public Vector3 AxisUp      { get; private set; }
        /// <summary>
        /// Bone-relative right direction (perpendicular to axis, rotates with
        /// forearm pronation, does NOT change with camera movement). Used by
        /// TemporalInfill for stable cylindrical caching across frames.
        /// </summary>
        public Vector3 StableRight { get; private set; }
        /// <summary>
        /// Bone-relative up direction (perpendicular to axis and StableRight,
        /// rotates with forearm pronation). Completes the arm-intrinsic basis.
        /// </summary>
        public Vector3 StableUp    { get; private set; }
        public float   Pronation   { get; private set; }
        public float   Orientation { get; private set; }
        public Camera  Cam         { get; private set; }

        public ArmFrame(OVRSkeleton bodySkeleton, Transform centerEyeAnchor)
        {
            _bodySkeleton    = bodySkeleton;
            _centerEyeAnchor = centerEyeAnchor;
        }

        /// <summary>
        /// Resolves bones, validates tracking, computes the full arm coordinate
        /// frame, and smooths pronation/orientation. Call once per LateUpdate.
        /// Returns false if tracking is unavailable or bones are invalid.
        /// </summary>
        public bool TryUpdate()
        {
            // 1. VALIDATION
            if (_bodySkeleton == null || _centerEyeAnchor == null) return false;

            if (_cam == null) _cam = _centerEyeAnchor.GetComponent<Camera>();
            if (_cam == null) return false;
            Cam = _cam;

            var bones = _bodySkeleton.Bones;
            if (bones == null || bones.Count <= Mathf.Max(WristBoneIndex, ElbowBoneIndex)) return false;

            _wrist = bones[WristBoneIndex].Transform;
            _elbow = bones[ElbowBoneIndex].Transform;
            if (_wrist == null || _elbow == null) return false;

            // 2. AXIS
            Vector3 wristPos = _wrist.position;
            Vector3 elbowPos = _elbow.position;
            Vector3 delta = elbowPos - wristPos;
            if (delta.sqrMagnitude < 0.001f) return false;

            Vector3 axis = delta.normalized;
            float boneLength = delta.magnitude;

            WristPos = wristPos;
            ElbowPos = elbowPos;
            Axis     = axis;
            BoneLength = boneLength;

            // 3. ORTHOGONAL FRAME (camera-facing)
            Vector3 armMid = (wristPos + elbowPos) * 0.5f;
            Vector3 toCamera = (_cam.transform.position - armMid).normalized;

            AxisRight = Vector3.Cross(axis, toCamera).normalized;
            AxisUp    = Vector3.Cross(AxisRight, axis).normalized;

            // 4. PRONATION (forearm twist)
            Vector3 wristUpWorld = (_wrist.rotation * WristUpLocal).normalized;
            Vector3 boneRight = Vector3.Cross(axis, wristUpWorld).normalized;

            float cos = Vector3.Dot(boneRight, AxisRight);
            float sin = Vector3.Dot(Vector3.Cross(boneRight, AxisRight), axis);
            _rawPronation = Mathf.Atan2(sin, cos);
            _smoothedPronation = Mathf.Lerp(_smoothedPronation, _rawPronation, 0.15f);
            Pronation = _smoothedPronation;

            // 4b. STABLE ARM-INTRINSIC BASIS
            StableRight = boneRight;
            StableUp    = Vector3.Cross(boneRight, axis).normalized;

            // 5. SCREEN ORIENTATION SNAPPING
            float screenX = Vector3.Dot(axis, _cam.transform.right);
            float screenY = Vector3.Dot(axis, _cam.transform.up);
            float target  = Mathf.Abs(screenX) > Mathf.Abs(screenY) ? -Mathf.PI * 0.5f : 0f;
            _orientationAngle = Mathf.LerpAngle(_orientationAngle, target, 0.1f);
            Orientation = _orientationAngle;

            return true;
        }
    }
}