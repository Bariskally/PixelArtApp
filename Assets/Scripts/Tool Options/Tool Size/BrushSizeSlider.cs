using UnityEngine;
using UnityEngine.UI;
using TMPro;   // TextMeshPro için

public class BrushSizeSlider : MonoBehaviour
{
    [SerializeField] private Slider slider;
    [SerializeField] private PixelCanvas pixelCanvas;
    [SerializeField] private TMP_Text sizeLabel;   // <-- TextMeshPro referansý

    private readonly int[] brushSizes = { 1, 2, 4, 8, 16, 32 };

    void Start()
    {
        if (slider == null)
            slider = GetComponent<Slider>();

        if (pixelCanvas == null)
            pixelCanvas = FindObjectOfType<PixelCanvas>();

        slider.minValue = 0;
        slider.maxValue = brushSizes.Length - 1;
        slider.wholeNumbers = true;
        slider.value = 0; // default 1

        slider.onValueChanged.AddListener(OnSliderChanged);
        OnSliderChanged(slider.value);
    }

    void OnDestroy()
    {
        if (slider != null)
            slider.onValueChanged.RemoveListener(OnSliderChanged);
    }

    void OnSliderChanged(float value)
    {
        int index = Mathf.RoundToInt(value);
        if (index >= 0 && index < brushSizes.Length)
        {
            int size = brushSizes[index];
            pixelCanvas?.SetBrushSize(size);

            if (sizeLabel != null)
            {
                sizeLabel.text = $"{size}x";
            }
        }
    }
}