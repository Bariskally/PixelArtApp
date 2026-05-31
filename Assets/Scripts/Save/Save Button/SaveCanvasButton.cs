using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Button))]
public class SaveCanvasButton : MonoBehaviour
{
    [Tooltip("Kaydedilecek PixelCanvas referansı")]
    public PixelCanvas pixelCanvas;

    [Tooltip("Kaydedilecek klasör adı (Resimler klasörü altına)")]
    public string subFolder = "Exports";

    [Tooltip("Dosya adı öneki (sonuna tarih-saat eklenir)")]
    public string filePrefix = "PixelArt";

    private Button button;

    void Start()
    {
        button = GetComponent<Button>();
        button.onClick.AddListener(OnSaveClicked);

        if (pixelCanvas == null)
            pixelCanvas = FindObjectOfType<PixelCanvas>();
    }

    void OnDestroy()
    {
        if (button != null)
            button.onClick.RemoveListener(OnSaveClicked);
    }

    public void OnSaveClicked()
    {
        if (pixelCanvas == null)
        {
            Debug.LogError("[SaveCanvasButton] PixelCanvas referansı atanmamış!");
            return;
        }

        int w = pixelCanvas.width;
        int h = pixelCanvas.height;
        Color32[] sourceBuffer = pixelCanvas.pixelBuffer;
        HashSet<int> modified = pixelCanvas.userModifiedPixels; // <-- maske

        Texture2D exportTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        Color32[] exportPixels = new Color32[w * h];

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = row + x;
                Color32 c = sourceBuffer[idx];

                if (modified.Contains(idx))
                    exportPixels[idx] = new Color32(c.r, c.g, c.b, 255);
                else
                    exportPixels[idx] = new Color32(c.r, c.g, c.b, 0);
            }
        }

        exportTex.SetPixels32(exportPixels);
        exportTex.Apply();


        Texture2D flippedTex = FlipTextureBoth(exportTex);
        Destroy(exportTex);

        byte[] pngBytes = flippedTex.EncodeToPNG();
        if (pngBytes == null || pngBytes.Length == 0)
        {
            Debug.LogError("[SaveCanvasButton] PNG kodlaması başarısız oldu.");
            Destroy(flippedTex);
            return;
        }

        string picturesFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string dir = Path.Combine(picturesFolder, subFolder);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string fileName = $"{filePrefix}_{timestamp}.png";
        string fullPath = Path.Combine(dir, fileName);

        File.WriteAllBytes(fullPath, pngBytes);
        Debug.Log($"[SaveCanvasButton] Çizim kaydedildi: {fullPath}");

        Destroy(flippedTex);
    }

    // Dokuyu dikey eksende ters çevirir
    private Texture2D FlipTextureVertically(Texture2D original)
    {
        int w = original.width;
        int h = original.height;
        Texture2D flipped = new Texture2D(w, h, original.format, false);

        Color32[] origPixels = original.GetPixels32();
        Color32[] flipPixels = new Color32[origPixels.Length];

        for (int y = 0; y < h; y++)
        {
            int srcRow = (h - 1 - y) * w;
            int dstRow = y * w;
            for (int x = 0; x < w; x++)
            {
                flipPixels[dstRow + x] = origPixels[srcRow + x];
            }
        }

        flipped.SetPixels32(flipPixels);
        flipped.Apply();
        return flipped;
    }

    // Hem dikey hem yatay çevirir (180° döndürme + ayna düzeltmesi)
    private Texture2D FlipTextureBoth(Texture2D original)
    {
        int w = original.width;
        int h = original.height;
        Texture2D flipped = new Texture2D(w, h, original.format, false);

        Color32[] origPixels = original.GetPixels32();
        Color32[] flipPixels = new Color32[origPixels.Length];

        for (int y = 0; y < h; y++)
        {
            int srcRow = (h - 1 - y) * w;          // dikey çevir: alt satır üste
            int dstRow = y * w;
            for (int x = 0; x < w; x++)
            {
                int srcX = (w - 1 - x);            // yatay çevir: sol ↔ sağ
                flipPixels[dstRow + x] = origPixels[srcRow + srcX];
            }
        }

        flipped.SetPixels32(flipPixels);
        flipped.Apply();
        return flipped;
    }
}