using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Button))]
public class PenToolButton : MonoBehaviour
{
    public ToolPanelController controller;

    void Awake()
    {
        var b = GetComponent<Button>();
        b.onClick.AddListener(OnClick);
    }

    void OnDestroy()
    {
        var b = GetComponent<Button>();
        b.onClick.RemoveListener(OnClick);
    }

    void OnClick()
    {
        if (controller != null) controller.OnPenPressed();
    }
}