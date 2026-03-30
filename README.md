# Ink & Interface — XR Prototype

On-body interaction paradigms for Extended Reality. Renders interactive digital UI on the user's skin (forearm + hand) via Meta Quest 3 passthrough, with direct-touch input from the opposite hand.

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

## Current Project Structure

```
Assets/
├── _Project/                 <- OUR CODE AND ASSETS
│   ├── Scripts/
│   │   ├── Core/
│   │   ├── Surface/
│   │   ├── Interaction/                      
│   │   ├── UI/                               
│   │   └── Data/                            
│   ├── Materials/
│   ├── Prefabs/
│   ├── Shaders/
│   └── Textures/
├── Scenes/
├── Oculus/                   ← DO NOT MODIFY (Meta SDK config)
├── Resources/                ← DO NOT MODIFY (Meta SDK runtime settings)
├── Settings/                 ← DO NOT MODIFY (URP pipeline settings)
├── StreamingAssets/          ← DO NOT MODIFY
└── XR/                       ← DO NOT MODIFY (XR plugin settings)
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

## ⚠️ Scene Editing Rules

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

## Common Issues

| Symptom | Fix |
|---------|-----|
| Black screen on Quest | Camera background = solid black alpha 0. OVRPassthroughLayer = Underlay. Passthrough Support = Required. |
| Hand tracking not working | OVRManager > Hand Tracking Support must NOT be "Controllers Only". Ensure adequate lighting. |
| "InteractionSDK OpenXR skeleton" warning | OVRManager > Hand Tracking Skeleton Version → set to **OpenXR**. This is a data format, NOT the Unity OpenXR plugin. |
| Body tracking not initializing | Body Tracking Support = Required. Joint Set = Upper Body. |
| Joints return (0,0,0) | OVRSkeleton needs 1–2 frames to init. Check `IsTracked` before reading. |
| Build fails: Gradle error | Ensure Android SDK/NDK installed via Unity Hub modules. Min API ≥ 32. Try deleting `Library/Bee/`. |
| App crashes on launch | Run `adb logcat -s Unity` and look for NullReferenceException. Usually a missing Inspector reference. |
| Library/ folder is huge | Normal. It's gitignored. Unity regenerates it from Assets/ and ProjectSettings/. |
| Missing .meta files after pull | Someone deleted a .meta without its asset. **Never delete .meta files independently.** |
| Scene merge conflict | See Scene Editing Rules above. Don't manually merge `.unity` files. |

---

## Development Phases

| Phase | Status | Description |
|-------|--------|-------------|
| 1. Hand Tracking Foundation |  Done | Passthrough + hand skeleton + wrist anchor |
| 2. Arm Surface Mesh |  Done | Body-tracked forearm cylinder + hand mesh |
| 3. Touch Interaction |  In-Progress | Poke detection from opposite hand |
| 4. UI Design Integration | Next | Interview-informed UI on arm surface |
| 5. User Evaluation | Later | Data logging + evaluation protocol |