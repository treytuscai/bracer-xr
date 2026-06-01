using UnityEngine;

/// <summary>
/// <see cref="TouchInputManager"/> → <see cref="ArmLayoutController"/>.
/// VerticalListReorder: legacy list drag / reshuffle.
/// FreePlace: press on canvas widget → detach and follow right index fingertip until you briefly leave the arm then
/// press again on skin to glue the widget onto the decal (UV → linear Canvas map).
/// Also supports picking templates from <see cref="PossibleUIPaletteController"/> and placing them onto ItemList.
/// </summary>
[DefaultExecutionOrder(100)]
public class ForearmTouchManager : MonoBehaviour
{
    public TouchInputManager touchInput;
    public ArmLayoutController layoutController;
    public PossibleUIPaletteController possibleUIPalette;
    public HandTrackingController handTracking;

    [Header("List reorder mode")]
    [Tooltip("Frames in TouchState.None before cancelling a vertical list-drag.")]
    [Min(1)] public int releaseGraceFrames = 18;

    public bool requirePressToBegin;

    [Header("Free fingertip detach / stick-back")]
    [Tooltip("Minimal frames between pickup Press and Stick Press so the gestures never merge into a single double-press.")]
    [Min(3)] public int minFramesBetweenPickAndStick = 6;

    [Tooltip("If true, palette picks take priority over arm picks when the finger is over the floating palette.")]
    public bool palettePickHasPriority = true;

    [Tooltip("Log gesture transitions to the Console. Combined with ArmLayoutController.debugLogInteractions, lets you trace exactly why a tap did or did not place.")]
    public bool debugLogGestures = true;

    bool listDragging;
    int listOffSurfaceFrames;
    Vector2 lastOnSurfaceUv;

    bool carrying;
    bool sawReleaseSincePickup;
    int pickupFrameStamp;
    bool wasPressTouch;
    bool wasPalettePress;

    void Awake()
    {
        ResolveReferences();
    }

    void ResolveReferences()
    {
        if (touchInput       == null) touchInput       = FindObjectOfType<TouchInputManager>();
        if (layoutController == null) layoutController = FindObjectOfType<ArmLayoutController>();
        if (possibleUIPalette == null) possibleUIPalette = FindObjectOfType<PossibleUIPaletteController>();
        if (handTracking     == null) handTracking     = FindObjectOfType<HandTrackingController>();
    }

    void LateUpdate()
    {
        if (touchInput == null || layoutController == null) return;

        Transform indexTip =
            handTracking != null ? handTracking.rightIndexTip : null;

        TouchInputManager.TouchState st = touchInput.touchState;
        bool onSkin = st != TouchInputManager.TouchState.None;
        Vector2 meshUvRaw = touchInput.currentUV;

        if (onSkin)
        {
            lastOnSurfaceUv = meshUvRaw;
            listOffSurfaceFrames = 0;
        }

        bool pressHeld = st == TouchInputManager.TouchState.Press;
        // Compute BOTH edges before updating wasPressTouch, otherwise the release edge
        // is always false (because wasPressTouch == pressHeld by the time the tick reads it).
        bool pressBegan    = pressHeld && !wasPressTouch;
        bool pressReleased = !pressHeld && wasPressTouch;

        if (possibleUIPalette != null && indexTip != null)
            possibleUIPalette.UpdateTouchFromFinger(indexTip.position);

        bool palettePressHeld =
            possibleUIPalette != null &&
            possibleUIPalette.TouchState == PossibleUIPaletteController.PaletteTouchState.Press;
        bool palettePressBegan = palettePressHeld && !wasPalettePress;
        bool palettePressReleased = !palettePressHeld && wasPalettePress;

        if (layoutController.interactionMode == ArmLayoutController.InteractionMode.FreePlace)
            TickFreeFingertipDetach(
                onSkin,
                meshUvRaw,
                pressBegan,
                pressReleased,
                palettePressBegan,
                palettePressReleased,
                indexTip);
        else
            TickListReorder(onSkin, meshUvRaw, pressHeld);

        wasPressTouch = pressHeld;
        wasPalettePress = palettePressHeld;
    }

    void TickListReorder(bool onSurface, Vector2 meshUvRaw, bool pressHeld)
    {
        if (listDragging && !onSurface)
            listOffSurfaceFrames++;

        if (listDragging && !onSurface && listOffSurfaceFrames >= releaseGraceFrames)
        {
            layoutController.EndDrag();
            listDragging = false;
            listOffSurfaceFrames = 0;
            return;
        }

        bool mayBegin = !requirePressToBegin || pressHeld;

        if (onSurface)
        {
            if (!listDragging && mayBegin)
                listDragging = layoutController.StartDrag(meshUvRaw) != null;

            if (listDragging)
                layoutController.UpdateDrag(meshUvRaw);
        }
        else if (listDragging)
        {
            layoutController.UpdateDrag(lastOnSurfaceUv);
        }
    }

    void TickFreeFingertipDetach(
        bool onSkin,
        Vector2 meshUvRaw,
        bool pressBegan,
        bool pressReleased,
        bool palettePressBegan,
        bool palettePressReleased,
        Transform indexTip)
    {
        // Another system (e.g. experiment-specific palette assist) may have started a carry.
        if (!carrying && layoutController.IsCarrying)
        {
            carrying = true;
            sawReleaseSincePickup = false;
            pickupFrameStamp = Time.frameCount;
        }

        // PICKUP — palette first, then arm widgets already on the forearm.
        if (!carrying && indexTip != null)
        {
            bool tryPalette =
                possibleUIPalette != null &&
                possibleUIPalette.IsFingerOverPalette &&
                palettePressBegan;

            bool tryArm =
                onSkin &&
                pressBegan &&
                (!palettePickHasPriority || possibleUIPalette == null || !possibleUIPalette.IsFingerOverPalette);

            if (tryPalette && possibleUIPalette.TryBeginCarryFromPalette(indexTip))
            {
                carrying = true;
                sawReleaseSincePickup = false;
                pickupFrameStamp = Time.frameCount;
                if (debugLogGestures) Debug.Log($"[Gesture] PALETTE PICKUP @ frame {pickupFrameStamp}");
                return;
            }

            if (tryArm)
            {
                Vector3 contactPt = touchInput.contactPoint;
                if (layoutController.TryBeginCarry(contactPt, indexTip))
                {
                    carrying = true;
                    sawReleaseSincePickup = false;
                    pickupFrameStamp = Time.frameCount;
                    if (debugLogGestures) Debug.Log($"[Gesture] ARM PICKUP @ frame {pickupFrameStamp}");
                }
            }

            return;
        }

        if (!carrying) return;

        // CARRY
        if (indexTip != null)
            layoutController.TickCarryFollowFinger(indexTip);

        // EDGE-TRIGGERED RELEASE — accept either arm press-release or palette press-release.
        if ((pressReleased || palettePressReleased) && !sawReleaseSincePickup)
        {
            sawReleaseSincePickup = true;
            if (debugLogGestures)
                Debug.Log("[Gesture] RELEASE edge detected. Ready to place on next press.");
        }

        // PLACE on arm — or DELETE on palette trash icon.
        bool readyForDelete =
            sawReleaseSincePickup &&
            palettePressBegan &&
            possibleUIPalette != null &&
            indexTip != null &&
            possibleUIPalette.IsFingerOverDelete(indexTip.position) &&
            Time.frameCount - pickupFrameStamp >= minFramesBetweenPickAndStick;

        bool readyForStickGesture =
            sawReleaseSincePickup &&
            onSkin &&
            pressBegan &&
            Time.frameCount - pickupFrameStamp >= minFramesBetweenPickAndStick;

        if (debugLogGestures && (pressBegan || palettePressBegan))
            Debug.Log($"[Gesture] press during carry. " +
                      $"sawRelease={sawReleaseSincePickup} onSkin={onSkin} " +
                      $"overDelete={possibleUIPalette != null && indexTip != null && possibleUIPalette.IsFingerOverDelete(indexTip.position)} " +
                      $"framesSincePickup={Time.frameCount - pickupFrameStamp} " +
                      $"readyPlace={readyForStickGesture} readyDelete={readyForDelete}");

        if (readyForDelete)
        {
            if (debugLogGestures) Debug.Log("[Gesture] DELETE carried widget");
            layoutController.DestroyCarriedItem();
            carrying = false;
            sawReleaseSincePickup = false;
            return;
        }

        if (readyForStickGesture)
        {
            if (debugLogGestures) Debug.Log("[Gesture] PLACE @ contact " + touchInput.contactPoint.ToString("F3"));
            layoutController.CommitPlace(touchInput.contactPoint);
            carrying = false;
            sawReleaseSincePickup = false;
        }
    }
}
