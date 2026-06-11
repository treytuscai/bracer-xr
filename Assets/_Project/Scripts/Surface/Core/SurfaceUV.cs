// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Trey Tuscai

using UnityEngine;

namespace Surface.Core
{
    /// <summary>
    /// Single source of truth for the display UV mapping, shared by mesh generation
    /// (MeshGenerator.VertexJob) and touch detection (ForearmInteraction). Both must address the
    /// same texture region — any divergence would offset touch coordinates from the visual content.
    /// Static and Burst-compatible so the job can call it directly.
    /// </summary>
    public static class SurfaceUV
    {
        /// <summary>
        /// Maps a world-space point to display UV by linear projection onto the arm axes.
        /// Two-panel layout: U=[0,0.5] is the dorsal panel, U=[0.5,1] the palmar panel.
        ///   V: Dot(fromWrist, axis) / height, centered on offset and flipped so V=0 is
        ///      elbow-side, V=1 wrist-side.
        ///   U: Dot(fromWrist, axisRight) / width, centered on projCenter and offset to the dorsal
        ///      panel center (0.25). pronationScroll (pronation / 2π) adds up to 0.5 at full
        ///      palm-up — content scrolls to the palmar panel; the frame doesn't spin because
        ///      axisRight is camera-fixed (see ArmFrame).
        /// </summary>
        public static Vector2 Compute(
            Vector3 pt, Vector3 wristPos, Vector3 axis, Vector3 axisRight,
            float projCenter, float pronationScroll,
            float offset, float width, float height)
        {
            Vector3 fromWrist = pt - wristPos;

            float distAlong = Vector3.Dot(fromWrist, axis);
            float v = 1f - (((distAlong - offset) / Mathf.Max(height, 1e-4f)) + 0.5f);

            float projR = Vector3.Dot(fromWrist, axisRight);
            float u = ((projR - projCenter) / Mathf.Max(width, 1e-4f)) + 0.25f + pronationScroll;

            return new Vector2(u, v);
        }
    }
}
