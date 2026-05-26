using UnityEngine;
using Unity.Collections;
using System;
using Surface.Buffer;

namespace Surface.Core
{
    /// <summary>
    /// Freezes the forearm surface on occlusion and holds it stable.
    ///
    /// Every live frame: captures all smoothed IsSurface hits into a
    /// grid-indexed wrist-local buffer (same density as the depth surface).
    ///
    /// On freeze: stops capturing. Infill() reprojects each stored local
    /// position back to world space via the current wrist transform and
    /// injects it directly into the matching grid cell — no VP math,
    /// one-to-one grid index mapping at full pixel-stride density.
    ///
    /// Wrist-local storage keeps the frozen surface glued to the wrist
    /// through minor arm movement during interaction.
    /// </summary>
    public class ForearmModel : IDisposable
    {
        private const float JumpThresh = 0.30f;

        private NativeArray<Vector3> _frozenLocalHits;
        private NativeArray<bool>    _frozenIsSurface;
        private int  _frozenRows, _frozenCols;
        private bool _isFrozen;
        private bool _hasData;
        private Vector3 _frozenWristPos;
        private bool    _hasFrozenWrist;

        public bool IsFrozen => _isFrozen;

        // ──────────────────────────────────────────────────────────────────
        // CAPTURE  (call every live frame when not frozen)
        // ──────────────────────────────────────────────────────────────────

        public void Capture(
            SurfaceBuffer buf, int rows, int cols,
            Vector3 wristPos, Quaternion wristRot)
        {
            if (_isFrozen) return;

            int total = rows * cols;
            EnsureArrays(total);

            Quaternion invRot = Quaternion.Inverse(wristRot);
            for (int i = 0; i < total; i++)
            {
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

        public void Freeze()   => _isFrozen = true;
        public void Unfreeze() => _isFrozen = false;

        // ──────────────────────────────────────────────────────────────────
        // INFILL  (call when frozen, before MeshGenerator)
        //
        // Injects by grid index, no reprojection needed.
        // Tolerates ±1 cell grid size jitter from bone tracking.
        // Gap fill covers masked cells the frozen buffer didn't reach.
        // ──────────────────────────────────────────────────────────────────

        public void Infill(
            SurfaceBuffer buf, int rows, int cols,
            Vector3 wristPos, Quaternion wristRot)
        {
            if (!_isFrozen || !_hasData) return;
            if (_hasFrozenWrist && Vector3.Distance(wristPos, _frozenWristPos) > JumpThresh) return;

            // Discard live seed+flood results — frozen buffer owns the surface entirely
            int total = rows * cols;
            for (int i = 0; i < total; i++)
                buf.IsSurface[i] = false;

            int injectRows = Mathf.Min(_frozenRows, rows);
            int injectCols = Mathf.Min(_frozenCols, cols);

            for (int r = 0; r < injectRows; r++)
            {
                for (int c = 0; c < injectCols; c++)
                {
                    int frozenIdx = r * _frozenCols + c;
                    int liveIdx   = r * cols + c;

                    if (!_frozenIsSurface[frozenIdx]) continue;

                    buf.Hits[liveIdx]      = wristPos + wristRot * _frozenLocalHits[frozenIdx];
                    buf.IsSurface[liveIdx] = true;
                }
            }

            // Gap fill — average 4-connected neighbors for cells the frozen buffer missed
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

        private void EnsureArrays(int total)
        {
            if (_frozenLocalHits.IsCreated && _frozenLocalHits.Length == total) return;
            if (_frozenLocalHits.IsCreated) _frozenLocalHits.Dispose();
            if (_frozenIsSurface.IsCreated) _frozenIsSurface.Dispose();
            _frozenLocalHits = new NativeArray<Vector3>(total, Allocator.Persistent);
            _frozenIsSurface = new NativeArray<bool>(total,    Allocator.Persistent);
        }

        public void Dispose()
        {
            if (_frozenLocalHits.IsCreated) _frozenLocalHits.Dispose();
            if (_frozenIsSurface.IsCreated) _frozenIsSurface.Dispose();
        }
    }
}
