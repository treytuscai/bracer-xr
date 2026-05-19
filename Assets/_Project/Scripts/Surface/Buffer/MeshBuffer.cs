using Unity.Collections;
using UnityEngine;
using System;

namespace Surface.Buffer
{
    public class MeshBuffer : IDisposable
    {
        public NativeArray<Vector3> Vertices;
        public NativeArray<Vector2> UVs;
        public NativeArray<Vector2> EdgeDists;
        public NativeArray<int> Triangles;
        
        // Lookup to map grid (r,c) to vertex index
        public NativeArray<int> CellToVert;
        
        // For parallel triangle counting
        public NativeReference<int> TriCount;
        public NativeReference<int> VertCount;

        public void ResizeIfNeeded(int rows, int cols)
        {
            int totalCells = rows * cols;
            int maxTris = (rows - 1) * (cols - 1) * 6;

            if (Vertices.IsCreated && Vertices.Length == totalCells) return;

            Dispose();

            Vertices = new NativeArray<Vector3>(totalCells, Allocator.Persistent);
            UVs = new NativeArray<Vector2>(totalCells, Allocator.Persistent);
            EdgeDists = new NativeArray<Vector2>(totalCells, Allocator.Persistent);
            Triangles = new NativeArray<int>(maxTris, Allocator.Persistent);
            CellToVert = new NativeArray<int>(totalCells, Allocator.Persistent);
            
            TriCount = new NativeReference<int>(Allocator.Persistent);
            VertCount = new NativeReference<int>(Allocator.Persistent);
        }

        public void Dispose()
        {
            if (Vertices.IsCreated) Vertices.Dispose();
            if (UVs.IsCreated) UVs.Dispose();
            if (EdgeDists.IsCreated) EdgeDists.Dispose();
            if (Triangles.IsCreated) Triangles.Dispose();
            if (CellToVert.IsCreated) CellToVert.Dispose();
            if (TriCount.IsCreated) TriCount.Dispose();
            if (VertCount.IsCreated) VertCount.Dispose();
        }
    }
}