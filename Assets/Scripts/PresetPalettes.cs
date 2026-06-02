using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Hazır renk paletleri (rapor: kullanıcıya hazır paletler sunulması).
/// İsimler <see cref="PresetPaletteApplier"/> tarafından kullanılır.
/// </summary>
public static class PresetPalettes
{
    public static readonly IReadOnlyList<string> PresetNames = new[]
    {
        "Varsayılan (8 renk)",
        "PICO-8",
        "Game Boy",
        "NES Klasik",
        "Dawnbringer 16",
        "Gri tonlar"
    };

    public static List<Color32> GetColors(string presetName)
    {
        if (string.IsNullOrEmpty(presetName)) return new List<Color32>();

        if (presetName.StartsWith("Varsayılan")) return Default8();
        if (presetName.StartsWith("PICO-8")) return Pico8();
        if (presetName.StartsWith("Game Boy")) return GameBoy();
        if (presetName.StartsWith("NES")) return NesClassic();
        if (presetName.StartsWith("Dawnbringer")) return Dawnbringer16();
        if (presetName.StartsWith("Gri tonlar")) return Grayscale();

        return Default8();
    }

    static List<Color32> Default8()
    {
        return new List<Color32>
        {
            new Color32(0, 0, 0, 255),
            new Color32(255, 255, 255, 255),
            new Color32(255, 0, 0, 255),
            new Color32(0, 255, 0, 255),
            new Color32(0, 0, 255, 255),
            new Color32(255, 255, 0, 255),
            new Color32(255, 0, 255, 255),
            new Color32(0, 255, 255, 255)
        };
    }

    /// <summary>Resmi PICO-8 paleti (hex kaynak: lexaloffle).</summary>
    static List<Color32> Pico8()
    {
        string[] hex =
        {
            "#000000", "#1D2B53", "#7E2553", "#008751", "#AB5236", "#5F574F", "#C2C3C7", "#FFF1E8",
            "#FF004D", "#FFA300", "#FFEC27", "#00E436", "#29ADFF", "#83769C", "#FF77A8", "#FFCCAA"
        };
        return HexList(hex);
    }

    static List<Color32> GameBoy()
    {
        string[] hex = { "#0f380f", "#306230", "#8bac0f", "#9bbc0f" };
        return HexList(hex);
    }

    static List<Color32> NesClassic()
    {
        string[] hex =
        {
            "#000000", "#FCFCFC", "#F8F8F8", "#BCBCBC", "#7C7C7C", "#A4E4FC", "#3CBCFC", "#0078F8",
            "#0000FC", "#282828", "#F83800", "#D82800", "#FC7460", "#FCBCB0", "#F8B8F8", "#F878F8"
        };
        return HexList(hex);
    }

    static List<Color32> Dawnbringer16()
    {
        string[] hex =
        {
            "#140c1c", "#442434", "#30346d", "#4e4a4e", "#854c30", "#346524", "#d04648", "#757161",
            "#597dce", "#d27d2c", "#8595a1", "#6daa2c", "#d2aa99", "#6dc2ca", "#d77bba", "#ffecd6"
        };
        return HexList(hex);
    }

    static List<Color32> Grayscale()
    {
        var list = new List<Color32>(16);
        for (int i = 0; i < 16; i++)
        {
            byte v = (byte)Mathf.RoundToInt(Mathf.Lerp(0, 255, i / 15f));
            list.Add(new Color32(v, v, v, 255));
        }
        return list;
    }

    static List<Color32> HexList(string[] hexes)
    {
        var list = new List<Color32>(hexes.Length);
        foreach (var h in hexes)
        {
            if (TryParseHex(h, out Color32 c)) list.Add(c);
        }
        return list;
    }

    static bool TryParseHex(string hex, out Color32 c)
    {
        c = new Color32(0, 0, 0, 255);
        if (string.IsNullOrEmpty(hex)) return false;
        string s = hex.Trim();
        if (s.StartsWith("#")) s = s.Substring(1);
        if (s.Length != 6) return false;
        try
        {
            byte r = System.Convert.ToByte(s.Substring(0, 2), 16);
            byte g = System.Convert.ToByte(s.Substring(2, 2), 16);
            byte b = System.Convert.ToByte(s.Substring(4, 2), 16);
            c = new Color32(r, g, b, 255);
            return true;
        }
        catch { return false; }
    }
}
