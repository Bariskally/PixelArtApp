using UnityEngine;

/// <summary>
/// Katman seçimi ve yeni katman ekleme için basit UI bağlantıları (Inspector'dan butonlara atanır).
/// </summary>
public class LayerToolbarUI : MonoBehaviour
{
    public PixelLayerController layers;

    public void SelectLayer0() { if (layers != null) layers.SelectLayer(0); }
    public void SelectLayer1() { if (layers != null) layers.SelectLayer(1); }
    public void SelectLayer2() { if (layers != null) layers.SelectLayer(2); }
    public void AddLayer() { if (layers != null) layers.AddLayer(); }
}
