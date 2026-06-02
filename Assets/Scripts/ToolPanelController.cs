using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System;
using System.Collections.Generic;

public class ToolPanelController : MonoBehaviour
{
    [Header("Buttons (tool buttons only)")]
    public Button penButton;
    public Button eraserButton;
    public Button bucketButton;
    public Button moveButton;
    public Button selectButton;

    [Header("Shape Buttons")]
    public Button lineButton;
    public Button rectButton;
    public Button circleButton;
    public Button triangleButton;
    public Button starButton;

    [Header("Undo/Redo Buttons")]
    public Button undoButton;
    public Button redoButton;

    [Header("References")]
    public PixelCanvas pixelCanvas;

    [Header("Visuals")]
    public Color selectedColor = new Color(0.2f, 0.6f, 1f, 1f);
    public Color normalColor = new Color(0.8f, 0.8f, 0.8f, 1f); // Varsayılan gri (isteğe bağlı, aslında orijinal renkler kullanılacak)

    // Orijinal buton renklerini sakla
    private Dictionary<Button, Color> originalColors = new Dictionary<Button, Color>();

    void Start()
    {
        if (pixelCanvas == null)
            Debug.LogWarning("ToolPanelController: pixelCanvas not assigned.");

        // Tüm butonların başlangıç renklerini kaydet
        SaveOriginalColor(penButton);
        SaveOriginalColor(eraserButton);
        SaveOriginalColor(bucketButton);
        SaveOriginalColor(moveButton);
        SaveOriginalColor(selectButton);
        SaveOriginalColor(lineButton);
        SaveOriginalColor(rectButton);
        SaveOriginalColor(circleButton);
        SaveOriginalColor(triangleButton);
        SaveOriginalColor(starButton);

        // Başlangıçta pen butonunu seçili göster (çünkü SetModePen() çağrılacak)
        UpdateSelectionVisuals(penButton);
        if (pixelCanvas != null) pixelCanvas.SetModePen();
        if (pixelCanvas != null) pixelCanvas.OnModeChanged += OnPixelCanvasModeChanged;
        if (pixelCanvas != null) pixelCanvas.OnHistoryChanged += UpdateUndoRedoInteractable;
        UpdateUndoRedoInteractable();
        if (pixelCanvas != null) pixelCanvas.ClearSelectedUIImmediate();
    }

    void OnDestroy()
    {
        if (pixelCanvas != null)
        {
            pixelCanvas.OnHistoryChanged -= UpdateUndoRedoInteractable;
            pixelCanvas.OnModeChanged -= OnPixelCanvasModeChanged;   // <--- EKLENEK SATIR
        }
    }

    void SaveOriginalColor(Button btn)
    {
        if (btn != null && btn.image != null)
            originalColors[btn] = btn.image.color;
    }

    public void OnPenPressed()
    {
        if (pixelCanvas != null)
        {
            pixelCanvas.SetModePen();
            pixelCanvas.IgnorePointerForOneFrame();
            pixelCanvas.ClearSelectedUINextFrame();
        }
        UpdateSelectionVisuals(penButton);
    }

    public void OnEraserPressed()
    {
        if (pixelCanvas != null)
        {
            pixelCanvas.SetModeEraser();
            pixelCanvas.IgnorePointerForOneFrame();
            pixelCanvas.ClearSelectedUINextFrame();
        }
        UpdateSelectionVisuals(eraserButton);
    }

    public void OnBucketPressed()
    {
        if (pixelCanvas != null)
        {
            pixelCanvas.SetModeBucket();
            pixelCanvas.IgnorePointerForOneFrame();
            pixelCanvas.ClearSelectedUINextFrame();
        }
        UpdateSelectionVisuals(bucketButton);
    }

    public void OnMovePressed()
    {
        if (pixelCanvas != null)
        {
            pixelCanvas.SetModeMove();
            pixelCanvas.IgnorePointerForOneFrame();
            pixelCanvas.ClearSelectedUINextFrame();
        }
        UpdateSelectionVisuals(moveButton);
    }

    public void OnSelectPressed()
    {
        if (pixelCanvas != null)
        {
            pixelCanvas.SetModeSelect();
            pixelCanvas.ClearSelectedUINextFrame();
        }
        UpdateSelectionVisuals(selectButton);
        if (EventSystem.current != null && selectButton != null)
            EventSystem.current.SetSelectedGameObject(selectButton.gameObject);
    }

    void UpdateSelectionVisuals(Button selected)
    {
        SetButtonColor(penButton, selected);
        SetButtonColor(eraserButton, selected);
        SetButtonColor(bucketButton, selected);
        SetButtonColor(moveButton, selected);
        SetButtonColor(selectButton, selected);
        SetButtonColor(lineButton, selected);
        SetButtonColor(rectButton, selected);
        SetButtonColor(circleButton, selected);
        SetButtonColor(triangleButton, selected);
        SetButtonColor(starButton, selected);
    }

    void SetButtonColor(Button btn, Button selected)
    {
        if (btn == null) return;
        Image img = btn.GetComponent<Image>();
        if (img != null)
        {
            if (btn == selected)
                img.color = selectedColor;
            else if (originalColors.ContainsKey(btn))
                img.color = originalColors[btn]; // Orijinal rengine dön
            else
                img.color = normalColor; // Yedek (normalColor gri olabilir)
        }
    }

    void UpdateUndoRedoInteractable()
    {
        if (undoButton != null && pixelCanvas != null)
            undoButton.interactable = pixelCanvas.CanUndo();
        if (redoButton != null && pixelCanvas != null)
            redoButton.interactable = pixelCanvas.CanRedo();
    }

    public void OnLinePressed()
    {
        UpdateSelectionVisuals(lineButton);
        if (pixelCanvas != null)
        {
            pixelCanvas.StartShapeLine();
            pixelCanvas.IgnorePointerForOneFrame();
            pixelCanvas.ClearSelectedUINextFrame();
        }
    }

    public void OnRectPressed()
    {
        UpdateSelectionVisuals(rectButton);
        if (pixelCanvas != null)
        {
            pixelCanvas.StartShapeRect();
            pixelCanvas.IgnorePointerForOneFrame();
            pixelCanvas.ClearSelectedUINextFrame();
        }
    }

    public void OnCirclePressed()
    {
        UpdateSelectionVisuals(circleButton);
        if (pixelCanvas != null)
        {
            pixelCanvas.StartShapeCircle();
            pixelCanvas.IgnorePointerForOneFrame();
            pixelCanvas.ClearSelectedUINextFrame();
        }
    }

    public void OnTrianglePressed()
    {
        UpdateSelectionVisuals(triangleButton);
        if (pixelCanvas != null)
        {
            pixelCanvas.StartShapeTriangle();
            pixelCanvas.IgnorePointerForOneFrame();
            pixelCanvas.ClearSelectedUINextFrame();
        }
    }

    public void OnStarPressed()
    {
        UpdateSelectionVisuals(starButton);
        if (pixelCanvas != null)
        {
            pixelCanvas.StartShapeStar();
            pixelCanvas.IgnorePointerForOneFrame();
            pixelCanvas.ClearSelectedUINextFrame();
        }
    }

    void OnPixelCanvasModeChanged(PixelCanvas.Mode newMode)
    {
        if (newMode == PixelCanvas.Mode.Pen)
            UpdateSelectionVisuals(penButton);
        else if (newMode == PixelCanvas.Mode.Eraser)
            UpdateSelectionVisuals(eraserButton);
        else if (newMode == PixelCanvas.Mode.Bucket)
            UpdateSelectionVisuals(bucketButton);
        else if (newMode == PixelCanvas.Mode.Move)
            UpdateSelectionVisuals(moveButton);
        else if (newMode == PixelCanvas.Mode.Select)
            UpdateSelectionVisuals(selectButton);
    }
}