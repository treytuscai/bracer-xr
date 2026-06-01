using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// World-space canvas for widgets carried on the fingertip.
/// Palette clones are reparented here during carry so Unity UI keeps rendering them.
/// Arm widgets stay on ItemList (rendered via the forearm RT) and never use this.
/// </summary>
public class WidgetCarryCanvas : MonoBehaviour
{
    static WidgetCarryCanvas _instance;

    public int sortingOrder = 200;

    RectTransform _root;

    public static RectTransform Root
    {
        get
        {
            EnsureInstance();
            return _instance._root;
        }
    }

    static void EnsureInstance()
    {
        if (_instance != null)
            return;

        var existing = FindObjectOfType<WidgetCarryCanvas>();
        if (existing != null)
        {
            _instance = existing;
            _instance.EnsureRoot();
            return;
        }

        var go = new GameObject("WidgetCarryCanvas");
        _instance = go.AddComponent<WidgetCarryCanvas>();
        _instance.EnsureRoot();
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        EnsureRoot();
    }

    void EnsureRoot()
    {
        if (_root != null)
            return;

        _root = GetComponent<RectTransform>();
        if (_root == null)
            _root = gameObject.AddComponent<RectTransform>();

        var canvas = GetComponent<Canvas>();
        if (canvas == null)
            canvas = gameObject.AddComponent<Canvas>();

        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = sortingOrder;

        if (GetComponent<GraphicRaycaster>() == null)
            gameObject.AddComponent<GraphicRaycaster>();

        _root.sizeDelta = new Vector2(400f, 400f);
        transform.localScale = Vector3.one;
    }
}
