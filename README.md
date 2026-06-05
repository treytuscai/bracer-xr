# Ink & Interface — XR Prototype

On-body interaction for Extended Reality. Ink & Interface renders an interactive UI directly on the user's forearm through Meta Quest 3 passthrough, and detects direct-touch input from the opposite hand against a forearm surface that is reconstructed live from the headset's depth data.

---

## How It Works

The forearm surface is continuously reconstructed from the Quest 3's environment depth API (stereo-camera computed depth). The pipeline runs on the GPU and Burst-compiled worker threads and is **frame-pipelined**: reconstruction jobs are scheduled and run asynchronously, and the main thread uploads the finished mesh only once they are complete.

_(An earlier prototype approximated the forearm as a geometric cylinder fit to the arm bones; it was dropped in favor of live depth reconstruction.)_

**Per-frame pipeline:**

1. Resolve wrist and elbow bones from the body skeleton to build the arm coordinate frame (axis, lateral, normal, pronation, orientation).
2. Stabilize the depth with a 3-frame, motion-reprojected per-texel median (the median rejects stereo "flying pixels" — temporal outliers — so the arm boundary stops flickering; reprojecting the history into the current head pose keeps it stable under head motion).
3. Render the interacting hand as a GPU silhouette and blit the stabilized depth through a reconstruction shader — both at the forearm crop's native depth-texel resolution, not full screen, so only the arm region is computed. Hand pixels are rejected at the source so they never enter the reconstruction.
4. Async GPU readback of the forearm crop; a Burst job unprojects depth texels into a world-space hit grid.
5. A seed region plus BFS flood isolates the forearm patch from background geometry.
6. Edge-aware (bilateral) depth smoothing on the GPU during the blit denoises the surface interior, and a parallel Burst boundary smoother de-steps the extracted edge cells.
7. Mesh generation via atomic parallel vertex/triangle emission with parallel grid-based normal computation, and linear, camera-fixed UV projection (plus a pronation scroll offset).

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
│   ├── DepthTemporalMedian.shader   3-frame reprojected median that stabilizes the depth
│   ├── MetaDepthCopy.shader         depth-reconstruction blit (world pos from depth)
│   └── HandMaskRender.shader        GPU hand silhouette
├── Materials/   Scenes/   Textures/
```

---

## Key Parameters

Primary tuning surface, on the `ForearmDepthSurface` component:

| Parameter | Default | Description |
|-----------|---------|-------------|
| `maskDilateTexels` | 0.8 | Hand-mask dilation radius (grid/depth texels) to cover fast-motion depth bleed |
| `depthSmoothRadius` | 1 | Edge-aware depth blur radius (depth texels; 0 = off, 1 = 3×3) |
| `depthSmoothThreshold` | 0.01 m | Max linear depth difference for a neighbor to be averaged in (keeps the blur from crossing the arm/background edge) |
| `seedRadialDist` | 0.05 m | Inner radius for confident forearm seed cells |
| `maxRadialDist` | 0.15 m | Outer wall that caps BFS flood growth away from the arm |
| `minFromWrist` / `maxFromElbow` | −0.12 / 0.02 m | Axial bounds for seed cells along the arm |
| `connectivityThreshold` | 0.01 m | Max 3D step between adjacent flood cells to count as connected |
| `edgeSmoothPasses` / `edgeWindowRadius` | 3 / 2 | Boundary smoothing iterations and per-pass neighborhood half-width (cells) |
| `maxQuadEdge` | 0.02 m | Rejects quads whose longest edge exceeds this (prevents bridging gaps). Must stay ≥ ~√2 × `connectivityThreshold` to admit valid quad diagonals |
| `displayHeight` / `displayWidth` | 0.4 / 0.4 m | Physical size of the UV display window (equal = square pixels) |
| `displayOffset` | 0.08 m | Center of the display window along the arm from the wrist |
| `lockOrientation` | false | Prevents the portrait to landscape rotation flip |

Touch tuning, on the `ForearmInteraction` component:

| Parameter | Default | Description |
|-----------|---------|-------------|
| `touchHoverHeight` | 0.005 m | How far above the surface a finger still registers |
| `touchDepth` | 0.04 m | How far through the surface a press can go before being ignored |
| `maxCellSearchDist` | 0.04 m | Max arm-frame distance to the nearest surface cell for a touch to register |

---

## License

The original work in this repository (primarily the code under `Assets/_Project/`) is licensed under the [Apache License 2.0](LICENSE) — © 2026 Trey Tuscai. You are free to use, modify, and build upon it, provided you retain the copyright and attribution notices (see [NOTICE](NOTICE)).

Third-party SDKs vendored elsewhere under `Assets/` and `Packages/` (Meta XR SDK, Meta Movement SDK, etc.) are governed by their own license terms, not the Apache License above.
