using UnityEngine;

/// <summary>
/// Touch orchestration for RevisedPlaceRotateScale.
/// Mirrors <see cref="ForearmTouchManager"/> but uses <see cref="ForearmInteraction"/>
/// on the depth surface and <see cref="IForearmWidgetPlacement"/> for grid placement.
/// </summary>
[DefaultExecutionOrder(115)]
public class RevisedForearmTouchManager : MonoBehaviour
{
    [Header("References")]
    public ForearmInteraction interaction;
    [Tooltip("RevisedGridPlacementController (or any IForearmWidgetPlacement).")]
    public MonoBehaviour widgetPlacement;
    public PossibleUIPaletteController possibleUIPalette;
    public OVRSkeleton rightHandSkeleton;

    IForearmWidgetPlacement Placement => ResolvePlacement(widgetPlacement);

    static IForearmWidgetPlacement ResolvePlacement(MonoBehaviour behaviour)
    {
        if (behaviour == null) return null;
        if (behaviour is IForearmWidgetPlacement direct) return direct;

        foreach (var mb in behaviour.GetComponents<MonoBehaviour>())
        {
            if (mb is IForearmWidgetPlacement found)
                return found;
        }

        return null;
    }

    [Header("Gestures")]
    [Min(3)] public int minFramesBetweenPickAndStick = 6;
    public bool palettePickHasPriority = true;
    public bool debugLogGestures;

    bool carrying;
    bool sawReleaseSincePickup;
    int  pickupFrameStamp;
    bool wasPressTouch;
    bool wasPalettePress;

    const int IndexTipBone = 10;

    void Awake()
    {
        if (interaction == null)
        {
            var surface = FindObjectOfType<ForearmDepthSurface>();
            if (surface != null)
                interaction = surface.GetComponent<ForearmInteraction>();
        }

        if (possibleUIPalette == null)
            possibleUIPalette = FindObjectOfType<PossibleUIPaletteController>();

        if (widgetPlacement == null)
        {
            var revised = FindObjectOfType<RevisedGridPlacementController>();
            if (revised != null)
                widgetPlacement = revised;
        }

        if (Placement == null)
            Debug.LogError("[RevisedForearmTouchManager] Widget Placement must reference a GameObject " +
                           "with RevisedGridPlacementController (e.g. SurfaceManager). Pickup will not work.");

        if (rightHandSkeleton == null)
        {
            foreach (var s in FindObjectsOfType<OVRSkeleton>())
            {
                if (s.GetSkeletonType() == OVRSkeleton.SkeletonType.XRHandRight)
                {
                    rightHandSkeleton = s;
                    break;
                }
            }
        }
    }

    void LateUpdate()
    {
        if (interaction == null || Placement == null) return;

        Transform indexTip = IndexTip;
        Vector2 uv = interaction.TouchUV;
        bool onSkin = interaction.IsActive
                      && uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;

        bool pressHeld = onSkin;
        bool pressBegan = pressHeld && !wasPressTouch;
        bool pressReleased = !pressHeld && wasPressTouch;

        if (possibleUIPalette != null && indexTip != null)
            possibleUIPalette.UpdateTouchFromFinger(indexTip.position);

        bool palettePressHeld = possibleUIPalette != null &&
            possibleUIPalette.TouchState == PossibleUIPaletteController.PaletteTouchState.Press;
        bool palettePressBegan = palettePressHeld && !wasPalettePress;
        bool palettePressReleased = !palettePressHeld && wasPalettePress;

        TickFreePlace(onSkin, pressBegan, pressReleased, palettePressBegan, palettePressReleased, indexTip);

        wasPressTouch = pressHeld;
        wasPalettePress = palettePressHeld;
    }

    Transform IndexTip
    {
        get
        {
            if (rightHandSkeleton == null ||
                !rightHandSkeleton.IsInitialized ||
                !rightHandSkeleton.IsDataValid) return null;

            var bones = rightHandSkeleton.Bones;
            if (bones == null || bones.Count <= IndexTipBone) return null;
            return bones[IndexTipBone].Transform;
        }
    }

    void TickFreePlace(
        bool onSkin,
        bool pressBegan,
        bool pressReleased,
        bool palettePressBegan,
        bool palettePressReleased,
        Transform indexTip)
    {
        if (!carrying && Placement.IsCarrying)
        {
            carrying = true;
            sawReleaseSincePickup = false;
            pickupFrameStamp = Time.frameCount;
        }

        if (!carrying && indexTip != null)
        {
            bool tryPalette = possibleUIPalette != null &&
                              possibleUIPalette.IsFingerOverPalette &&
                              palettePressBegan;

            if (tryPalette && possibleUIPalette.TryBeginCarryFromPalette(indexTip))
            {
                carrying = true;
                sawReleaseSincePickup = false;
                pickupFrameStamp = Time.frameCount;
                if (debugLogGestures)
                    Debug.Log($"[RevisedGesture] PALETTE PICKUP @ frame {pickupFrameStamp}");
                return;
            }

            return;
        }

        if (!carrying) return;

        if (indexTip != null)
            Placement.TickCarryFollowFinger(indexTip);

        if ((pressReleased || palettePressReleased) && !sawReleaseSincePickup)
        {
            sawReleaseSincePickup = true;
            if (debugLogGestures)
                Debug.Log("[RevisedGesture] RELEASE edge — ready to place on next arm press.");
        }

        bool readyForDelete =
            sawReleaseSincePickup &&
            palettePressBegan &&
            possibleUIPalette != null &&
            indexTip != null &&
            possibleUIPalette.IsFingerOverDelete(indexTip.position) &&
            Time.frameCount - pickupFrameStamp >= minFramesBetweenPickAndStick;

        bool readyForPlace =
            sawReleaseSincePickup &&
            onSkin &&
            pressBegan &&
            Time.frameCount - pickupFrameStamp >= minFramesBetweenPickAndStick &&
            (possibleUIPalette == null || !possibleUIPalette.IsFingerOverPalette);

        if (readyForDelete)
        {
            if (debugLogGestures) Debug.Log("[RevisedGesture] DELETE carried widget");
            Placement.DestroyCarriedItem();
            carrying = false;
            sawReleaseSincePickup = false;
            return;
        }

        if (readyForPlace)
        {
            if (debugLogGestures)
                Debug.Log("[RevisedGesture] PLACE in grid cell @ " + interaction.TouchWorldPoint);
            Placement.CommitPlace(interaction.TouchWorldPoint);
            carrying = false;
            sawReleaseSincePickup = false;
        }
    }
}
