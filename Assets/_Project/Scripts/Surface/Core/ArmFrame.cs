// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Trey Tuscai

using UnityEngine;

namespace Surface.Core
{
    /// <summary>
    /// Which screen orientations the forearm display supports.
    ///   Auto: swap between portrait and landscape from the arm angle.
    ///   Portrait: lock upright.
    ///   Landscape: lock sideways.
    /// </summary>
    public enum DisplayOrientation { Auto, Portrait, Landscape }

    /// <summary>
    /// Resolves the wrist/elbow bones each frame and builds the forearm coordinate frame
    /// (Axis = wrist->elbow, AxisRight = camera-fixed lateral, AxisUp = surface normal), plus
    /// temporally-smoothed pronation and a portrait/landscape flag. ForearmDepthSurface calls
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
        private const int PalmCapBoneIndex = 30;

        // Forearm twist (deg) at the home pose (palm to camera): the rig's fixed wrist/elbow neutral roll
        // there. Pronation is measured from this so home reads PI (the palmar panel).
        private const float HomeTwistDeg = 67f;

        // ------------------------------------------------------------------
        // REFERENCES
        // _bodySkeleton and _centerEyeAnchor are set at construction.
        // Cam is resolved lazily on the first valid TryUpdate call because
        // the Camera component may not exist at construction time.
        // ------------------------------------------------------------------
        private readonly OVRSkeleton _bodySkeleton;
        private readonly Transform _centerEyeAnchor;

        // ------------------------------------------------------------------
        // CONFIGURATION
        // ------------------------------------------------------------------
        /// <summary> Which orientations the display supports; decides IsLandscape (see step 5). </summary>
        public DisplayOrientation OrientationMode;
        // When false, HasPalm stays false and the surface is forearm-only.
        public bool EnablePalm;

        // ------------------------------------------------------------------
        // TEMPORAL SMOOTHING STATE (survives across frames, damps bone-tracking jitter)
        // The Lerp alpha (0.15) is frame-rate dependent.
        // ------------------------------------------------------------------
        private float _smoothedPronation;

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
        // Forearm twist in radians, clamped [0, PI]: PI at the home pose (palm to camera), 0 at the
        // opposite face. Pitch-invariant (elbow-referenced; see step 4). MeshGenerator adds it as a U
        // scroll offset so wrist rotation scrolls the display.
        public float      Pronation     { get; private set; }
        // True when the arm is held horizontally (landscape), false when upright (portrait). The
        // consumer reads this directly to pick which image to show.
        public bool       IsLandscape   { get; private set; }
        public Camera     Cam           { get; private set; }
        // Middle-finger MCP of the arm's own hand — the palm-side cap. Valid only when HasPalm.
        public Vector3    PalmCapPos    { get; private set; }
        public bool       HasPalm       { get; private set; }

        /// <summary>
        /// Stores references for later use. Camera is resolved lazily on the first TryUpdate call.
        /// </summary>
        public ArmFrame(OVRSkeleton bodySkeleton, Transform centerEyeAnchor, DisplayOrientation orientationMode, bool enablePalm)
        {
            _bodySkeleton    = bodySkeleton;
            _centerEyeAnchor = centerEyeAnchor;
            OrientationMode = orientationMode;
            EnablePalm = enablePalm;
        }

        /// <summary>
        /// Resolves bones, validates tracking, computes the full arm coordinate
        /// frame, smooths pronation, and resolves the portrait/landscape orientation. Call once per LateUpdate.
        /// Returns false if tracking is unavailable or bones are invalid; the caller
        /// should hold the last valid frame rather than updating downstream state.
        /// </summary>
        public bool TryUpdate()
        {
            // 1. VALIDATION — fail fast if critical references are missing.
            if (_bodySkeleton == null || _centerEyeAnchor == null) return false;

            // Lazy camera resolution: defer GetComponent until the first valid frame.
            if (Cam == null)
            {
                Cam = _centerEyeAnchor.GetComponent<Camera>();
                if (Cam == null) return false;
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
            Vector3 toCamera = (Cam.transform.position - armMid).normalized;

            // Cross(axis, toCamera) lies flat across the arm as seen from the headset,
            // regardless of forearm rotation. Guard: if the arm points almost directly at
            // the camera the cross product is near-zero and normalization would produce NaN.
            Vector3 axisRightRaw = Vector3.Cross(axis, toCamera);
            if (axisRightRaw.sqrMagnitude < 0.001f) return false;
            AxisRight = axisRightRaw.normalized;

            // Cross(AxisRight, axis) completes the right-handed orthonormal frame.
            // The result is the arm's surface normal pointing generally toward the camera.
            AxisUp = Vector3.Cross(AxisRight, axis).normalized;

            // 4. PRONATION (signed forearm twist, measured against the elbow so it's pitch-invariant).
            // boneRight = Cross(axis, -wrist.up) is the wrist's lateral (wrist.up faces the palm); elbowRef
            // is elbow.up flattened onto the plane perpendicular to axis, a lateral fixed to the elbow. The
            // signed angle between them around axis is the wrist's roll relative to the forearm — whole-arm
            // pitch turns both together and cancels, leaving only real twist.
            Vector3 boneRightRaw = Vector3.Cross(axis, -wrist.up);
            Vector3 elbowRef     = elbow.up - Vector3.Dot(elbow.up, axis) * axis;

            if (boneRightRaw.sqrMagnitude >= 0.001f && elbowRef.sqrMagnitude >= 0.001f)
            {
                Vector3 boneRight = boneRightRaw.normalized;
                elbowRef          = elbowRef.normalized;

                float cos          = Vector3.Dot(boneRight, elbowRef);
                float sin          = Vector3.Dot(Vector3.Cross(boneRight, elbowRef), axis);
                float rawPronation = Mathf.Atan2(sin, cos);
                _smoothedPronation = Mathf.LerpAngle(
                    _smoothedPronation * Mathf.Rad2Deg,
                    rawPronation       * Mathf.Rad2Deg,
                    0.15f) * Mathf.Deg2Rad;
            }
            // Anchor the scroll to the home pose: palm-to-camera reads PI (palmar panel) and rotating
            // toward the dorsal side drops to 0 (dorsal panel), so the shown panel tracks the visible
            // face. DeltaAngle handles wraparound; the clamp drops over-rotation past either end.
            float pronationDeg = 180f - Mathf.DeltaAngle(_smoothedPronation * Mathf.Rad2Deg, HomeTwistDeg);
            Pronation    = Mathf.Clamp(pronationDeg * Mathf.Deg2Rad, 0f, Mathf.PI);

            // 5. ORIENTATION. Decide portrait vs landscape from how the arm projects onto the screen
            // (camera right vs up), ignoring depth. A dead band gives hysteresis: tilt past 55° to enter
            // landscape, back under 35° to return to portrait, holding the choice in between so boundary
            // wobble doesn't chatter the swap. Camera-relative, so it tracks head roll. Portrait and
            // Landscape modes lock the choice.
            if      (OrientationMode == DisplayOrientation.Portrait)  IsLandscape = false;
            else if (OrientationMode == DisplayOrientation.Landscape) IsLandscape = true;
            else
            {
                float screenH = Mathf.Abs(Vector3.Dot(axis, Cam.transform.right));
                float screenV = Mathf.Abs(Vector3.Dot(axis, Cam.transform.up));
                float angle   = Mathf.Atan2(screenH, screenV);
                const float flipToLandscape = 55f * Mathf.Deg2Rad;
                const float flipToPortrait  = 35f * Mathf.Deg2Rad;
                if      (angle > flipToLandscape) IsLandscape = true;
                else if (angle < flipToPortrait)  IsLandscape = false;
                // else: within the dead band, hold the current IsLandscape.
            }

            // 6. PALM CAP (optional) — middle-finger MCP; absent when the hand isn't tracked (forearm-only).
            HasPalm    = false;
            PalmCapPos = Vector3.zero;
            if (EnablePalm && bones.Count > PalmCapBoneIndex)
            {
                Transform cap = bones[PalmCapBoneIndex].Transform;
                if (cap != null)
                {
                    PalmCapPos = cap.position;
                    HasPalm    = true;
                }
            }

            return true;
        }
    }
}
