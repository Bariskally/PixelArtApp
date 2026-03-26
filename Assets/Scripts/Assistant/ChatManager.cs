// ChatManager.cs
using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// ChatManager (improved):
/// - SendPromptAndHandleResponse: palette ve normal sohbet akýþý (mevcut davranýþ korunur)
/// - SendRawPrompt: ham assistant content döndüren, truncation/retry-aware, prompt-baiting korumasý eklenmiþ metod
/// - Tolerant hex extraction (escaped \u0023, bare hex)
/// - Structured array detection
/// - Bulk ekleme via runtimePaletteController.AddColorsBulk
/// - Coroutine exits use yield break
/// </summary>
public class ChatManager : MonoBehaviour
{
    [Header("OpenRouter (chat completions)")]
    public string endpointUrl = "https://openrouter.ai/api/v1/chat/completions";
    [Tooltip("Paste your OpenRouter API Key here (keep secret).")]
    public string apiKey = "";

    [Header("Integration hooks")]
    public RuntimeColorPaletteController runtimePaletteController; // inspector baðla ya da null býrak

    [Header("System prompt (optional)")]
    [Tooltip("If true, the systemMessage will be sent as a system role to the model before the user's prompt.")]
    public bool includeSystemMessage = true;

    [TextArea(6, 12)]
    [Tooltip("Default strict system prompt that instructs the model to return only JSON arrays when user requests palette changes.")]
    public string systemMessage = @"You are a strict Pixel Art Assistant whose single job is to supply color palettes on demand. RULES (read carefully):

1. When the user explicitly asks to ""add"", ""add to palette"", ""add colors"", ""palet ekle"", ""palete renk ekle"", or otherwise requests colors for the palette, RESPOND WITH ONLY a JSON array of hex color codes in the form ""#RRGGBB"". Example valid output: [""#FF00FF"", ""#00FF00""]. 
2. The JSON array must be the whole response — no extra text, no explanation, no surrounding markdown, no leading/trailing words, no comments.
3. Hex codes must be 6-digit RGB; leading '#' required. Uppercase or lowercase both accepted, but prefer uppercase (e.g. ""#AABBCC"").
4. If the user's request is ambiguous or missing exact numbers (e.g. ""make it warmer"", ""autumn palette please""), choose a reasonable default number of colors (3–6) and still return only the JSON array with those hex codes.
5. Never ask clarifying questions when the user asks to add colors. If additional clarification would normally be needed, still return a reasonable palette array instead of asking.
6. If the user asks for a format other than palette (normal conversation), reply normally — but if they indicate ""palet"" or ""palette"" anywhere in the request, follow rules 1–5.
7. Do not include color names, descriptions, or indexes inside the array — single-purpose array only.
8. If you must present multiple alternatives, return only one array (the recommended set). Do not return multiple arrays or nested objects.
9. If you cannot generate colors for technical reasons, return an empty JSON array [] (still valid JSON) instead of any human text.";

    [Header("Tuning")]
    [Tooltip("Base max tokens used for SendRawPrompt. Lower for smaller canvas / faster responses.")]
    public int baseMaxTokens = 600; // daha küçük canvas'lar için makul baþlangýç

    [Tooltip("HTTP request timeout (seconds) used for SendRawPrompt. Increase if you see Request timeout for long responses.")]
    public int requestTimeoutSeconds = 120;

    [Tooltip("If prompt length (chars) exceeds this, SendRawPrompt will return __PROMPT_TOO_LARGE__ so caller can fallback to compact state.")]
    public int maxRequestBodyWarn = 30000;

    // Public: high-level prompt -> palette handling (keeps original behavior)
    public IEnumerator SendPromptAndHandleResponse(string prompt, Action<string> onTextResponse)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            onTextResponse?.Invoke("");
            yield break;
        }

        // build messages
        List<object> messagesList = new List<object>();
        if (includeSystemMessage && !string.IsNullOrEmpty(systemMessage))
        {
            messagesList.Add(new Dictionary<string, string>() { { "role", "system" }, { "content", systemMessage } });
        }
        messagesList.Add(new Dictionary<string, string>() { { "role", "user" }, { "content", prompt } });

        var body = new Dictionary<string, object>()
        {
            { "model", "openrouter/healer-alpha" },
            { "messages", messagesList.ToArray() },
            { "max_tokens", 300 }
        };

        string jsonBody = MiniJSON.Serialize(body);

        using (UnityWebRequest www = new UnityWebRequest(endpointUrl, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(apiKey)) www.SetRequestHeader("Authorization", "Bearer " + apiKey);
            www.timeout = Mathf.Min(requestTimeoutSeconds, 60); // palette kýsa -> 60s yeterli

            yield return www.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
            bool isErr = (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError);
#else
            bool isErr = (www.isNetworkError || www.isHttpError);
#endif
            if (isErr)
            {
                Debug.LogWarning($"[ChatManager] Request failed. code={www.responseCode} err={www.error} body={www.downloadHandler.text}");
                onTextResponse?.Invoke(""); // empty -> caller (AIDrawController) treats as empty and can fallback
                yield break;
            }

            string responseText = www.downloadHandler.text ?? "";
            responseText = responseText.Trim();
            Debug.Log("[ChatManager] raw response (truncated): " + Truncate(responseText, 2000));

            string assistantContent = ExtractAssistantContent(responseText);
            List<string> hexes = new List<string>();

            var structuredHexes = ExtractHexArrayFromJson(responseText);
            if (structuredHexes != null && structuredHexes.Count > 0)
            {
                hexes.AddRange(structuredHexes);
                Debug.Log("[ChatManager] found structured hex array: " + string.Join(", ", structuredHexes));
            }
            else
            {
                if (!string.IsNullOrEmpty(assistantContent))
                {
                    var h = ExtractHexColors(assistantContent);
                    if (h.Count > 0) hexes.AddRange(h);
                }

                if (hexes.Count == 0)
                {
                    var h2 = ExtractHexColors(responseText);
                    if (h2.Count > 0) hexes.AddRange(h2);
                }
            }

            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var finalHexes = new List<string>();
            foreach (var hx in hexes)
            {
                string n = NormalizeHex(hx);
                if (!string.IsNullOrEmpty(n) && !normalized.Contains(n))
                {
                    normalized.Add(n);
                    finalHexes.Add(n);
                }
            }

            if (finalHexes.Count > 0)
            {
                var colors = new List<Color32>();
                foreach (var h in finalHexes)
                {
                    if (TryHexToColor32(h, out Color32 c)) colors.Add(c);
                }

                if (colors.Count > 0)
                {
                    AddColorsToRuntimePaletteBulk(colors);
                    onTextResponse?.Invoke("Renk paleti eklendi: " + string.Join(", ", finalHexes));
                    yield break;
                }
            }

            if (!string.IsNullOrEmpty(assistantContent))
            {
                onTextResponse?.Invoke(assistantContent);
            }
            else
            {
                onTextResponse?.Invoke(responseText);
            }
        }
    }

    /// <summary>
    /// Send a raw prompt and return the assistant's textual content (best-effort).
    /// Retries once on truncation / length finish_reason.
    /// If prompt is too large, returns "__PROMPT_TOO_LARGE__" so caller can fallback.
    /// Always invokes onTextResponse with a non-null string (empty string on hard failure).
    /// </summary>
    public IEnumerator SendRawPrompt(string prompt, string overrideSystemMessage, Action<string> onTextResponse)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            onTextResponse?.Invoke("");
            yield break;
        }

        // Protect: if very large prompt, avoid sending and let caller send compact alternative
        if (prompt.Length > maxRequestBodyWarn)
        {
            Debug.LogWarning($"[ChatManager] Prompt too large ({prompt.Length} chars) — returning __PROMPT_TOO_LARGE__ token so caller can use a compact fallback.");
            onTextResponse?.Invoke("__PROMPT_TOO_LARGE__");
            yield break;
        }

        int attempt = 0;
        int maxAttempts = 2;
        int maxTokens = Math.Max(64, Math.Min(3200, baseMaxTokens));

        while (attempt < maxAttempts)
        {
            attempt++;

            List<object> messagesList = new List<object>();
            if (!string.IsNullOrEmpty(overrideSystemMessage))
            {
                messagesList.Add(new Dictionary<string, string>() { { "role", "system" }, { "content", overrideSystemMessage } });
            }
            messagesList.Add(new Dictionary<string, string>() { { "role", "user" }, { "content", prompt } });

            var body = new Dictionary<string, object>()
            {
                { "model", "openrouter/healer-alpha" },
                { "messages", messagesList.ToArray() },
                { "max_tokens", maxTokens }
            };

            string jsonBody = MiniJSON.Serialize(body);

            using (UnityWebRequest www = new UnityWebRequest(endpointUrl, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                www.uploadHandler = new UploadHandlerRaw(bodyRaw);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(apiKey)) www.SetRequestHeader("Authorization", "Bearer " + apiKey);
                www.timeout = requestTimeoutSeconds;

                Debug.Log($"[ChatManager] Sending request (attempt {attempt}/{maxAttempts}) promptChars={prompt.Length} bodyBytes={bodyRaw.Length} max_tokens={maxTokens} timeout={www.timeout}s");

                yield return www.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                bool isErr = (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError);
#else
                bool isErr = (www.isNetworkError || www.isHttpError);
#endif
                if (isErr)
                {
                    Debug.LogWarning($"[ChatManager] Request failed. code={www.responseCode} err={www.error} attempt={attempt}/{maxAttempts}");
                    if (attempt < maxAttempts)
                    {
                        // simple jittered backoff
                        float backoff = Mathf.Min(4f, 0.5f * Mathf.Pow(2, attempt - 1)) + UnityEngine.Random.Range(0f, 0.25f);
                        yield return new WaitForSeconds(backoff);
                        continue;
                    }
                    else
                    {
                        onTextResponse?.Invoke("");
                        yield break;
                    }
                }

                string responseText = www.downloadHandler.text ?? "";
                responseText = responseText.Trim();

                Debug.Log("[ChatManager] raw response (truncated): " + Truncate(responseText, 2000));

                string assistantContent = ExtractAssistantContent(responseText);

                if (string.IsNullOrEmpty(assistantContent))
                {
                    string unescaped = UnescapeCommon(responseText);
                    var mReason = Regex.Match(unescaped, "\"reasoning\"\\s*:\\s*\"([\\s\\S]*?)\"");
                    if (mReason.Success)
                    {
                        assistantContent = mReason.Groups[1].Value;
                        assistantContent = assistantContent.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\"", "\"");
                        Debug.Log("[ChatManager] extracted assistant reasoning field as fallback.");
                    }
                }

                if (string.IsNullOrEmpty(assistantContent))
                {
                    string unescaped2 = UnescapeCommon(responseText);
                    var mText = Regex.Match(unescaped2, "\"text\"\\s*:\\s*\"([\\s\\S]*?)\"");
                    if (mText.Success)
                    {
                        assistantContent = mText.Groups[1].Value;
                        Debug.Log("[ChatManager] extracted top-level text field as fallback.");
                    }
                }

                if (string.IsNullOrEmpty(assistantContent))
                    assistantContent = null;

                bool truncated = false;
                {
                    string unesc = UnescapeCommon(responseText);
                    truncated = Regex.IsMatch(unesc, "\"finish_reason\"\\s*:\\s*\"length\"") || Regex.IsMatch(unesc, "\"native_finish_reason\"\\s*:\\s*\"length\"");
                    if (truncated) Debug.LogWarning("[ChatManager] Model finish_reason indicates truncation (length). attempt=" + attempt + "/" + maxAttempts);
                }

                if (truncated && attempt < maxAttempts)
                {
                    Debug.Log("[ChatManager] Retrying once with larger max_tokens to avoid truncated response.");
                    yield return null;
                    maxTokens = Mathf.Min(3200, maxTokens * 2);
                    continue; // retry
                }

                if (!string.IsNullOrEmpty(assistantContent))
                {
                    onTextResponse?.Invoke(assistantContent);
                    yield break;
                }
                else
                {
                    // try to extract any textual block if available
                    string fallback = null;
                    {
                        string unesc = UnescapeCommon(responseText);
                        var mReason2 = Regex.Match(unesc, "\"reasoning\"\\s*:\\s*\"([\\s\\S]*?)\"");
                        if (mReason2.Success) fallback = mReason2.Groups[1].Value;
                        if (string.IsNullOrEmpty(fallback))
                        {
                            var mContentAny = Regex.Match(unesc, "\"content\"\\s*:\\s*\"([\\s\\S]*?)\"");
                            if (mContentAny.Success) fallback = mContentAny.Groups[1].Value;
                        }
                        if (string.IsNullOrEmpty(fallback))
                        {
                            var mTextAny = Regex.Match(unesc, "\"text\"\\s*:\\s*\"([\\s\\S]*?)\"");
                            if (mTextAny.Success) fallback = mTextAny.Groups[1].Value;
                        }
                    }

                    if (!string.IsNullOrEmpty(fallback))
                    {
                        fallback = UnescapeCommon(fallback);
                        onTextResponse?.Invoke(fallback);
                        yield break;
                    }

                    // final: return empty to let caller decide fallback
                    onTextResponse?.Invoke("");
                    yield break;
                }
            } // end using www
        } // end while

        onTextResponse?.Invoke("");
        yield break;
    }

    // ---------- improved helpers ----------
    // Try to find JSON array of hex strings anywhere in response,
    // examples it matches: ["#FF00FF", "#00FF00"] or ["FF00FF","00FF00"]
    List<string> ExtractHexArrayFromJson(string response)
    {
        if (string.IsNullOrEmpty(response)) return null;

        // unescape unicode like \u0023 -> #
        string unescaped = UnescapeCommon(response);

        // regex to find array of quoted hex strings
        var arrPattern = new Regex(@"\[\s*(""|')\s*#?[0-9A-Fa-f]{6}\s*(""|')(\s*,\s*(""|')\s*#?[0-9A-Fa-f]{6}\s*(""|'))*\s*\]");
        var m = arrPattern.Match(unescaped);
        if (!m.Success) return null;

        string arrayText = m.Value; // e.g. ["#FF00FF","#00FF00"]
        // extract all hexes inside
        return ExtractHexColors(arrayText);
    }

    // Extract assistant content: tolerant regex (handles nested occurrences)
    string ExtractAssistantContent(string response)
    {
        if (string.IsNullOrEmpty(response)) return null;

        // First unescape common sequences to make regex easier.
        string unescaped = UnescapeCommon(response);

        var m = Regex.Matches(unescaped, "\"content\"\\s*:\\s*\"([\\s\\S]*?)\"");
        if (m.Count > 0)
        {
            string c = m[m.Count - 1].Groups[1].Value;
            return c;
        }

        // fallback: "text":"..."
        var m2 = Regex.Matches(unescaped, "\"text\"\\s*:\\s*\"([\\s\\S]*?)\"");
        if (m2.Count > 0) return m2[m2.Count - 1].Groups[1].Value;

        return null;
    }

    // Normalize hex to uppercase with leading '#'
    string NormalizeHex(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        string s = hex.Trim();
        // handle \u0023 -> #
        s = UnescapeCommon(s);
        if (s.StartsWith("\"") && s.EndsWith("\"")) s = s.Substring(1, s.Length - 2);
        if (s.StartsWith("#") == false && s.Length == 6) s = "#" + s;
        if (s.StartsWith("#") && s.Length == 7) return s.ToUpperInvariant();
        return null;
    }

    // Unescape some common sequences that appear in JSON dumps (esp. \u0023 for '#')
    string UnescapeCommon(string s)
    {
        if (s == null) return null;
        // convert \u0023 or \\u0023 to #
        s = s.Replace("\\u0023", "#").Replace("\\\\u0023", "#");
        // convert escaped sequences to visible chars for regex convenience
        s = s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
        return s;
    }

    List<string> ExtractHexColors(string text)
    {
        var outHex = new List<string>();
        if (string.IsNullOrEmpty(text)) return outHex;

        // Unescape common escapes so patterns like \u0023FF00FF become #FF00FF
        string input = UnescapeCommon(text);

        // match #RRGGBB
        Regex r1 = new Regex("#[0-9A-Fa-f]{6}");
        foreach (Match m in r1.Matches(input))
        {
            string v = m.Value;
            if (!outHex.Contains(v)) outHex.Add(v);
        }

        // match bare RRGGBB not preceded by hex char or '#' (avoid false positives inside larger hex strings)
        Regex r2 = new Regex("(?<![0-9A-Fa-f#])([0-9A-Fa-f]{6})(?![0-9A-Fa-f])");
        foreach (Match m in r2.Matches(input))
        {
            string v = m.Groups[1].Value;
            string normalized = "#" + v;
            if (!outHex.Contains(normalized)) outHex.Add(normalized);
        }

        return outHex;
    }

    void AddColorsToRuntimePaletteBulk(List<Color32> colors)
    {
        if (runtimePaletteController == null)
        {
            Debug.LogWarning("[ChatManager] runtimePaletteController not assigned. Cannot add colors.");
            return;
        }

        try
        {
            runtimePaletteController.AddColorsBulk(colors);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ChatManager] Exception while adding colors to runtime palette: " + ex);
        }
    }

    bool TryHexToColor32(string hex, out Color32 color)
    {
        color = new Color32(255, 255, 255, 255);
        if (string.IsNullOrEmpty(hex)) return false;
        string s = hex.Trim();
        if (s.StartsWith("#")) s = s.Substring(1);
        if (s.Length != 6) return false;
        try
        {
            byte r = Convert.ToByte(s.Substring(0, 2), 16);
            byte g = Convert.ToByte(s.Substring(2, 2), 16);
            byte b = Convert.ToByte(s.Substring(4, 2), 16);
            color = new Color32(r, g, b, 255);
            return true;
        }
        catch { return false; }
    }

    string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "...(truncated)";
    }
}

// ----------------- MINIMAL JSON SERIALIZER (unchanged) -----------------
public static class MiniJSON
{
    public static string Serialize(object obj)
    {
        return SerializeValue(obj);
    }

    static string SerializeValue(object value)
    {
        if (value == null) return "null";
        if (value is string) return $"\"{Escape((string)value)}\"";
        if (value is bool) return (bool)value ? "true" : "false";
        if (value is IDictionary<string, object> dict) return SerializeObject(dict);
        if (value is IDictionary<string, string> dict2) return SerializeObject(dict2);
        if (value is IEnumerable<object> list) return SerializeArray(list);
        if (value is Array arr) return SerializeArray(arr as IEnumerable<object>);
        if (value is int || value is long || value is float || value is double || value is decimal) return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        if (value is IEnumerable<KeyValuePair<string, object>> kvs) return SerializeObject(kvs);
        return $"\"{Escape(value.ToString())}\"";
    }

    static string SerializeObject(IEnumerable kvsEnum)
    {
        var sb = new StringBuilder();
        sb.Append("{");
        bool first = true;
        foreach (var item in kvsEnum)
        {
            string key = null;
            object val = null;
            if (item is KeyValuePair<string, object> kv)
            {
                key = kv.Key;
                val = kv.Value;
            }
            else if (item is DictionaryEntry de)
            {
                key = de.Key.ToString();
                val = de.Value;
            }
            else if (item is KeyValuePair<string, string> kvs)
            {
                key = kvs.Key; val = kvs.Value;
            }
            else if (item is System.Collections.DictionaryEntry de2)
            {
                key = de2.Key.ToString(); val = de2.Value;
            }
            if (key == null) continue;
            if (!first) sb.Append(",");
            sb.Append($"\"{Escape(key)}\":");
            sb.Append(SerializeValue(val));
            first = false;
        }
        sb.Append("}");
        return sb.ToString();
    }

    static string SerializeObject(IDictionary dict)
    {
        var sb = new StringBuilder();
        sb.Append("{");
        bool first = true;
        foreach (DictionaryEntry de in dict)
        {
            if (!first) sb.Append(",");
            sb.Append($"\"{Escape(de.Key.ToString())}\":");
            sb.Append(SerializeValue(de.Value));
            first = false;
        }
        sb.Append("}");
        return sb.ToString();
    }

    static string SerializeObject(IDictionary<string, object> dict)
    {
        var sb = new StringBuilder();
        sb.Append("{");
        bool first = true;
        foreach (var kv in dict)
        {
            if (!first) sb.Append(",");
            sb.Append($"\"{Escape(kv.Key)}\":");
            sb.Append(SerializeValue(kv.Value));
            first = false;
        }
        sb.Append("}");
        return sb.ToString();
    }

    static string SerializeObject(IDictionary<string, string> dict)
    {
        var sb = new StringBuilder();
        sb.Append("{");
        bool first = true;
        foreach (var kv in dict)
        {
            if (!first) sb.Append(",");
            sb.Append($"\"{Escape(kv.Key)}\":");
            sb.Append(SerializeValue(kv.Value));
            first = false;
        }
        sb.Append("}");
        return sb.ToString();
    }

    static string SerializeArray(IEnumerable<object> array)
    {
        var sb = new StringBuilder();
        sb.Append("[");
        bool first = true;
        foreach (var obj in array)
        {
            if (!first) sb.Append(",");
            sb.Append(SerializeValue(obj));
            first = false;
        }
        sb.Append("]");
        return sb.ToString();
    }

    static string Escape(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}