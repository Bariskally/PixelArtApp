using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Runtime color palette controller (creates UI at runtime if prefabs provided).
/// - instantiates ScrollView, AddButton, ColorSlot prefabs into a target Canvas
/// - prevents duplicate colors from being added
/// - forces layout rebuilds after changes so ScrollRect handles resize correctly
/// - saves palette via optional saveSystem
/// 
/// Improvements:
/// - AddColorsBulk(List<Color32>) for bulk inserts (single layout/save)
/// - pendingAdds queue for calls that happen before Start() finishes
/// - AddColorSlot attempts Ensure* initialization if needed
/// </summary>
public class RuntimeColorPaletteController : MonoBehaviour
{
    [Header("Prefabs (assign in Inspector) — these can remain only in Project/Assets")]
    public GameObject scrollViewPrefab;
    public GameObject colorSlotPrefab;
    public GameObject addButtonPrefab;
    public GameObject colorPickerPanelPrefab;

    [Header("Save System")]
    public ColorPaletteSaveSystem saveSystem;

    [Header("Optional runtime references (will be found/created if null)")]
    public Canvas targetCanvas;
    public RectTransform contentPanel;

    [Header("Runtime options")]
    public bool openPickerOnStart = false;
    public bool preloadSomeColors = true;

    GameObject instantiatedScrollView;
    GameObject instantiatedPicker;
    Button instantiatedAddButton;
    List<GameObject> instantiatedSlots = new List<GameObject>();

    List<Color32> paletteColors = new List<Color32>();

    InputField rInput, gInput, bInput;
    Button confirmButton, cancelButton;

    public PixelCanvas pixelCanvas;

    bool isLoadingPalette = false;

    // NEW: readiness / pending queue and state
    bool isReady = false;
    List<Color32> pendingAdds = new List<Color32>();

    void Start()
    {
        EnsureCanvas();
        EnsureScrollViewAndContent();
        EnsureColorPickerPanel();
        EnsureAddButton();

        if (instantiatedAddButton != null)
        {
            instantiatedAddButton.onClick.RemoveAllListeners();
            instantiatedAddButton.onClick.AddListener(OpenPicker);
        }

        // initial layout/update
        if (contentPanel != null)
        {
            Canvas.ForceUpdateCanvases();
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(contentPanel);
            UpdateScrollbarHandle();
        }

        LoadPalette();

        if (paletteColors.Count == 0 && preloadSomeColors)
        {
            // use bulk add to avoid repeated layout thrash
            AddColorsBulk(new List<Color32> {
                new Color32(0,0,0,255),
                new Color32(255,255,255,255),
                new Color32(255,0,0,255),
                new Color32(0,255,0,255),
                new Color32(0,0,255,255)
            });
        }

        if (openPickerOnStart) OpenPicker();

        // mark ready and flush any pending adds
        isReady = true;
        if (pendingAdds != null && pendingAdds.Count > 0)
        {
            Debug.Log($"[RuntimeColorPaletteController] Flushing {pendingAdds.Count} pending colors added before initialization.");
            AddColorsBulk(new List<Color32>(pendingAdds));
            pendingAdds.Clear();
        }
    }

    void EnsureCanvas()
    {
        if (targetCanvas != null) return;

        targetCanvas = FindObjectOfType<Canvas>();

        if (targetCanvas != null) return;

        GameObject cgo = new GameObject("RuntimeCanvas");
        targetCanvas = cgo.AddComponent<Canvas>();
        targetCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        cgo.AddComponent<CanvasScaler>();
        cgo.AddComponent<GraphicRaycaster>();
    }

    void EnsureScrollViewAndContent()
    {
        if (contentPanel != null) return;

        if (scrollViewPrefab == null)
        {
            Debug.LogWarning("[RuntimeColorPaletteController] scrollViewPrefab is null — cannot create runtime scroll view.");
            return;
        }

        // reuse existing if already instantiated in a previous run
        if (instantiatedScrollView == null)
        {
            instantiatedScrollView = Instantiate(scrollViewPrefab, targetCanvas != null ? targetCanvas.transform : null);
            instantiatedScrollView.name = "RuntimeScrollView";
        }
        else
        {
            // parent correctly
            instantiatedScrollView.transform.SetParent(targetCanvas != null ? targetCanvas.transform : null, false);
        }

        instantiatedScrollView.SetActive(true);
        instantiatedScrollView.transform.localScale = Vector3.one;
        instantiatedScrollView.transform.SetAsLastSibling();

        // disable nested Canvas in prefab if present (avoid nested canvas ordering issues)
        var childCanvas = instantiatedScrollView.GetComponentInChildren<Canvas>(true);
        if (childCanvas != null && childCanvas != targetCanvas)
        {
            childCanvas.enabled = false;
            Debug.Log("[RuntimeColorPaletteController] Disabled nested Canvas inside scrollViewPrefab to avoid render/order issues.");
        }

        // Try to find Viewport/Content
        Transform contentTr = instantiatedScrollView.transform.Find("Viewport/Content");
        if (contentTr == null)
        {
            // fallback: direct Content or first matching child
            contentTr = instantiatedScrollView.transform.Find("Content");
            if (contentTr == null)
                contentTr = instantiatedScrollView.GetComponentInChildren<RectTransform>(true)?.transform;
        }

        if (contentTr != null)
            contentPanel = contentTr as RectTransform;
        else
            Debug.LogWarning("[RuntimeColorPaletteController] Could not find Content (Viewport/Content) inside the scrollViewPrefab. Make sure prefab contains Viewport/Content path.");

        var sr = instantiatedScrollView.GetComponentInChildren<ScrollRect>(true);
        if (sr == null)
        {
            Debug.LogWarning("[RuntimeColorPaletteController] No ScrollRect found in instantiated scroll view prefab.");
        }
        else
        {
            if (sr.viewport == null)
            {
                var viewportTr = instantiatedScrollView.transform.Find("Viewport") as RectTransform;
                if (viewportTr != null) sr.viewport = viewportTr;
            }
            if (sr.content == null && contentPanel != null)
            {
                sr.content = contentPanel;
            }
        }

        // Ensure layout group & content fitter config (helpful if prefab settings aren't perfect)
        if (contentPanel != null)
        {
            var vlg = contentPanel.GetComponent<VerticalLayoutGroup>();
            if (vlg != null)
            {
                vlg.childControlHeight = false;
                vlg.childForceExpandHeight = false;
                vlg.childControlWidth = true;
                vlg.childForceExpandWidth = true;
                vlg.childAlignment = TextAnchor.UpperCenter;
            }

            var csf = contentPanel.GetComponent<ContentSizeFitter>();
            if (csf != null)
            {
                csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            }

            Canvas.ForceUpdateCanvases();
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(contentPanel);
            if (sr != null && sr.viewport != null)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(sr.viewport);

            UpdateScrollbarHandle();
        }

        DebugLogScrollViewState();
    }

    void EnsureColorPickerPanel()
    {
        if (colorPickerPanelPrefab == null) return;

        if (instantiatedPicker == null)
        {
            instantiatedPicker = Instantiate(colorPickerPanelPrefab, targetCanvas.transform);
            instantiatedPicker.name = "RuntimeColorPickerPanel";
            instantiatedPicker.SetActive(false);
        }

        var inputs = instantiatedPicker.GetComponentsInChildren<InputField>(true);
        if (inputs.Length >= 3)
        {
            rInput = inputs[0];
            gInput = inputs[1];
            bInput = inputs[2];
        }

        var buttons = instantiatedPicker.GetComponentsInChildren<Button>(true);
        if (buttons.Length >= 2)
        {
            confirmButton = buttons[0];
            cancelButton = buttons[1];
        }

        if (confirmButton != null)
        {
            confirmButton.onClick.RemoveAllListeners();
            confirmButton.onClick.AddListener(ClosePicker);
        }

        if (cancelButton != null)
        {
            cancelButton.onClick.RemoveAllListeners();
            cancelButton.onClick.AddListener(ClosePicker);
        }
    }

    void EnsureAddButton()
    {
        if (contentPanel == null || addButtonPrefab == null) return;

        var existing = contentPanel.Find("RuntimeAddButton");
        if (existing != null)
        {
            instantiatedAddButton = existing.GetComponent<Button>();
            return;
        }

        GameObject go = Instantiate(addButtonPrefab, contentPanel);
        go.name = "RuntimeAddButton";

        instantiatedAddButton = go.GetComponent<Button>();

        if (instantiatedAddButton != null)
        {
            go.transform.SetAsLastSibling();
        }
    }

    public void OpenPicker()
    {
        if (instantiatedPicker == null) return;

        if (rInput != null) rInput.text = "255";
        if (gInput != null) gInput.text = "255";
        if (bInput != null) bInput.text = "255";

        instantiatedPicker.SetActive(true);

        if (rInput != null) rInput.Select();
    }

    public void ClosePicker()
    {
        if (instantiatedPicker != null)
            instantiatedPicker.SetActive(false);
    }

    // Instantiate a color slot at the end of contentPanel (before the addButton ideally)
    // NOTE: If the controller is not ready / contentPanel null, this will attempt to initialize,
    // and if still not ready it will queue the color for later so it is not lost.
    public GameObject AddColorSlot(Color32 color)
    {
        // Ensure initialization in case Start() hasn't run yet
        if (contentPanel == null || colorSlotPrefab == null)
        {
            EnsureCanvas();
            EnsureScrollViewAndContent();
            EnsureColorPickerPanel();
            EnsureAddButton();
        }

        if (colorSlotPrefab == null || contentPanel == null)
        {
            // not ready yet -> queue and return (will be flushed when Start marks isReady)
            pendingAdds.Add(color);
            Debug.LogWarning("[RuntimeColorPaletteController] AddColorSlot called before controller ready - queued color.");
            return null;
        }

        // 1) duplicate check (compare RGB only)
        for (int i = 0; i < paletteColors.Count; i++)
        {
            Color32 c = paletteColors[i];
            if (c.r == color.r && c.g == color.g && c.b == color.b)
            {
                Debug.Log($"[RuntimeColorPaletteController] Duplicate color detected, selecting existing slot for {color.r},{color.g},{color.b}");
                if (i < instantiatedSlots.Count && instantiatedSlots[i] != null)
                {
                    UpdateSelectionVisual(instantiatedSlots[i]);
                    EnsureLayoutUpdated();
                    RefreshContentSizeAndScroll();
                    return instantiatedSlots[i];
                }
                return null;
            }
        }

        // 2) Instantiate new slot
        GameObject go = Instantiate(colorSlotPrefab, contentPanel);
        go.name = "ColorSlot_" + color.r + "_" + color.g + "_" + color.b;

        // Ensure the slot has an Image to color
        Image img = go.GetComponent<Image>();
        if (img != null) img.color = color;
        else
        {
            var imgChild = go.GetComponentInChildren<Image>();
            if (imgChild != null) imgChild.color = color;
            else Debug.LogWarning("[RuntimeColorPaletteController] colorSlotPrefab has no Image component to set the color on.");
        }

        // Guarantee LayoutElement exists so LayoutGroup can compute sizes
        RectTransform slotRt = go.GetComponent<RectTransform>();
        var le = go.GetComponent<LayoutElement>();
        if (le == null) le = go.AddComponent<LayoutElement>();

        // If prefab doesn't provide a preferred height, use the rect height (or fallback)
        if (le.preferredHeight <= 0f)
        {
            float h = 0f;
            if (slotRt != null) h = Mathf.Abs(slotRt.rect.height);
            if (h <= 0f) h = 40f; // fallback height if prefab rect is zero
            le.preferredHeight = h;
        }

        if (!go.activeSelf) go.SetActive(true);

        if (slotRt != null)
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(slotRt);

        ColorSlotButton slot = go.GetComponent<ColorSlotButton>();
        if (slot == null)
            slot = go.AddComponent<ColorSlotButton>();

        slot.Setup(color, new ColorPaletteProxy(this), pixelCanvas);

        // Insert before runtime add button if present
        if (instantiatedAddButton != null)
        {
            go.transform.SetSiblingIndex(instantiatedAddButton.transform.GetSiblingIndex());
        }

        instantiatedSlots.Add(go);
        paletteColors.Add(color);

        if (!isLoadingPalette && saveSystem != null)
        {
            // Save after adding a single slot (preserve previous behavior)
            saveSystem.SavePalette(paletteColors);
        }

        // Force layout rebuild so scrollbars update properly
        EnsureLayoutUpdated();

        // Refresh content size, scrollbar and scroll to bottom
        RefreshContentSizeAndScroll();

        ExpandContentByRows();

        return go;
    }

    /// <summary>
    /// Tüm renk slotlar?n? kald?r?p yeni paleti yükler (haz?r palet seçimi için).
    /// </summary>
    public void ClearAllSlotsAndAdd(List<Color32> colors)
    {
        if (colors == null || colors.Count == 0) return;

        if (contentPanel == null || colorSlotPrefab == null)
        {
            EnsureCanvas();
            EnsureScrollViewAndContent();
            EnsureColorPickerPanel();
            EnsureAddButton();
        }

        if (contentPanel == null) return;

        for (int i = instantiatedSlots.Count - 1; i >= 0; i--)
        {
            if (instantiatedSlots[i] != null)
                Destroy(instantiatedSlots[i]);
        }
        instantiatedSlots.Clear();
        paletteColors.Clear();

        AddColorsBulk(colors);
    }

    /// <summary>
    /// NEW: Bulk-add colors in one operation.
    /// - Prevents repeated layout rebuilds and repeated Save calls.
    /// - Safe to call before Start() finishes (colors will be queued).
    /// </summary>
    public void AddColorsBulk(List<Color32> colors)
    {
        if (colors == null || colors.Count == 0) return;

        // If not ready, queue them
        if (!isReady && (contentPanel == null || colorSlotPrefab == null))
        {
            Debug.Log($"[RuntimeColorPaletteController] AddColorsBulk called before ready - queuing {colors.Count} colors.");
            pendingAdds.AddRange(colors);
            return;
        }

        // ensure UI prepared
        if (contentPanel == null || colorSlotPrefab == null)
        {
            EnsureCanvas();
            EnsureScrollViewAndContent();
            EnsureColorPickerPanel();
            EnsureAddButton();

            // still not ready?
            if (contentPanel == null || colorSlotPrefab == null)
            {
                pendingAdds.AddRange(colors);
                Debug.LogWarning("[RuntimeColorPaletteController] AddColorsBulk could not initialize UI, queued colors.");
                return;
            }
        }

        int added = 0;
        foreach (var color in colors)
        {
            // duplicate check
            bool duplicated = false;
            for (int i = 0; i < paletteColors.Count; i++)
            {
                Color32 c = paletteColors[i];
                if (c.r == color.r && c.g == color.g && c.b == color.b)
                {
                    duplicated = true;
                    break;
                }
            }
            if (duplicated) continue;

            // Instantiate
            GameObject go = Instantiate(colorSlotPrefab, contentPanel);
            go.name = "ColorSlot_" + color.r + "_" + color.g + "_" + color.b;

            Image img = go.GetComponent<Image>() ?? go.GetComponentInChildren<Image>();
            if (img != null) img.color = color;

            RectTransform slotRt = go.GetComponent<RectTransform>();
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            if (le.preferredHeight <= 0f)
            {
                float h = 0f;
                if (slotRt != null) h = Mathf.Abs(slotRt.rect.height);
                if (h <= 0f) h = 40f;
                le.preferredHeight = h;
            }

            if (!go.activeSelf) go.SetActive(true);

            if (slotRt != null)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(slotRt);

            ColorSlotButton slot = go.GetComponent<ColorSlotButton>();
            if (slot == null)
                slot = go.AddComponent<ColorSlotButton>();

            slot.Setup(color, new ColorPaletteProxy(this), pixelCanvas);

            // Insert before add button if present
            if (instantiatedAddButton != null)
            {
                go.transform.SetSiblingIndex(instantiatedAddButton.transform.GetSiblingIndex());
            }

            instantiatedSlots.Add(go);
            paletteColors.Add(color);
            added++;
        }

        if (added > 0)
        {
            // Save once after bulk add
            if (!isLoadingPalette && saveSystem != null)
            {
                saveSystem.SavePalette(paletteColors);
            }

            // One layout rebuild for the whole batch
            EnsureLayoutUpdated();
            RefreshContentSizeAndScroll();
            ExpandContentByRows();
            Debug.Log($"[RuntimeColorPaletteController] Added {added} colors in bulk.");
        }
    }

    // Force UI/Layout updates to recalc content size and scrollbar handle
    void EnsureLayoutUpdated()
    {
        if (contentPanel == null) return;
        Canvas.ForceUpdateCanvases();
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(contentPanel);
    }

    // Recalculate content size (preferred height) and apply it, update scrollbar and scroll to bottom
    void RefreshContentSizeAndScroll()
    {
        if (contentPanel == null || instantiatedScrollView == null) return;

        var sr = instantiatedScrollView.GetComponentInChildren<ScrollRect>(true);
        if (sr == null) return;

        // Ensure layout system has latest info
        Canvas.ForceUpdateCanvases();

        // Rebuild layout for each child (so LayoutGroup/Element measurements are fresh)
        for (int i = 0; i < contentPanel.childCount; i++)
        {
            var childRt = contentPanel.GetChild(i) as RectTransform;
            if (childRt != null)
                UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(childRt);
        }

        // Rebuild parent content
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(contentPanel);

        // Compute preferred height
        float preferredH = UnityEngine.UI.LayoutUtility.GetPreferredHeight(contentPanel);

        // Fallback compute by summing if LayoutUtility failed
        if (preferredH <= 0f)
        {
            float accum = 0f;
            for (int i = 0; i < contentPanel.childCount; i++)
            {
                var c = contentPanel.GetChild(i) as RectTransform;
                if (c == null) continue;
                accum += Mathf.Abs(c.rect.height);
            }
            if (accum > 0f) preferredH = accum;
            else preferredH = contentPanel.rect.height;
        }

        // Apply computed height
        contentPanel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, preferredH);

        // Rebuild again to settle
        Canvas.ForceUpdateCanvases();
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(contentPanel);

        // Update scrollbar handle and scroll to bottom
        UpdateScrollbarHandle();

        if (sr.vertical)
            sr.verticalNormalizedPosition = 0f;
    }

    // Update scrollbar handle size based on viewport/content ratio
    void UpdateScrollbarHandle()
    {
        if (instantiatedScrollView == null) return;
        var sr = instantiatedScrollView.GetComponentInChildren<ScrollRect>(true);
        if (sr == null) return;

        RectTransform vp = sr.viewport;
        RectTransform content = sr.content;
        if (vp == null || content == null) return;

        Canvas.ForceUpdateCanvases();
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        if (vp != null) UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(vp);

        float vpH = Mathf.Abs(vp.rect.height);
        float contentH = Mathf.Abs(content.rect.height);

        float size = 1f;
        if (contentH > 0f)
            size = Mathf.Clamp01(vpH / contentH);

        if (sr.vertical && sr.verticalScrollbar != null)
        {
            sr.verticalScrollbar.size = size;
            sr.verticalScrollbar.gameObject.SetActive(contentH > vpH);
        }

        if (sr.horizontal && sr.horizontalScrollbar != null)
        {
            float vpW = Mathf.Abs(vp.rect.width);
            float contentW = Mathf.Abs(content.rect.width);
            float hsize = (contentW > 0f) ? Mathf.Clamp01(vpW / contentW) : 1f;
            sr.horizontalScrollbar.size = hsize;
            sr.horizontalScrollbar.gameObject.SetActive(contentW > vpW);
        }
    }

    // Scroll a slot into view (not used as main scrolling now, but kept)
    void ScrollSlotIntoView(GameObject slot)
    {
        if (slot == null || contentPanel == null) return;

        var sr = instantiatedScrollView != null ? instantiatedScrollView.GetComponentInChildren<ScrollRect>() : null;
        if (sr == null) return;

        EnsureLayoutUpdated();

        if (sr.vertical)
            sr.verticalNormalizedPosition = 0f;
        else if (sr.horizontal)
            sr.horizontalNormalizedPosition = 1f;
    }

    void LoadPalette()
    {
        if (saveSystem == null) return;

        var loaded = saveSystem.LoadPalette();

        if (loaded == null || loaded.Count == 0) return;

        isLoadingPalette = true;

        // Use bulk add for efficiency
        AddColorsBulk(loaded);

        isLoadingPalette = false;

        // ensure layout + scrollbar reflect loaded items
        RefreshContentSizeAndScroll();
    }

    // Lightweight proxy that routes clicks to the runtime controller.
    // NOTE: kept as subclass to preserve existing Setup signature expectations.
    class ColorPaletteProxy : ColorPaletteController
    {
        RuntimeColorPaletteController parent;

        public ColorPaletteProxy(RuntimeColorPaletteController p)
        {
            parent = p;
        }

        public virtual void OnColorSlotClicked(ColorSlotButton slot)
        {
            if (parent == null) return;

            if (parent.pixelCanvas != null)
                parent.pixelCanvas.SetDrawColor(slot.color);

            parent.UpdateSelectionVisual(slot.gameObject);
        }
    }

    void UpdateSelectionVisual(GameObject slotGO)
    {
        foreach (var s in instantiatedSlots)
        {
            if (s == null) continue;

            var o = s.GetComponent<Outline>();
            if (o != null) o.enabled = (s == slotGO);
        }

        if (slotGO != null)
        {
            var o = slotGO.GetComponent<Outline>();
            if (o == null)
            {
                o = slotGO.AddComponent<Outline>();
                o.effectColor = Color.yellow;
                o.effectDistance = new Vector2(2f, 2f);
            }
            o.enabled = true;
        }
    }

    // Debug helper to log state (call from Start or editor to inspect)
    void DebugLogScrollViewState()
    {
        if (instantiatedScrollView == null) { Debug.Log("[RuntimeColorPaletteController] No instantiatedScrollView"); return; }
        var sr = instantiatedScrollView.GetComponentInChildren<ScrollRect>();
        Debug.Log($"[RuntimeColorPaletteController] ScrollView active: {instantiatedScrollView.activeSelf}, scrollRect present: {(sr != null)}");
        if (contentPanel != null)
        {
            Debug.Log($"contentPanel childCount: {contentPanel.childCount}, content rect size: {contentPanel.rect.size}");
        }
        var canvas = targetCanvas;
        Debug.Log($"Canvas present: {(canvas != null)}, canvas scaleFactor: {(canvas != null ? canvas.scaleFactor.ToString() : "null")}");
    }

    void ExpandContentByRows()
    {
        if (contentPanel == null) return;

        int colorsPerRow = 6;
        int visibleRows = 7;

        GridLayoutGroup grid = contentPanel.GetComponent<GridLayoutGroup>();
        if (grid == null) return;

        float rowHeight = grid.cellSize.y + grid.spacing.y;

        int totalColors = instantiatedSlots.Count;

        int totalRows = Mathf.CeilToInt((float)totalColors / colorsPerRow);

        int rowsToShow = Mathf.Max(totalRows, visibleRows);

        float newHeight = rowsToShow * rowHeight;

        contentPanel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, newHeight);

        Canvas.ForceUpdateCanvases();
    }
}