using Unity.Collections;
using UnityEngine;
using System;

namespace Surface.Buffer
{
    public class SurfaceBuffer : IDisposable
    {
        // Grid Data (Resizes with screen crop)
        public NativeArray<Vector3> Hits;
        public NativeArray<Vector3> Smoothed;
        public NativeArray<bool> IsSurface;
        public NativeArray<bool> HasDepth;
        public NativeQueue<int> BFSQueue;
        public NativeArray<bool> BoundaryVisited;
        public NativeArray<bool> IsHandMasked;

        // Atlas Data (Persistent across the whole session)
        public const int AtlasV = 128; 
        public const int AtlasU = 64;  
        public NativeArray<float> AtlasRadius; 
        public NativeArray<float>   AtlasWeights; 

        private int _currentSize = -1;

        public void InitializeAtlas() 
        {
        if (AtlasRadius.IsCreated) return;
            AtlasRadius = new NativeArray<float>(AtlasV * AtlasU, Allocator.Persistent);
            AtlasWeights = new NativeArray<float>(AtlasV * AtlasU, Allocator.Persistent);
        }

        public void ResizeIfNeeded(int rows, int cols)
        {
            int total = rows * cols;
            if (_currentSize == total) return;

            // ONLY dispose the grid-based arrays
            DisposeGridArrays();

            Hits = new NativeArray<Vector3>(total, Allocator.Persistent);
            Smoothed = new NativeArray<Vector3>(total, Allocator.Persistent);
            IsSurface = new NativeArray<bool>(total, Allocator.Persistent);
            HasDepth = new NativeArray<bool>(total, Allocator.Persistent);
            BoundaryVisited = new NativeArray<bool>(total, Allocator.Persistent);
            IsHandMasked = new NativeArray<bool>(total, Allocator.Persistent);
            
            // Queue is persistent, just clear it
            if (!BFSQueue.IsCreated) BFSQueue = new NativeQueue<int>(Allocator.Persistent);
            
            _currentSize = total;
        }

        private void DisposeGridArrays()
        {
            if (Hits.IsCreated) Hits.Dispose();
            if (Smoothed.IsCreated) Smoothed.Dispose();
            if (IsSurface.IsCreated) IsSurface.Dispose();
            if (HasDepth.IsCreated) HasDepth.Dispose();
            if (BoundaryVisited.IsCreated) BoundaryVisited.Dispose();
            if (IsHandMasked.IsCreated) IsHandMasked.Dispose();
        }

        public void Dispose()
        {
            DisposeGridArrays();
            if (BFSQueue.IsCreated) BFSQueue.Dispose();
            if (AtlasRadius.IsCreated) AtlasRadius.Dispose();
            if (AtlasWeights.IsCreated) AtlasWeights.Dispose();
            _currentSize = -1;
        }
    }
}