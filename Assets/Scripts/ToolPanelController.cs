using UnityEngine;
using UnityEngine.UI;
using System;

public class ToolPanelController : MonoBehaviour
{
    [Header("Buttons (tool buttons only)")]
    public Button penButton;
    public Button eraserButton;
    public Button bucketButton;
    public Button moveButton; // yeni

    [Header("Undo/Redo Buttons (these are momentary and managed separately)")]
    public Button undoButton; // referans sadece interactable güncellemesi için
    public Button redoButton; // referans sadece interactable güncellemesi için

    [Header("References")]
    public PixelCanvas pixelCanvas;

    [Header("Visuals")]
    public Color selectedColor = new Color(0.2f, 0.6f, 1f, 1f); // seçili araç rengi
    public Color normalColor = Color.white;

    void Start()
    {
        if (pixelCanvas == null)
            Debug.LogWarning("ToolPanelController: pixelCanvas not assigned.");

        // default selection: none
        UpdateSelectionVisuals(null);

        if (pixelCanvas != null) pixelCanvas.SetModePen(); // default internal tool mode; visual none

        // subscribe to history change event so we update undo/redo interactables only when needed
        if (pixelCanvas != null) pixelCanvas.OnHistoryChanged += UpdateUndoRedoInteractable;

        // initial update
        UpdateUndoRedoInteractable();

        // ensure no UI element shows as selected (clears keyboard focus highlight)
        if (pixelCanvas != null) pixelCanvas.ClearSelectedUIImmediate();
    }

    void OnDestroy()
    {
        if (pixelCanvas != null) pixelCanvas.OnHistoryChanged -= UpdateUndoRedoInteractable;
    }

    // These are called by helper button scripts or can be wired directly; they manage mode + visuals.
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

    // Undo/Redo are handled by separate UndoButton/RedoButton scripts.
    // This function only updates the visuals of tool buttons (pen/eraser/bucket/move)
    void UpdateSelectionVisuals(Button selected)
    {
        if (penButton != null)
        {
            var img = penButton.GetComponent<Image>();
            if (img != null) img.color = (penButton == selected) ? selectedColor : normalColor;
        }

        if (eraserButton != null)
        {
            var img = eraserButton.GetComponent<Image>();
            if (img != null) img.color = (eraserButton == selected) ? selectedColor : normalColor;
        }

        if (bucketButton != null)
        {
            var img = bucketButton.GetComponent<Image>();
            if (img != null) img.color = (bucketButton == selected) ? selectedColor : normalColor;
        }

        if (moveButton != null)
        {
            var img = moveButton.GetComponent<Image>();
            if (img != null) img.color = (moveButton == selected) ? selectedColor : normalColor;
        }
    }

    // Only update interactable state when history really changes
    void UpdateUndoRedoInteractable()
    {
        if (undoButton != null)
            undoButton.interactable = (pixelCanvas != null && pixelCanvas.CanUndo());

        if (redoButton != null)
            redoButton.interactable = (pixelCanvas != null && pixelCanvas.CanRedo());
    }
}