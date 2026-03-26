using UnityEngine;
using UnityEngine.UI;
using System.Globalization;
using TMPro;

/// <summary>
/// Color picker panel controller (TextMeshPro input fields version)
/// - TMP_InputField'lara göre live preview günceller
/// - hex input ile çift yönlü senkronize eder
/// - allColorsImage için bir hue-strip texture üretir (veya sprite olarak atanmýþsa onu kullanýr)
/// - confirmButton çaðrýsýnda RuntimeColorPaletteController.AddColorSlot çaðýrýlýr (bulunursa)
/// </summary>
public class ColorPickerPanelController_TMP : MonoBehaviour
{
    [Header("Inputs (TMP)")]
    public TMP_InputField rInput;
    public TMP_InputField gInput;
    public TMP_InputField bInput;
    public TMP_InputField hexInput; // accepts "#RRGGBB" or "RRGGBB"

    [Header("Previews")]
    public Image livePreviewImage;   // single color preview (small square)
    public Image allColorsImage;     // full palette preview (will be populated with generated texture)

    [Header("Buttons")]
    public Button confirmButton;
    public Button cancelButton;

    [Header("Optional")]
    public PixelCanvas pixelCanvas;  // optional quick-apply for debug

    [Header("Display options")]
    [Tooltip("If true: keep aspect using AspectRatioFitter. If false: stretch to parent width and use displayHeight.")]
    public bool useAspectFitter = false;
    [Tooltip("Used when useAspectFitter == false (pixels).")]
    public float displayHeight = 120f;

    // internal
    Texture2D paletteTexture;
    bool isUpdatingFromCode = false; // prevents recursion when we update inputs programmatically

    void Start()
    {
        // Hook TMP input listeners
        if (rInput != null) rInput.onValueChanged.AddListener(OnRGBInputChanged);
        if (gInput != null) gInput.onValueChanged.AddListener(OnRGBInputChanged);
        if (bInput != null) bInput.onValueChanged.AddListener(OnRGBInputChanged);
        if (hexInput != null) hexInput.onValueChanged.AddListener(OnHexInputChanged);

        if (confirmButton != null) confirmButton.onClick.AddListener(OnConfirmAdd);
        if (cancelButton != null) cancelButton.onClick.AddListener(Close);

        // Create palette texture (hue gradient) and assign sprite + layout handling
        CreatePaletteTexture(360, 40);

        // initial preview (reads inputs or defaults)
        UpdateLivePreviewFromInputs();
    }

    void OnDestroy()
    {
        if (rInput != null) rInput.onValueChanged.RemoveListener(OnRGBInputChanged);
        if (gInput != null) gInput.onValueChanged.RemoveListener(OnRGBInputChanged);
        if (bInput != null) bInput.onValueChanged.RemoveListener(OnRGBInputChanged);
        if (hexInput != null) hexInput.onValueChanged.RemoveListener(OnHexInputChanged);

        if (confirmButton != null) confirmButton.onClick.RemoveListener(OnConfirmAdd);
        if (cancelButton != null) cancelButton.onClick.RemoveListener(Close);
    }

    /// <summary>
    /// Creates a simple hue strip texture and assigns it as a sprite to allColorsImage.
    /// Also configures display based on useAspectFitter / displayHeight.
    /// </summary>
    void CreatePaletteTexture(int width, int height)
    {
        if (allColorsImage == null) return;

        paletteTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        paletteTexture.wrapMode = TextureWrapMode.Clamp;
        paletteTexture.filterMode = FilterMode.Bilinear;

        for (int x = 0; x < width; x++)
        {
            float h = (float)x / Mathf.Max(1, width - 1); // 0..1
            Color col = Color.HSVToRGB(h, 1f, 1f);
            for (int y = 0; y < height; y++)
            {
                paletteTexture.SetPixel(x, y, col);
            }
        }

        paletteTexture.Apply();

        // create sprite and assign to image
        Sprite s = Sprite.Create(paletteTexture, new Rect(0, 0, paletteTexture.width, paletteTexture.height), new Vector2(0.5f, 0.5f), 100f);
        allColorsImage.sprite = s;
        allColorsImage.type = Image.Type.Simple;

        // === Do NOT blindly set allColorsImage.preserveAspect = true (that overwrites inspector).
        // Instead, explicitly handle layout based on the selected display option.

        // disable Image.preserveAspect to allow our layout control to work
        allColorsImage.preserveAspect = false;

        if (useAspectFitter)
        {
            // Use AspectRatioFitter to keep aspect and fill parent as configured
            var arf = allColorsImage.GetComponent<AspectRatioFitter>();
            if (arf == null) arf = allColorsImage.gameObject.AddComponent<AspectRatioFitter>();
            arf.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent; // or FitInParent if you prefer
            arf.aspectRatio = (float)paletteTexture.width / (float)paletteTexture.height; // e.g. 360/40 = 9.0
        }
        else
        {
            // Stretch to parent width + fixed height (displayHeight)
            // Force layout update so parent rect has meaningful size
            Canvas.ForceUpdateCanvases();

            RectTransform parentRect = allColorsImage.rectTransform.parent as RectTransform;
            float parentWidth = 0f;
            if (parentRect != null)
            {
                parentWidth = parentRect.rect.width;
            }

            if (parentWidth <= 0f)
            {
                // fallback to canvas width
                var canvas = allColorsImage.canvas;
                if (canvas != null) parentWidth = canvas.pixelRect.width;
            }

            if (parentWidth <= 0f) parentWidth = 800f; // ultimate fallback

            allColorsImage.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, parentWidth);
            allColorsImage.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, displayHeight);
        }

        // Ensure UI updates immediately
        Canvas.ForceUpdateCanvases();
    }

    // Called when any R/G/B input changes (TMP onValueChanged passes new string)
    void OnRGBInputChanged(string _)
    {
        if (isUpdatingFromCode) return;
        UpdateLivePreviewFromInputs();
    }

    // Called when hex input changes
    void OnHexInputChanged(string _)
    {
        if (isUpdatingFromCode) return;
        if (hexInput == null) return;

        if (TryParseHex(hexInput.text, out Color32 c))
        {
            // update rgb fields without recursion
            isUpdatingFromCode = true;
            if (rInput != null) rInput.text = c.r.ToString();
            if (gInput != null) gInput.text = c.g.ToString();
            if (bInput != null) bInput.text = c.b.ToString();
            isUpdatingFromCode = false;

            ApplyColorToLivePreview(c);
        }
    }

    void UpdateLivePreviewFromInputs()
    {
        byte r = ParseByteFromInput(rInput, 255);
        byte g = ParseByteFromInput(gInput, 255);
        byte b = ParseByteFromInput(bInput, 255);

        Color32 c = new Color32(r, g, b, 255);

        // update hex field programmatically (suspend recursion)
        isUpdatingFromCode = true;
        if (hexInput != null) hexInput.text = ColorToHex(c);
        isUpdatingFromCode = false;

        ApplyColorToLivePreview(c);
    }

    void ApplyColorToLivePreview(Color32 c)
    {
        if (livePreviewImage != null)
        {
            livePreviewImage.color = new Color32(c.r, c.g, c.b, c.a);
        }

        // optional: apply instantly to canvas for quick test
        if (pixelCanvas != null)
        {
            pixelCanvas.SetDrawColor(c);
        }
    }

    // parse 0..255 from TMP_InputField
    byte ParseByteFromInput(TMP_InputField f, byte fallback)
    {
        if (f == null) return fallback;
        if (int.TryParse(f.text, out int v))
        {
            v = Mathf.Clamp(v, 0, 255);
            return (byte)v;
        }
        return fallback;
    }

    // hex helpers
    string ColorToHex(Color32 c)
    {
        return $"{c.r:X2}{c.g:X2}{c.b:X2}";
    }

    bool TryParseHex(string input, out Color32 c)
    {
        c = new Color32(255, 255, 255, 255);
        if (string.IsNullOrEmpty(input)) return false;
        string s = input.Trim();
        if (s.StartsWith("#")) s = s.Substring(1);
        if (s.Length != 6) return false;
        if (uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
        {
            byte r = (byte)((hex >> 16) & 0xFF);
            byte g = (byte)((hex >> 8) & 0xFF);
            byte b = (byte)(hex & 0xFF);
            c = new Color32(r, g, b, 255);
            return true;
        }
        return false;
    }

    // Called by AllColorsPicker when user clicks the palette; uv in [0..1]
    public void OnPalettePicked(Vector2 uv)
    {
        if (paletteTexture == null) return;

        int px = Mathf.Clamp(Mathf.FloorToInt(uv.x * paletteTexture.width), 0, paletteTexture.width - 1);
        int py = Mathf.Clamp(Mathf.FloorToInt(uv.y * paletteTexture.height), 0, paletteTexture.height - 1);
        Color32 c = paletteTexture.GetPixel(px, py);

        // update inputs + preview
        isUpdatingFromCode = true;
        if (rInput != null) rInput.text = c.r.ToString();
        if (gInput != null) gInput.text = c.g.ToString();
        if (bInput != null) bInput.text = c.b.ToString();
        if (hexInput != null) hexInput.text = ColorToHex(c);
        isUpdatingFromCode = false;

        ApplyColorToLivePreview(c);
    }

    // Close picker (hide)
    public void Close()
    {
        gameObject.SetActive(false);
    }

    // Confirm: add slot via runtime controller if present
    public void OnConfirmAdd()
    {
        byte r = ParseByteFromInput(rInput, 255);
        byte g = ParseByteFromInput(gInput, 255);
        byte b = ParseByteFromInput(bInput, 255);
        Color32 c = new Color32(r, g, b, 255);

        var runtime = FindObjectOfType<RuntimeColorPaletteController>();
        if (runtime != null)
        {
            runtime.AddColorSlot(c);
        }
        else
        {
            Debug.Log("[ColorPickerPanelController_TMP] No RuntimeColorPaletteController found; you can hook confirm to your own method.");
        }

        Close();
    }
}