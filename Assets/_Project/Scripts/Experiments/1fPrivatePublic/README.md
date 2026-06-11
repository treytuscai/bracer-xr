# 1F — Private vs Public

This experiment uses the **same logic and scene setup as [1D Elicitation of Placement](../1d%20Elicitation%20of%20Placement/)**. The only intentional difference is the **palette images** (private vs public stimuli instead of 1D’s interface set).

## Shared behavior (from 1D)

- **RevisedPlaceRotateScale** grid placement on the forearm mesh (`RevisedGridController`, `RevisedGridPlacementController`, `RevisedForearmTouchManager`).
- **One PossibleUI template at a time**, shown in a **randomized order** via `OneDSequentialPaletteController` on the `PossibleUIs` object.
- Participant picks the visible template, places it on the arm, then advances with the CLI:

  ```bash
  ./Tools/expctl next
  ```

  `next` clears placed items on the arm and shows the next shuffled template.

## What differs in 1F

- **Scene:** `Assets/_Project/Scenes/1fPrivatePublic.unity`
- **Images:** Replace or assign palette templates under `PossibleUIs` with the 1F private/public artwork. No separate 1F-specific scripts are required unless behavior diverges from 1D later.

## Setup checklist

1. Duplicate or mirror the 1D scene wiring (`PossibleUIs` + `OneDSequentialPaletteController`, SurfaceManager placement stack).
2. Swap palette child images for the 1F stimulus set.
3. Optionally set `shuffleSeed` on `OneDSequentialPaletteController` for a fixed trial order.

Do not change scripts under `RevisedPlaceRotateScale` or other experiment folders for 1F-only needs—reuse 1D’s controller and shared components instead.
