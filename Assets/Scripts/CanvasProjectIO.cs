using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Çizimi JSON dosyası olarak kaydeder / yükler (rapor gereksinimi).
/// </summary>
public class CanvasProjectIO : MonoBehaviour
{
    [Header("References")]
    public PixelCanvas canvas;
    [Tooltip("İsteğe bağlı: birden fazla katman kaydı için.")]
    public PixelLayerController layerController;

    [Header("Default file (persistent data path)")]
    public string defaultFileName = "pixelart_project.json";

    public string DefaultPath => Path.Combine(Application.persistentDataPath, defaultFileName);

    public void SaveToDefaultPath()
    {
        SaveToPath(DefaultPath);
    }

    public void LoadFromDefaultPath()
    {
        LoadFromPath(DefaultPath);
    }

    public void SaveToPath(string fullPath)
    {
        if (canvas == null)
        {
            Debug.LogWarning("[CanvasProjectIO] canvas atanmadı.");
            return;
        }

        if (layerController != null)
            layerController.FlushActiveToStorage();

        var json = BuildProjectJson();
        string text = JsonUtility.ToJson(json, true);
        try
        {
            File.WriteAllText(fullPath, text);
            Debug.Log("[CanvasProjectIO] Kaydedildi: " + fullPath);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[CanvasProjectIO] Kayıt hatası: " + ex.Message);
        }
    }

    public void LoadFromPath(string fullPath)
    {
        if (canvas == null)
        {
            Debug.LogWarning("[CanvasProjectIO] canvas atanmadı.");
            return;
        }

        if (!File.Exists(fullPath))
        {
            Debug.LogWarning("[CanvasProjectIO] Dosya yok: " + fullPath);
            return;
        }

        string text = File.ReadAllText(fullPath);
        CanvasProjectJson data = JsonUtility.FromJson<CanvasProjectJson>(text);
        if (data == null || data.layers == null || data.layers.Length == 0)
        {
            Debug.LogWarning("[CanvasProjectIO] Geçersiz proje JSON.");
            return;
        }

        ApplyMetaToCanvas(data);

        var decoded = new Color32[data.layers.Length][];
        for (int i = 0; i < data.layers.Length; i++)
        {
            decoded[i] = DecodePixelsFromBase64(data.layers[i].pixelsBase64, data.width, data.height);
            if (decoded[i] == null)
            {
                Debug.LogWarning("[CanvasProjectIO] Katman " + i + " çözümlenemedi.");
                return;
            }
        }

        if (data.layers.Length > 1 && layerController != null)
        {
            layerController.ImportLayers(data.layers, decoded);
        }
        else
        {
            if (data.layers.Length > 1 && layerController == null)
                Debug.LogWarning("[CanvasProjectIO] Çoklu katman dosyası; PixelLayerController yok — yalnızca ilk katman yüklendi.");
            canvas.ReplacePixelsFrom(decoded[0]);
        }

        Debug.Log("[CanvasProjectIO] Yüklendi: " + fullPath);
    }

    CanvasProjectJson BuildProjectJson()
    {
        var json = new CanvasProjectJson
        {
            version = 1,
            width = canvas.width,
            height = canvas.height,
            showCheckerboard = canvas.showCheckerboard,
            tileSize = canvas.tileSize,
            bgColorA = ToSaved(canvas.bgColorA),
            bgColorB = ToSaved(canvas.bgColorB),
            showGridLines = canvas.showGridLines,
            gridLineWidth = canvas.gridLineWidth,
            gridLineColor = ToSaved(canvas.gridLineColor)
        };

        if (layerController != null && layerController.LayerCount > 0)
        {
            json.layers = layerController.ExportForProject();
        }
        else
        {
            json.layers = new[]
            {
                new LayerPixelData
                {
                    name = "Katman 1",
                    pixelsBase64 = EncodePixelsToBase64(canvas.pixelBuffer)
                }
            };
        }

        return json;
    }

    void ApplyMetaToCanvas(CanvasProjectJson data)
    {
        canvas.width = data.width;
        canvas.height = data.height;
        canvas.showCheckerboard = data.showCheckerboard;
        canvas.tileSize = Mathf.Max(1, data.tileSize);
        canvas.bgColorA = data.bgColorA != null ? FromSaved(data.bgColorA) : new Color32(255, 255, 255, 255);
        canvas.bgColorB = data.bgColorB != null ? FromSaved(data.bgColorB) : new Color32(200, 200, 200, 255);
        canvas.showGridLines = data.showGridLines;
        canvas.gridLineWidth = Mathf.Max(1, data.gridLineWidth);
        canvas.gridLineColor = data.gridLineColor != null ? FromSaved(data.gridLineColor) : new Color32(160, 160, 160, 255);

        canvas.RebuildTexture();
    }

    static SavedColor ToSaved(Color32 c)
    {
        return new SavedColor { r = c.r, g = c.g, b = c.b };
    }

    static Color32 FromSaved(SavedColor s)
    {
        if (s == null) return new Color32(255, 255, 255, 255);
        return new Color32(s.r, s.g, s.b, 255);
    }

    public static string EncodePixelsToBase64(Color32[] pixels)
    {
        if (pixels == null) return "";
        int byteLen = pixels.Length * 4;
        byte[] raw = new byte[byteLen];
        Buffer.BlockCopy(pixels, 0, raw, 0, byteLen);
        return Convert.ToBase64String(raw);
    }

    public static Color32[] DecodePixelsFromBase64(string b64, int width, int height)
    {
        if (string.IsNullOrEmpty(b64) || width <= 0 || height <= 0) return null;
        int expected = width * height;
        try
        {
            byte[] raw = Convert.FromBase64String(b64);
            if (raw.Length < expected * 4) return null;
            var pixels = new Color32[expected];
            Buffer.BlockCopy(raw, 0, pixels, 0, expected * 4);
            return pixels;
        }
        catch
        {
            return null;
        }
    }
}
