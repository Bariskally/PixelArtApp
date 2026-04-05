using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Hazır palet seçimini RuntimeColorPaletteController ile bağlar (Dropdown + isteğe bağlı buton).
/// </summary>
public class PresetPaletteApplier : MonoBehaviour
{
    [Header("References")]
    public RuntimeColorPaletteController runtimePalette;
    [Tooltip("Boş bırakılırsa Start'ta Dropdown bulunur.")]
    public Dropdown presetDropdown;

    [Header("Behavior")]
    [Tooltip("Açılışta mevcut sloları temizleyip seçilen paleti yükler.")]
    public bool replaceExistingSlotsOnChange = true;

    void Start()
    {
        if (presetDropdown == null)
            presetDropdown = GetComponent<Dropdown>();

        if (presetDropdown != null)
        {
            presetDropdown.ClearOptions();
            var opts = new System.Collections.Generic.List<string>(PresetPalettes.PresetNames);
            presetDropdown.AddOptions(opts);
            presetDropdown.onValueChanged.AddListener(OnPresetChanged);
        }
    }

    void OnDestroy()
    {
        if (presetDropdown != null)
            presetDropdown.onValueChanged.RemoveListener(OnPresetChanged);
    }

    void OnPresetChanged(int index)
    {
        if (runtimePalette == null || presetDropdown == null) return;
        string name = PresetPalettes.PresetNames[index];
        ApplyPreset(name);
    }

    /// <summary>İsim veya indeks ile paleti uygular (UI dışından çağrılabilir).</summary>
    public void ApplyPreset(string presetName)
    {
        if (runtimePalette == null) return;
        var colors = PresetPalettes.GetColors(presetName);
        if (colors == null || colors.Count == 0) return;

        if (replaceExistingSlotsOnChange)
            runtimePalette.ClearAllSlotsAndAdd(colors);
        else
            runtimePalette.AddColorsBulk(colors);
    }

    /// <summary>Dropdown indeksine göre uygular.</summary>
    public void ApplyPresetByDropdownIndex(int index)
    {
        if (index < 0 || index >= PresetPalettes.PresetNames.Count) return;
        ApplyPreset(PresetPalettes.PresetNames[index]);
    }
}
