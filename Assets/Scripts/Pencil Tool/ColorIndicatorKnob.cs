using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// PixelCanvas'taki DrawColor deðiþtiðinde baðlý olduðu Image'in rengini günceller.
/// Kalem, silgi gibi araçlarýn köþesindeki renk göstergesi için kullanýlýr.
/// </summary>
[RequireComponent(typeof(Image))]
public class ColorIndicatorKnob : MonoBehaviour
{
    [Tooltip("Referans ver ya da sahnede otomatik bulsun")]
    public PixelCanvas pixelCanvas;

    private Image knobImage;

    void Start()
    {
        knobImage = GetComponent<Image>();

        if (pixelCanvas == null)
            pixelCanvas = FindObjectOfType<PixelCanvas>();

        if (pixelCanvas != null)
        {
            // Event'e abone ol ve mevcut rengi hemen uygula
            pixelCanvas.OnDrawColorChanged += UpdateKnobColor;
            UpdateKnobColor(pixelCanvas.drawColor);
        }
    }

    void OnDestroy()
    {
        if (pixelCanvas != null)
            pixelCanvas.OnDrawColorChanged -= UpdateKnobColor;
    }

    private void UpdateKnobColor(Color32 newColor)
    {
        if (knobImage != null)
            knobImage.color = newColor; // Color32 -> Color implicit dönüþüm
    }
}