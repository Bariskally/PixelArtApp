using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;

/// <summary>
/// ChatUIController (güncellendi):
/// - Eðer kullanýcý metni bir "çizme" isteði içeriyorsa, AIDrawController'a yönlendirir.
/// - Eðer 'sendCanvasStateToggle' açýksa, RequestDrawWithState çaðrýlýr; deðilse RequestDraw çaðrýlýr.
/// </summary>
public class ChatUIController : MonoBehaviour
{
    [Header("UI refs (TextMeshPro)")]
    public TMP_InputField inputField;
    public Button sendButton;
    public RectTransform messagesContent; // scroll view content
    public GameObject messagePrefab; // prefab whose child contains TMP_Text

    [Header("Integration")]
    public ChatManager chatManager; // assign in inspector (mevcut)
    public AIDrawController aiDrawController; // assign in inspector (yeni)

    [Header("Canvas state toggle")]
    [Tooltip("If assigned and ON, the canvas state will be sent with draw requests (RequestDrawWithState).")]
    public Toggle sendCanvasStateToggle; // inspector baðla (opsiyonel)

    // Basit çizim anahtar kelimeleri (büyütebilirsin)
    readonly string[] drawKeywords = new string[] { "çiz", "çizim", "draw", "paint", "boya", "çizim yap", "çiz lütfen", "çizermisin", "çizebilir misin" };

    void Start()
    {
        if (sendButton != null) sendButton.onClick.AddListener(OnSendClicked);

        // TMP: onSubmit is triggered when user presses Enter/Return
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

    void OnSubmit(string s)
    {
        OnSendClicked();
    }

    void OnEndEdit(string text)
    {
        // kept for compatibility
    }

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
                AddMessageToUI("Assistant: (Hata) aiDrawController inspector'a atanmadý.");
                Debug.LogWarning("[ChatUIController] aiDrawController not assigned in inspector.");
                return;
            }

            AddMessageToUI("Assistant: Çizim isteðiniz iþleniyor...");

            bool sendState = (sendCanvasStateToggle != null) ? sendCanvasStateToggle.isOn : false;

            if (sendState)
            {
                aiDrawController.RequestDrawWithState(userText, sendFullCanvas: false, maxRuns: 1200);
            }
            else
            {
                aiDrawController.RequestDraw(userText);
            }

            return;
        }

        // Normal akýþ: palette/soru cevap vs.
        if (chatManager != null)
        {
            StartCoroutine(chatManager.SendPromptAndHandleResponse(userText, OnAssistantResponse));
        }
    }

    bool IsDrawRequest(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string lower = text.ToLowerInvariant();
        foreach (var kw in drawKeywords)
        {
            if (lower.Contains(kw)) return true;
        }
        return false;
    }

    void OnAssistantResponse(string assistantText)
    {
        AddMessageToUI("Assistant: " + assistantText);
    }

    void AddMessageToUI(string text)
    {
        if (messagePrefab == null || messagesContent == null)
        {
            Debug.LogWarning("[ChatUIController] messagePrefab or messagesContent not assigned.");
            return;
        }

        GameObject go = Instantiate(messagePrefab, messagesContent);
        var tmp = go.GetComponentInChildren<TMP_Text>();
        if (tmp != null) tmp.text = text;
        else
        {
            Debug.LogWarning("[ChatUIController] messagePrefab has no TMP_Text child; expected one.");
        }

        Canvas.ForceUpdateCanvases();
        var sv = messagesContent.GetComponentInParent<ScrollRect>();
        if (sv != null)
        {
            sv.verticalNormalizedPosition = 0f;
        }
    }
}
