using UnityEngine;

[ExecuteAlways]
public class PaletteContentAutoExpand : MonoBehaviour
{
    public int itemsPerRow = 6;
    public int visibleRows = 7;
    public float rowHeight = 40f;

    RectTransform rect;

    void Awake()
    {
        rect = GetComponent<RectTransform>();
    }

    void LateUpdate()
    {
        if (rect == null) return;

        int childCount = transform.childCount;

        int rows = Mathf.CeilToInt((float)childCount / itemsPerRow);

        int rowsToShow = Mathf.Max(rows, visibleRows);

        float newHeight = rowsToShow * rowHeight;

        rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, newHeight);
    }
}