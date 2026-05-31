using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems; // EventSystem için eklendi
using System;

public class ToolPanelController : MonoBehaviour
{
    [Header("Buttons (tool buttons only)")]
    public Button penButton;
    public Button eraserButton;
    public Button bucketButton;
    public Button moveButton;
    public Button selectButton;   // ← Select butonu referansı

    [Header("Undo/Redo Buttons")]
    public Button undoButton;
    public Button redoButton;

    [Header("References")]
    public PixelCanvas pixelCanvas;

    [Header("Visuals")]
    public Color selectedColor = new Color(0.2f, 0.6f, 1f, 1f);
    public Color normalColor = Color.white;

    void Start()
    {
        if (pixelCanvas == null)
            Debug.LogWarning("ToolPanelController: pixelCanvas not assigned.");

        UpdateSelectionVisuals(null);
        if (pixelCanvas != null) pixelCanvas.SetModePen();
        if (pixelCanvas != null) pixelCanvas.OnHistoryChanged += UpdateUndoRedoInteractable;
        UpdateUndoRedoInteractable();
        if (pixelCanvas != null) pixelCanvas.ClearSelectedUIImmediate();
    }

    void OnDestroy()
    {
        if (pixelCanvas != null) pixelCanvas.OnHistoryChanged -= UpdateUndoRedoInteractable;
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
            pixelCanvas.ClearSelectedUINextFrame(); // Diğerleriyle tutarlı
        }
        UpdateSelectionVisuals(selectButton);
        // Event System seçimini de güncelle (mavi çerçeve)
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
    }

    void SetButtonColor(Button btn, Button selected)
    {
        if (btn == null) return;
        Image img = btn.GetComponent<Image>();
        if (img != null)
            img.color = (btn == selected) ? selectedColor : normalColor;
    }

    void UpdateUndoRedoInteractable()
    {
        if (undoButton != null && pixelCanvas != null)
            undoButton.interactable = pixelCanvas.CanUndo();
        if (redoButton != null && pixelCanvas != null)
            redoButton.interactable = pixelCanvas.CanRedo();
    }
}