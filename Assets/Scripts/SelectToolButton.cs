using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

[RequireComponent(typeof(Button))]
public class SelectToolButton : MonoBehaviour
{
    public ToolPanelController controller;
    private Button btn;

    void Awake()
    {
        btn = GetComponent<Button>();
        btn.onClick.AddListener(OnClick);
    }

    void OnDestroy()
    {
        btn.onClick.RemoveListener(OnClick);
    }

    void OnClick()
    {
        if (controller != null) controller.OnSelectPressed();
        // Tıklanan butonu zorla seçili yap
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(btn.gameObject);
    }
}