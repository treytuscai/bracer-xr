// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Trey Tuscai

using UnityEngine;
using System;

namespace Surface.Core
{
    /// <summary>
    /// Each frame produces two outputs from the interacting hand:
    ///   BakedMesh / LocalToWorld — full skinned mesh consumed by DepthReadback to render
    ///                              the GPU silhouette into the hand mask RenderTexture.
    ///   Fingertips (Vector4[])   — world-space bone joint positions for each active fingertip,
    ///                              consumed by ForearmInteraction for touch detection.
    ///
    /// Touch detection uses the bone joint center directly. It sits ~5-10mm above the skin (inside
    /// the finger), so `above` reads slightly positive even when the finger is flat; touchHoverHeight
    /// covers this offset plus intentional hover.
    ///
    /// The two outputs update at different rates: UpdateFingertips() runs every LateUpdate so
    /// touch tracks the hand at render rate, while BakeSilhouette() runs only when DepthReadback
    /// commits a dispatch (~depth rate) so the silhouette pairs with the depth frame it masks.
    /// </summary>
    public class HandMask : IDisposable
    {
        // ------------------------------------------------------------------
        // OVR HAND SKELETON BONE INDICES (XRHand layout)
        // Verified against runtime Transform.name via adb logcat 2026-05-30. Kept as a full
        // reference table; add indices to ActiveFingerTips to enable them.
        // ------------------------------------------------------------------
        // Suppress IDE0051 (unused private member) on the reference-only indices.
#pragma warning disable IDE0051
        private const int Palm       = 0;
        private const int Wrist      = 1;
        private const int ThumbMeta  = 2,  ThumbProx  = 3,  ThumbDist  = 4,  ThumbTip  = 5;
        private const int IndexMeta  = 6,  IndexProx  = 7,  IndexInter = 8,  IndexDist = 9,  IndexTip = 10;
        private const int MiddleMeta = 11, MiddleProx = 12, MiddleInter = 13, MiddleDist = 14, MiddleTip = 15;
        private const int RingMeta   = 16, RingProx   = 17, RingInter  = 18, RingDist  = 19, RingTip  = 20;
        private const int LittleMeta = 21, LittleProx = 22, LittleInter = 23, LittleDist = 24, LittleTip = 25;
#pragma warning restore IDE0051

        // ------------------------------------------------------------------
        // ACTIVE FINGERTIPS
        // Add bone indices here to enable additional fingers.
        // Currently index only — other fingers cause false positives when
        // resting near the forearm surface.
        // ------------------------------------------------------------------
        private static readonly int[] ActiveFingerTips = { IndexTip };

        private readonly SkinnedMeshRenderer _handMesh;
        private readonly OVRSkeleton         _handSkeleton;
        private bool _isInitialized;

        private Mesh               _bakedMesh;
        private readonly Vector4[] _fingertips = new Vector4[ActiveFingerTips.Length];
        private int _fingertipCount;

        public Vector4[] Fingertips     => _fingertips;
        public int       FingertipCount => _fingertipCount;
        public bool      HasFingertips  => _fingertipCount > 0;
        public Mesh      BakedMesh    => _bakedMesh;
        public Matrix4x4 LocalToWorld => _handMesh != null
            ? _handMesh.transform.localToWorldMatrix : Matrix4x4.identity;

        public HandMask(SkinnedMeshRenderer handMesh, OVRSkeleton handSkeleton)
        {
            _handMesh     = handMesh;
            _handSkeleton = handSkeleton;
        }

        /// <summary>
        /// Bakes the full hand mesh for the GPU silhouette. Called by DepthReadback on committed
        /// dispatches only (~depth rate): BakeMesh is expensive and the silhouette is only
        /// consumed there.
        /// </summary>
        public void BakeSilhouette()
        {
            // sharedMesh is null until OVRMeshRenderer finishes hand-tracking init;
            // BakeMesh would error on every dispatch until then.
            if (_handMesh == null || _handMesh.sharedMesh == null) return;
            if (!_isInitialized && !TryInit()) return;

            _handMesh.BakeMesh(_bakedMesh);
        }

        /// <summary>
        /// Records the world-space joint position of each active fingertip bone for touch
        /// detection. Cheap bone reads — call every LateUpdate so touch tracks the hand at
        /// render rate.
        /// </summary>
        public void UpdateFingertips()
        {
            _fingertipCount = 0;
            var bones = _handSkeleton != null ? _handSkeleton.Bones : null;
            if (bones == null || bones.Count == 0) return;

            foreach (int boneIdx in ActiveFingerTips)
            {
                if (boneIdx >= bones.Count) continue;
                Transform bone = bones[boneIdx].Transform;
                if (bone == null) continue;
                Vector3 p = bone.position;
                _fingertips[_fingertipCount++] = new Vector4(p.x, p.y, p.z, 0f);
            }
        }

        public void Dispose()
        {
            if (_bakedMesh != null) UnityEngine.Object.Destroy(_bakedMesh);
        }

        /// <summary>
        /// Allocates the baked mesh target on first use. Sets updateWhenOffscreen = true
        /// so Unity continues skinning when the hand leaves the camera frustum.
        /// </summary>
        private bool TryInit()
        {
            if (_handMesh == null) return false;
            _handMesh.updateWhenOffscreen = true;
            _bakedMesh     = new Mesh();
            _isInitialized = true;
            return true;
        }
    }
}
