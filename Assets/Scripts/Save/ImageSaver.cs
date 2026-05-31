using UnityEngine;
using System.Collections;
using System.IO;

public class ImageSaver : MonoBehaviour
{
    // Bu fonksiyonu butonun OnClick() olayýna baðlayacaðýz
    public void SaveCanvasImage()
    {
        StartCoroutine(CaptureAndSave());
    }

    // Ekran görüntüsünü yakalayýp kaydeden coroutine
    private IEnumerator CaptureAndSave()
    {
        // Kare sonunu bekle (render iþlemleri tamamlansýn)
        yield return new WaitForEndOfFrame();

        // Yakalama alaný (tüm ekraný alýyoruz, istersen Rect deðerlerini deðiþtir)
        int width = Screen.width;
        int height = Screen.height;
        Rect captureRect = new Rect(0, 0, width, height);

        // Geçici Texture2D oluþtur
        Texture2D capturedTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
        capturedTexture.ReadPixels(captureRect, 0, 0);
        capturedTexture.Apply();

        // Texture'ý PNG byte[]'a çevir
        byte[] imageData = capturedTexture.EncodeToPNG();
        Destroy(capturedTexture); // Belleði temizle

        // Þimdi bu byte[]'ý kaydetme fonksiyonuna gönder
        SaveImageToDevice(imageData);
    }

    // Burasý seçtiðin yönteme göre deðiþecek
    private void SaveImageToDevice(byte[] imageData)
    {
        string fileName = "pixel_art_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
        string savePath = Path.Combine(Application.persistentDataPath, fileName);
        File.WriteAllBytes(savePath, imageData);
        Debug.Log("Görsel kaydedildi: " + savePath);
        // Ýstersen ekranda bir mesaj göster (UI Text ile)
    }
}
