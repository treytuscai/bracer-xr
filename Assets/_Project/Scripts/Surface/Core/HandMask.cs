using UnityEngine;
using Surface.Buffer;
using Unity.Burst;
using Unity.Jobs;
using Unity.Collections;
using System;
using System.Collections.Generic;

namespace Surface.Core
{
    /// <summary>
    /// Flags depth grid cells that fall inside the interacting hand's volume each frame.
    ///
    /// APPROACH — dense vertex sampling instead of bone capsules
    /// Per-bone capsule masking requires walking the hand's bone hierarchy, computing
    /// capsule endpoints per bone, and testing each depth cell against multiple capsules.
    /// Instead we bake the SkinnedMeshRenderer to get the hand's actual skinned geometry,
    /// downsample to MaxVerts world-space points, and test each grid cell as a sphere
    /// against each sampled vertex. No hierarchy tracking, no capsule math — just a
    /// tight Burst inner loop of sqrMagnitude comparisons.
    ///
    /// DUAL STORAGE
    /// Vertices are stored in two forms simultaneously:
    ///   _nativeVerts  — NativeArray<Vector3> fed into the Burst masking job each frame.
    ///   _managedVerts — Vector4[] exposed publicly for CPU consumers (e.g. ForearmInteraction)
    ///                   and material SetVectorArray calls. The w component is unused (0).
    ///
    /// ALIGNMENT WITH DEPTH FRAME
    /// SnapshotMesh() is called from LateUpdate alongside DepthReadback.Schedule() so
    /// both the depth data and the hand vertices correspond to the same frame. Because
    /// _isProcessingMesh blocks SnapshotMesh while HandMaskJob is running, there is no
    /// race between the main-thread write to _nativeVerts and the Burst read of it.
    /// </summary>
    public class HandMask : IDisposable
    {
        // ------------------------------------------------------------------
        // CONSTANTS
        // ------------------------------------------------------------------
        // Maximum number of hand vertices kept per frame. Higher values improve
        // coverage on complex hand poses but increase the Burst inner-loop length
        // (each of the rows*cols grid cells is tested against every vertex).
        // 64 gives adequate coverage for a full hand at typical pixelStride values.
        private const int MaxVerts = 64;

        // ------------------------------------------------------------------
        // REFERENCES AND CONFIGURATION
        // ------------------------------------------------------------------
        // Sphere test radius around each sampled vertex (world meters).
        // Exposed publicly as Radius for shader/interaction consumers.
        private readonly float _padding;
        private SkinnedMeshRenderer _handMesh;
        private bool _isInitialized;

        // ------------------------------------------------------------------
        // VERTEX STORAGE
        // ------------------------------------------------------------------
        // Temporary mesh target for BakeMesh — reused each frame to avoid allocation.
        private Mesh _bakedMesh;
        // Managed list reused by GetVertices to avoid a new List<> allocation per frame.
        private readonly List<Vector3> _vertList = new List<Vector3>(1024);
        // Burst-readable vertex positions written by SnapshotMesh, read by HandMaskJob.
        private NativeArray<Vector3> _nativeVerts;
        // CPU/shader-readable copy of the same positions as Vector4 (w = 0, unused).
        // Fixed-size array avoids allocation; only [0.._vertCount) is valid.
        private readonly Vector4[] _managedVerts = new Vector4[MaxVerts];
        // Number of vertices actually written this frame (≤ MaxVerts).
        private int _vertCount;

        // ------------------------------------------------------------------
        // PUBLIC API
        // ------------------------------------------------------------------
        public Vector4[] Vertices    => _managedVerts;
        public int       VertexCount => _vertCount;
        public bool      HasVertices => _vertCount > 0;
        public float     Radius      => _padding;

        /// <summary>
        /// Stores references. Initialization of the baked mesh and NativeArray is deferred
        /// to the first SnapshotMesh call via TryInit so it happens after Unity has set up
        /// the SkinnedMeshRenderer.
        /// </summary>
        public HandMask(SkinnedMeshRenderer handMesh, float maskRadius)
        {
            _handMesh = handMesh;
            _padding  = maskRadius;
        }

        /// <summary>
        /// Bakes the hand mesh for the current skinned pose, downsamples to at most MaxVerts
        /// world-space points, and writes them into both native and managed storage.
        /// Call from LateUpdate alongside DepthReadback.Schedule() to stay frame-aligned.
        ///
        /// Downsampling: stride = total / MaxVerts, sampling every nth vertex. Gives uniform
        /// coverage across the vertex list but is biased toward the front of the mesh's vertex
        /// ordering — adequate for masking but not a true spatial uniform sample.
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
                _nativeVerts[_vertCount]  = world;
                _managedVerts[_vertCount] = new Vector4(world.x, world.y, world.z, 0f);
                _vertCount++;
            }
        }

        /// <summary>
        /// Schedules the Burst job that writes IsHandMasked for every grid cell.
        /// If no hand vertices are available (hand not tracked or off-screen),
        /// schedules ClearMaskJob instead to ensure IsHandMasked is in a known
        /// all-false state for the downstream seed+flood stage.
        /// </summary>
        public JobHandle Schedule(SurfaceBuffer surfBuf, JobHandle dependency)
        {
            if (_vertCount == 0)
            {
                var clearJob = new ClearMaskJob { IsHandMasked = surfBuf.IsHandMasked };
                return clearJob.Schedule(surfBuf.IsHandMasked.Length, 128, dependency);
            }

            var job = new HandMaskJob
            {
                Hits         = surfBuf.Hits,
                HasDepth     = surfBuf.HasDepth,
                Verts        = _nativeVerts,
                VertCount    = _vertCount,
                RadiusSq     = _padding * _padding,
                IsHandMasked = surfBuf.IsHandMasked
            };

            return job.Schedule(surfBuf.Hits.Length, 128, dependency);
        }

        /// <summary>
        /// Disposes the NativeArray and destroys the baked mesh asset.
        /// </summary>
        public void Dispose()
        {
            if (_nativeVerts.IsCreated) _nativeVerts.Dispose();
            if (_bakedMesh != null) UnityEngine.Object.Destroy(_bakedMesh);
        }

        /// <summary>
        /// Allocates the baked mesh target and persistent NativeArray on first use.
        /// Sets updateWhenOffscreen = true so Unity continues skinning the mesh even
        /// when the hand leaves the camera frustum — without this, BakeMesh returns
        /// stale geometry whenever the hand is partially outside the view.
        /// </summary>
        private bool TryInit()
        {
            if (_handMesh == null) return false;
            _handMesh.updateWhenOffscreen = true;
            _bakedMesh   = new Mesh();
            _nativeVerts = new NativeArray<Vector3>(MaxVerts, Allocator.Persistent);
            _isInitialized = true;
            return true;
        }

        // ==================================================================
        // BURST JOBS
        // ==================================================================

        /// <summary>
        /// Tests each depth grid cell against all sampled hand vertices.
        /// A cell is masked if its world-space hit position falls within RadiusSq
        /// of any vertex. Exits early on the first match to minimize inner-loop work.
        ///
        /// The distance check is written as a manual component expansion
        /// (d.x*d.x + d.y*d.y + d.z*d.z) rather than Vector3.sqrMagnitude to avoid
        /// a property call inside the tight VertCount inner loop under Burst compilation.
        /// </summary>
        [BurstCompile]
        struct HandMaskJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> Hits;
            [ReadOnly] public NativeArray<bool>    HasDepth;
            [ReadOnly] public NativeArray<Vector3> Verts;
            public int   VertCount;
            public float RadiusSq;
            [WriteOnly] public NativeArray<bool> IsHandMasked;

            public void Execute(int index)
            {
                if (!HasDepth[index]) { IsHandMasked[index] = false; return; }

                Vector3 p = Hits[index];
                for (int vi = 0; vi < VertCount; vi++)
                {
                    Vector3 d = p - Verts[vi];
                    if (d.x*d.x + d.y*d.y + d.z*d.z < RadiusSq)
                    {
                        IsHandMasked[index] = true;
                        return;
                    }
                }

                IsHandMasked[index] = false;
            }
        }

        /// <summary>
        /// Clears IsHandMasked to false for all cells when no hand vertices are available.
        /// Without this, stale mask values from previous frames could corrupt the seed+flood.
        /// </summary>
        [BurstCompile]
        struct ClearMaskJob : IJobParallelFor
        {
            [WriteOnly] public NativeArray<bool> IsHandMasked;
            public void Execute(int index) { IsHandMasked[index] = false; }
        }
    }
}
