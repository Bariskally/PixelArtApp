using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Her bir renk slotu için küçük yardýmcý.
/// Setup ile rengini ve controller'ý alýr, týklanýnca controller'a haber verir.
/// </summary>
[RequireComponent(typeof(Button))]
public class ColorSlotButton : MonoBehaviour
{
    public Color32 color;
    ColorPaletteController controller;
    public PixelCanvas pixelCanvas; // public yapýp inspector'dan da atayabilirsin

    Button btn;
    Image img;

    public void Setup(Color32 c, ColorPaletteController ctrl, PixelCanvas canvasRef)
    {
        color = c;
        controller = ctrl;
        pixelCanvas = canvasRef;

        btn = GetComponent<Button>();
        img = GetComponent<Image>();

        if (img != null)
            img.color = color;

        // ensure no duplicate listeners
        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(OnClick);
    }

    void OnClick()
    {
        // Debug: hangi renk geldiðini görebilmek için log ekliyoruz
        Debug.Log($"ColorSlotButton clicked. color = R:{color.r} G:{color.g} B:{color.b} A:{color.a} | pixelCanvas present: {(pixelCanvas != null)} | controller present: {(controller != null)}");

        // Öncelikle doðrudan pixelCanvas referansý varsa ona ata.
        if (pixelCanvas != null)
        {
            pixelCanvas.SetDrawColor(color);
            return;
        }

        // Eðer pixelCanvas null ise controller'a haber ver (controller proxy varsa o parent üzerinden atama yapabilir)
        if (controller != null)
        {
            controller.OnColorSlotClicked(this);
            return;
        }

        // Son çare: sahnede bir PixelCanvas bulmaya çalýþ ve ata
        var found = FindObjectOfType<PixelCanvas>();
        if (found != null)
        {
            Debug.Log("[ColorSlotButton] fallback found PixelCanvas: " + found.name);
            found.SetDrawColor(color);
        }
        else
        {
            Debug.LogWarning("[ColorSlotButton] No PixelCanvas available to set color!");
        }
    }

    void OnDestroy()
    {
        if (btn != null) btn.onClick.RemoveListener(OnClick);
    }
}