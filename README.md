# Ink & Interface — XR Prototype

On-body interaction for Extended Reality. Ink & Interface renders an interactive UI directly on the user's forearm through Meta Quest 3 passthrough, and detects direct-touch input from the opposite hand against a forearm surface that is reconstructed live from the headset's depth data.

---

## How It Works

The forearm surface is reconstructed every frame from the Quest 3's environment depth API (stereo-camera computed depth). The pipeline runs on the GPU and Burst-compiled worker threads. It is **frame-pipelined**: the depth/extraction/mesh jobs scheduled from one depth frame are harvested on the *next* `LateUpdate`, by which point they are already complete — so the main thread never blocks waiting on them. The only main-thread work is the unavoidable mesh upload to Unity.

_(An earlier prototype approximated the forearm as a geometric cylinder fit to the arm bones; it was dropped in favor of live depth reconstruction.)_

**Per-frame pipeline:**

1. Resolve wrist and elbow bones from the body skeleton to build the arm coordinate frame (axis, lateral, normal, pronation, orientation).
2. Render the interacting hand as a GPU silhouette and blit the depth texture through a reconstruction shader — both at the forearm crop's native depth-texel resolution, not full screen, so only the arm region is computed. Hand pixels are rejected at the source so they never enter the reconstruction.
3. Async GPU readback of the forearm crop; a Burst job unprojects depth texels into a world-space hit grid.
4. A seed region plus BFS flood isolates the forearm patch from background geometry.
5. Edge-aware (bilateral) depth smoothing on the GPU during the blit, and a parallel Burst boundary smoother on the extracted edge cells.
6. Mesh generation via atomic parallel vertex/triangle emission with parallel grid-based normal computation, and linear, camera-fixed UV projection (plus a pronation scroll offset).

**Touch detection:** each frame the interacting hand's skinned-mesh vertices are tested against the reconstructed surface. The nearest surface cell within range is found, the signed depth above the surface is computed, and a UV coordinate is derived at sub-cell precision from the actual finger position. Touch is live — the surface keeps updating during interaction, pronation works mid-touch, and there is no freeze step.

**UV design:** UV is a linear projection (not cylindrical). The camera-fixed lateral axis keeps the viewport upright regardless of wrist rotation, and pronation adds a U scroll offset so rotating the wrist reveals new content rather than spinning the image. Two panels: `U=[0,0.5]` dorsal, `U=[0.5,1]` palmar.

---

## Requirements

| | |
|------|------|
| Meta Quest 3 | v69+ firmware, Developer Mode enabled |
| Unity Editor | **2022.3.62f3** (must match exactly) with Android Build Support (SDK, NDK, OpenJDK) |
| Git LFS | `git lfs install` before cloning |
| SDKs (already in repo) | Meta XR All-in-One SDK (hand tracking, passthrough), Meta Movement SDK (body tracking) |

---

## Build & Run

```bash
git lfs install
git clone <repo-url>
```

1. Open the project in Unity Hub with editor **2022.3.62f3**.
2. Open `Assets/_Project/Scenes/MainScene.unity`.
3. Connect the Quest 3 over USB (`adb devices` to confirm).
4. **File -> Build Settings** -> confirm Android is the active platform and the scene is listed -> **Build and Run**.

All project, SDK, and OVRManager settings are committed — no manual configuration is required.

---

## Project Structure

Application code and assets live in `Assets/_Project/`; everything else under `Assets/` is SDK- or Unity-managed.

```
Assets/_Project/
├── Scripts/
│   └── Surface/
│       ├── Core/                  depth pipeline stages
│       │   ├── ArmFrame.cs            wrist/elbow -> arm coordinate frame
│       │   ├── DepthReadback.cs       async GPU readback + unprojection
│       │   ├── HandMask.cs            GPU hand silhouette for depth exclusion
│       │   ├── SurfaceExtractor.cs    seed + BFS flood isolation
│       │   ├── BoundarySmoother.cs    parallel Burst boundary smoothing
│       │   └── MeshGenerator.cs       parallel mesh + UV + normal emission
│       ├── Buffer/                shared NativeArray data buses
│       │   ├── SurfaceBuffer.cs
│       │   └── MeshBuffer.cs
│       ├── ForearmDepthSurface.cs     root MonoBehaviour; orchestrates the pipeline
│       └── ForearmInteraction.cs      touch detection against the surface
├── Shaders/
│   ├── ForearmProjection.shader    URP transparent shader for the surface mesh
│   ├── MetaDepthCopy.shader         depth-reconstruction blit (world pos from depth)
│   └── HandMaskRender.shader        GPU hand silhouette
├── Materials/   Scenes/   Textures/
```

---

## Key Parameters

Primary tuning surface, on the `ForearmDepthSurface` component:

The grid is sized to the forearm crop's native depth-texel footprint, so there is no sample-stride/density knob — sampling self-tunes to the depth resolution.

| Parameter | Default | Description |
|-----------|---------|-------------|
| `maskDilateTexels` | 1 | Hand-mask dilation radius (grid/depth texels) to cover fast-motion depth bleed |
| `depthSmoothRadius` | 1 | Edge-aware depth blur radius (depth texels; 0 = off, 1 = 3×3) |
| `depthSmoothThreshold` | 0.01 m | Max linear depth difference for a neighbor to be averaged in (keeps the blur from crossing the arm/background edge) |
| `seedRadialDist` | 0.05 m | Inner radius for confident forearm seed cells |
| `maxRadialDist` | 0.1 m | Outer wall that caps BFS flood growth away from the arm |
| `minFromWrist` / `maxFromElbow` | −0.12 / 0.02 m | Axial bounds for seed cells along the arm |
| `connectivityThreshold` | 0.02 m | Max 3D step between adjacent flood cells to count as connected |
| `edgeSmoothPasses` / `edgeWindowRadius` | 3 / 2 | Boundary smoothing iterations and per-pass neighborhood half-width (cells) |
| `maxQuadEdge` | 0.032 m | Rejects quads whose longest edge exceeds this (prevents bridging gaps) |
| `displayHeight` / `displayWidth` | 0.4 / 0.4 m | Physical size of the UV display window (equal = square pixels) |
| `displayOffset` | 0.08 m | Center of the display window along the arm from the wrist |

Touch tuning, on the `ForearmInteraction` component:

| Parameter | Default | Description |
|-----------|---------|-------------|
| `touchHoverHeight` | 0.02 m | How far above the surface a finger still registers |
| `touchDepth` | 0.04 m | How far through the surface a press can go before being ignored |
| `maxCellSearchDist` | 0.04 m | Max arm-frame distance to the nearest surface cell for a touch to register |
