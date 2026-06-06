# Experiments Overview

A plain-language summary of the five experiments, each with an accessible
description of what the participant experiences followed by how it is built.

## Shared Foundation: `ForearmDepthSurface`

All five experiments share one core piece of technology. Rather than projecting
onto a fixed cylinder, `ForearmDepthSurface` reconstructs the actual shape of the
forearm in real time from the headset's depth sensors and hand tracking, then
renders content onto that live surface. Every experiment below draws its arm
content onto this surface.

---

## Color

**What it does:** Displays text ("Hello World") on the forearm and lets the user
recolor it in real time. Two floating control panels sit to the user's side — one
for the text color, one for the canvas/background behind it. The user adjusts the
color by sliding a finger up and down vertical sliders.

**How it's built:**

- The text and background live in a custom shader (`ForearmColorText`) as two
  layers, each with its own color and opacity.
- `ColorExperimentController` exposes simple setters (`SetTextColor`,
  `SetBackgroundColor`, opacity, etc.) that write into the *runtime copy* of the
  surface material, so changes never leak back to shared project assets or other
  scenes.
- `ForearmColorWheel` builds each floating panel as four vertical sliders —
  **Hue, Saturation, Lightness, and Opacity**. It reads the right index
  fingertip position (from the hand skeleton) and, when the finger presses close
  enough to a track, converts the finger's height on that track into a value. A
  header label auto-reads "Text Color Settings" or "Canvas Color Settings"
  depending on which channel the panel controls.

---

## OrientationDegree

**What it does:** Projects a single fixed image onto the forearm and shows a
floating readout of the arm's **elevation angle** — 0° when the forearm is level
with the horizon, 90° when it points straight up. The number updates as the user
raises and lowers their arm.

**How it's built:**

- The image is drawn on the arm using the `ForearmImageDisplay` shader (the same
  approach as SizeScaleText), with controls for how large the image appears on
  the arm.
- The angle is computed in pure **world space** from the forearm direction
  (`AxisDir`, the wrist→elbow vector taken from the body skeleton's bone
  positions). The elevation is `arcsin` of that vector's vertical component, so
  it depends only on how far up the arm tilts — wrist rotation and head turning
  don't affect the math.
- A small floating HUD (a world-space canvas) shows the rounded angle, with
  smoothing and a deadband to suppress tracking jitter, plus a head-motion freeze
  that holds the reading steady while the headset is turning.

---

## ExperimentSelect

**What it does:** The main menu. A panel floats centered in view listing the
available experiments; the user pokes a button with either index fingertip to
load that scene.

**How it's built:**

- `ExperimentSelectMenuController` builds the menu entirely from a world-space UI
  canvas (title + one button per scene), so there are no prefabs to maintain.
- It optionally follows head rotation so the menu stays in front of the user, and
  it waits for valid head tracking before anchoring (with sensible fallbacks if
  tracking is slow to start).
- Touch is handled by measuring the distance from each fingertip to each button —
  close enough triggers a hover highlight, closer still counts as a press, which
  calls the scene loader. Buttons change color across normal/hover/pressed states
  for clear feedback.

---

## SizeScaleText

**What it does:** Projects an image onto the forearm that the user can change with
two sliders. One slider ("size") cycles through a sequence of images; the second
slider ("gap") switches between entirely different *folders* of images. Together
they let the user browse a 2-D grid of interface variants hands-free.

**How it's built:**

- Images are organized as `gaps[gapIndex].sizes[sizeIndex]` — each "gap" is a
  group holding an ordered set of "size" textures.
- `SizeScaleController` creates a runtime material using the `ForearmImageDisplay`
  shader and assigns it directly to the arm surface, leaving shared assets
  untouched. The shader supports scaling and offsetting the image so it sits *on*
  the mesh at a chosen size rather than stretching across the whole arm.
- `SizeScaleSlider` is a two-track floating panel. The left track drives the gap
  index, the right track drives the size index; finger height on a track selects
  which image in that dimension is shown.

---

## PlaceRotateScale (Main Scene)

**What it does:** The richest experiment. A floating palette of UI templates
hovers in front of the user. The user picks one with a finger, it sticks to the
fingertip, and they place it onto the forearm. Placed items can then be
**rotated** and **scaled** via dedicated mode buttons, and removed via a trash
icon.

**How it's built:**

- `PossibleUIPaletteController` is the floating template palette. Picking a
  template clones it and hands it off to the fingertip; placing it on the arm
  commits it into a list of on-arm items. It also manages the delete/trash icon.
- `ArmLayoutController` is the core: it maps the arm surface's UV coordinates to a
  canvas, handles the "pick → follow finger → commit" flow, and projects the
  committed item back onto the correct spot on the forearm.
- `ForearmTouchManager` interprets fingertip-to-surface proximity into pick/place
  gestures.
- `ArmRotateController` and `ArmScaleController` each add a toggle button to the
  palette. When active, they temporarily disable the normal touch manager (so
  gestures don't conflict), let the user select an on-arm item, and show a drag
  handle — a line with a colored grab dot — that the user drags to spin or resize
  the item, committing on release.
