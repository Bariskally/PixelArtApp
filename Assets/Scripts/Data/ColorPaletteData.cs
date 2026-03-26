using System.Collections.Generic;
using System.IO;
using UnityEngine;

[System.Serializable]
public class SavedColor
{
    public byte r;
    public byte g;
    public byte b;
}

[System.Serializable]
public class ColorPaletteData
{
    public List<SavedColor> colors = new List<SavedColor>();
}

public class ColorPaletteSaveSystem : MonoBehaviour
{
    string SavePath => Path.Combine(Application.persistentDataPath, "colorPalette.json");

    public void SavePalette(List<Color32> colors)
    {
        ColorPaletteData data = new ColorPaletteData();

        foreach (var c in colors)
        {
            data.colors.Add(new SavedColor
            {
                r = c.r,
                g = c.g,
                b = c.b
            });
        }

        string json = JsonUtility.ToJson(data, true);
        File.WriteAllText(SavePath, json);

        Debug.Log("Palette saved: " + SavePath);
    }

    public List<Color32> LoadPalette()
    {
        List<Color32> result = new List<Color32>();

        if (!File.Exists(SavePath))
        {
            Debug.Log("No palette save file.");
            return result;
        }

        string json = File.ReadAllText(SavePath);
        ColorPaletteData data = JsonUtility.FromJson<ColorPaletteData>(json);

        if (data == null || data.colors == null)
            return result;

        foreach (var c in data.colors)
        {
            result.Add(new Color32(c.r, c.g, c.b, 255));
        }

        Debug.Log("Palette loaded.");
        return result;
    }
}