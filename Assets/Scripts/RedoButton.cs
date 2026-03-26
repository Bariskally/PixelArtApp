using System.Collections;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Button))]
public class RedoButton : MonoBehaviour
{
    public PixelCanvas pixelCanvas; // assign in inspector
    public float flashDuration = 0.12f;
    public Color flashColor = new Color(0.8f, 0.8f, 0.8f, 1f);

    Button btn;
    Image img;
    Color originalColor;

    void Awake()
    {
        btn = GetComponent<Button>();
        img = GetComponent<Image>();
        if (img != null) originalColor = img.color;

        btn.onClick.AddListener(OnClick);
    }

    void OnDestroy()
    {
        if (btn != null) btn.onClick.RemoveListener(OnClick);
    }

    void OnClick()
    {
        if (pixelCanvas == null) return;

        pixelCanvas.ClearSelectedUINextFrame();
        pixelCanvas.IgnorePointerForOneFrame();

        pixelCanvas.Redo();

        if (img != null)
            StartCoroutine(FlashCoroutine());
    }

    IEnumerator FlashCoroutine()
    {
        img.color = flashColor;
        yield return new WaitForSeconds(flashDuration);
        img.color = originalColor;
    }
}