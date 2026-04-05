using UnityEngine;

/// <summary>
/// Kaydet / Yükle butonlarından <see cref="CanvasProjectIO"/> çağırmak için ince sarmalayıcı.
/// </summary>
public class ProjectFileUI : MonoBehaviour
{
    public CanvasProjectIO projectIO;

    public void SaveDefault()
    {
        if (projectIO != null) projectIO.SaveToDefaultPath();
    }

    public void LoadDefault()
    {
        if (projectIO != null) projectIO.LoadFromDefaultPath();
    }
}
