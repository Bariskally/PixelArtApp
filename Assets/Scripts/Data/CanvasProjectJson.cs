using System;
using UnityEngine;

/// <summary>
/// JSON proje dosyası şeması (rapor: çizimlerin JSON formatında kaydedilmesi / yüklenmesi).
/// Piksel verisi büyük olduğu için katman başına RGBA ham bayt Base64 alanında tutulur.
/// </summary>
[Serializable]
public class CanvasProjectJson
{
    public int version = 1;
    public int width = 1024;
    public int height = 1024;

    public bool showCheckerboard = true;
    public int tileSize = 32;
    public SavedColor bgColorA;
    public SavedColor bgColorB;

    public bool showGridLines;
    public int gridLineWidth = 1;
    public SavedColor gridLineColor;

    /// <summary>Katmanlar (en az 1). Her biri RGBA Base64.</summary>
    public LayerPixelData[] layers;
}

[Serializable]
public class LayerPixelData
{
    public string name = "Layer";
    /// <summary>RGBA sıralı ham piksel verisi, Base64.</summary>
    public string pixelsBase64;
}
