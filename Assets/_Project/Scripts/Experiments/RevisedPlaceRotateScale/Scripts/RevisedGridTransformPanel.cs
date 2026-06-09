using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Floating scale / rotation sliders for editing baked grid-cell images.
/// </summary>
[DefaultExecutionOrder(108)]
public class RevisedGridTransformPanel : MonoBehaviour
{
    public enum ActiveSlider
    {
        None,
        Scale,
        Rotation
    }

    [Header("References")]
    public Transform headAnchor;
    public OVRSkeleton rightHandSkeleton;

    [Header("Placement")]
    [Min(0.2f)] public float panelDistance = 0.55f;
    public float panelLeftMeters = 0.22f;
    public float panelUpMeters = -0.08f;
    [Min(0.5f)] public float minHeadHeightToAnchor = 1.0f;
    [Min(1)] public int maxAnchorWaitFrames = 300;
    [Min(0.5f)] public float fallbackEyeHeightMeters = 1.45f;
    [Min(0.05f)] public float panelWorldWidthMeters = 0.16f;

    [Header("Ranges")]
    [Min(0.1f)] public float minScale = 0.25f;
    [Min(0.2f)] public float maxScale = 3f;
    public float defaultScale = 1f;
    public float defaultRotationDegrees = 0f;

    [Header("Input")]
    [Min(0.005f)] public float pressDistanceMeters = 0.04f;
    [Min(0.01f)] public float releaseDistanceMeters = 0.07f;

    const int PanelW = 160;
    const int PanelH = 300;
    const int TopPad = 36;
    const int BottomPad = 20;
    const int TrackW = 22;
    const int HandleW = 44;
    const int HandleH = 18;
    const int TrackHitHalfWidth = 38;
    const int TrackHitYPad = 16;
    const int IndexTipBone = 10;

    // Layout X places tracks/labels; hit X matches inverse-transform finger coords with negative panel scale.
    const float ScaleLayoutX = -TrackW * 2f;
    const float RotateLayoutX = TrackW * 2f;
    const float ScaleHitX = TrackW * 2f;
    const float RotateHitX = -TrackW * 2f;

    RectTransform _root;
    RectTransform _scaleHandle;
    RectTransform _rotateHandle;
    bool _panelPlaced;
    int _anchorWaitFrames;
    ActiveSlider _activeSlider = ActiveSlider.None;
    ActiveSlider _capturedSlider = ActiveSlider.None;
    float _scaleValue = 1f;
    float _rotationValue;

    public bool IsVisible => _root != null && _root.gameObject.activeSelf;
    public bool IsFingerOnPanel { get; private set; }
    public ActiveSlider FocusedSlider => _activeSlider;

    public event Action<float> ScaleChanged;
    public event Action<float> RotationChanged;

    void Awake()
    {
        _scaleValue = defaultScale;
        _rotationValue = defaultRotationDegrees;
        BuildUI();
        RefreshHandles();
        if (_root != null)
            _root.gameObject.SetActive(false);
    }

    void Start()
    {
        if (headAnchor == null)
        {
            var surface = FindObjectOfType<ForearmDepthSurface>();
            if (surface != null) headAnchor = surface.centerEyeAnchor;
        }

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
        if (!_panelPlaced && IsVisible)
            TryPlacePanel();

        if (!IsVisible)
        {
            IsFingerOnPanel = false;
            _activeSlider = ActiveSlider.None;
            _capturedSlider = ActiveSlider.None;
            return;
        }

        UpdateFingerInteraction();
    }

    public void SetVisible(bool visible)
    {
        if (_root == null) return;
        _root.gameObject.SetActive(visible);
        if (visible)
        {
            if (_root.parent != null)
                _root.SetParent(null, true);
            _panelPlaced = false;
            _capturedSlider = ActiveSlider.None;
            TryPlacePanel();
        }
        else
        {
            _capturedSlider = ActiveSlider.None;
        }
    }

    public void SetActiveSlider(ActiveSlider slider) => _activeSlider = slider;

    public void SyncValues(float scale, float rotationDegrees)
    {
        _scaleValue = Mathf.Clamp(scale, minScale, maxScale);
        _rotationValue = Mathf.Repeat(rotationDegrees, 360f);
        RefreshHandles();
    }

    void UpdateFingerInteraction()
    {
        Transform tip = IndexTip;
        if (tip == null || _root == null)
        {
            IsFingerOnPanel = false;
            _activeSlider = ActiveSlider.None;
            _capturedSlider = ActiveSlider.None;
            return;
        }

        float planeDist = Mathf.Abs(Vector3.Dot(tip.position - _root.position, _root.forward));
        if (planeDist > releaseDistanceMeters)
        {
            IsFingerOnPanel = false;
            _activeSlider = ActiveSlider.None;
            _capturedSlider = ActiveSlider.None;
            return;
        }

        Vector2 local = FingerLocalOnPanel(tip.position);
        ActiveSlider slider = _capturedSlider;

        if (slider == ActiveSlider.None)
        {
            if (IsInTrackColumn(local, ScaleHitX))
                slider = ActiveSlider.Scale;
            else if (IsInTrackColumn(local, RotateHitX))
                slider = ActiveSlider.Rotation;
        }

        if (slider == ActiveSlider.None)
        {
            IsFingerOnPanel = planeDist <= pressDistanceMeters;
            _activeSlider = ActiveSlider.None;
            return;
        }

        _capturedSlider = slider;
        _activeSlider = slider;
        IsFingerOnPanel = true;

        if (slider == ActiveSlider.Scale)
            SetScaleFromLocalY(local.y);
        else
            SetRotationFromLocalY(local.y);
    }

    void SetScaleFromLocalY(float localY)
    {
        float t = Mathf.InverseLerp(TrackBottomY(), TrackTopY(), localY);
        float next = Mathf.Lerp(minScale, maxScale, Mathf.Clamp01(t));
        if (Mathf.Approximately(next, _scaleValue)) return;
        _scaleValue = next;
        RefreshHandles();
        ScaleChanged?.Invoke(_scaleValue);
    }

    void SetRotationFromLocalY(float localY)
    {
        float t = Mathf.InverseLerp(TrackBottomY(), TrackTopY(), localY);
        float next = Mathf.Lerp(0f, 360f, Mathf.Clamp01(t));
        if (Mathf.Approximately(next, _rotationValue)) return;
        _rotationValue = next;
        RefreshHandles();
        RotationChanged?.Invoke(_rotationValue);
    }

    static float TrackTopY() => PanelH * 0.5f - TopPad;
    static float TrackBottomY() => -PanelH * 0.5f + BottomPad;

    bool IsInTrackColumn(Vector2 fingerLocal, float trackCenterX)
    {
        if (Mathf.Abs(fingerLocal.x - trackCenterX) > TrackHitHalfWidth)
            return false;

        float bottom = TrackBottomY() - TrackHitYPad;
        float top = TrackTopY() + TrackHitYPad;
        return fingerLocal.y >= bottom && fingerLocal.y <= top;
    }

    Transform IndexTip
    {
        get
        {
            if (rightHandSkeleton == null || !rightHandSkeleton.IsInitialized || !rightHandSkeleton.IsDataValid)
                return null;
            var bones = rightHandSkeleton.Bones;
            if (bones == null || bones.Count <= IndexTipBone) return null;
            return bones[IndexTipBone].Transform;
        }
    }

    Vector2 FingerLocalOnPanel(Vector3 fingerWorld)
    {
        Vector3 local = _root.InverseTransformPoint(fingerWorld);
        return new Vector2(local.x, local.y);
    }

    void TryPlacePanel()
    {
        if (headAnchor == null || _root == null) return;

        float headY = headAnchor.position.y;
        if (headY < minHeadHeightToAnchor)
        {
            if (++_anchorWaitFrames >= maxAnchorWaitFrames)
                headY = fallbackEyeHeightMeters;
            else
                return;
        }

        Vector3 flatForward = headAnchor.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 1e-6f) flatForward = Vector3.forward;
        flatForward.Normalize();

        Vector3 flatRight = headAnchor.right;
        flatRight.y = 0f;
        if (flatRight.sqrMagnitude < 1e-6f)
            flatRight = Vector3.Cross(Vector3.up, flatForward).normalized;
        else
            flatRight.Normalize();

        Vector3 pos = headAnchor.position
                      + flatForward * panelDistance
                      - flatRight * panelLeftMeters;
        pos.y = headY + panelUpMeters;

        Vector3 face = headAnchor.position - pos;
        face.y = 0f;
        if (face.sqrMagnitude < 1e-6f) face = -flatForward;
        _root.SetPositionAndRotation(pos, Quaternion.LookRotation(face.normalized, Vector3.up));
        float scale = panelWorldWidthMeters / PanelW;
        _root.localScale = new Vector3(-scale, scale, scale);
        _panelPlaced = true;
    }

    void RefreshHandles()
    {
        if (_scaleHandle != null)
        {
            float t = Mathf.InverseLerp(minScale, maxScale, _scaleValue);
            _scaleHandle.anchoredPosition = new Vector2(ScaleLayoutX, Mathf.Lerp(TrackBottomY(), TrackTopY(), t));
        }

        if (_rotateHandle != null)
        {
            float t = _rotationValue / 360f;
            _rotateHandle.anchoredPosition = new Vector2(RotateLayoutX, Mathf.Lerp(TrackBottomY(), TrackTopY(), t));
        }
    }

    void BuildUI()
    {
        if (_root != null)
            Destroy(_root.gameObject);

        var rootGo = new GameObject("GridTransformPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasRenderer));
        _root = rootGo.GetComponent<RectTransform>();
        _root.sizeDelta = new Vector2(PanelW, PanelH);
        _root.pivot = new Vector2(0.5f, 0.5f);
        _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0.5f);

        var canvas = rootGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        var bg = rootGo.AddComponent<Image>();
        bg.color = new Color(0.08f, 0.08f, 0.1f, 0.88f);

        float labelY = TrackTopY() + 22f;
        AddLabel(_root, "Edit Image", new Vector2(0f, PanelH * 0.5f - 16f), 18);
        AddLabel(_root, "Size", new Vector2(ScaleLayoutX, labelY), 14);
        AddLabel(_root, "Rotate", new Vector2(RotateLayoutX, labelY), 14);

        float trackMidY = (TrackBottomY() + TrackTopY()) * 0.5f;
        float trackHeight = TrackTopY() - TrackBottomY();
        CreateTrack(_root, ScaleLayoutX, trackMidY, trackHeight, out _scaleHandle, new Color(0.35f, 0.85f, 1f, 0.55f));
        CreateTrack(_root, RotateLayoutX, trackMidY, trackHeight, out _rotateHandle, new Color(1f, 0.75f, 0.25f, 0.55f));
    }

    static void AddLabel(RectTransform parent, string text, Vector2 pos, int fontSize)
    {
        var go = new GameObject(text, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.sizeDelta = new Vector2(72f, 24f);
        rt.anchoredPosition = pos;
        var t = go.GetComponent<Text>();
        t.text = text;
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = fontSize;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        t.raycastTarget = false;
    }

    static void CreateTrack(RectTransform parent, float x, float midY, float trackHeight,
        out RectTransform handle, Color trackColor)
    {
        var trackGo = new GameObject("Track", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        var track = trackGo.GetComponent<RectTransform>();
        track.SetParent(parent, false);
        track.sizeDelta = new Vector2(TrackW, trackHeight);
        track.anchoredPosition = new Vector2(x, midY);
        track.GetComponent<Image>().color = trackColor;
        track.GetComponent<Image>().raycastTarget = false;

        var handleGo = new GameObject("Handle", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        handle = handleGo.GetComponent<RectTransform>();
        handle.SetParent(parent, false);
        handle.sizeDelta = new Vector2(HandleW, HandleH);
        handle.GetComponent<Image>().color = Color.white;
        handle.GetComponent<Image>().raycastTarget = false;
    }
}
