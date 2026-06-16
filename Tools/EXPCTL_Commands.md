# expctl command reference

Drive experiments on a connected Quest build from a laptop via HTTP (`adb forward`).

## Setup (once per USB session)

```bash
# macOS / Linux / Git Bash
./Tools/expctl forward
./Tools/expctl load <SceneName>
./Tools/expctl help

# Windows (PowerShell / CMD)
Tools\expctl.bat forward
Tools\expctl.bat load <SceneName>
Tools\expctl.bat help
```

`help` lists **global** commands plus **scene-specific** commands for whatever scene is currently loaded.

---

## Global commands (all scenes)

| Command | Description |
|---------|-------------|
| `ping` | Health check; returns `pong`. |
| `help` | List global and current scene commands. |
| `scenes` | List scene names in Build Settings (for use with `load`). |
| `load <sceneName>` | Load a scene (e.g. `load Color`). |
| `reload` | Reload the active scene. |
| `screenshot [filename]` | Capture 1280×720 PNG to device storage; optional filename. |
| `quit` | Exit the app. |

### Examples

```bash
./Tools/expctl ping
./Tools/expctl scenes
./Tools/expctl load RevisedBoundingBox
./Tools/expctl reload
./Tools/expctl screenshot trial_01.png
```

---

## Scene-specific commands

### `Color`

| Command | Description |
|---------|-------------|
| `hex` | Log text and canvas colors as `#RRGGBBAA` (also printed to Unity console). |

```bash
./Tools/expctl load Color
./Tools/expctl hex
# e.g. text=#FFFFFFFF canvas=#FF8800CC
```

---

### `RevisedBoundingBox`

| Command | Description |
|---------|-------------|
| `clear` | Wipe all painted grid cells on the arm. |

Erase vs draw is toggled on-device via the **Toggle Erase** HUD button (not expctl).

```bash
./Tools/expctl load RevisedBoundingBox
./Tools/expctl clear
```

---

### `RevisedPlaceRotateScale`

| Command | Description |
|---------|-------------|
| `clear` | Remove all widgets baked into the forearm grid. |
| `placed` | Original and current size (content scale) and rotation (degrees) of the most recently placed image. Updates when the participant edits that cell via the resize/rotate panel. |

```bash
./Tools/expctl load RevisedPlaceRotateScale
./Tools/expctl clear
./Tools/expctl placed
# e.g. original_size=2.00 original_rotation=0.00 current_size=2.50 current_rotation=45.00
```

---

### `1dElicitationofPlacement`

| Command | Description |
|---------|-------------|
| `clear` | Clear all placed widgets from the arm grid. |
| `placed` | Original and current size/rotation of the most recently placed image (updates after resize/rotate edits on that cell). |
| `next` | Clear the arm and show the next template in the randomized sequence. Returns `all templates complete` when finished. |

```bash
./Tools/expctl load 1dElicitationofPlacement
./Tools/expctl next
./Tools/expctl placed
```

---

### `1fPrivatePublic`

| Command | Description |
|---------|-------------|
| `clear` | Clear all placed widgets from the arm grid. |
| `placed` | Original and current size/rotation of the most recently placed image (updates after resize/rotate edits on that cell). |
| `next` | Same as 1d — advance to the next palette template. |

```bash
./Tools/expctl load 1fPrivatePublic
./Tools/expctl next
```

---

### `1gSituationDependent`

| Command | Description |
|---------|-------------|
| `clear` | Clear all placed widgets from the arm grid. |
| `placed` | Original and current size/rotation of the most recently placed image (updates after resize/rotate edits on that cell). |
| `next` | Clear the arm and enable manual placement from the palette. |

```bash
./Tools/expctl load 1gSituationDependent
./Tools/expctl next
```

---

### `1eContentDependent` / `1lRankingPlacement`

| Command | Description |
|---------|-------------|
| `clear` | Clear all placed widgets from the arm grid. |
| `placed` | Original and current size/rotation of the most recently placed image (updates after resize/rotate edits on that cell). |

```bash
./Tools/expctl load 1eContentDependent
./Tools/expctl clear
```

---

### `1hHorizVertical`

| Command | Description |
|---------|-------------|
| `clear` | Clear all placed widgets from the arm grid. |
| `placed` | Original and current size/rotation of the most recently grid-placed image (updates after resize/rotate edits on that cell). |
| `next` | Advance to the next step (body region × vertical/horizontal interface). |
| `loguv` / `uv` | Log latched touch UV on the arm. Touch the arm first, then run the command. |
| `status` | Current step, placement source (saved/inspector), UV center, size, rotation. |
| `orient` | Toggle vertical ↔ horizontal interface. |
| `orient v` / `orient vertical` | Force vertical interface. |
| `orient h` / `orient horizontal` | Force horizontal interface. |
| `config clear` | Clear saved placement presets. |

```bash
./Tools/expctl load 1hHorizVertical
./Tools/expctl next
./Tools/expctl status
./Tools/expctl orient h
# touch arm, then:
./Tools/expctl loguv
```

---

### `1kFlowVersusGrid`

| Command | Description |
|---------|-------------|
| `next` | Show the next interface image in the sequence (8 flow/grid variants). |
| `back` | Show the previous interface image. Returns `at first image` on the first image. |
| `status` | Current image index and filename. |

```bash
./Tools/expctl load 1kFlowVersusGrid
./Tools/expctl next
./Tools/expctl back
./Tools/expctl status
```

---

### `OrientationDegree`

| Command | Description |
|---------|-------------|
| `switch` | Swap between primary and alternate forearm image. |
| `angle` | Log arm elevation angle (raw, smoothed, calibrated). |
| `offset <degrees>` | Set a single angle offset (legacy single-offset mode). |
| `calibrate <ref>` | Single-point calibration shorthand: `calibrate 0`, `45`, or `90`. |
| `calibrate point <0\|45\|90>` | Capture a calibration sample at that reference angle. |
| `calibrate apply` | Apply linear scale+offset fit from captured points. |
| `calibrate apply piecewise` | Apply piecewise linear calibration. |
| `calibrate status` | Show calibration mode and captured points. |
| `calibrate clear` | Reset calibration to defaults. |

```bash
./Tools/expctl load OrientationDegree
./Tools/expctl angle
./Tools/expctl calibrate point 0
./Tools/expctl calibrate point 45
./Tools/expctl calibrate point 90
./Tools/expctl calibrate apply
./Tools/expctl calibrate status
```

---

### `placeRotateScaleInterfaces` / `chooseColorWithColorwheel`

Legacy canvas placement on the arm (not grid-baked). `size` is the placed widget’s canvas pixel extent; `rotation` is degrees (typically `0`).

| Command | Description |
|---------|-------------|
| `placed` | Original and current size/rotation of the most recently placed image. |

```bash
./Tools/expctl load placeRotateScaleInterfaces
./Tools/expctl placed
```

---

## Scenes with no scene-specific commands

These scenes only support **global** commands:

| Scene | Notes |
|-------|-------|
| `experimentSelectScene` | Experiment picker menu |
| `boundingBoxDemarcation` | Polygon bounding-box demarcation (`placed` available if widgets are placed via legacy canvas) |
| `SizeAndScale` | Size/gap slider experiment |
| `MainScene` | — |

After loading any scene, run `help` to confirm which commands are registered on your build.

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `command not found` / connection refused | Run `expctl forward` again; rebuild APK if a command was added recently. |
| Scene command missing | Verify the scene is loaded (`help` shows scene list); confirm the scene’s controller is in the scene and enabled. |
| `loguv` says touch arm first | Touch the forearm with the non-dominant hand, then run `loguv` again (value is latched). |

---

## Implementation notes

- Server: `ExperimentCommandServer` (port **9999**, loopback only).
- Scene commands are registered by components implementing `IExperimentCommands` when each scene loads.
- `placed` records original values at bake/commit time; `current_*` updates when the participant edits that same cell via the resize/rotate panel (`SetSelectedCellScale` / `SetSelectedCellRotation`). Also logged to the Unity console.
- Wrapper scripts: `Tools/expctl` (bash), `Tools/expctl.bat` (Windows).
