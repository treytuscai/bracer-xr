using UnityEngine;

/// <summary>
/// Shared contract for carrying and placing palette widgets onto the forearm.
/// Implemented by <see cref="ArmLayoutController"/> (legacy cylinder canvas) and
/// <see cref="RevisedGridPlacementController"/> (depth-surface grid cells).
/// </summary>
public interface IForearmWidgetPlacement
{
    bool IsCarrying { get; }

    bool BeginCarryExternal(RectTransform widget, Transform indexTipWorld, bool destroyOnAbort = true);
    void TickCarryFollowFinger(Transform indexTipWorld);
    void CommitPlace(Vector3 contactWorldPoint);
    void DestroyCarriedItem();
}
