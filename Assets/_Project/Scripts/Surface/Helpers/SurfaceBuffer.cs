using Unity.Collections;
using UnityEngine;
using System;

namespace Surface.Helpers
{
    public class SurfaceBuffer : IDisposable
    {
        public NativeArray<Vector3> Source;
        public NativeArray<Vector3> Smoothed;
        public NativeArray<bool> IsSurface;

        private int _currentSize = -1;

        public void ResizeIfNeeded(int rows, int cols)
        {
            int total = rows * cols;
            if (_currentSize == total) return;

            Dispose(); // Clear old memory

            Source = new NativeArray<Vector3>(total, Allocator.Persistent);
            Smoothed = new NativeArray<Vector3>(total, Allocator.Persistent);
            IsSurface = new NativeArray<bool>(total, Allocator.Persistent);
            _currentSize = total;
        }

        // Copies data from your 2D arrays into the 1D NativeArrays
        public void Pack(Vector3[,] hits, bool[,] kept, int rows, int cols)
        {
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    Source[i] = hits[r, c];
                    IsSurface[i] = kept[r, c];
                }
        }

        // Copies data back to your 2D hits array
        public void Unpack(Vector3[,] hits, int rows, int cols)
        {
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    hits[r, c] = Source[r * cols + c];
        }

        public void Dispose()
        {
            if (Source.IsCreated) Source.Dispose();
            if (Smoothed.IsCreated) Smoothed.Dispose();
            if (IsSurface.IsCreated) IsSurface.Dispose();
            _currentSize = -1;
        }
    }
}