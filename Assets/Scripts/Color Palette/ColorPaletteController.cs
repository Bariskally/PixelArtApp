using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Renk havuzu yönetimi:
/// - contentPanel içinde ColorSlot prefab'larýný instantiate eder
/// - + butonuna basýnca colorPickerPanel açar
/// - picker'dan confirm ile yeni renk ekler
/// - eklenen slotlara týklanýnca pixelCanvas.SetDrawColor çaðrýlýr
/// </summary>
public class ColorPaletteController : MonoBehaviour
{
    [Header("UI References")]
    public RectTransform contentPanel;       // Scroll View > Viewport > Content
    public GameObject colorSlotPrefab;       // prefab with Button + Image + ColorSlotButton script
    public Button addButton;                 // + butonu (child of contentPanel if you want it at end)

    [Header("Color Picker UI")]
    public GameObject colorPickerPanel;      // pop-up panel (inactive by default)
    public InputField rInput;
    public InputField gInput;
    public InputField bInput;
    public Button confirmAddButton;
    public Button cancelButton;

    [Header("References")]
    public PixelCanvas pixelCanvas;          // assign your PixelCanvas here

    // selection visual
    Image selectedSlotBorder;
    GameObject selectedSlotGO;

    void Start()
    {
        if (addButton != null) addButton.onClick.AddListener(OnAddButtonClicked);

        if (confirmAddButton != null) confirmAddButton.onClick.AddListener(OnConfirmAdd);
        if (cancelButton != null) cancelButton.onClick.AddListener(ClosePicker);

        // Ensure picker is closed initially
        if (colorPickerPanel != null) colorPickerPanel.SetActive(false);
    }

    void OnDestroy()
    {
        if (addButton != null) addButton.onClick.RemoveListener(OnAddButtonClicked);
        if (confirmAddButton != null) confirmAddButton.onClick.RemoveListener(OnConfirmAdd);
        if (cancelButton != null) cancelButton.onClick.RemoveListener(ClosePicker);
    }

    // + butonuna basýldý
    public void OnAddButtonClicked()
    {
        OpenPicker();
    }

    public void OpenPicker()
    {
        if (colorPickerPanel == null) return;
        // Reset inputs to white (255)
        if (rInput != null) rInput.text = "255";
        if (gInput != null) gInput.text = "255";
        if (bInput != null) bInput.text = "255";

        colorPickerPanel.SetActive(true);

        // Optionally focus first field
        if (rInput != null) rInput.Select();
    }

    public void ClosePicker()
    {
        if (colorPickerPanel == null) return;
        colorPickerPanel.SetActive(false);
    }

    void OnConfirmAdd()
    {
        // Parse RGB
        int r = Parse255(rInput);
        int g = Parse255(gInput);
        int b = Parse255(bInput);

        Color32 col = new Color32((byte)r, (byte)g, (byte)b, 255);
        AddColorSlot(col);

        ClosePicker();
    }

    int Parse255(InputField f)
    {
        if (f == null) return 255;
        int v = 255;
        if (!int.TryParse(f.text, out v)) v = 255;
        v = Mathf.Clamp(v, 0, 255);
        return v;
    }

    // Instantiate a color slot at the end of contentPanel (before the addButton ideally)
    public GameObject AddColorSlot(Color32 color)
    {
        if (colorSlotPrefab == null || contentPanel == null) return null;

        // Instantiate
        GameObject go = Instantiate(colorSlotPrefab, contentPanel);
        go.name = "ColorSlot_" + color.r + "_" + color.g + "_" + color.b;

        // Set image color
        Image img = go.GetComponent<Image>();
        if (img != null)
        {
            img.color = color;
        }
        else
        {
            // maybe the Image is on a child
            var imgChild = go.GetComponentInChildren<Image>();
            if (imgChild != null) imgChild.color = color;
        }

        // Setup ColorSlotButton script
        ColorSlotButton slotScript = go.GetComponent<ColorSlotButton>();
        if (slotScript == null)
        {
            slotScript = go.AddComponent<ColorSlotButton>();
        }
        slotScript.Setup(color, this, pixelCanvas);

        // Make sure added slot is before addButton if addButton is a child of contentPanel
        if (addButton != null && addButton.transform.parent == contentPanel)
        {
            // move our slot before the last child (addButton)
            go.transform.SetSiblingIndex(contentPanel.childCount - 1);
        }

        return go;
    }

    // Called by ColorSlotButton when clicked
    public void OnColorSlotClicked(ColorSlotButton slot)
    {
        if (slot == null) return;

        // Set canvas draw color
        if (pixelCanvas != null) pixelCanvas.SetDrawColor(slot.color);

        // Update selection visual
        UpdateSelectionVisual(slot.gameObject);
    }

    void UpdateSelectionVisual(GameObject newSelected)
    {
        // Clear previous
        if (selectedSlotGO != null)
        {
            var prevOutline = selectedSlotGO.GetComponent<Outline>();
            if (prevOutline != null) prevOutline.enabled = false;
        }

        selectedSlotGO = newSelected;

        if (selectedSlotGO != null)
        {
            var o = selectedSlotGO.GetComponent<Outline>();
            if (o == null)
            {
                o = selectedSlotGO.AddComponent<Outline>();
                o.effectColor = Color.yellow;
                o.effectDistance = new Vector2(2f, 2f);
            }
            o.enabled = true;
        }
    }

    // Optional helper to preload some default colors
    [ContextMenu("Add basic colors")]
    public void AddBasicColors()
    {
        AddColorSlot(new Color32(0, 0, 0, 255));
        AddColorSlot(new Color32(255, 255, 255, 255));
        AddColorSlot(new Color32(255, 0, 0, 255));
        AddColorSlot(new Color32(0, 255, 0, 255));
        AddColorSlot(new Color32(0, 0, 255, 255));
    }
}