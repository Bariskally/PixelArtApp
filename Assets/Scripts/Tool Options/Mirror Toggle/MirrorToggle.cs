using UnityEngine;
using UnityEngine.UI;

public class MirrorToggle : MonoBehaviour
{
    [SerializeField] private Toggle toggle;
    [SerializeField] private PixelCanvas pixelCanvas;

    public enum MirrorAxis { X, Y }
    public MirrorAxis axis;

    void Start()
    {
        if (toggle == null) toggle = GetComponent<Toggle>();
        if (pixelCanvas == null) pixelCanvas = FindObjectOfType<PixelCanvas>();

        toggle.onValueChanged.AddListener(OnToggleChanged);
        OnToggleChanged(toggle.isOn); // ilk deðeri uygula
    }

    void OnDestroy()
    {
        if (toggle != null)
            toggle.onValueChanged.RemoveListener(OnToggleChanged);
    }

    void OnToggleChanged(bool isOn)
    {
        if (pixelCanvas == null) return;
        if (axis == MirrorAxis.X) pixelCanvas.SetMirrorX(isOn);
        else pixelCanvas.SetMirrorY(isOn);
    }
}