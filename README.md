# Ink & Interface — XR Prototype

On-body interaction paradigms for Extended Reality. Renders interactive digital UI on the user's forearm via Meta Quest 3 passthrough, with direct-touch input detected from the opposite hand against a live-reconstructed skin surface.

---

## Prerequisites

| Tool | Version | Notes |
|------|---------|-------|
| Unity Hub | 3.8+ | [unity.com/download](https://unity.com/download) |
| Unity Editor | **2022.3.62f3** | Must match exactly. Install via Unity Hub with **Android Build Support** (Android SDK & NDK, OpenJDK) |
| Git LFS | 2.0+ | `git lfs install` before cloning |
| Meta Quest Developer Hub | Latest | [developer.oculus.com/downloads](https://developer.oculus.com/downloads) |
| Meta Quest 3 | v69+ firmware | Developer Mode enabled via Meta phone app |

> **Important:** The Unity version must match exactly (`2022.3.62f3`). Mismatched versions cause reimports and can break prefab references.

---

## Getting Started

```bash
git lfs install
git clone <repo-url>
```

1. Open **Unity Hub** --> **Open** --> select the cloned `InkAndInterface` folder
2. Wait for Library regeneration (10–15 minutes on first open — this is normal)
3. Open the scene: `Assets/Scenes/MainScene.unity`
4. Connect Quest 3 via USB
5. **File → Build Settings** --> verify Android is the active platform and MainScene is in the scene list
6. **Build and Run**

That's it. All project settings, SDK config, and OVRManager settings are already committed.

---

## System Overview

The project has gone through two surface approaches. The depth-based reconstruction is the current system. The cylinder approach lives in `Deprecated/` and is preserved as a reference baseline for the team's earlier interaction experiments.

### Depth-Based Surface Reconstruction (Current)

The forearm surface is reconstructed live each frame from the Quest 3's environment depth API (stereo camera computed depth, not an IR sensor). The pipeline runs entirely on the GPU and CPU worker threads with no main thread blocking beyond two minimal sync points.

**Pipeline per frame:**
1. Resolve wrist and elbow bones from the body skeleton to compute the arm coordinate frame (axis, lateral, normal vectors, pronation, orientation).
2. Render the interacting hand as a GPU silhouette using Meta's depth camera projection. Blit the full depth texture through a reconstruction shader — hand pixels are rejected at the source so they never enter the reconstruction.
3. Async GPU readback of the forearm crop. Burst jobs unproject depth pixels into a world-space hit grid.
4. Seed cylinder + BFS flood isolates the forearm patch from background geometry.
5. Laplacian smoothing + boundary contour smoothing on the hit grid.
6. Mesh generation: atomic parallel vertex/triangle emission with UV projection (linear, camera-fixed, with pronation scroll offset).

**Touch detection:**  
Each frame, the interacting hand's skinned mesh vertices are tested against the reconstructed surface. The nearest surface cell within range is found, the signed depth above the surface is computed, and a UV coordinate is derived at sub-cell precision from the actual finger position. Touch is live — the surface updates continuously during interaction, pronation works mid-touch, and there is no freeze step.

**UV design:**  
UV is a linear projection (not cylindrical). The camera-fixed lateral axis keeps the viewport upright regardless of wrist rotation. Pronation adds a U scroll offset so rotating the wrist reveals new content rather than spinning the image. Two panels: U=[0,0.5] dorsal, U=[0.5,1] palmar.

### Cylinder-Based Surface (Deprecated — the team's Baseline)

An earlier approach approximating the forearm as a geometric cylinder fit to the arm bones. No depth data, no live reconstruction. Used as the foundation for the team's UI and interaction experiments on a separate branch. These experiments are pending port to the depth-based surface.

Scripts in `Assets/_Project/Scripts/Surface/Deprecated/`:

| File | Role |
|------|------|
| `ArmSurfaceGenerator.cs` | Builds the cylinder mesh from wrist/elbow bones |
| `CalibrationManager.cs` | Maps the cylinder to the physical arm at session start |
| `HandTrackingController.cs` | Hand input against the cylinder surface |
| `TouchInputManager.cs` | Touch event routing |
| `VisualFeedbackController.cs` | Feedback effects on touch |

---

## Current Project Structure

```
Assets/
├── _Project/                 <- OUR CODE AND ASSETS
│   ├── Scripts/
│   │   ├── Surface/
│   │   │   ├── Core/               depth pipeline stages (ArmFrame, DepthReadback, SurfaceExtractor, etc.)
│   │   │   ├── Buffer/             shared NativeArray data buses (SurfaceBuffer, MeshBuffer)
│   │   │   ├── ForearmDepthSurface.cs   root MonoBehaviour, orchestrates pipeline
│   │   │   ├── ForearmInteraction.cs    touch detection against reconstructed surface
│   │   │   └── Deprecated/         cylinder-based prototype (preserved for reference)
│   │   ├── Interaction/
│   │   ├── UI/
│   │   └── Data/
│   ├── Materials/
│   ├── Prefabs/
│   ├── Shaders/
│   │   ├── ForearmProjection.shader     URP transparent shader for the surface mesh
│   │   ├── MetaDepthCopy.shader         depth reconstruction blit (world positions from depth texture)
│   │   └── HandMaskRender.shader        GPU hand silhouette for depth exclusion
│   └── Textures/
├── Scenes/
├── Oculus/                   <- DO NOT MODIFY (Meta SDK config)
├── Resources/                <- DO NOT MODIFY (Meta SDK runtime settings)
├── Settings/                 <- DO NOT MODIFY (URP pipeline settings)
├── StreamingAssets/          <- DO NOT MODIFY
└── XR/                       <- DO NOT MODIFY (XR plugin settings)
```

All our work goes in `_Project/`. Everything else is SDK or Unity-managed.

---

## Scene Hierarchy

```
MainScene
├── OVRCameraRig
│   ├── TrackingSpace
│   │   ├── LeftHandAnchor
│   │   ├── RightHandAnchor
│   │   └── CenterEyeAnchor
│   └── OVRPassthroughLayer
├── Directional Light
└── [Managers]
```

---

## Scene Editing Rules

**DO NOT edit `MainScene.unity` without coordinating first.**

Unity scenes are serialized YAML. Even small changes (moving an object, clicking a checkbox) rewrite large sections of the file, causing **unmergeable git conflicts**. Two people editing the same scene simultaneously will almost certainly lose someone's work.

**Rules:**
- **Announce in Discord before opening the scene for editing**
- **Announce when you're done** so others can pull
- If you need to add new functionality, **use prefabs** — build your feature as a prefab in `_Project/Prefabs/`, then one person adds it to the scene
- If a merge conflict does happen on a `.unity` file, **do not attempt to manually merge** — have the person with the most recent working version rebuild the scene

---

## OVRManager Settings (already configured)

These are set on the OVRCameraRig and committed. Listed here for reference — you shouldn't need to change them.

| Setting | Value |
|---------|-------|
| Hand Tracking Support | Hands Only |
| Hand Tracking Frequency | HIGH |
| Hand Tracking Skeleton Version | **OpenXR** |
| Body Tracking Support | Required |
| Body Tracking Fidelity | High |
| Body Tracking Joint Set | Upper Body |
| Passthrough Support | Required |
| Insight Passthrough | Enabled |
| Quest Features > Hand Tracking | Required |
| Quest Features > Body Tracking | Required |
| Quest Features > Passthrough | Required |
| Tracking Origin Type | Floor Level |

---

## Player Settings (already configured)

| Setting | Value |
|---------|-------|
| Color Space | Linear |
| Minimum API Level | 32 (Android 12L) |
| Scripting Backend | IL2CPP |
| Target Architectures | ARM64 only |
| Graphics APIs | Vulkan, OpenGL ES 3.0 |
| XR Plug-in Management | Oculus (NOT OpenXR) |

---

## Installed SDKs

| Package | Purpose |
|---------|---------|
| Meta XR All-in-One SDK | Hand tracking, passthrough, device integration |
| Meta Movement SDK | Body tracking (forearm/elbow estimation) |

---

## Building & Deploying

1. Connect Quest 3 via USB (verify with `adb devices`)
2. **File → Build Settings** → confirm Android platform and scene is listed
3. **Build and Run**
4. First IL2CPP build takes 5–15 minutes. Subsequent builds are faster.

---

## Key Inspector Parameters

These live on the `ForearmDepthSurface` component and are the primary tuning surface.

| Parameter | Default | Description |
|-----------|---------|-------------|
| `pixelStride` | 6 | Depth sample spacing in pixels. Lower = denser mesh, more compute |
| `seedRadialDist` | 0.05m | Tight inner cylinder radius for confident forearm seeds |
| `maxRadialDist` | 0.15m | Outer flood wall — caps BFS growth away from arm |
| `connectivityThreshold` | 0.010m | Max 3D distance between adjacent flood cells |
| `maxQuadEdge` | 0.014m | Rejects quads whose edges exceed this — prevents bridging depth gaps |
| `smoothPasses` | 3 | Laplacian smoothing iterations |
| `edgeSmoothPasses` | 2 | Boundary contour smoothing iterations |
| `handMaskInflate` | 0.001m | Outward silhouette inflation to cover fast-motion depth bleed |
| `displayHeight` | 0.4m | Physical height of the UV display window on the arm |
| `displayWidth` | 0.4m | Physical width of one display panel (set equal to height for square pixels) |
| `displayOffset` | 0.08m | Center of display window along arm from wrist |
| `touchHoverHeight` | 0.02m | How far above the surface a finger can hover and still register |
| `touchDepth` | 0.04m | How far through the surface a press can go before being rejected |

---

## Development Phases

| Phase | Status | Description |
|-------|--------|-------------|
| 1. Hand Tracking Foundation | Done | Passthrough + hand skeleton + wrist anchor |
| 2. Cylinder Prototype | Done | Geometric arm cylinder, calibration, basic touch — baseline for UI experiments |
| 3. Depth Surface Reconstruction | Done | Live forearm mesh from Quest depth API, GPU hand masking, sub-cell touch detection |
| 4. UI Experiments + Integration | In Progress | Port the team's interaction work from cylinder to depth surface |
| 5. User Evaluation | Later | Data logging + evaluation protocol |

---

## Common Issues

| Symptom | Fix |
|---------|-----|
| Black screen on Quest | Camera background = solid black alpha 0. OVRPassthroughLayer = Underlay. Passthrough Support = Required. |
| Hand tracking not working | OVRManager > Hand Tracking Support must NOT be "Controllers Only". Ensure adequate lighting. |
| "InteractionSDK OpenXR skeleton" warning | OVRManager > Hand Tracking Skeleton Version → set to **OpenXR**. This is a data format, NOT the Unity OpenXR plugin. |
| Body tracking not initializing | Body Tracking Support = Required. Joint Set = Upper Body. |
| Joints return (0,0,0) | OVRSkeleton needs 1–2 frames to init. Check `IsTracked` before reading. |
| Surface not appearing | Check that `ForearmDepthSurface` Inspector references (bodySkeleton, handMesh, centerEyeAnchor, surfaceMaterial) are all assigned. |
| Hand depth bleeding through surface | Increase `handMaskInflate` slightly (try 0.003m). |
| Surface has large hole where hand is | Depth sensor has wider FOV than render display — the mask is intentionally conservative. Expected behavior. |
| Touch not registering | Verify `ForearmInteraction` is on the same GameObject as `ForearmDepthSurface`. Check `maxCellSearchDist` is not too small. |
| Build fails: Gradle error | Ensure Android SDK/NDK installed via Unity Hub modules. Min API >= 32. Try deleting `Library/Bee/`. |
| App crashes on launch | Run `adb logcat -s Unity` and look for NullReferenceException. Usually a missing Inspector reference. |
| Library/ folder is huge | Normal. It's gitignored. Unity regenerates it from Assets/ and ProjectSettings/. |
| Missing .meta files after pull | Someone deleted a .meta without its asset. Never delete .meta files independently. |
| Scene merge conflict | See Scene Editing Rules above. Do not manually merge `.unity` files. |
