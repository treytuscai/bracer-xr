using UnityEngine;
using Unity.Collections;
using System;
using Surface.Buffer;

namespace Surface.Core
{
    /// <summary>
    /// Holds the forearm surface stable while the hand is in contact with the arm.
    ///
    /// PROBLEM: when the hand touches the forearm it occludes depth pixels, creating
    /// holes in the reconstruction that cause the mesh to flicker or collapse under
    /// the fingers. ForearmDepthSurface detects occlusion and calls Freeze/Infill
    /// to substitute a clean pre-contact snapshot instead of the corrupted live data.
    ///
    /// HOW IT WORKS — three roles:
    ///   Capture()  — called every live (non-frozen) frame to keep the snapshot fresh.
    ///                Stores each surface hit in wrist-local coordinates so the frozen
    ///                surface tracks minor arm movement during interaction.
    ///   Freeze()   — called on the frame occlusion is first detected. Stops capture so
    ///                the last clean snapshot is held indefinitely.
    ///   Infill()   — called every frozen frame, before MeshGenerator. Reprojects the
    ///                wrist-local snapshot back to world space and writes it into
    ///                SurfaceBuffer, replacing the corrupted live reconstruction entirely.
    ///
    /// WHY WRIST-LOCAL STORAGE?
    /// If world positions were frozen, the surface would stay fixed in space while the
    /// user moved their arm — the mesh would slide off the forearm during interaction.
    /// Storing hits relative to the wrist transform means the frozen surface rigidly
    /// follows the wrist: rotate or translate the arm and the snapshot moves with it.
    ///
    /// JUMP THRESHOLD
    /// If bone tracking jumps (wrist teleports > JumpThresh meters from the frozen
    /// reference), Infill aborts rather than wildly misplacing the frozen surface.
    /// </summary>
    public class ForearmModel : IDisposable
    {
        // ------------------------------------------------------------------
        // CONSTANTS
        // ------------------------------------------------------------------
        // Maximum wrist displacement (meters) from the frozen reference position
        // before Infill aborts. Guards against bone tracking jumps during interaction.
        private const float JumpThresh = 0.30f;

        // ------------------------------------------------------------------
        // FROZEN SNAPSHOT STORAGE
        // ------------------------------------------------------------------
        // Per-cell wrist-local hit positions captured by the last Capture() call.
        // Indexed by flat grid (row * _frozenCols + col), same layout as SurfaceBuffer.
        private NativeArray<Vector3> _frozenLocalHits;
        // Per-cell surface flag corresponding to _frozenLocalHits.
        private NativeArray<bool>    _frozenIsSurface;
        // Grid dimensions at capture time. May differ by ±1 from the live grid due
        // to bone tracking jitter shifting the depth crop region slightly each frame.
        private int _frozenRows, _frozenCols;

        // ------------------------------------------------------------------
        // STATE
        // ------------------------------------------------------------------
        private bool _isFrozen;
        // True once at least one successful Capture() has completed.
        // Prevents Infill from running before any valid snapshot exists.
        private bool _hasData;
        // Wrist position recorded at the last Capture() call. Used by Infill to
        // detect tracking jumps that would misplace the reprojected snapshot.
        private Vector3 _frozenWristPos;
        // False until the first Capture() sets _frozenWristPos, so the jump check
        // does not fire against the default Vector3.zero before any reference exists.
        private bool _hasFrozenWrist;

        public bool IsFrozen => _isFrozen;

        // ──────────────────────────────────────────────────────────────────
        // CAPTURE
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Stores the current smoothed surface into the wrist-local snapshot buffer.
        /// Call every live (non-frozen) frame so the snapshot is always up-to-date.
        /// Hand-masked cells are excluded to keep the snapshot clean for future infill.
        /// </summary>
        public void Capture(
            SurfaceBuffer buf, int rows, int cols,
            Vector3 wristPos, Quaternion wristRot)
        {
            if (_isFrozen) return;

            int total = rows * cols;
            EnsureArrays(total);

            // Invert the wrist rotation once for the whole loop.
            // The wrist-local transform is: localPos = invRot * (worldPos - wristPos).
            // This is equivalent to transforming into the wrist bone's local space.
            Quaternion invRot = Quaternion.Inverse(wristRot);
            for (int i = 0; i < total; i++)
            {
                // Exclude hand-masked cells so the snapshot only contains clean arm surface.
                bool onSurface = buf.IsSurface[i] && !buf.IsHandMasked[i];
                _frozenIsSurface[i] = onSurface;
                _frozenLocalHits[i] = onSurface
                    ? invRot * (buf.Hits[i] - wristPos)
                    : Vector3.zero;
            }

            _frozenRows     = rows;
            _frozenCols     = cols;
            _frozenWristPos = wristPos;
            _hasFrozenWrist = true;
            _hasData        = true;
        }

        // ──────────────────────────────────────────────────────────────────
        // FREEZE / UNFREEZE
        // ──────────────────────────────────────────────────────────────────

        /// <summary> Stop capturing and hold the current snapshot. Call on the first occluded frame. </summary>
        public void Freeze()   => _isFrozen = true;

        /// <summary> Resume capturing from live depth data. Call when occlusion ends. </summary>
        public void Unfreeze() => _isFrozen = false;

        // ──────────────────────────────────────────────────────────────────
        // INFILL
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reprojects the wrist-local snapshot into SurfaceBuffer, replacing the live
        /// reconstruction entirely. Call every frozen frame, before MeshGenerator.
        ///
        /// Grid size tolerance: injects only the overlapping region between the frozen
        /// and live grids (Min of each dimension), so ±1 cell jitter in the depth crop
        /// doesn't cause out-of-bounds access or a complete surface drop.
        ///
        /// Gap fill: after injection, cells with no frozen counterpart are filled by
        /// averaging their 4-connected surface neighbours (if at least 2 are valid).
        /// Note that the fill runs in a single forward pass, so it can chain across
        /// multiple hops — this is intentional and fills holes more completely.
        /// </summary>
        public void Infill(
            SurfaceBuffer buf, int rows, int cols,
            Vector3 wristPos, Quaternion wristRot)
        {
            if (!_isFrozen || !_hasData) return;

            // Abort if the wrist has jumped far from the frozen reference — the reprojected
            // surface would land in the wrong position. Skipping one frame is preferable.
            if (_hasFrozenWrist && Vector3.Distance(wristPos, _frozenWristPos) > JumpThresh) return;

            // Clear all live seed+flood results; the frozen snapshot owns the surface entirely.
            int total = rows * cols;
            for (int i = 0; i < total; i++)
                buf.IsSurface[i] = false;

            // Inject frozen cells into the overlapping grid region.
            int injectRows = Mathf.Min(_frozenRows, rows);
            int injectCols = Mathf.Min(_frozenCols, cols);

            for (int r = 0; r < injectRows; r++)
            {
                for (int c = 0; c < injectCols; c++)
                {
                    int frozenIdx = r * _frozenCols + c;
                    int liveIdx   = r * cols + c;

                    if (!_frozenIsSurface[frozenIdx]) continue;

                    // Inverse of the capture transform: worldPos = wristPos + wristRot * localPos.
                    buf.Hits[liveIdx]      = wristPos + wristRot * _frozenLocalHits[frozenIdx];
                    buf.IsSurface[liveIdx] = true;
                }
            }

            // Gap fill: cover cells outside the frozen region or missed by grid jitter.
            // Requires >= 2 valid neighbours to avoid spawning surface from a single
            // isolated point, which would produce unreliable geometry.
            for (int i = 0; i < total; i++)
            {
                if (buf.IsSurface[i]) continue;

                int r = i / cols, c = i % cols;
                Vector3 sum   = Vector3.zero;
                int     count = 0;

                if (r > 0      && buf.IsSurface[i - cols]) { sum += buf.Hits[i - cols]; count++; }
                if (r < rows-1 && buf.IsSurface[i + cols]) { sum += buf.Hits[i + cols]; count++; }
                if (c > 0      && buf.IsSurface[i - 1])    { sum += buf.Hits[i - 1];    count++; }
                if (c < cols-1 && buf.IsSurface[i + 1])    { sum += buf.Hits[i + 1];    count++; }

                if (count >= 2)
                {
                    buf.Hits[i]      = sum / count;
                    buf.IsSurface[i] = true;
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // HELPERS
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures the snapshot arrays are allocated to exactly <paramref name="total"/> cells.
        /// Reallocates if the size has changed and clears _hasData so a stale snapshot
        /// from the old allocation is never used by Infill after the resize.
        /// </summary>
        private void EnsureArrays(int total)
        {
            if (_frozenLocalHits.IsCreated && _frozenLocalHits.Length == total) return;
            if (_frozenLocalHits.IsCreated) _frozenLocalHits.Dispose();
            if (_frozenIsSurface.IsCreated) _frozenIsSurface.Dispose();
            _frozenLocalHits = new NativeArray<Vector3>(total, Allocator.Persistent);
            _frozenIsSurface = new NativeArray<bool>(total,    Allocator.Persistent);
            // Invalidate the snapshot so Infill doesn't read uninitialized array data.
            _hasData = false;
        }

        /// <summary>
        /// Disposes the snapshot NativeArrays. Call when the component is destroyed.
        /// </summary>
        public void Dispose()
        {
            if (_frozenLocalHits.IsCreated) _frozenLocalHits.Dispose();
            if (_frozenIsSurface.IsCreated) _frozenIsSurface.Dispose();
        }
    }
}
