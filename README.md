# Ink & Interface — XR Prototype

On-body interaction for Extended Reality. Ink & Interface renders an interactive UI directly on the user's forearm through Meta Quest 3 passthrough, and detects direct-touch input from the opposite hand against a forearm surface that is reconstructed live from the headset's depth data.

---

## How It Works

The forearm surface is reconstructed every frame from the Quest 3's environment depth API (stereo-camera computed depth). The pipeline runs on the GPU and Burst-compiled worker threads, with no main-thread blocking beyond two minimal sync points.

_(An earlier prototype approximated the forearm as a geometric cylinder fit to the arm bones; it was dropped in favor of live depth reconstruction.)_

**Per-frame pipeline:**

1. Resolve wrist and elbow bones from the body skeleton to build the arm coordinate frame (axis, lateral, normal, pronation, orientation).
2. Render the interacting hand as a GPU silhouette using Meta's depth-camera projection, and blit the depth texture through a reconstruction shader — hand pixels are rejected at the source so they never enter the reconstruction.
3. Async GPU readback of the forearm crop; Burst jobs unproject depth pixels into a world-space hit grid.
4. A seed region plus BFS flood isolates the forearm patch from background geometry.
5. Laplacian smoothing on the hit grid and contour smoothing on the boundary.
6. Mesh generation via atomic parallel vertex/triangle emission, with linear, camera-fixed UV projection (plus a pronation scroll offset).

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
4. **File → Build Settings** → confirm Android is the active platform and the scene is listed → **Build and Run**.

All project, SDK, and OVRManager settings are committed — no manual configuration is required.

---

## Project Structure

Application code and assets live in `Assets/_Project/`; everything else under `Assets/` is SDK- or Unity-managed.

```
Assets/_Project/
├── Scripts/
│   └── Surface/
│       ├── Core/                  depth pipeline stages
│       │   ├── ArmFrame.cs            wrist/elbow → arm coordinate frame
│       │   ├── DepthReadback.cs       async GPU readback + unprojection
│       │   ├── HandMask.cs            GPU hand silhouette for depth exclusion
│       │   ├── SurfaceExtractor.cs    seed + BFS flood isolation
│       │   ├── SurfaceSmoother.cs     Laplacian + contour smoothing
│       │   └── MeshGenerator.cs       parallel mesh + UV emission
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

| Parameter | Default | Description |
|-----------|---------|-------------|
| `pixelStride` | 6 | Depth sample spacing (px). Lower = denser mesh, more compute |
| `maskDilateTexels` | 1 | Hand-mask dilation radius (mask texels) to cover fast-motion depth bleed |
| `seedRadialDist` | 0.05 m | Inner radius for confident forearm seed cells |
| `maxRadialDist` | 0.15 m | Outer wall that caps BFS flood growth away from the arm |
| `minFromWrist` / `maxFromElbow` | −0.12 / 0.02 m | Axial bounds for seed cells along the arm |
| `connectivityThreshold` | 0.010 m | Max 3D step between adjacent flood cells to count as connected |
| `maxQuadEdge` | 0.014 m | Rejects quads whose longest edge exceeds this (prevents bridging gaps) |
| `smoothPasses` / `edgeSmoothPasses` | 3 / 2 | Surface and boundary-contour smoothing iterations |
| `displayHeight` / `displayWidth` | 0.4 / 0.4 m | Physical size of the UV display window (equal = square pixels) |
| `displayOffset` | 0.08 m | Center of the display window along the arm from the wrist |

Touch tuning, on the `ForearmInteraction` component:

| Parameter | Default | Description |
|-----------|---------|-------------|
| `touchHoverHeight` | 0.02 m | How far above the surface a finger still registers |
| `touchDepth` | 0.04 m | How far through the surface a press can go before being ignored |
| `maxCellSearchDist` | 0.04 m | Max arm-frame distance to the nearest surface cell for a touch to register |
