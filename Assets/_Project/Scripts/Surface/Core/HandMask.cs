using UnityEngine;
using System;
using System.Collections.Generic;

namespace Surface.Core
{
    /// <summary>
    /// Bakes the OVR hand SkinnedMeshRenderer each frame to produce two outputs:
    ///   BakedMesh / LocalToWorld — consumed by DepthReadback to render the hand silhouette
    ///                              into a mask RenderTexture via CommandBuffer.DrawMesh.
    ///                              MetaDepthCopy samples this texture to reject hand depth
    ///                              pixels at the source, marking them HasDepth=false so they
    ///                              are excluded from seed+flood and touch detection.
    ///   Vertices (Vector4[])     — downsampled world-space hand positions consumed by
    ///                              ForearmInteraction to iterate finger candidates for touch.
    ///
    /// SnapshotMesh() is called once per LateUpdate before DepthReadback.Schedule() so
    /// both the GPU mask and the touch detection use the same frame's hand pose.
    /// </summary>
    public class HandMask : IDisposable
    {
        // Number of vertices downsampled into _managedVerts for touch detection.
        // BakedMesh retains the full mesh for GPU silhouette rendering (DrawMesh).
        // This only controls how many candidates ForearmInteraction scans per frame.
        private const int MaxVerts = 32;

        private SkinnedMeshRenderer _handMesh;
        private bool _isInitialized;

        private Mesh _bakedMesh;
        private readonly List<Vector3> _vertList = new List<Vector3>(1024);
        private readonly Vector4[] _managedVerts = new Vector4[MaxVerts];
        private int _vertCount;

        public Vector4[] Vertices    => _managedVerts;
        public int       VertexCount => _vertCount;
        public bool      HasVertices => _vertCount > 0;
        public Mesh      BakedMesh   => _bakedMesh;
        public Matrix4x4 LocalToWorld => _handMesh != null
            ? _handMesh.transform.localToWorldMatrix : Matrix4x4.identity;

        public HandMask(SkinnedMeshRenderer handMesh)
        {
            _handMesh = handMesh;
        }

        /// <summary>
        /// Bakes the hand mesh for the current skinned pose and downsamples to MaxVerts
        /// world-space points. Call from LateUpdate before DepthReadback.Schedule().
        /// </summary>
        public void SnapshotMesh()
        {
            if (_handMesh == null) return;
            if (!_isInitialized && !TryInit()) return;

            _handMesh.BakeMesh(_bakedMesh);
            _bakedMesh.GetVertices(_vertList);

            int total = _vertList.Count;
            int step  = Mathf.Max(1, total / MaxVerts);
            Matrix4x4 l2w = _handMesh.transform.localToWorldMatrix;
            _vertCount = 0;

            for (int i = 0; i < total && _vertCount < MaxVerts; i += step)
            {
                Vector3 world = l2w.MultiplyPoint3x4(_vertList[i]);
                _managedVerts[_vertCount] = new Vector4(world.x, world.y, world.z, 0f);
                _vertCount++;
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
