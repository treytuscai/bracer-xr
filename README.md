# Bracer XR

[![Unity](https://img.shields.io/badge/Unity-2022.3.62f3-000000?logo=unity&logoColor=white)](https://unity.com/releases/editor/archive)
[![Platform](https://img.shields.io/badge/Platform-Meta%20Quest%203-0467DF?logo=meta&logoColor=white)](https://www.meta.com/quest/quest-3/)
[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)

**The forearm as a touchscreen.** A directly touchable interface, reconstructed in real time from the Meta Quest 3's environment depth. No controllers, no markers, no external hardware.

This is an on-body interaction system for extended reality: the forearm acts as the display surface and the opposite hand as the input, bringing phone-style direct touch to bare skin.

_Built for the **Ink & Interface** research study, exploring how body artist principles carry over to authoring on-skin interfaces. This repository is the XR prototype behind it: the reconstructed forearm surface those interfaces build on._

---

## How It Works

The forearm surface is reconstructed continuously from the Quest 3's environment depth, which the headset computes by stereo-matching its passthrough cameras. The pipeline spans the GPU and Burst-compiled worker threads and is **frame-pipelined**: reconstruction jobs are scheduled asynchronously, and the main thread uploads each finished mesh only once its jobs complete.

**In brief:** each frame, take the headset's depth image, discard everything that is not forearm, turn what remains into a mesh, and project a touchable UI onto it.

```
depth -> hand silhouette + 3-frame median -> reconstruction blit -> async readback
      -> seed + BFS flood -> boundary smooth -> mesh + UV + normals -> touch test
```

_(An earlier version of Bracer XR approximated the forearm as a geometric cylinder fit to the arm bones; it was dropped in favor of the live depth reconstruction it uses today.)_

<details>
<summary><b>Per-frame pipeline (step by step)</b></summary>

1. Resolve the wrist and elbow bones from the body skeleton to construct the arm coordinate frame — axis, lateral, and normal vectors, plus pronation and orientation angles.
2. Render the interacting hand as a full-frame GPU silhouette, then stabilize the depth with a 3-frame, motion-reprojected per-texel median (the median rejects stereo "flying pixels" so the arm boundary stops flickering; reprojecting the history into the current head pose keeps it stable under head motion). The hand is carved out of the depth history during stabilization.
3. Blit the stabilized depth through a reconstruction shader at the forearm crop's native depth-texel resolution (not full screen, so only the arm region is computed). Hand pixels are rejected at the source using the same silhouette, dilated by a small margin to cover the 1-2 frame readback latency, so they never enter the reconstruction.
4. Read back the forearm crop from the GPU asynchronously; a Burst job unprojects its depth texels into a world-space hit grid.
5. A seed region plus BFS flood isolates the patch from background geometry, gated to two cylinders — the forearm and the palm (wrist to the middle-finger knuckle, so the hand is captured when waved or turned but the fingers are excluded).
6. A parallel Burst boundary smoother de-steps the extracted edge cells (the temporal median in step 2 is the depth denoise).
7. Generate the mesh: vertices and triangles are emitted in parallel through atomic counters, normals are computed in parallel across the grid, and UVs follow a linear, camera-fixed projection (with a pronation scroll offset).

</details>

### Touch detection

Each frame, the interacting hand's skinned-mesh vertices are tested against the reconstructed surface: the nearest surface cell within range is found, the signed distance above the surface is computed, and a UV coordinate is derived at sub-cell precision from the finger position. The surface keeps updating throughout interaction (pronation included), with no freeze step.

### UV design

UV is a linear projection (not cylindrical). The camera-fixed lateral axis keeps the viewport upright regardless of wrist rotation, and pronation adds a U scroll offset so rotating the wrist reveals new content rather than spinning the image. Two panels: `U=[0,0.5]` dorsal, `U=[0.5,1]` palmar.

---

## Limitations & Scope

Bracer XR is a research prototype with a deliberately narrow scope:

- **Quest 3 only.** It depends on Meta's environment depth API and Movement SDK body tracking; it does not run on other headsets.
- **The depth source is low-resolution.** Quest's environment depth is a single ~320×320 image spread across the entire field of view, so the forearm occupies only a fraction of it. The surface captures the arm and a finger fine, but thin gaps and small, fine-grained features are below what it can resolve.
- **One forearm, one interacting hand.** The system reconstructs a single arm and tracks a single touching hand at a time.
- **The touching hand is masked out, not reconstructed under.** To keep it from corrupting the mesh, the hand is cut from the depth, leaving a hole where it sits. Touch is detected from hand tracking rather than by sensing the fingertip in the depth map, so this does not break interaction.

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

All project, SDK, and OVRManager settings are committed. No manual configuration is required.

---

## Usage

Once the app is running on the headset:

1. **Hold out your left arm** in view of the headset. The UI appears on your forearm once body and hand tracking lock on.
2. **Touch with your other hand.** Bring a fingertip to the surface. Contact registers from a slight hover down through a shallow press (`touchHoverHeight` / `touchDepth`).
3. **Rotate your wrist to switch panels.** The display has two: dorsal (`U=[0,0.5]`) and palmar (`U=[0.5,1]`).
4. **Turn your arm upright or sideways** to flip between portrait and landscape (disable with `lockOrientation`).

The surface updates continuously. Keep your hand on it while moving or rotating the arm. There is no need to pull away or recalibrate.

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
│   ├── ForearmProjection.shader     URP transparent shader for the surface mesh
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
| `maskDilateTexels` | 1 | Hand-mask dilation radius (grid texels); carved from the depth history and rejected at consume with the same radius. Keep small — large values erode surface in a ring around the hand |
| `enablePalm` | true | Include the palm (wrist -> middle-finger MCP) in the reconstruction; off = forearm only |
| `seedRadialDist` | 0.05 m | Inner radius for confident forearm seed cells |
| `maxFloodDist` | 0.1 m | Outer wall that caps BFS flood growth away from the arm |
| `maxFromElbow` | 0.02 m | How far past the elbow the forearm cylinder extends (the wrist-side cap is flat; the palm cylinder takes over) |
| `connectivityThreshold` | 0.01 m | Max 3D step between adjacent flood cells to count as connected |
| `edgeSmoothPasses` / `edgeWindowRadius` | 3 / 2 | Boundary smoothing iterations and per-pass neighborhood half-width (cells) |
| `depthStepRatio` | 0.15 | Triangle discontinuity cut: drops a face whose cells differ in true depth by more than this fraction. Grazing-tolerant (fills steep surface, no holes) but cuts self-occluded folds (no webbing) |
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
