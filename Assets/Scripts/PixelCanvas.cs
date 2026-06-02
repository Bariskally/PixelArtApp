using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

[RequireComponent(typeof(RawImage))]
public class PixelCanvas : MonoBehaviour
{
    public enum Mode { Pen, Eraser, Bucket, Move, Select, Shape, Eyedropper }
    public enum ShapeType { None, Line, Rectangle, Circle, Triangle, Star }



    private Mode previousMode = Mode.Pen;  // Şekil moduna geçmeden önceki modu saklar
    private Mode previousNonEyedropperMode = Mode.Pen;
    private Mode shapeDrawingMode = Mode.Pen; // Şekil çiziminde kullanılacak kalıcı mod
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
    private Mode _currentMode = Mode.Pen;
    public Mode currentMode
    {
        get => _currentMode;
        set
        {
            if (_currentMode != value)
            {
                _currentMode = value;
                OnModeChanged?.Invoke(_currentMode);
            }
        }
    }
    [Header("Eyedropper")]
    public Button eyedropperButton;

    [Header("History Settings")]
    public int maxHistory = 100;

    [Header("Mirror Drawing")]
    public bool mirrorX = false;
    public bool mirrorY = false;

    public void SetMirrorX(bool on) { mirrorX = on; }
    public void SetMirrorY(bool on) { mirrorY = on; }

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

    // Diğer değişkenlerin yanına (örneğin `bool dirty = false;` altına)
    public HashSet<int> userModifiedPixels = new HashSet<int>();

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

    // Shape drawing state
    ShapeType currentShape = ShapeType.None;
    bool isDrawingShape = false;
    Vector2Int shapeStartPixel, shapeCurrentPixel;

    // Event: UI can subscribe to this to refresh undo/redo buttons only when history changes
    public event Action OnHistoryChanged;

    public event Action<Mode> OnModeChanged;

    // *** YENİ EVENT: Renk değiştiğinde tetiklenir ***
    public event Action<Color32> OnDrawColorChanged;

    // Move / Pan state
    bool isPanning = false;
    Vector3 lastPanWorldPos;

    // Selection state
    bool isSelecting = false;
    Vector2Int selectionStart, selectionEnd;
    bool hasSelection = false;
    RectInt selectedRect;

    // Taşıma
    bool isMovingSelection = false;
    Vector2Int moveOffset;
    Vector2Int moveStartMousePixel;
    Dictionary<int, Color32> originalSelectionColors = new Dictionary<int, Color32>();

    // Pano
    Color32[] clipboardPixels;
    int clipboardWidth, clipboardHeight;
    bool clipboardValid = false;


    public event Action<RectInt> OnSelectionChanged;

    // Overlay for selection visualization
    RawImage overlayRawImage;
    Texture2D overlayTex;
    HashSet<int> selectedPixels = new HashSet<int>();
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



        // --- Setup overlay RawImage for selection ---
        GameObject overlayGO = new GameObject("SelectionOverlay", typeof(RectTransform), typeof(RawImage));
        overlayGO.transform.SetParent(rt, false);
        overlayRawImage = overlayGO.GetComponent<RawImage>();

        overlayTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        overlayTex.filterMode = FilterMode.Point;
        Color32[] clear = new Color32[width * height];
        for (int i = 0; i < clear.Length; i++) clear[i] = new Color32(0, 0, 0, 0);
        overlayTex.SetPixels32(clear);
        overlayTex.Apply();
        overlayRawImage.texture = overlayTex;
        overlayRawImage.raycastTarget = false;

        RectTransform overlayRT = overlayGO.GetComponent<RectTransform>();
        overlayRT.pivot = rt.pivot;
        overlayRT.anchorMin = rt.anchorMin;
        overlayRT.anchorMax = rt.anchorMax;
        overlayRT.anchoredPosition = rt.anchoredPosition;
        overlayRT.sizeDelta = rt.sizeDelta;


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

        // SCALEFACTOR FIX — 1:1 ekran piksel eşlemesi
        float scale = parentCanvas != null ? parentCanvas.scaleFactor : 1f;
        rt.sizeDelta = new Vector2(width / scale, height / scale);
        rt.pivot = new Vector2(0.5f, 0.5f);

        userModifiedPixels.Clear();
    }

    void Update()
    {
        HandleZoom();
        if (ignorePointerFrames > 0) ignorePointerFrames--;

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

        // --- Move ---
        if (currentMode == Mode.Move)
        {
            if (ignorePointerFrames > 0) return;

            Camera cam = parentCanvas != null ? parentCanvas.worldCamera : null;

            // Sol tık ile pan başlat
            if (Input.GetMouseButtonDown(0) && IsPointerOverCanvasTexture())
            {
                isPanning = true;
                RectTransformUtility.ScreenPointToWorldPointInRectangle(
                    rt, Input.mousePosition, cam, out lastPanWorldPos);
            }

            // Sürükleme sırasında canvas'ı taşı
            if (Input.GetMouseButton(0) && isPanning)
            {
                Vector3 currentWorldPos;
                RectTransformUtility.ScreenPointToWorldPointInRectangle(
                    rt, Input.mousePosition, cam, out currentWorldPos);

                Vector3 delta = currentWorldPos - lastPanWorldPos;
                rt.position += delta;
                lastPanWorldPos = currentWorldPos;

                if (enforceViewportBounds) ClampPositionToViewport_Strict();
            }

            // Tuş bırakıldığında pan bitir
            if (Input.GetMouseButtonUp(0))
            {
                isPanning = false;
            }

            return;
        }

        // --- Select ---
        if (currentMode == Mode.Select)
        {
            HandleSelectMode();

            if (hasSelection && !isMovingSelection && !isSelecting)
            {
                if (Input.GetKeyDown(KeyCode.Delete)) DeleteSelectedPixels();
            }
            if (ctrl && Input.GetKeyDown(KeyCode.C)) CopySelectedPixels();
            if (ctrl && Input.GetKeyDown(KeyCode.V) && clipboardValid) PasteClipboardAtMouse();

            if (dirty)
            {
                tex.SetPixels32(pixelBuffer);
                tex.Apply();
                dirty = false;
            }
            return;   // <-- bu return sadece Select modundayken çalışır, diğer modlara geçince atlanır
        }

        // --- Eyedropper (Pipet) ---
        if (currentMode == Mode.Eyedropper)
        {
            if (ignorePointerFrames > 0) return;

            // Sadece canvas üzerinde sol tık ile renk al
            if (Input.GetMouseButtonDown(0) && IsPointerOverCanvasTexture())
            {
                if (TryGetMousePixel(out int px, out int py))
                {
                    // Tıklanan pikselin rengini al ve drawColor yap
                    Color32 pickedColor = GetPixelColor(px, py);
                    SetDrawColor(pickedColor); // OnDrawColorChanged event’ini de tetikler

                    // Renk alındıktan sonra önceki çizim moduna geri dön
                    if (previousNonEyedropperMode == Mode.Pen ||
                        previousNonEyedropperMode == Mode.Eraser ||
                        previousNonEyedropperMode == Mode.Bucket)
                    {
                        currentMode = previousNonEyedropperMode;
                    }
                    else
                    {
                        // Eğer önceki mod Move/Select/Shape ise varsayılan olarak Pen'e dön
                        currentMode = Mode.Pen;
                    }
                }
            }
            return; // Eyedropper modunda başka işlem yapılmasın
        }

        // --- Şekil çizimi (tıkla-sürükle) ---
        if (currentMode == Mode.Shape && currentShape != ShapeType.None)
        {
            HandleShapeInput();
            if (dirty)
            {
                tex.SetPixels32(pixelBuffer);
                tex.Apply();
                dirty = false;
            }
            return; // Şekil modundayken diğer çizimleri engelle
        }

        // --- Pen / Eraser / Bucket (orijinal HandleInput) ---
        if (Input.GetMouseButtonDown(0) && ignorePointerFrames == 0)
        {
            if ((currentMode == Mode.Pen || currentMode == Mode.Eraser) && IsPointerOverCanvasTexture())
                BeginAction();
        }

        HandleInput();   // <-- kalem, silgi, kova burada çalışır

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

    void HandleShapeInput()
    {
        if (ignorePointerFrames > 0) return;

        // Mouse down: başlangıç noktasını al
        if (Input.GetMouseButtonDown(0) && IsPointerOverCanvasTexture())
        {
            if (TryGetMousePixel(out int px, out int py))
            {
                isDrawingShape = true;
                shapeStartPixel = new Vector2Int(px, py);
                shapeCurrentPixel = shapeStartPixel;
                ClearShapePreviewOverlay(); // önceki önizlemeyi temizle
            }
            return;
        }

        // Mouse held: sürükleme devam ediyor, önizlemeyi güncelle
        if (isDrawingShape && Input.GetMouseButton(0))
        {
            if (TryGetMousePixel(out int px, out int py))
            {
                if (shapeCurrentPixel.x != px || shapeCurrentPixel.y != py)
                {
                    shapeCurrentPixel = new Vector2Int(px, py);
                    DrawShapePreview();
                }
            }
            return;
        }

        // Mouse up: şekli kesin olarak çiz
        if (isDrawingShape && Input.GetMouseButtonUp(0))
        {
            isDrawingShape = false;
            // Son geçerli noktayı kullan (fare canvas dışına çıkmış olabilir)
            if (TryGetMousePixel(out int px, out int py))
                shapeCurrentPixel = new Vector2Int(px, py);

            DrawFinalShape();
            ClearShapePreviewOverlay();
            // Eski mod sadece Pen veya Eraser ise ona dön, değilse Pen moduna geç
            if (previousMode == Mode.Pen || previousMode == Mode.Eraser)
                currentMode = previousMode;
            else
                currentMode = Mode.Pen;
            currentShape = ShapeType.None;  // Sadece şekil tipini sıfırla, modu değiştirme
        }
    }

    void ClearShapePreviewOverlay()
    {
        // Seçim overlay'ini kullanarak önizleme yapacağız. 
        // DİKKAT: Bu, selection overlay'ini sıfırlar. Eğer ayrı bir overlay isterseniz ayrı texture oluşturabiliriz.
        Color32[] clear = new Color32[width * height];
        for (int i = 0; i < clear.Length; i++) clear[i] = new Color32(0, 0, 0, 0);
        overlayTex.SetPixels32(clear);
        overlayTex.Apply();
    }

    void DrawShapePreview()
    {
        ClearShapePreviewOverlay();
        int x1 = shapeStartPixel.x, y1 = shapeStartPixel.y;
        int x2 = shapeCurrentPixel.x, y2 = shapeCurrentPixel.y;

        // Önizleme rengi: Pen modunda drawColor'ın yarı saydamı, Eraser modunda arka plan renginin yarı saydamı
        Color32 previewColor;
        if (shapeDrawingMode == Mode.Pen)
            previewColor = new Color32(drawColor.r, drawColor.g, drawColor.b, 255);
        else
        {
            Color32 bg = GetBackgroundColorAt(x1, y1);
            previewColor = new Color32(bg.r, bg.g, bg.b, 255);
        }

        switch (currentShape)
        {
            case ShapeType.Line:
                DrawLineOnOverlay(x1, y1, x2, y2, previewColor);
                break;
            case ShapeType.Rectangle:
                DrawHollowRectOnOverlay(x1, y1, x2, y2, previewColor);
                break;
            case ShapeType.Circle:
                int cx = (x1 + x2) / 2;
                int cy = (y1 + y2) / 2;
                int radius = Mathf.Max(Mathf.Abs(x2 - x1), Mathf.Abs(y2 - y1)) / 2;
                if (radius < 1) radius = 1;
                DrawHollowCircleOnOverlay(cx, cy, radius, previewColor);
                break;
            case ShapeType.Triangle:
                DrawHollowTriangleOnOverlay(x1, y1, x2, y2, previewColor);
                break;
            case ShapeType.Star:
                DrawHollowStarOnOverlay(x1, y1, x2, y2, previewColor);
                break;
        }
        overlayTex.Apply();
    }


    void DrawFinalShape()
    {
        int x1 = shapeStartPixel.x, y1 = shapeStartPixel.y;
        int x2 = shapeCurrentPixel.x, y2 = shapeCurrentPixel.y;
        Color32 col = (shapeDrawingMode == Mode.Pen) ? drawColor : GetBackgroundColorAt(x1, y1);

        BeginAction();

        switch (currentShape)
        {
            case ShapeType.Line:
                DrawLineImmediate(x1, y1, x2, y2, col);
                break;
            case ShapeType.Rectangle:
                DrawHollowRectFromCorners(x1, y1, x2, y2, col);
                break;
            case ShapeType.Circle:
                DrawHollowCircleFromCorners(x1, y1, x2, y2, col);
                break;
            case ShapeType.Triangle:
                DrawHollowTriangleFromCorners(x1, y1, x2, y2, col);
                break;
            case ShapeType.Star:
                DrawHollowStarFromCorners(x1, y1, x2, y2, col);
                break;
        }

        EndAction();
        dirty = true;
    }

    // Aşağıdaki metotlar, iki köşe noktasından içi boş şekil çizer
    void DrawHollowRectFromCorners(int x1, int y1, int x2, int y2, Color32 color)
    {
        int xMin = Mathf.Min(x1, x2), xMax = Mathf.Max(x1, x2);
        int yMin = Mathf.Min(y1, y2), yMax = Mathf.Max(y1, y2);
        // Kenarlar
        for (int x = xMin; x <= xMax; x++)
        {
            if (x >= 0 && x < width)
            {
                if (yMin >= 0 && yMin < height) DrawPixelRecord(yMin * width + x, color);
                if (yMax >= 0 && yMax < height && yMax != yMin) DrawPixelRecord(yMax * width + x, color);
            }
        }
        for (int y = yMin + 1; y <= yMax - 1; y++)
        {
            if (y >= 0 && y < height)
            {
                if (xMin >= 0 && xMin < width) DrawPixelRecord(y * width + xMin, color);
                if (xMax >= 0 && xMax < width) DrawPixelRecord(y * width + xMax, color);
            }
        }
    }

    void DrawHollowCircleFromCorners(int x1, int y1, int x2, int y2, Color32 color)
    {
        int cx = (x1 + x2) / 2;
        int cy = (y1 + y2) / 2;
        int radius = Mathf.Max(Mathf.Abs(x2 - x1), Mathf.Abs(y2 - y1)) / 2;
        if (radius < 1) radius = 1;
        DrawHollowCircleImmediate(cx, cy, radius, color);
    }

    void DrawHollowTriangleFromCorners(int x1, int y1, int x2, int y2, Color32 color)
    {
        int topX = (x1 + x2) / 2;
        int topY = Mathf.Min(y1, y2);
        int bottomLeftX = Mathf.Min(x1, x2);
        int bottomRightX = Mathf.Max(x1, x2);
        int bottomY = Mathf.Max(y1, y2);

        DrawLineRecord(topX, topY, bottomLeftX, bottomY, color);
        DrawLineRecord(topX, topY, bottomRightX, bottomY, color);
        DrawLineRecord(bottomLeftX, bottomY, bottomRightX, bottomY, color);
    }

    void DrawHollowStarFromCorners(int x1, int y1, int x2, int y2, Color32 color)
    {
        int cx = (x1 + x2) / 2;
        int cy = (y1 + y2) / 2;
        int outerRadius = Mathf.Max(Mathf.Abs(x2 - x1), Mathf.Abs(y2 - y1)) / 2;
        if (outerRadius < 2) outerRadius = 2;
        int innerRadius = outerRadius / 2;
        DrawHollowStarImmediate(cx, cy, outerRadius, innerRadius, color);
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

        StampBrush(x, y, drawColor);
        if (mirrorX) StampBrush(width - 1 - x, y, drawColor);
        if (mirrorY) StampBrush(x, height - 1 - y, drawColor);
        if (mirrorX && mirrorY) StampBrush(width - 1 - x, height - 1 - y, drawColor);
    }

    void FillBackgroundPattern()
    {
        if (!showCheckerboard)
        {
            for (int i = 0; i < pixelBuffer.Length; i++)
                pixelBuffer[i] = bgColorA;
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
                        pixelBuffer[row + x] = gridLineColor;
                        continue;
                    }
                }

                pixelBuffer[row + x] = baseCol;
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
        userModifiedPixels.Clear();
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

        StampEraser(x, y);
        if (mirrorX) StampEraser(width - 1 - x, y);
        if (mirrorY) StampEraser(x, height - 1 - y);
        if (mirrorX && mirrorY) StampEraser(width - 1 - x, height - 1 - y);
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
            userModifiedPixels.Add(idx);

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

        foreach (PixelEdit e in action.edits)
        {
            pixelBuffer[e.idx] = e.prev;

            if (IsBackgroundColor(e.prev))
                userModifiedPixels.Remove(e.idx);
            else
                userModifiedPixels.Add(e.idx);
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

        foreach (PixelEdit e in action.edits)
        {
            pixelBuffer[e.idx] = e.next;

            if (IsBackgroundColor(e.next))
                userModifiedPixels.Remove(e.idx);
            else
                userModifiedPixels.Add(e.idx);
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

    // *** GÜNCELLENMİŞ SetDrawColor ***
    public void SetDrawColor(Color32 c)
    {
        if (ColorsEqual(drawColor, c)) return; // aynı renkse tetikleme
        drawColor = c;
        OnDrawColorChanged?.Invoke(drawColor);
    }

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
    /// Export pixel list as compact JSON object (array of {x,y,color}) — useful if model expects JSON.
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

    private bool IsBackgroundColor(Color32 c)
    {
        if (showCheckerboard)
        {
            if (ColorsEqual(c, bgColorA) || ColorsEqual(c, bgColorB))
                return true;
            if (showGridLines && ColorsEqual(c, gridLineColor))
                return true;
        }
        else
        {
            if (ColorsEqual(c, bgColorA))
                return true;
        }
        return false;
    }

    public void SetModeSelect()
    {
        currentMode = Mode.Select;
    }


    public void SetModeEyedropper()
    {
        // Eğer şu anki mod Eyedropper değilse, önceki modu sakla
        if (currentMode != Mode.Eyedropper)
            previousNonEyedropperMode = currentMode;

        currentMode = Mode.Eyedropper;
    }

    // Example ColorsEqual helper (kept)
    // (Also BeginAction/RecordChange/EndAction/Undo/Redo are present above and used by these methods.)


    // ------------------------------------------------------------
    // SELECTION HANDLING
    // ------------------------------------------------------------
    // ------------------------------------------------------------
    // SELECTION HANDLING
    // ------------------------------------------------------------
    void HandleSelectMode()
    {
        if (ignorePointerFrames > 0) return;

        // --- Mouse Down ---
        if (Input.GetMouseButtonDown(0) && IsPointerOverCanvasTexture())
        {
            if (TryGetMousePixel(out int px, out int py))
            {
                // Eğer seçili alan varsa ve tıklanan piksel seçili alanın içindeyse → taşıma başlat
                if (hasSelection && IsPixelInSelection(px, py))
                {
                    isMovingSelection = true;
                    moveStartMousePixel = new Vector2Int(px, py);
                    moveOffset = Vector2Int.zero;
                    StoreOriginalSelectionPixels();
                    ClearSelectionPixels(true); // true: undo kaydı yap
                    ClearOverlayPixels();
                    return;
                }
                else
                {
                    // Mevcut seçimi temizle
                    ClearSelection();
                    // Yeni bir seçim başlat
                    isSelecting = true;
                    selectionStart = new Vector2Int(px, py);
                    selectionEnd = selectionStart;
                }
            }
        }

        // --- Mouse Held (sürükleme) ---
        if (isSelecting && Input.GetMouseButton(0))
        {
            if (TryGetMousePixel(out int px, out int py))
            {
                Vector2Int current = new Vector2Int(px, py);
                if (current != selectionEnd)
                {
                    selectionEnd = current;
                    DrawOverlayRect(selectionStart, selectionEnd);
                }
            }
        }

        // --- Mouse Held (taşıma) ---
        if (isMovingSelection && Input.GetMouseButton(0))
        {
            if (TryGetMousePixel(out int px, out int py))
            {
                Vector2Int current = new Vector2Int(px, py);
                Vector2Int delta = current - moveStartMousePixel;
                if (delta != moveOffset)
                {
                    moveOffset = ClampMoveOffset(delta);
                    DrawMoveOverlay(moveOffset);
                }
            }
        }

        // --- Mouse Up ---
        if (Input.GetMouseButtonUp(0))
        {
            if (isSelecting)
            {
                isSelecting = false;
                if (selectionStart == selectionEnd)
                {
                    // Tek tıklama → mevcut seçimi temizlemişti zaten, başka işlem yok
                    hasSelection = false;
                    OnSelectionChanged?.Invoke(new RectInt(0, 0, 0, 0));
                }
                else
                {
                    // Dikdörtgen seçimi
                    int xMin = Mathf.Min(selectionStart.x, selectionEnd.x);
                    int xMax = Mathf.Max(selectionStart.x, selectionEnd.x);
                    int yMin = Mathf.Min(selectionStart.y, selectionEnd.y);
                    int yMax = Mathf.Max(selectionStart.y, selectionEnd.y);

                    selectedPixels.Clear();
                    for (int y = yMin; y <= yMax; y++)
                        for (int x = xMin; x <= xMax; x++)
                            selectedPixels.Add(y * width + x);

                    hasSelection = selectedPixels.Count > 0;
                    OnSelectionChanged?.Invoke(GetBoundingBoxFromSelectedPixels());
                }
            }
            else if (isMovingSelection)
            {
                isMovingSelection = false;
                // Taşınan pikselleri yeni konuma yerleştir
                ApplyMoveSelection(moveOffset);
                // Yeni seçili alanı güncelle
                UpdateSelectionAfterMove();
                // Overlay'ı yeni seçili alanda göster
                DrawOverlayRect(GetBoundingBoxFromSelectedPixels());
            }
        }
    }



    public void ClearSelection()
    {
        ClearOverlayPixels();               // overlay'i temizle, selectedPixels'i de temizler
        hasSelection = false;
        isSelecting = false;
        OnSelectionChanged?.Invoke(new RectInt(0, 0, 0, 0));
    }

    public IEnumerable<int> GetSelectedPixelIndices()
    {
        return selectedPixels;
    }


    public bool HasSelection => hasSelection;
    public RectInt SelectedRect => selectedRect;



    /// <summary>
    /// Overlay texture'daki belirli bir pikseli mavi (seçili) veya transparan (seçili değil) yapar.
    /// </summary>
    void SetOverlayPixel(int idx, bool selected)
    {
        Color32 col = selected ? new Color32(0, 0, 255, 180) : new Color32(0, 0, 0, 0);
        overlayTex.SetPixel(idx % width, idx / width, col);
    }

    /// <summary>
    /// Tüm overlay texture'ı transparan yapar.
    /// </summary>
    void ClearOverlayTexture()
    {
        Color32[] clear = new Color32[width * height];
        for (int i = 0; i < clear.Length; i++)
            clear[i] = new Color32(0, 0, 0, 0);
        overlayTex.SetPixels32(clear);
        overlayTex.Apply();
    }

    /// <summary>
    /// Seçili piksellerin oluşturduğu bounding rectangle'ı döndürür.
    /// </summary>
    RectInt GetBoundingBoxFromSelectedPixels()
    {
        if (selectedPixels.Count == 0) return new RectInt(0, 0, 0, 0);
        int xMin = width, yMin = height, xMax = -1, yMax = -1;
        foreach (int idx in selectedPixels)
        {
            int x = idx % width;
            int y = idx / width;
            if (x < xMin) xMin = x;
            if (x > xMax) xMax = x;
            if (y < yMin) yMin = y;
            if (y > yMax) yMax = y;
        }
        return new RectInt(xMin, yMin, xMax - xMin + 1, yMax - yMin + 1);
    }

    /// <summary>
    /// Seçili piksellerin indislerini döndürür (zaten var olan GetSelectedPixelIndices ile aynı).
    /// </summary>

    /// <summary>
    /// Overlay texture'ı tamamen saydam yapar (seçimi temizler).
    /// </summary>
    void ClearOverlayPixels()
    {
        Color32[] clear = new Color32[width * height];
        for (int i = 0; i < clear.Length; i++) clear[i] = new Color32(0, 0, 0, 0);
        overlayTex.SetPixels32(clear);
        overlayTex.Apply();
        selectedPixels.Clear();
    }

    /// <summary>
    /// Overlay üzerinde iki nokta arasındaki dikdörtgeni mavi ile doldurur.
    /// </summary>
    void DrawOverlayRect(Vector2Int from, Vector2Int to)
    {
        int xMin = Mathf.Clamp(Mathf.Min(from.x, to.x), 0, width - 1);
        int xMax = Mathf.Clamp(Mathf.Max(from.x, to.x), 0, width - 1);
        int yMin = Mathf.Clamp(Mathf.Min(from.y, to.y), 0, height - 1);
        int yMax = Mathf.Clamp(Mathf.Max(from.y, to.y), 0, height - 1);

        // Önce overlay'i temizle
        Color32[] clear = new Color32[width * height];
        for (int i = 0; i < clear.Length; i++) clear[i] = new Color32(0, 0, 0, 0);

        // Dikdörtgen alanı mavi yap
        Color32 blue = new Color32(0, 0, 255, 180);
        for (int y = yMin; y <= yMax; y++)
        {
            int row = y * width;
            for (int x = xMin; x <= xMax; x++)
            {
                clear[row + x] = blue;
            }
        }

        overlayTex.SetPixels32(clear);
        overlayTex.Apply();
    }

    bool IsPixelInSelection(int x, int y)
    {
        RectInt r = GetBoundingBoxFromSelectedPixels();
        return x >= r.xMin && x <= r.xMax && y >= r.yMin && y <= r.yMax;
    }

    void StoreOriginalSelectionPixels()
    {
        originalSelectionColors.Clear();
        foreach (int idx in selectedPixels)
        {
            int x = idx % width;
            int y = idx / width;
            if (!IsBackgroundAt(x, y))              // <-- arka plan değilse
                originalSelectionColors[idx] = pixelBuffer[idx];
        }
    }

    void ClearSelectionPixels(bool recordUndo)
    {
        if (recordUndo) BeginAction();
        foreach (int idx in originalSelectionColors.Keys)   // sadece saklananlar
        {
            Color32 bg = GetBackgroundColorAt(idx % width, idx / width);
            if (!ColorsEqual(pixelBuffer[idx], bg))
            {
                if (recordUndo) RecordChange(idx, pixelBuffer[idx], bg);
                pixelBuffer[idx] = bg;
            }
        }
        if (recordUndo) EndAction();
        dirty = true;
    }

    Vector2Int ClampMoveOffset(Vector2Int offset)
    {
        RectInt bounds = GetBoundingBoxFromOriginalSelection();
        int minX = bounds.xMin + offset.x;
        int maxX = bounds.xMax + offset.x;
        int minY = bounds.yMin + offset.y;
        int maxY = bounds.yMax + offset.y;

        if (minX < 0) offset.x -= minX;
        if (maxX >= width) offset.x -= (maxX - width + 1);
        if (minY < 0) offset.y -= minY;
        if (maxY >= height) offset.y -= (maxY - height + 1);

        return offset;
    }

    RectInt GetBoundingBoxFromOriginalSelection()
    {
        if (originalSelectionColors.Count == 0) return GetBoundingBoxFromSelectedPixels();
        int xMin = width, yMin = height, xMax = -1, yMax = -1;
        foreach (int idx in originalSelectionColors.Keys)
        {
            int x = idx % width;
            int y = idx / width;
            if (x < xMin) xMin = x;
            if (x > xMax) xMax = x;
            if (y < yMin) yMin = y;
            if (y > yMax) yMax = y;
        }
        return new RectInt(xMin, yMin, xMax - xMin + 1, yMax - yMin + 1);
    }

    void DrawMoveOverlay(Vector2Int offset)
    {
        // Overlay'da, orijinal seçim alanını offset kadar kaydırarak göster
        ClearOverlayPixels(); // overlay temizle
        if (originalSelectionColors.Count == 0) return;

        // Yeni overlay için geçici bir dizi oluştur (tekrar overlayTex.Apply yapılacak)
        foreach (var kv in originalSelectionColors)
        {
            int srcIdx = kv.Key;
            Color32 col = kv.Value;
            int srcX = srcIdx % width;
            int srcY = srcIdx / width;
            int dstX = srcX + offset.x;
            int dstY = srcY + offset.y;
            if (dstX >= 0 && dstX < width && dstY >= 0 && dstY < height)
            {
                int dstIdx = dstY * width + dstX;
                // Yarı saydam olarak göster
                Color32 overlayCol = new Color32(col.r, col.g, col.b, 120);
                overlayTex.SetPixel(dstX, dstY, overlayCol);
            }
        }
        overlayTex.Apply();
    }

    void ApplyMoveSelection(Vector2Int offset)
    {
        BeginAction();
        // Yeni pikselleri yaz
        foreach (var kv in originalSelectionColors)
        {
            int srcIdx = kv.Key;
            Color32 col = kv.Value;
            int srcX = srcIdx % width;
            int srcY = srcIdx / width;
            int dstX = srcX + offset.x;
            int dstY = srcY + offset.y;
            if (dstX >= 0 && dstX < width && dstY >= 0 && dstY < height)
            {
                int dstIdx = dstY * width + dstX;
                if (!ColorsEqual(pixelBuffer[dstIdx], col))
                {
                    RecordChange(dstIdx, pixelBuffer[dstIdx], col);
                    pixelBuffer[dstIdx] = col;
                }
            }
        }
        EndAction();
        dirty = true;
    }

    void UpdateSelectionAfterMove()
    {
        // selectedPixels'i yeni konuma göre güncelle
        selectedPixels.Clear();
        foreach (var kv in originalSelectionColors)
        {
            int srcIdx = kv.Key;
            int srcX = srcIdx % width;
            int srcY = srcIdx / width;
            int dstX = srcX + moveOffset.x;
            int dstY = srcY + moveOffset.y;
            if (dstX >= 0 && dstX < width && dstY >= 0 && dstY < height)
            {
                selectedPixels.Add(dstY * width + dstX);
            }
        }
        hasSelection = selectedPixels.Count > 0;
        OnSelectionChanged?.Invoke(GetBoundingBoxFromSelectedPixels());
    }


    // ---- KOPYALA / YAPIŞTIR ----
    void CopySelectedPixels()
    {
        if (!hasSelection) return;
        RectInt rect = GetBoundingBoxFromSelectedPixels();
        clipboardWidth = rect.width;
        clipboardHeight = rect.height;
        clipboardPixels = new Color32[clipboardWidth * clipboardHeight];
        for (int y = 0; y < clipboardHeight; y++)
        {
            for (int x = 0; x < clipboardWidth; x++)
            {
                int srcX = rect.xMin + x;
                int srcY = rect.yMin + y;
                Color32 srcCol = pixelBuffer[srcY * width + srcX];
                if (IsBackgroundAt(srcX, srcY))
                {
                    // Arka plan pikseli → tamamen saydam yap
                    clipboardPixels[y * clipboardWidth + x] = new Color32(0, 0, 0, 0);
                }
                else
                {
                    // Kullanıcı çizimi → alfa 255 ile sakla
                    clipboardPixels[y * clipboardWidth + x] = new Color32(srcCol.r, srcCol.g, srcCol.b, 255);
                }
            }
        }
        clipboardValid = true;
    }

    void PasteClipboardAtMouse()
    {
        if (!clipboardValid) return;
        if (!TryGetMousePixel(out int px, out int py)) return;

        int startX = px;
        int startY = py;

        BeginAction();
        for (int y = 0; y < clipboardHeight; y++)
        {
            for (int x = 0; x < clipboardWidth; x++)
            {
                int dstX = startX + x;
                int dstY = startY + y;
                if (dstX < 0 || dstX >= width || dstY < 0 || dstY >= height) continue;

                Color32 srcCol = clipboardPixels[y * clipboardWidth + x];
                // Saydam (alfa 0) pikselleri atla
                if (srcCol.a == 0) continue;

                int dstIdx = dstY * width + dstX;
                Color32 oldColor = pixelBuffer[dstIdx];
                if (!ColorsEqual(oldColor, srcCol))
                {
                    RecordChange(dstIdx, oldColor, srcCol);
                    pixelBuffer[dstIdx] = srcCol;
                }
            }
        }
        EndAction();
        dirty = true;

        // Yeni yapıştırılan alanı seçili hale getir
        selectedPixels.Clear();
        for (int y = 0; y < clipboardHeight; y++)
        {
            for (int x = 0; x < clipboardWidth; x++)
            {
                int px2 = startX + x;
                int py2 = startY + y;
                if (px2 >= 0 && px2 < width && py2 >= 0 && py2 < height)
                    selectedPixels.Add(py2 * width + px2);
            }
        }
        hasSelection = selectedPixels.Count > 0;
        DrawOverlayRect(GetBoundingBoxFromSelectedPixels());
        OnSelectionChanged?.Invoke(GetBoundingBoxFromSelectedPixels());
    }

    // ---- DELETE ----
    void DeleteSelectedPixels()
    {
        if (!hasSelection) return;
        BeginAction();
        foreach (int idx in selectedPixels)
        {
            int x = idx % width;
            int y = idx / width;
            Color32 bg = GetBackgroundColorAt(x, y);
            if (!ColorsEqual(pixelBuffer[idx], bg))
            {
                RecordChange(idx, pixelBuffer[idx], bg);
                pixelBuffer[idx] = bg;
            }
        }
        EndAction();
        dirty = true;
        ClearOverlayPixels();
        selectedPixels.Clear();
        hasSelection = false;
        OnSelectionChanged?.Invoke(new RectInt(0, 0, 0, 0));
    }

    void DrawOverlayRect(RectInt rect)
    {
        DrawOverlayRect(new Vector2Int(rect.xMin, rect.yMin), new Vector2Int(rect.xMax, rect.yMax));
    }

    // YENİ YARDIMCI METOTLAR (sınıfın içine, DrawAt'ın hemen üstüne ekleyebilirsin)
    void StampBrush(int cx, int cy, Color32 color)
    {
        int half = (brushSize - 1) / 2;
        int startX = cx - half;
        int startY = cy - half;
        int w = width;
        for (int yy = 0; yy < brushSize; yy++)
        {
            int py = startY + yy;
            if (py < 0 || py >= height) continue;
            int row = py * w;
            for (int xx = 0; xx < brushSize; xx++)
            {
                int px = startX + xx;
                if (px < 0 || px >= width) continue;
                int idx = row + px;
                Color32 prev = pixelBuffer[idx];
                Color32 next = color;
                if (!ColorsEqual(prev, next))
                {
                    pixelBuffer[idx] = next;
                    RecordChange(idx, prev, next);
                    dirty = true;
                }
                userModifiedPixels.Add(idx);
            }
        }
    }

    void StampEraser(int cx, int cy)
    {
        int half = (brushSize - 1) / 2;
        int startX = cx - half;
        int startY = cy - half;
        int w = width;
        for (int yy = 0; yy < brushSize; yy++)
        {
            int py = startY + yy;
            if (py < 0 || py >= height) continue;
            int row = py * w;
            for (int xx = 0; xx < brushSize; xx++)
            {
                int px = startX + xx;
                if (px < 0 || px >= width) continue;
                int idx = row + px;
                Color32 prev = pixelBuffer[idx];
                Color32 next = GetBackgroundColorAt(px, py);
                if (!ColorsEqual(prev, next))
                {
                    pixelBuffer[idx] = next;
                    RecordChange(idx, prev, next);
                    userModifiedPixels.Remove(idx);
                    dirty = true;
                }
            }
        }
    }

    // ==================== İÇİ BOŞ ŞEKİL ÇİZME METODLARI ====================

    /// <summary>İçi boş kare (sadece kenar)</summary>
    public void DrawHollowRectImmediate(int centerX, int centerY, int halfSize, Color32 color)
    {
        int xMin = centerX - halfSize;
        int xMax = centerX + halfSize;
        int yMin = centerY - halfSize;
        int yMax = centerY + halfSize;

        BeginAction();
        // Üst ve alt kenar
        for (int x = xMin; x <= xMax; x++)
        {
            if (x >= 0 && x < width)
            {
                if (yMin >= 0 && yMin < height)
                    DrawPixelRecord(yMin * width + x, color);
                if (yMax >= 0 && yMax < height && yMax != yMin)
                    DrawPixelRecord(yMax * width + x, color);
            }
        }
        // Sol ve sağ kenar (köşeler hariç)
        for (int y = yMin + 1; y <= yMax - 1; y++)
        {
            if (y >= 0 && y < height)
            {
                if (xMin >= 0 && xMin < width)
                    DrawPixelRecord(y * width + xMin, color);
                if (xMax >= 0 && xMax < width)
                    DrawPixelRecord(y * width + xMax, color);
            }
        }
        EndAction();
        dirty = true;
    }

    /// <summary>İçi boş daire (sadece kenar) - Bresenham algoritması</summary>
    public void DrawHollowCircleImmediate(int cx, int cy, int radius, Color32 color)
    {
        if (radius <= 0) return;
        BeginAction();
        int x = radius, y = 0;
        int err = 0;
        while (x >= y)
        {
            DrawPixelRecord(GetIndex(cx + x, cy + y), color);
            DrawPixelRecord(GetIndex(cx - x, cy + y), color);
            DrawPixelRecord(GetIndex(cx + x, cy - y), color);
            DrawPixelRecord(GetIndex(cx - x, cy - y), color);
            DrawPixelRecord(GetIndex(cx + y, cy + x), color);
            DrawPixelRecord(GetIndex(cx - y, cy + x), color);
            DrawPixelRecord(GetIndex(cx + y, cy - x), color);
            DrawPixelRecord(GetIndex(cx - y, cy - x), color);

            y++;
            err += 1 + 2 * y;
            if (2 * (err - x) + 1 > 0) { x--; err += 1 - 2 * x; }
        }
        EndAction();
        dirty = true;
    }

    /// <summary>İçi boş üçgen (eşkenar, tepe yukarı)</summary>
    public void DrawHollowTriangleImmediate(int centerX, int centerY, int size, Color32 color)
    {
        int halfSize = size / 2;
        int topX = centerX, topY = centerY - halfSize;
        int bottomLeftX = centerX - halfSize, bottomY = centerY + halfSize;
        int bottomRightX = centerX + halfSize;

        BeginAction();
        DrawLineRecord(topX, topY, bottomLeftX, bottomY, color);
        DrawLineRecord(topX, topY, bottomRightX, bottomY, color);
        DrawLineRecord(bottomLeftX, bottomY, bottomRightX, bottomY, color);
        EndAction();
        dirty = true;
    }

    /// <summary>İçi boş 5 köşeli yıldız</summary>
    public void DrawHollowStarImmediate(int centerX, int centerY, int outerRadius, int innerRadius, Color32 color)
    {
        List<Vector2Int> points = new List<Vector2Int>();
        for (int i = 0; i < 10; i++)
        {
            float angle = i * 36 * Mathf.Deg2Rad;
            int r = (i % 2 == 0) ? outerRadius : innerRadius;
            int x = centerX + Mathf.RoundToInt(r * Mathf.Sin(angle));
            int y = centerY + Mathf.RoundToInt(r * Mathf.Cos(angle));
            points.Add(new Vector2Int(x, y));
        }
        BeginAction();
        for (int i = 0; i < points.Count; i++)
        {
            Vector2Int p1 = points[i];
            Vector2Int p2 = points[(i + 1) % points.Count];
            DrawLineRecord(p1.x, p1.y, p2.x, p2.y, color);
        }
        EndAction();
        dirty = true;
    }

    // ==================== YARDIMCI METODLAR ====================

    private int GetIndex(int x, int y)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return -1;
        return y * width + x;
    }

    private void DrawPixelRecord(int idx, Color32 color)
    {
        if (idx < 0 || idx >= pixelBuffer.Length) return;
        Color32 prev = pixelBuffer[idx];
        if (!ColorsEqual(prev, color))
        {
            pixelBuffer[idx] = color;
            RecordChange(idx, prev, color);
            dirty = true;
        }
    }

    private void DrawLineRecord(int x0, int y0, int x1, int y1, Color32 color)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int x = x0, y = y0;
        while (true)
        {
            int idx = GetIndex(x, y);
            if (idx != -1) DrawPixelRecord(idx, color);
            if (x == x1 && y == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }

    // ==================== BUTONLARIN ÇAĞIRACAĞI PUBLIC METODLAR ====================

    /// <summary>Çizgi çizer (Pen modunda drawColor, Eraser modunda arka plan rengi)</summary>
    public void DrawShapeLine()
    {
        if (!TryGetMousePixel(out int x, out int y)) { x = width / 2; y = height / 2; }
        int endX = Mathf.Min(x + 30, width - 1);
        Color32 col = (currentMode == Mode.Pen) ? drawColor : GetBackgroundColorAt(x, y);

        if (currentMode == Mode.Pen)
            DrawLineImmediate(x, y, endX, y, col);
        else
        {
            BeginAction();
            for (int xi = x; xi <= endX; xi++)
                DrawPixelRecord(y * width + xi, GetBackgroundColorAt(xi, y));
            EndAction();
            dirty = true;
        }
    }

    /// <summary>İçi boş kare çizer</summary>
    public void DrawShapeSquare()
    {
        if (!TryGetMousePixel(out int x, out int y)) { x = width / 2; y = height / 2; }
        Color32 col = (currentMode == Mode.Pen) ? drawColor : GetBackgroundColorAt(x, y);
        DrawHollowRectImmediate(x, y, 20, col);
    }

    /// <summary>İçi boş daire çizer</summary>
    public void DrawShapeCircle()
    {
        if (!TryGetMousePixel(out int x, out int y)) { x = width / 2; y = height / 2; }
        Color32 col = (currentMode == Mode.Pen) ? drawColor : GetBackgroundColorAt(x, y);
        DrawHollowCircleImmediate(x, y, 20, col);
    }

    /// <summary>İçi boş üçgen çizer</summary>
    public void DrawShapeTriangle()
    {
        if (!TryGetMousePixel(out int x, out int y)) { x = width / 2; y = height / 2; }
        Color32 col = (currentMode == Mode.Pen) ? drawColor : GetBackgroundColorAt(x, y);
        DrawHollowTriangleImmediate(x, y, 40, col);
    }

    /// <summary>İçi boş yıldız çizer</summary>
    public void DrawShapeStar()
    {
        if (!TryGetMousePixel(out int x, out int y)) { x = width / 2; y = height / 2; }
        Color32 col = (currentMode == Mode.Pen) ? drawColor : GetBackgroundColorAt(x, y);
        DrawHollowStarImmediate(x, y, 25, 12, col);
    }

    // ==================== ŞEKİL MODUNU BAŞLATAN METOTLAR (BUTONLAR BUNLARI ÇAĞIRACAK) ====================
    public void StartShapeLine()
    {
        // Sadece Pen veya Eraser modunda çizim modunu güncelle
        if (currentMode == Mode.Pen || currentMode == Mode.Eraser)
        {
            shapeDrawingMode = currentMode;
            previousMode = currentMode;
        }
        // Diğer modlarda (Select, Move, Bucket) shapeDrawingMode ve previousMode değişmez
        // (en son kullanılan Pen/Eraser değeri kalır)
        currentMode = Mode.Shape;
        currentShape = ShapeType.Line;
        IgnorePointerForOneFrame();
    }

    public void StartShapeRect()
    {
        if (currentMode == Mode.Pen || currentMode == Mode.Eraser)
        {
            shapeDrawingMode = currentMode;
            previousMode = currentMode;
        }
        currentMode = Mode.Shape;
        currentShape = ShapeType.Rectangle;
        IgnorePointerForOneFrame();
    }
    public void StartShapeCircle()
    {
        if (currentMode == Mode.Pen || currentMode == Mode.Eraser)
        {
            shapeDrawingMode = currentMode;
            previousMode = currentMode;
        }
        currentMode = Mode.Shape;
        currentShape = ShapeType.Circle;
        IgnorePointerForOneFrame();
    }
    public void StartShapeTriangle()
    {
        if (currentMode == Mode.Pen || currentMode == Mode.Eraser)
        {
            shapeDrawingMode = currentMode;
            previousMode = currentMode;
        }
        currentMode = Mode.Shape;
        currentShape = ShapeType.Triangle;
        IgnorePointerForOneFrame();
    }
    public void StartShapeStar()
    {
        if (currentMode == Mode.Pen || currentMode == Mode.Eraser)
        {
            shapeDrawingMode = currentMode;
            previousMode = currentMode;
        }
        currentMode = Mode.Shape;
        currentShape = ShapeType.Star;
        IgnorePointerForOneFrame();
    }


    // ==================== OVERLAY ŞEKİL ÇİZİM METOTLARI (ÖNİZLEME İÇİN) ====================

    void DrawHollowRectOnOverlay(int x1, int y1, int x2, int y2, Color32 color)
    {
        int xMin = Mathf.Min(x1, x2), xMax = Mathf.Max(x1, x2);
        int yMin = Mathf.Min(y1, y2), yMax = Mathf.Max(y1, y2);
        for (int x = xMin; x <= xMax; x++)
        {
            if (x >= 0 && x < width)
            {
                if (yMin >= 0 && yMin < height) overlayTex.SetPixel(x, yMin, color);
                if (yMax >= 0 && yMax < height && yMax != yMin) overlayTex.SetPixel(x, yMax, color);
            }
        }
        for (int y = yMin + 1; y <= yMax - 1; y++)
        {
            if (y >= 0 && y < height)
            {
                if (xMin >= 0 && xMin < width) overlayTex.SetPixel(xMin, y, color);
                if (xMax >= 0 && xMax < width) overlayTex.SetPixel(xMax, y, color);
            }
        }
    }

    void DrawHollowCircleOnOverlay(int cx, int cy, int radius, Color32 color)
    {
        if (radius <= 0) return;
        int x = radius, y = 0;
        int err = 0;
        while (x >= y)
        {
            SetOverlayPixelSafe(cx + x, cy + y, color);
            SetOverlayPixelSafe(cx - x, cy + y, color);
            SetOverlayPixelSafe(cx + x, cy - y, color);
            SetOverlayPixelSafe(cx - x, cy - y, color);
            SetOverlayPixelSafe(cx + y, cy + x, color);
            SetOverlayPixelSafe(cx - y, cy + x, color);
            SetOverlayPixelSafe(cx + y, cy - x, color);
            SetOverlayPixelSafe(cx - y, cy - x, color);
            y++;
            err += 1 + 2 * y;
            if (2 * (err - x) + 1 > 0) { x--; err += 1 - 2 * x; }
        }
    }

    void DrawHollowTriangleOnOverlay(int x1, int y1, int x2, int y2, Color32 color)
    {
        int topX = (x1 + x2) / 2;
        int topY = Mathf.Min(y1, y2);
        int bottomLeftX = Mathf.Min(x1, x2);
        int bottomRightX = Mathf.Max(x1, x2);
        int bottomY = Mathf.Max(y1, y2);
        DrawLineOnOverlay(topX, topY, bottomLeftX, bottomY, color);
        DrawLineOnOverlay(topX, topY, bottomRightX, bottomY, color);
        DrawLineOnOverlay(bottomLeftX, bottomY, bottomRightX, bottomY, color);
    }

    void DrawHollowStarOnOverlay(int x1, int y1, int x2, int y2, Color32 color)
    {
        int cx = (x1 + x2) / 2;
        int cy = (y1 + y2) / 2;
        int outerRadius = Mathf.Max(Mathf.Abs(x2 - x1), Mathf.Abs(y2 - y1)) / 2;
        if (outerRadius < 2) outerRadius = 2;
        int innerRadius = outerRadius / 2;
        List<Vector2Int> points = new List<Vector2Int>();
        for (int i = 0; i < 10; i++)
        {
            float angle = i * 36 * Mathf.Deg2Rad;
            int r = (i % 2 == 0) ? outerRadius : innerRadius;
            int x = cx + Mathf.RoundToInt(r * Mathf.Sin(angle));
            int y = cy + Mathf.RoundToInt(r * Mathf.Cos(angle));
            points.Add(new Vector2Int(x, y));
        }
        for (int i = 0; i < points.Count; i++)
        {
            Vector2Int p1 = points[i];
            Vector2Int p2 = points[(i + 1) % points.Count];
            DrawLineOnOverlay(p1.x, p1.y, p2.x, p2.y, color);
        }
    }

    void DrawLineOnOverlay(int x0, int y0, int x1, int y1, Color32 color)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int x = x0, y = y0;
        while (true)
        {
            SetOverlayPixelSafe(x, y, color);
            if (x == x1 && y == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }

    void SetOverlayPixelSafe(int x, int y, Color32 color)
    {
        if (x >= 0 && x < width && y >= 0 && y < height)
            overlayTex.SetPixel(x, y, color);
    }
    // ---- end of file ----

}