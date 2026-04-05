using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Katmanların temel yapısı (rapor): her katman tam boyutlu piksel dizisi.
/// Görüntüleme için tek bir "aktif" katman <see cref="PixelCanvas.pixelBuffer"/> üzerinde çalışır;
/// katman değişince bellek içi takas yapılır (performans için gerçek birleştirme yok — ders projesi için yeterli).
/// </summary>
public class PixelLayerController : MonoBehaviour
{
    [Header("References")]
    public PixelCanvas canvas;

    [Header("Layers")]
    [Tooltip("Başlangıç katman sayısı (Awake'te oluşturulur).")]
    public int initialLayerCount = 2;

    readonly List<Color32[]> layerBuffers = new List<Color32[]>();
    readonly List<string> layerNames = new List<string>();

    int activeIndex;

    public int ActiveIndex => activeIndex;
    public int LayerCount => layerBuffers.Count;

    void Awake()
    {
        if (canvas == null) canvas = FindObjectOfType<PixelCanvas>();
    }

    void Start()
    {
        if (canvas == null) return;
        if (layerBuffers.Count > 0) return;

        int n = Mathf.Max(1, initialLayerCount);
        for (int i = 0; i < n; i++)
        {
            if (i == 0)
                layerBuffers.Add(canvas.ClonePixelBuffer());
            else
                layerBuffers.Add(canvas.CreateBackgroundBufferCopy());
            layerNames.Add("Katman " + (i + 1));
        }
        activeIndex = 0;
    }

    /// <summary>Proje yüklemesi için: mevcut dizileri sıfırlayıp dışarıdan doldurur.</summary>
    public void ImportLayers(LayerPixelData[] layers, Color32[][] decodedPixels)
    {
        if (canvas == null || layers == null || decodedPixels == null) return;
        if (layers.Length != decodedPixels.Length) return;

        layerBuffers.Clear();
        layerNames.Clear();

        for (int i = 0; i < layers.Length; i++)
        {
            layerNames.Add(string.IsNullOrEmpty(layers[i].name) ? ("Katman " + (i + 1)) : layers[i].name);
            layerBuffers.Add(decodedPixels[i]);
        }

        activeIndex = 0;
        canvas.ReplacePixelsFrom(layerBuffers[activeIndex]);
    }

    public void SelectLayer(int index)
    {
        if (canvas == null) return;
        if (index < 0 || index >= layerBuffers.Count) return;
        if (index == activeIndex) return;

        Color32[] cur = canvas.pixelBuffer;
        if (cur != null && layerBuffers.Count > activeIndex)
            Array.Copy(cur, layerBuffers[activeIndex], cur.Length);

        activeIndex = index;
        canvas.ReplacePixelsFrom(layerBuffers[activeIndex]);
        canvas.ClearHistory();
    }

    public void AddLayer()
    {
        if (canvas == null) return;
        Color32[] cur = canvas.pixelBuffer;
        if (cur != null && layerBuffers.Count > activeIndex)
            Array.Copy(cur, layerBuffers[activeIndex], cur.Length);

        var blank = canvas.CreateBackgroundBufferCopy();
        layerBuffers.Add(blank);
        layerNames.Add("Katman " + (layerBuffers.Count));
        activeIndex = layerBuffers.Count - 1;
        canvas.ReplacePixelsFrom(layerBuffers[activeIndex]);
        canvas.ClearHistory();
    }

    /// <summary>Kaydetmeden önce çağrılmalı: aktif çizimi bellekteki katmana yazar.</summary>
    public void FlushActiveToStorage()
    {
        if (canvas == null || layerBuffers.Count == 0) return;
        Color32[] cur = canvas.pixelBuffer;
        if (cur == null || activeIndex < 0 || activeIndex >= layerBuffers.Count) return;
        if (layerBuffers[activeIndex] == null || layerBuffers[activeIndex].Length != cur.Length)
            layerBuffers[activeIndex] = new Color32[cur.Length];
        Array.Copy(cur, layerBuffers[activeIndex], cur.Length);
    }

    public LayerPixelData[] ExportForProject()
    {
        FlushActiveToStorage();
        var arr = new LayerPixelData[layerBuffers.Count];
        for (int i = 0; i < layerBuffers.Count; i++)
        {
            arr[i] = new LayerPixelData
            {
                name = i < layerNames.Count ? layerNames[i] : ("Katman " + (i + 1)),
                pixelsBase64 = CanvasProjectIO.EncodePixelsToBase64(layerBuffers[i])
            };
        }
        return arr;
    }
}
