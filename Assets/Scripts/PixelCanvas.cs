using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

[RequireComponent(typeof(RawImage))]
public class PixelCanvas : MonoBehaviour
{
    public enum Mode { Pen, Eraser, Bucket, Move }

    [Header("Canvas size (pixels)")]
    public int width = 1024;
    public int height = 1024;

    [Header("Zoom Settings")]
    public float zoomSpeed = 1f;
    public float minZoom = 0.5f;
    public float maxZoom = 40f;

    float currentZoom = 1f;

    [Header("Background / Checkerboard")]
    public bool showCheckerboard = true;
    public int tileSize = 32;
    public Color32 bgColorA = new Color32(255, 255, 255, 255);
    public Color32 bgColorB = new Color32(200, 200, 200, 255);

    [Header("Optional grid lines between tiles")]
    public bool showGridLines = false;
    public int gridLineWidth = 1;
    public Color32 gridLineColor = new Color32(160, 160, 160, 255);

    [Header("Drawing")]
    public Color32 drawColor = new Color32(0, 0, 0, 255);
    public int brushSize = 1;

    [Header("Runtime")]
    public Mode currentMode = Mode.Pen;

    [Header("History Settings")]
    public int maxHistory = 100;

    [Header("Viewport Clamping")]
    [Tooltip("RectTransform of the visible panel (the mask/viewport containing the canvas). If left null, parent RectTransform is used.")]
    public RectTransform viewport;
    [Tooltip("Padding (in world units) to keep between canvas edges and viewport edges.")]
    public float viewportPadding = 8f;
    [Tooltip("If true, canvas position will be clamped to remain visible within the viewport after pan/zoom.")]
    public bool enforceViewportBounds = true;

    // Internal graphic buffer
    Texture2D tex;
    RawImage rawImage;
    public Color32[] pixelBuffer; // public for debugging / AI controller read access
    bool dirty = false;

    RectTransform rt;
    Canvas parentCanvas;
    GraphicRaycaster canvasRaycaster;

    // Undo/Redo structures
    class PixelEdit { public int idx; public Color32 prev; public Color32 next; }
    class EditAction { public List<PixelEdit> edits = new List<PixelEdit>(); }
    List<EditAction> undoStack = new List<EditAction>();
    List<EditAction> redoStack = new List<EditAction>();
    EditAction currentAction = null;
    HashSet<int> currentActionSet = null;

    // Prevent clicks that originate from UI buttons (to avoid "click button then accidentally draw" bug)
    int ignorePointerFrames = 0;

    // Event: UI can subscribe to this to refresh undo/redo buttons only when history changes
    public event Action OnHistoryChanged;

    // Move / Pan state
    bool isPanning = false;
    Vector3 lastPanWorldPos;

    void Start()
    {
        rawImage = GetComponent<RawImage>();
        rt = rawImage.rectTransform;
        parentCanvas = rawImage.canvas;
        if (parentCanvas != null)
        {
            canvasRaycaster = parentCanvas.GetComponent<GraphicRaycaster>();
            if (canvasRaycaster == null)
                canvasRaycaster = parentCanvas.gameObject.AddComponent<GraphicRaycaster>();
        }

        CreateTexture();

        // ensure no UI element stays selected on start (prevents "pressed" highlight)
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }

    void CreateTexture()
    {
        if (width <= 0) width = 1;
        if (height <= 0) height = 1;
        if (tileSize <= 0) tileSize = 1;

        tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;

        pixelBuffer = new Color32[width * height];

        FillBackgroundPattern();

        tex.SetPixels32(pixelBuffer);
        tex.Apply();

        rawImage.texture = tex;

        // SCALEFACTOR FIX ù 1:1 ekran piksel eùlemesi
        float scale = parentCanvas != null ? parentCanvas.scaleFactor : 1f;
        rt.sizeDelta = new Vector2(width / scale, height / scale);
        rt.pivot = new Vector2(0.5f, 0.5f);
    }

    void Update()
    {
        HandleZoom();

        // decrement ignore pointer frames if set
        if (ignorePointerFrames > 0) ignorePointerFrames--;

        // Klavye kùsayollarù (Ctrl/Cmd+Z, Ctrl/Cmd+Y)
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)
                    || Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);

        if (ctrl && Input.GetKeyDown(KeyCode.Z))
        {
            if (currentAction != null) EndAction();
            Undo();
            ClearSelectedUINextFrame();
        }

        if (ctrl && Input.GetKeyDown(KeyCode.Y))
        {
            if (currentAction != null) EndAction();
            Redo();
            ClearSelectedUINextFrame();
        }

        // --- Move (pan) baùlangùcù ---
        if (currentMode == Mode.Move)
        {
            // Baùlangùù: mouse down ise ve pointer canvas'ùn ùzerinde ise panning baùlat
            if (Input.GetMouseButtonDown(0) && ignorePointerFrames == 0 && IsPointerOverCanvasTexture())
            {
                Camera cam = parentCanvas != null ? parentCanvas.worldCamera : null;
                Vector3 worldPoint;
                if (RectTransformUtility.ScreenPointToWorldPointInRectangle(rt, Input.mousePosition, cam, out worldPoint))
                {
                    isPanning = true;
                    lastPanWorldPos = worldPoint;
                    ClearSelectedUINextFrame();
                }
            }

            // Panning devamù: mouse held
            if (isPanning && Input.GetMouseButton(0))
            {
                Camera cam = parentCanvas != null ? parentCanvas.worldCamera : null;
                Vector3 currentWorld;
                if (RectTransformUtility.ScreenPointToWorldPointInRectangle(rt, Input.mousePosition, cam, out currentWorld))
                {
                    Vector3 delta = currentWorld - lastPanWorldPos;
                    rt.position += delta;
                    lastPanWorldPos = currentWorld;

                    if (enforceViewportBounds) ClampPositionToViewport_Strict();
                }
            }

            // Panning bitiùi
            if (isPanning && Input.GetMouseButtonUp(0))
            {
                isPanning = false;
            }

            // while in Move mode, don't process drawing input
            return;
        }
        // --- Move (pan) sonu ---

        // BeginAction only if mouse down is actually on the canvas texture (topmost element)
        if (Input.GetMouseButtonDown(0))
        {
            if (ignorePointerFrames == 0)
            {
                if ((currentMode == Mode.Pen || currentMode == Mode.Eraser) && IsPointerOverCanvasTexture())
                {
                    BeginAction();
                }
            }
        }

        HandleInput();

        if (Input.GetMouseButtonUp(0))
        {
            if (currentMode == Mode.Pen || currentMode == Mode.Eraser)
                EndAction();
        }

        if (dirty)
        {
            tex.SetPixels32(pixelBuffer);
            tex.Apply();
            dirty = false;
        }
    }

    // Public: tool controller calls this when it wants to prevent the immediate next pointer from drawing
    public void IgnorePointerForOneFrame()
    {
        ignorePointerFrames = 1;
    }

    void NotifyHistoryChanged()
    {
        OnHistoryChanged?.Invoke();
    }

    bool IsPointerOverCanvasTexture()
    {
        if (EventSystem.current == null || canvasRaycaster == null) return false;

        PointerEventData ped = new PointerEventData(EventSystem.current);
        ped.position = Input.mousePosition;
        List<RaycastResult> results = new List<RaycastResult>();
        canvasRaycaster.Raycast(ped, results);
        if (results == null || results.Count == 0) return false;

        RaycastResult top = results[0];

        if (top.gameObject == rawImage.gameObject) return true;
        if (top.gameObject.transform.IsChildOf(rawImage.transform)) return true;

        return false;
    }

    void HandleZoom()
    {
        float scroll = Input.mouseScrollDelta.y;
        if (scroll == 0f) return;

        Camera cam = parentCanvas != null ? parentCanvas.worldCamera : null;
        Vector3 mousePos = Input.mousePosition;

        Vector3 beforeZoomPos;
        RectTransformUtility.ScreenPointToWorldPointInRectangle(rt, mousePos, cam, out beforeZoomPos);

        float baseFactor = 1.2f;
        float factor = Mathf.Pow(baseFactor, scroll * zoomSpeed);

        float desiredZoom = currentZoom * factor;
        desiredZoom = Mathf.Clamp(desiredZoom, minZoom, maxZoom);

        currentZoom = desiredZoom;
        rt.localScale = Vector3.one * currentZoom;

        Vector3 afterZoomPos;
        RectTransformUtility.ScreenPointToWorldPointInRectangle(rt, mousePos, cam, out afterZoomPos);

        Vector3 offset = beforeZoomPos - afterZoomPos;
        rt.position += offset;

        if (enforceViewportBounds) ClampPositionToViewport_Strict();
    }

    void HandleInput()
    {
        if (currentMode == Mode.Bucket)
        {
            if (ignorePointerFrames > 0) return;

            if (Input.GetMouseButtonDown(0) && IsPointerOverCanvasTexture())
            {
                if (TryGetMousePixel(out int ix, out int iy))
                {
                    BeginAction();
                    FloodFill(ix, iy, drawColor);
                    EndAction();
                }
            }
            return;
        }

        if (!Input.GetMouseButton(0)) return;
        if (!IsPointerOverCanvasTexture()) return;
        if (!TryGetMousePixel(out int x, out int y)) return;

        if (currentMode == Mode.Pen) DrawAt(x, y);
        else if (currentMode == Mode.Eraser) EraseAt(x, y);
    }

    bool TryGetMousePixel(out int px, out int py)
    {
        px = py = 0;
        Vector2 local;
        Camera cam = parentCanvas != null ? parentCanvas.worldCamera : null;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, Input.mousePosition, cam, out local))
            return false;

        float nx = (local.x / rt.rect.width) + 0.5f;
        float ny = (local.y / rt.rect.height) + 0.5f;

        int ix = Mathf.FloorToInt(nx * width);
        int iy = Mathf.FloorToInt(ny * height);

        if (ix < 0 || ix >= width || iy < 0 || iy >= height) return false;

        px = ix; py = iy;
        return true;
    }

    void BeginAction()
    {
        if (currentAction != null) return;
        currentAction = new EditAction();
        currentActionSet = new HashSet<int>();
        if (redoStack.Count > 0)
        {
            redoStack.Clear();
            NotifyHistoryChanged();
        }
    }

    void RecordChange(int idx, Color32 prev, Color32 next)
    {
        if (currentAction == null) return;
        if (currentActionSet.Contains(idx)) return;
        currentActionSet.Add(idx);
        currentAction.edits.Add(new PixelEdit { idx = idx, prev = prev, next = next });
    }

    void EndAction()
    {
        if (currentAction == null) return;
        if (currentAction.edits.Count > 0)
        {
            undoStack.Add(currentAction);
            if (undoStack.Count > maxHistory)
                undoStack.RemoveAt(0);
            NotifyHistoryChanged();
        }
        currentAction = null;
        currentActionSet = null;
    }

    void DrawAt(int x, int y)
    {
        if (currentAction == null) BeginAction();

        int half = brushSize / 2;
        int w = width;
        for (int yy = y - half; yy <= y + half; yy++)
        {
            if (yy < 0 || yy >= height) continue;
            int row = yy * w;
            for (int xx = x - half; xx <= x + half; xx++)
            {
                if (xx < 0 || xx >= width) continue;
                int idx = row + xx;
                Color32 prev = pixelBuffer[idx];
                Color32 next = drawColor;
                if (!ColorsEqual(prev, next))
                {
                    pixelBuffer[idx] = next;
                    RecordChange(idx, prev, next);
                    dirty = true;
                }
            }
        }
    }

    void FillBackgroundPattern()
    {
        FillBackgroundInto(pixelBuffer);
    }

    /// <summary>
    /// Arka plan desenini (damal? / dùz / ?zgara ùizgisi) verilen tam boyutlu diziye yazar.
    /// </summary>
    public void FillBackgroundInto(Color32[] buffer)
    {
        if (buffer == null || buffer.Length != width * height) return;

        if (!showCheckerboard)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = bgColorA;
            return;
        }

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int tileY = y / tileSize;
            for (int x = 0; x < width; x++)
            {
                int tileX = x / tileSize;
                bool isA = ((tileX + tileY) % 2 == 0);
                Color32 baseCol = isA ? bgColorA : bgColorB;

                if (showGridLines)
                {
                    int modX = x % tileSize;
                    int modY = y % tileSize;
                    if (modX < gridLineWidth || modY < gridLineWidth)
                    {
                        buffer[row + x] = gridLineColor;
                        continue;
                    }
                }

                buffer[row + x] = baseCol;
            }
        }
    }

    public void Clear()
    {
        BeginAction();
        for (int i = 0; i < pixelBuffer.Length; i++)
        {
            Color32 prev = pixelBuffer[i];
            Color32 next = GetBackgroundColorAt(i % width, i / width);
            if (!ColorsEqual(prev, next))
            {
                pixelBuffer[i] = next;
                RecordChange(i, prev, next);
            }
        }
        EndAction();
        dirty = true;
    }

    public Color32 GetBackgroundColorAt(int x, int y)
    {
        if (!showCheckerboard) return bgColorA;
        int tileX = x / tileSize;
        int tileY = y / tileSize;
        bool isA = ((tileX + tileY) % 2 == 0);
        Color32 baseCol = isA ? bgColorA : bgColorB;
        if (showGridLines)
        {
            int modX = x % tileSize;
            int modY = y % tileSize;
            if (modX < gridLineWidth || modY < gridLineWidth) return gridLineColor;
        }
        return baseCol;
    }

    void EraseAt(int x, int y)
    {
        if (currentAction == null) BeginAction();

        int half = brushSize / 2;
        int w = width;
        for (int yy = y - half; yy <= y + half; yy++)
        {
            if (yy < 0 || yy >= height) continue;
            int row = yy * w;
            for (int xx = x - half; xx <= x + half; xx++)
            {
                if (xx < 0 || xx >= width) continue;
                int idx = row + xx;
                Color32 prev = pixelBuffer[idx];
                Color32 next = GetBackgroundColorAt(xx, yy);
                if (!ColorsEqual(prev, next))
                {
                    pixelBuffer[idx] = next;
                    RecordChange(idx, prev, next);
                    dirty = true;
                }
            }
        }
    }

    public void FloodFill(int startX, int startY, Color32 newColor)
    {
        int w = width;
        int h = height;
        int startIdx = startY * w + startX;
        Color32 targetColor = pixelBuffer[startIdx];

        if (ColorsEqual(targetColor, newColor)) return;

        bool targetIsBackground = false;
        if (showCheckerboard)
        {
            if (ColorsEqual(targetColor, bgColorA) || ColorsEqual(targetColor, bgColorB))
                targetIsBackground = true;
        }

        Stack<int> stack = new Stack<int>();
        stack.Push(startIdx);

        while (stack.Count > 0)
        {
            int idx = stack.Pop();

            Color32 current = pixelBuffer[idx];

            bool match;
            if (targetIsBackground)
            {
                match = (ColorsEqual(current, bgColorA) || ColorsEqual(current, bgColorB));
                if (!match && showGridLines && ColorsEqual(current, gridLineColor))
                    match = true;
            }
            else
            {
                match = ColorsEqual(current, targetColor);
            }

            if (!match) continue;

            Color32 prev = current;
            pixelBuffer[idx] = newColor;
            RecordChange(idx, prev, newColor);

            int x = idx % w;
            int y = idx / w;

            if (x > 0) stack.Push(idx - 1);
            if (x < w - 1) stack.Push(idx + 1);
            if (y > 0) stack.Push(idx - w);
            if (y < h - 1) stack.Push(idx + w);
        }

        dirty = true;
    }

    bool ColorsEqual(Color32 a, Color32 b)
    {
        return a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
    }

    // ---- Undo / Redo ----

    public bool CanUndo() => undoStack.Count > 0;
    public bool CanRedo() => redoStack.Count > 0;

    public void Undo()
    {
        if (!CanUndo()) return;

        if (currentAction != null) EndAction();

        int lastIndex = undoStack.Count - 1;
        EditAction action = undoStack[lastIndex];
        undoStack.RemoveAt(lastIndex);

        for (int i = 0; i < action.edits.Count; i++)
        {
            PixelEdit e = action.edits[i];
            pixelBuffer[e.idx] = e.prev;
        }

        redoStack.Add(action);
        NotifyHistoryChanged();
        dirty = true;
    }

    public void Redo()
    {
        if (!CanRedo()) return;

        if (currentAction != null) EndAction();

        int lastIndex = redoStack.Count - 1;
        EditAction action = redoStack[lastIndex];
        redoStack.RemoveAt(lastIndex);

        for (int i = 0; i < action.edits.Count; i++)
        {
            PixelEdit e = action.edits[i];
            pixelBuffer[e.idx] = e.next;
        }

        undoStack.Add(action);
        NotifyHistoryChanged();
        dirty = true;
    }

    // ---- Public API for UI tools ----
    public void SetModePen() { currentMode = Mode.Pen; }
    public void SetModeEraser() { currentMode = Mode.Eraser; }
    public void SetModeBucket() { currentMode = Mode.Bucket; }
    public void SetModeMove() { currentMode = Mode.Move; }

    public void SetBrushSize(int newSize) { brushSize = Mathf.Max(1, newSize); }
    public void SetDrawColor(Color32 c) { drawColor = c; }
    public Mode GetMode() => currentMode;

    public void FillAll(Color32 color)
    {
        BeginAction();
        for (int i = 0; i < pixelBuffer.Length; i++)
        {
            Color32 prev = pixelBuffer[i];
            if (!ColorsEqual(prev, color))
            {
                pixelBuffer[i] = color;
                RecordChange(i, prev, color);
            }
        }
        EndAction();
        dirty = true;
    }

    public void ClearSelectedUINextFrame()
    {
        StartCoroutine(_ClearNextFrame());
    }

    public void ClearSelectedUIImmediate()
    {
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
    }

    System.Collections.IEnumerator _ClearNextFrame()
    {
        yield return null;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
    }

    // --- Viewport clamping helpers (STRICT: no canvas edge leaves viewport) ---
    RectTransform GetEffectiveViewport()
    {
        if (viewport != null) return viewport;
        if (rt != null && rt.parent != null) return rt.parent as RectTransform;
        return null;
    }

    void ClampPositionToViewport_Strict()
    {
        RectTransform vp = GetEffectiveViewport();
        if (vp == null) return;

        // Get world corners
        Vector3[] canvasCorners = new Vector3[4];
        Vector3[] viewportCorners = new Vector3[4];
        rt.GetWorldCorners(canvasCorners);
        vp.GetWorldCorners(viewportCorners);

        // Convenience values
        Vector3 cMin = canvasCorners[0], cMax = canvasCorners[2];
        Vector3 vMin = viewportCorners[0], vMax = viewportCorners[2];

        float pad = viewportPadding;

        // world sizes
        float canvasW = cMax.x - cMin.x;
        float canvasH = cMax.y - cMin.y;
        float viewportW = vMax.x - vMin.x;
        float viewportH = vMax.y - vMin.y;

        Vector3 shift = Vector3.zero;

        // X axis - strict containment when smaller, coverage when larger
        if (canvasW <= viewportW)
        {
            // keep canvas fully inside viewport
            if (cMin.x < vMin.x + pad) shift.x = (vMin.x + pad) - cMin.x;
            if (cMax.x > vMax.x - pad) shift.x = (vMax.x - pad) - cMax.x;
        }
        else
        {
            // canvas larger: ensure it still covers viewport (no empty space)
            if (cMin.x > vMin.x + pad) shift.x = (vMin.x + pad) - cMin.x;    // moved too far right
            if (cMax.x < vMax.x - pad) shift.x = (vMax.x - pad) - cMax.x;    // moved too far left
        }

        // Y axis
        if (canvasH <= viewportH)
        {
            if (cMin.y < vMin.y + pad) shift.y = (vMin.y + pad) - cMin.y;
            if (cMax.y > vMax.y - pad) shift.y = (vMax.y - pad) - cMax.y;
        }
        else
        {
            if (cMin.y > vMin.y + pad) shift.y = (vMin.y + pad) - cMin.y;    // moved too far down
            if (cMax.y < vMax.y - pad) shift.y = (vMax.y - pad) - cMax.y;    // moved too far up
        }

        if (shift != Vector3.zero)
        {
            rt.position += shift;
        }
    }

    // Aliasing old name if other code calls previous ClampPositionToViewport
    void ClampPositionToViewport()
    {
        ClampPositionToViewport_Strict();
    }

    // ------------------------
    // Programmatic drawing API (Immediate)
    // ------------------------

    /// <summary>
    /// Set a single pixel immediately (records undo).
    /// </summary>
    public void DrawPixelImmediate(int x, int y, Color32 color)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        int idx = y * width + x;
        Color32 prev = pixelBuffer[idx];
        if (ColorsEqual(prev, color)) return;

        BeginAction();
        pixelBuffer[idx] = color;
        RecordChange(idx, prev, color);
        EndAction();
        dirty = true;
    }

    /// <summary>
    /// Draw a Bresenham line between two points (records undo as one action).
    /// </summary>
    public void DrawLineImmediate(int x0, int y0, int x1, int y1, Color32 color)
    {
        BeginAction();
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int x = x0, y = y0;
        while (true)
        {
            if (x >= 0 && x < width && y >= 0 && y < height)
            {
                int idx = y * width + x;
                Color32 prev = pixelBuffer[idx];
                if (!ColorsEqual(prev, color))
                {
                    pixelBuffer[idx] = color;
                    RecordChange(idx, prev, color);
                }
            }
            if (x == x1 && y == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
        EndAction();
        dirty = true;
    }

    /// <summary>
    /// Draw filled rect (x, y) top-left with width/height.
    /// </summary>
    public void DrawRectImmediate(int x, int y, int w, int h, Color32 color)
    {
        BeginAction();
        for (int yy = y; yy < y + h; yy++)
        {
            if (yy < 0 || yy >= height) continue;
            int row = yy * width;
            for (int xx = x; xx < x + w; xx++)
            {
                if (xx < 0 || xx >= width) continue;
                int idx = row + xx;
                Color32 prev = pixelBuffer[idx];
                if (!ColorsEqual(prev, color))
                {
                    pixelBuffer[idx] = color;
                    RecordChange(idx, prev, color);
                }
            }
        }
        EndAction();
        dirty = true;
    }

    /// <summary>
    /// Draw a filled circle (midpoint approximation).
    /// </summary>
    public void DrawCircleImmediate(int cx, int cy, int radius, Color32 color)
    {
        if (radius <= 0) return;
        BeginAction();
        int x = radius, y = 0;
        int err = 0;
        while (x >= y)
        {
            // draw horizontal spans between symmetric points
            DrawHorizontalSpan(cx - x, cx + x, cy + y, color);
            DrawHorizontalSpan(cx - x, cx + x, cy - y, color);
            DrawHorizontalSpan(cx - y, cx + y, cy + x, color);
            DrawHorizontalSpan(cx - y, cx + y, cy - x, color);

            y += 1;
            err += 1 + 2 * y;
            if (2 * (err - x) + 1 > 0) { x -= 1; err += 1 - 2 * x; }
        }
        EndAction();
        dirty = true;
    }

    void DrawHorizontalSpan(int x0, int x1, int y, Color32 color)
    {
        if (y < 0 || y >= height) return;
        int sx = Math.Max(0, x0);
        int ex = Math.Min(width - 1, x1);
        int row = y * width;
        for (int x = sx; x <= ex; x++)
        {
            int idx = row + x;
            Color32 prev = pixelBuffer[idx];
            if (!ColorsEqual(prev, color))
            {
                pixelBuffer[idx] = color;
                RecordChange(idx, prev, color);
            }
        }
    }

    /// <summary>
    /// Flood fill at x,y with color (wraps existing FloodFill but records as one action).
    /// </summary>
    public void FloodFillAt(int x, int y, Color32 color)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        BeginAction();
        FloodFill(x, y, color);
        EndAction();
        // FloodFill already sets dirty = true
    }

    // -------------------------
    // Export + AI helpers
    // -------------------------

    /// <summary>
    /// Return current color of pixel (x,y).
    /// </summary>
    public Color32 GetPixelColor(int x, int y)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return new Color32(0, 0, 0, 0);
        return pixelBuffer[y * width + x];
    }

    /// <summary>
    /// Is the pixel at (x,y) considered background (checkerboard or bgA/bgB or gridline)?
    /// </summary>
    public bool IsBackgroundAt(int x, int y)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return true;
        string hex = ColorToHex(pixelBuffer[y * width + x]);
        string a = ColorToHex(bgColorA);
        if (!showCheckerboard)
        {
            return hex == a;
        }
        string b = ColorToHex(bgColorB);
        string g = ColorToHex(gridLineColor);
        if (hex == a || hex == b) return true;
        if (showGridLines && hex == g) return true;
        return false;
    }

    /// <summary>
    /// Convert a Color32 to uppercase #RRGGBB string.
    /// </summary>
    public string ColorToHex(Color32 c)
    {
        return $"#{c.r.ToString("X2")}{c.g.ToString("X2")}{c.b.ToString("X2")}";
    }

    /// <summary>
    /// Export every pixel as explicit lines: "PIXEL x y #RRGGBB"
    /// Options:
    /// - includeBackground: if false, background-colored pixels are skipped.
    /// - useCropIfPossible: if true and there is a non-background bbox, only that bbox is exported (saves tokens).
    /// - maxPixels: safety cap; if exceeded, method returns partial output with NOTE.
    /// Output begins with header lines:
    /// FULLPIXELS W H
    /// PALETTE #RRGGBB,...
    /// optionally: CROP xMin yMin w h
    /// then multiple lines "PIXEL x y #RRGGBB"
    /// </summary>
    public string ExportFullPixelList(bool includeBackground = false, bool useCropIfPossible = true, int maxPixels = 8192)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"FULLPIXELS {width} {height}");
        sb.AppendLine(ExportPaletteLine(256));

        int xMin = 0, yMin = 0, xMax = width - 1, yMax = height - 1;
        if (useCropIfPossible)
        {
            if (GetNonBackgroundBoundingBox(out int bx0, out int by0, out int bx1, out int by1))
            {
                xMin = bx0; yMin = by0; xMax = bx1; yMax = by1;
                sb.AppendLine($"CROP {xMin} {yMin} {xMax - xMin + 1} {yMax - yMin + 1}");
            }
        }

        int emitted = 0;
        for (int y = yMin; y <= yMax; y++)
        {
            int row = y * width;
            for (int x = xMin; x <= xMax; x++)
            {
                string hex = ColorToHex(pixelBuffer[row + x]);
                bool isBg = IsBackgroundAt(x, y);
                if (!includeBackground && isBg) continue;

                sb.AppendLine($"PIXEL {x} {y} {hex}");
                emitted++;
                if (emitted >= maxPixels)
                {
                    sb.AppendLine($"NOTE: pixel list truncated at maxPixels={maxPixels}");
                    return sb.ToString();
                }
            }
        }

        if (emitted == 0)
        {
            sb.AppendLine("NOTE: no non-background pixels exported.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Export pixel list as compact JSON object (array of {x,y,color}) ù useful if model expects JSON.
    /// </summary>
    public string ExportFullPixelListAsJson(bool includeBackground = false, bool useCropIfPossible = true, int maxPixels = 8192)
    {
        int xMin = 0, yMin = 0, xMax = width - 1, yMax = height - 1;
        if (useCropIfPossible)
        {
            if (GetNonBackgroundBoundingBox(out int bx0, out int by0, out int bx1, out int by1))
            {
                xMin = bx0; yMin = by0; xMax = bx1; yMax = by1;
            }
        }

        var entries = new List<string>();
        int emitted = 0;
        for (int y = yMin; y <= yMax; y++)
        {
            int row = y * width;
            for (int x = xMin; x <= xMax; x++)
            {
                bool isBg = IsBackgroundAt(x, y);
                if (!includeBackground && isBg) continue;
                string hex = ColorToHex(pixelBuffer[row + x]);
                entries.Add($"{{\"x\":{x},\"y\":{y},\"c\":\"{hex}\"}}");
                emitted++;
                if (emitted >= maxPixels) break;
            }
            if (emitted >= maxPixels) break;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("{");
        sb.AppendFormat("\"canvas\":{{\"w\":{0},\"h\":{1}}},", width, height);
        sb.Append("\"pixels\":[");
        sb.Append(string.Join(",", entries));
        sb.Append("]");
        if (emitted >= maxPixels) sb.AppendFormat(",\"note\":\"truncated at maxPixels={0}\"", maxPixels);
        sb.Append("}");
        return sb.ToString();
    }

    /// <summary>
    /// Respect-existing variants: these draw only into pixels that are background OR already equal to the color.
    /// Useful to prevent accidental overwrites from AI commands.
    /// </summary>
    public void DrawPixelRespectExisting(int x, int y, Color32 color)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        int idx = y * width + x;
        Color32 prev = pixelBuffer[idx];
        if (!ColorsEqual(prev, color) && !IsBackgroundAt(x, y)) return; // skip if target is non-background and not same color
        BeginAction();
        pixelBuffer[idx] = color;
        RecordChange(idx, prev, color);
        EndAction();
        dirty = true;
    }

    public void DrawRectRespectExisting(int x, int y, int w, int h, Color32 color)
    {
        BeginAction();
        for (int yy = y; yy < y + h; yy++)
        {
            if (yy < 0 || yy >= height) continue;
            int row = yy * width;
            for (int xx = x; xx < x + w; xx++)
            {
                if (xx < 0 || xx >= width) continue;
                int idx = row + xx;
                Color32 prev = pixelBuffer[idx];
                if (!ColorsEqual(prev, color) && !IsBackgroundAt(xx, yy)) continue;
                pixelBuffer[idx] = color;
                RecordChange(idx, prev, color);
            }
        }
        EndAction();
        dirty = true;
    }

    public void DrawLineRespectExisting(int x0, int y0, int x1, int y1, Color32 color)
    {
        BeginAction();
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int x = x0, y = y0;
        while (true)
        {
            if (x >= 0 && x < width && y >= 0 && y < height)
            {
                int idx = y * width + x;
                Color32 prev = pixelBuffer[idx];
                if (ColorsEqual(prev, color) || IsBackgroundAt(x, y))
                {
                    if (!ColorsEqual(prev, color))
                    {
                        pixelBuffer[idx] = color;
                        RecordChange(idx, prev, color);
                    }
                }
            }
            if (x == x1 && y == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
        EndAction();
        dirty = true;
    }

    public void DrawCircleRespectExisting(int cx, int cy, int radius, Color32 color)
    {
        if (radius <= 0) return;
        BeginAction();
        int x = radius, y = 0;
        int err = 0;
        while (x >= y)
        {
            DrawHorizontalSpanRespectExisting(cx - x, cx + x, cy + y, color);
            DrawHorizontalSpanRespectExisting(cx - x, cx + x, cy - y, color);
            DrawHorizontalSpanRespectExisting(cx - y, cx + y, cy + x, color);
            DrawHorizontalSpanRespectExisting(cx - y, cx + y, cy - x, color);

            y += 1;
            err += 1 + 2 * y;
            if (2 * (err - x) + 1 > 0) { x -= 1; err += 1 - 2 * x; }
        }
        EndAction();
        dirty = true;
    }

    void DrawHorizontalSpanRespectExisting(int x0, int x1, int y, Color32 color)
    {
        if (y < 0 || y >= height) return;
        int sx = Math.Max(0, x0);
        int ex = Math.Min(width - 1, x1);
        int row = y * width;
        for (int x = sx; x <= ex; x++)
        {
            int idx = row + x;
            Color32 prev = pixelBuffer[idx];
            if (!ColorsEqual(prev, color) && !IsBackgroundAt(x, y)) continue;
            pixelBuffer[idx] = color;
            RecordChange(idx, prev, color);
        }
    }

    /// <summary>
    /// FloodFill but respect existing: will only replace background-colored pixels during fill.
    /// If the target color isn't background, this method no-ops to avoid overwriting.
    /// </summary>
    public void FloodFillRespectExisting(int startX, int startY, Color32 newColor)
    {
        if (startX < 0 || startX >= width || startY < 0 || startY >= height) return;
        int w = width;
        int startIdx = startY * w + startX;
        Color32 target = pixelBuffer[startIdx];

        // Only allow flood fill if target is background (otherwise we don't overwrite)
        if (!IsBackgroundAt(startX, startY)) return;
        if (ColorsEqual(target, newColor)) return;

        Stack<int> stack = new Stack<int>();
        stack.Push(startIdx);

        while (stack.Count > 0)
        {
            int idx = stack.Pop();
            Color32 current = pixelBuffer[idx];

            if (!IsBackgroundAt(idx % w, idx / w)) continue; // we only replace background
            Color32 prev = current;
            pixelBuffer[idx] = newColor;
            RecordChange(idx, prev, newColor);

            int x = idx % w;
            int y = idx / w;
            if (x > 0) stack.Push(idx - 1);
            if (x < w - 1) stack.Push(idx + 1);
            if (y > 0) stack.Push(idx - w);
            if (y < height - 1) stack.Push(idx + w);
        }

        dirty = true;
    }

    // -------------------------
    // Export and compact helpers (preserved)
    // -------------------------

    public string ExportPaletteLine(int maxColors = 64)
    {
        var set = new HashSet<string>();
        for (int i = 0; i < pixelBuffer.Length; i++)
        {
            var h = ColorToHex(pixelBuffer[i]);
            if (!set.Contains(h))
            {
                set.Add(h);
                if (set.Count >= maxColors) break;
            }
        }
        return "PALETTE " + string.Join(",", set);
    }

    public string ExportStateRLE(bool includeAllRows = false, int maxRuns = 2000)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"CANVAS {width} {height}");
        sb.AppendLine(ExportPaletteLine());

        // Determine what counts as background:
        bool useChecker = showCheckerboard;
        string bgA = ColorToHex(bgColorA);
        string bgB = ColorToHex(bgColorB);

        int runsEmitted = 0;

        for (int y = 0; y < height; y++)
        {
            int rowIndex = y * width;
            int x = 0;
            var rowRuns = new System.Text.StringBuilder();
            bool rowHasRuns = false;

            while (x < width)
            {
                // current color
                string curHex = ColorToHex(pixelBuffer[rowIndex + x]);
                int start = x;
                x++;
                while (x < width && ColorToHex(pixelBuffer[rowIndex + x]) == curHex) x++;
                int end = x - 1;

                // if skipping background and this run is background, skip
                bool isBg = false;
                if (useChecker)
                {
                    if (curHex == bgA || curHex == bgB) isBg = true;
                    if (showGridLines && curHex == ColorToHex(gridLineColor)) isBg = true;
                }
                else
                {
                    if (curHex == bgA) isBg = true;
                }

                if (includeAllRows || !isBg)
                {
                    if (rowRuns.Length > 0) rowRuns.Append(",");
                    rowRuns.Append($"{curHex} {start}-{end}");
                    rowHasRuns = true;
                    runsEmitted++;
                    if (runsEmitted >= maxRuns)
                    {
                        sb.AppendLine($"ROW {y}: " + rowRuns.ToString());
                        sb.AppendLine($"NOTE: output truncated at maxRuns={maxRuns}");
                        return sb.ToString();
                    }
                }
            } // end row scan

            if (rowHasRuns)
            {
                sb.AppendLine($"ROW {y}: " + rowRuns.ToString());
            }
        } // end rows

        return sb.ToString();
    }

    public string ExportCroppedRLE(int xMin, int yMin, int xMax, int yMax, int maxRuns = 2000)
    {
        xMin = Mathf.Clamp(xMin, 0, width - 1);
        yMin = Mathf.Clamp(yMin, 0, height - 1);
        xMax = Mathf.Clamp(xMax, 0, width - 1);
        yMax = Mathf.Clamp(yMax, 0, height - 1);
        if (xMax < xMin || yMax < yMin) return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"CANVAS {width} {height}");
        sb.AppendLine(ExportPaletteLine());

        int runs = 0;
        for (int y = yMin; y <= yMax; y++)
        {
            int row = y * width;
            var rowRuns = new List<string>();
            int x = xMin;
            while (x <= xMax)
            {
                string curHex = ColorToHex(pixelBuffer[row + x]);
                int start = x;
                x++;
                while (x <= xMax && ColorToHex(pixelBuffer[row + x]) == curHex) x++;
                int end = x - 1;

                // Skip background runs if desired (we'll skip background by default)
                bool isBg = false;
                if (showCheckerboard)
                {
                    if (curHex == ColorToHex(bgColorA) || curHex == ColorToHex(bgColorB)) isBg = true;
                    if (showGridLines && curHex == ColorToHex(gridLineColor)) isBg = true;
                }
                else
                {
                    if (curHex == ColorToHex(bgColorA)) isBg = true;
                }

                if (!isBg)
                {
                    rowRuns.Add($"{curHex} {start}-{end}");
                    runs++;
                    if (runs >= maxRuns) break;
                }
            }
            if (rowRuns.Count > 0)
                sb.AppendLine($"ROW {y}: " + string.Join(",", rowRuns));
            if (runs >= maxRuns) break;
        }
        if (runs >= maxRuns) sb.AppendLine($"NOTE: cropped RLE truncated at maxRuns={maxRuns}");
        return sb.ToString();
    }

    public bool GetNonBackgroundBoundingBox(out int xMin, out int yMin, out int xMax, out int yMax)
    {
        xMin = width; yMin = height; xMax = -1; yMax = -1;
        bool useChecker = showCheckerboard;
        string hexBgA = ColorToHex(bgColorA);
        string hexBgB = ColorToHex(bgColorB);

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                string h = ColorToHex(pixelBuffer[row + x]);
                bool isBg = (useChecker && (h == hexBgA || h == hexBgB)) || (!useChecker && h == hexBgA);
                if (!isBg)
                {
                    if (x < xMin) xMin = x;
                    if (x > xMax) xMax = x;
                    if (y < yMin) yMin = y;
                    if (y > yMax) yMax = y;
                }
            }
        }

        if (xMax < 0)
        {
            // no non-background pixels
            return false;
        }
        return true;
    }


    // Linear interpolate between two colors, produce (steps) colors including endpoints.
    public List<Color32> GenerateIntermediateShades(Color32 a, Color32 b, int steps)
    {
        var outList = new List<Color32>();
        if (steps < 2) { outList.Add(a); outList.Add(b); return outList; }
        for (int i = 0; i < steps; i++)
        {
            float t = (float)i / (steps - 1);
            byte r = (byte)Mathf.Round(Mathf.Lerp(a.r, b.r, t));
            byte g = (byte)Mathf.Round(Mathf.Lerp(a.g, b.g, t));
            byte bb = (byte)Mathf.Round(Mathf.Lerp(a.b, b.b, t));
            outList.Add(new Color32(r, g, bb, 255));
        }
        return outList;
    }

    // Apply simple ordered dither into the non-background bounding box using the supplied shades.
    // Only writes into pixels that are considered background (so we avoid overwriting user art).
    public void ApplyOrderedDitherToNonBackgroundBoundingBox(List<Color32> shades, int tile = 2)
    {
        if (shades == null || shades.Count == 0) return;
        if (!GetNonBackgroundBoundingBox(out int xMin, out int yMin, out int xMax, out int yMax))
        {
            // If canvas empty, optional: apply dither to center area
            int cw = width, ch = height;
            xMin = Mathf.Max(0, cw / 2 - 16); yMin = Mathf.Max(0, ch / 2 - 16);
            xMax = Mathf.Min(cw - 1, cw / 2 + 16); yMax = Mathf.Min(ch - 1, ch / 2 + 16);
        }

        BeginAction();
        int w = width;
        for (int y = yMin; y <= yMax; y++)
        {
            int row = y * w;
            for (int x = xMin; x <= xMax; x++)
            {
                // only write into background pixels
                if (!IsBackgroundAt(x, y)) continue;

                // simple ordered dither pattern based on coordinates and tile size
                int px = (x / tile);
                int py = (y / tile);
                int idx = (px + py) % shades.Count;
                Color32 shade = shades[idx];

                int bufIdx = row + x;
                Color32 prev = pixelBuffer[bufIdx];
                if (!ColorsEqual(prev, shade))
                {
                    pixelBuffer[bufIdx] = shade;
                    RecordChange(bufIdx, prev, shade);
                }
            }
        }
        EndAction();
        dirty = true;
    }

    // Small helper to parse #RRGGBB strings (if you want here as convenience)
    public bool TryParseHexToColor32(string hex, out Color32 color)
    {
        color = new Color32(0, 0, 0, 255);
        if (string.IsNullOrEmpty(hex)) return false;
        string s = hex.Trim().Replace("\"", "").Replace("'", "");
        if (!s.StartsWith("#")) s = "#" + s;
        if (s.Length != 7) return false;
        try
        {
            byte r = Convert.ToByte(s.Substring(1, 2), 16);
            byte g = Convert.ToByte(s.Substring(3, 2), 16);
            byte b = Convert.ToByte(s.Substring(5, 2), 16);
            color = new Color32(r, g, b, 255);
            return true;
        }
        catch { return false; }
    }
    // Example ColorsEqual helper (kept)
    // (Also BeginAction/RecordChange/EndAction/Undo/Redo are present above and used by these methods.)

    // ---- Proje / katman API ----

    /// <summary>Boyut veya arka plan ayar? de?i?ince dokuyu ba?tan olu?turur; geùmi?i temizler.</summary>
    public void RebuildTexture()
    {
        CreateTexture();
        ClearHistory();
    }

    /// <summary>Geri al / yinele y???nlar?n? temizler.</summary>
    public void ClearHistory()
    {
        undoStack.Clear();
        redoStack.Clear();
        currentAction = null;
        currentActionSet = null;
        NotifyHistoryChanged();
    }

    /// <summary>Mevcut tuval piksellerinin kopyas?n? dùndùrùr.</summary>
    public Color32[] ClonePixelBuffer()
    {
        if (pixelBuffer == null) return null;
        var c = new Color32[pixelBuffer.Length];
        Array.Copy(pixelBuffer, c, pixelBuffer.Length);
        return c;
    }

    /// <summary>Sadece arka plan desenine gùre dolu bir tampon (yeni katman iùin).</summary>
    public Color32[] CreateBackgroundBufferCopy()
    {
        var buf = new Color32[width * height];
        FillBackgroundInto(buf);
        return buf;
    }

    /// <summary>Piksel tamponunu de?i?tirir ve dokuyu gùnceller.</summary>
    public void ReplacePixelsFrom(Color32[] src)
    {
        if (src == null || pixelBuffer == null || src.Length != pixelBuffer.Length)
        {
            Debug.LogWarning("[PixelCanvas] ReplacePixelsFrom: boyut uyu?muyor.");
            return;
        }

        Array.Copy(src, pixelBuffer, src.Length);
        tex.SetPixels32(pixelBuffer);
        tex.Apply();
        dirty = false;
        ClearHistory();
    }

    // ---- end of file ----
}
