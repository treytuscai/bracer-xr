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
    ///   Vertices (Vector4[])     — world-space bone joint positions for each active fingertip,
    ///                              consumed by ForearmInteraction for touch detection.
    ///
    /// Touch detection uses the bone joint center directly. It sits ~5-10mm above the skin (inside
    /// the finger), so `above` reads slightly positive even when the finger is flat; touchHoverHeight
    /// (~0.02m) covers this offset plus intentional hover.
    ///
    /// SnapshotMesh() runs once per LateUpdate before DepthReadback.TryDispatch() so the GPU
    /// silhouette and the touch candidates share the same frame's hand pose.
    /// </summary>
    public class HandMask : IDisposable
    {
        // ------------------------------------------------------------------
        // OVR HAND SKELETON BONE INDICES (XRHand layout)
        // Verified against runtime Transform.name via adb logcat 2026-05-30. Kept as a full
        // reference table; add indices to ActiveFingerTips to enable them. Must be declared
        // before ActiveFingerTips (static fields initialize in order).
        // ------------------------------------------------------------------
        // Suppress IDE0051/CS0414 (unused/assigned-but-never-read) on the reference-only indices.
#pragma warning disable IDE0051, CS0414
        private static readonly int Palm       = 0;
        private static readonly int Wrist      = 1;
        private static readonly int ThumbMeta  = 2,  ThumbProx  = 3,  ThumbDist  = 4,  ThumbTip  = 5;
        private static readonly int IndexMeta  = 6,  IndexProx  = 7,  IndexInter = 8,  IndexDist = 9,  IndexTip = 10;
        private static readonly int MiddleMeta = 11, MiddleProx = 12, MiddleInter = 13, MiddleDist = 14, MiddleTip = 15;
        private static readonly int RingMeta   = 16, RingProx   = 17, RingInter  = 18, RingDist  = 19, RingTip  = 20;
        private static readonly int LittleMeta = 21, LittleProx = 22, LittleInter = 23, LittleDist = 24, LittleTip = 25;
#pragma warning restore IDE0051, CS0414

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
        private readonly Vector4[] _managedVerts = new Vector4[ActiveFingerTips.Length];
        private int _vertCount;

        public Vector4[] Vertices     => _managedVerts;
        public int       VertexCount  => _vertCount;
        public bool      HasVertices  => _vertCount > 0;
        public Mesh      BakedMesh    => _bakedMesh;
        public Matrix4x4 LocalToWorld => _handMesh != null
            ? _handMesh.transform.localToWorldMatrix : Matrix4x4.identity;

        public HandMask(SkinnedMeshRenderer handMesh, OVRSkeleton handSkeleton)
        {
            _handMesh     = handMesh;
            _handSkeleton = handSkeleton;
        }

        /// <summary>
        /// Bakes the full hand mesh (for the GPU silhouette) and records the world-space
        /// joint position of each active fingertip bone (for touch detection).
        /// Call from LateUpdate before DepthReadback.TryDispatch().
        /// </summary>
        public void SnapshotMesh()
        {
            if (_handMesh == null) return;
            if (!_isInitialized && !TryInit()) return;

            _handMesh.BakeMesh(_bakedMesh);

            _vertCount = 0;
            var bones = _handSkeleton != null ? _handSkeleton.Bones : null;
            if (bones == null || bones.Count == 0) return;

            foreach (int boneIdx in ActiveFingerTips)
            {
                if (boneIdx >= bones.Count || _vertCount >= ActiveFingerTips.Length) continue;
                Transform bone = bones[boneIdx].Transform;
                if (bone == null) continue;
                Vector3 p = bone.position;
                _managedVerts[_vertCount++] = new Vector4(p.x, p.y, p.z, 0f);
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
