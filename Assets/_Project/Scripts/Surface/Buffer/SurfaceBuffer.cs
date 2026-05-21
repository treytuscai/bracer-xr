using Unity.Collections;
using UnityEngine;
using System;

namespace Surface.Buffer
{
    public class SurfaceBuffer : IDisposable
    {
        public NativeArray<Vector3> Hits;
        public NativeArray<Vector3> Smoothed;
        public NativeArray<bool> IsSurface;
        public NativeArray<bool> HasDepth;
        public NativeQueue<int> BFSQueue;
        public NativeArray<bool> BoundaryVisited;
        public NativeArray<bool> IsHandMasked;

        private int _currentSize = -1;

        public void ResizeIfNeeded(int rows, int cols)
        {
            int total = rows * cols;
            if (_currentSize == total) return;

            Dispose(); // Clear old memory

            Hits = new NativeArray<Vector3>(total, Allocator.Persistent);
            Smoothed = new NativeArray<Vector3>(total, Allocator.Persistent);
            IsSurface = new NativeArray<bool>(total, Allocator.Persistent);
            HasDepth = new NativeArray<bool>(total, Allocator.Persistent);
            BFSQueue = new NativeQueue<int>(Allocator.Persistent);
            BoundaryVisited = new NativeArray<bool>(total, Allocator.Persistent);
            IsHandMasked = new NativeArray<bool>(total, Allocator.Persistent);
            
            _currentSize = total;
        }

        public void Dispose()
        {
            if (Hits.IsCreated) Hits.Dispose();
            if (Smoothed.IsCreated) Smoothed.Dispose();
            if (IsSurface.IsCreated) IsSurface.Dispose();
            if (HasDepth.IsCreated) HasDepth.Dispose();
            if (BoundaryVisited.IsCreated) BoundaryVisited.Dispose();
            if (BFSQueue.IsCreated) BFSQueue.Dispose();
            if (IsHandMasked.IsCreated) IsHandMasked.Dispose();
            _currentSize = -1;
        }
    }
}