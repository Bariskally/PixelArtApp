using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Attach to the Image GameObject used for allColorsImage.
/// Requires a reference to the ColorPickerPanelController_TMP to call OnPalettePicked(uv).
/// </summary>
[RequireComponent(typeof(Image))]
public class AllColorsPicker_TMP : MonoBehaviour, IPointerDownHandler, IPointerClickHandler
{
    public ColorPickerPanelController_TMP controller; // assign in inspector (or Find in Start)
    RectTransform rt;
    Image img;

    void Start()
    {
        img = GetComponent<Image>();
        rt = GetComponent<RectTransform>();

        if (controller == null)
            controller = GetComponentInParent<ColorPickerPanelController_TMP>();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        TryPick(eventData);
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        TryPick(eventData);
    }

    void TryPick(PointerEventData eventData)
    {
        if (controller == null) return;
        Vector2 local;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, eventData.position, eventData.pressEventCamera, out local))
            return;

        Rect rect = rt.rect;
        float px = (local.x - rect.x) / rect.width;   // 0..1
        float py = (local.y - rect.y) / rect.height;  // 0..1

        px = Mathf.Clamp01(px);
        py = Mathf.Clamp01(py);

        controller.OnPalettePicked(new Vector2(px, py));
    }
}