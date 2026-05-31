using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

public class ChatUIController : MonoBehaviour
{
    [Header("UI refs (TextMeshPro)")]
    public TMP_InputField inputField;
    public Button sendButton;
    public RectTransform messagesContent;
    public GameObject messagePrefab;

    [Header("Integration")]
    public ChatManager chatManager;
    public AIDrawController_Streaming aiDrawController;   // ← artık Streaming sınıfı

    [Header("Canvas state toggle")]
    public Toggle sendCanvasStateToggle;

    readonly string[] drawKeywords = new string[] {
        "çiz", "çizim", "draw", "paint", "boya", "çizim yap",
        "çiz lütfen", "çizermisin", "çizebilir misin"
    };

    void Start()
    {
        if (sendButton != null) sendButton.onClick.AddListener(OnSendClicked);
        if (inputField != null)
        {
            inputField.onSubmit.AddListener(OnSubmit);
            inputField.onEndEdit.AddListener(OnEndEdit);
        }
    }

    void OnDestroy()
    {
        if (sendButton != null) sendButton.onClick.RemoveListener(OnSendClicked);
        if (inputField != null)
        {
            inputField.onSubmit.RemoveListener(OnSubmit);
            inputField.onEndEdit.RemoveListener(OnEndEdit);
        }
    }

    void OnSubmit(string s) => OnSendClicked();
    void OnEndEdit(string text) { }

    public void OnSendClicked()
    {
        if (inputField == null || string.IsNullOrWhiteSpace(inputField.text)) return;

        string userText = inputField.text.Trim();
        AddMessageToUI("You: " + userText);
        inputField.text = "";
        inputField.ActivateInputField();

        if (IsDrawRequest(userText))
        {
            if (aiDrawController == null)
            {
                AddMessageToUI("Assistant: (Hata) aiDrawController atanmadı.");
                return;
            }

            AddMessageToUI("Assistant: Çizim isteğiniz işleniyor...");

            bool sendState = (sendCanvasStateToggle != null) ? sendCanvasStateToggle.isOn : false;

            if (sendState)
                aiDrawController.RequestDrawWithState(userText, sendFullCanvas: false, maxRuns: 1200);
            else
                aiDrawController.RequestDraw(userText);

            return;
        }

        // Normal sohbet / palet istekleri
        if (chatManager != null)
            StartCoroutine(chatManager.SendPromptAndHandleResponse(userText, OnAssistantResponse));
    }

    bool IsDrawRequest(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string lower = text.ToLowerInvariant();
        foreach (var kw in drawKeywords)
            if (lower.Contains(kw)) return true;
        return false;
    }

    void OnAssistantResponse(string assistantText)
    {
        AddMessageToUI("Assistant: " + assistantText);
    }

    void AddMessageToUI(string text)
    {
        if (messagePrefab == null || messagesContent == null) return;
        GameObject go = Instantiate(messagePrefab, messagesContent);
        var tmp = go.GetComponentInChildren<TMP_Text>();
        if (tmp != null) tmp.text = text;
        Canvas.ForceUpdateCanvases();
        var sv = messagesContent.GetComponentInParent<ScrollRect>();
        if (sv != null) sv.verticalNormalizedPosition = 0f;
    }
}