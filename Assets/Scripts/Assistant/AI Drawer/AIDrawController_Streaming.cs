using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

public class AIDrawController_Streaming : MonoBehaviour
{
    [Header("Integration")]
    public ChatManager chatManager;
    public PixelCanvas pixelCanvas;

    [Header("Realtime / streaming settings")]
    public bool streamApply = true;
    public float commandDelay = 0.01f;   // her batch arası bekleme (saniye)
    public int batchSize = 4;           // bir karede kaç komut uygulanacak

    [Header("Fallback / safety")]
    public bool autoFallbackIfNoCommands = true;
    public int expectedTreeSize = 24;

    public event Action<int, int> OnApplyProgress;

    Coroutine applyCoroutine = null;
    bool stopRequested = false;
    bool paused = false;

    // ---------- Şablon Veri Tabanı ----------
    private Dictionary<string, string[]> templates = new Dictionary<string, string[]>
    {
        ["sword"] = new string[] {
            "RECT 28 4 8 40 #A0A0A0",
            "RECT 30 4 4 40 #C0C0C0",
            "RECT 24 44 16 4 #8B4513",
            "RECT 26 44 12 4 #A0522D",
            "RECT 30 0 4 4 #D0D0D0",
            "CIRCLE 32 44 4 #FFD700",
            "LINE 32 4 32 44 #FFFFFF"
        },
        ["tree"] = new string[] {
            "RECT 28 40 8 24 #8B4513",
            "CIRCLE 32 20 16 #125B1A",
            "CIRCLE 20 24 10 #2FA83D",
            "CIRCLE 44 24 10 #2FA83D"
        },
        ["pine"] = new string[] {
            "RECT 30 48 4 16 #8B4513",
            "LINE 32 16 16 48 #0F5C12",
            "LINE 32 16 48 48 #0F5C12",
            "LINE 32 24 20 48 #1A8A1A",
            "LINE 32 24 44 48 #1A8A1A",
            "LINE 32 32 24 48 #2DB82D",
            "LINE 32 32 40 48 #2DB82D"
        },
        ["house"] = new string[] {
            "RECT 16 32 32 32 #8B4513",
            "RECT 24 44 16 20 #FFD700",
            "RECT 28 52 4 12 #8B4513",
            "LINE 16 32 32 16 #A0A0A0",
            "LINE 48 32 32 16 #A0A0A0"
        },
        ["star"] = new string[] {
            "LINE 32 8 32 56 #FFD700",
            "LINE 8 32 56 32 #FFD700",
            "LINE 16 16 48 48 #FFD700",
            "LINE 48 16 16 48 #FFD700"
        },
        ["heart"] = new string[] {
            "CIRCLE 24 16 12 #FF0000",
            "CIRCLE 40 16 12 #FF0000",
            "LINE 16 24 32 52 #FF0000",
            "LINE 48 24 32 52 #FF0000"
        },
        ["flower"] = new string[] {
            "LINE 32 48 32 28 #0A5C0A",
            "CIRCLE 32 20 6 #FF69B4",
            "CIRCLE 26 24 5 #FF1493",
            "CIRCLE 38 24 5 #FF1493",
            "CIRCLE 24 30 5 #FF69B4",
            "CIRCLE 40 30 5 #FF69B4",
            "CIRCLE 30 34 4 #FFD700"
        },
        ["sun"] = new string[] {
            "CIRCLE 32 32 12 #FFD700",
            "LINE 32 12 32 20 #FFD700",
            "LINE 32 44 32 52 #FFD700",
            "LINE 12 32 20 32 #FFD700",
            "LINE 44 32 52 32 #FFD700",
            "LINE 20 20 26 26 #FFD700",
            "LINE 44 44 38 38 #FFD700",
            "LINE 44 20 38 26 #FFD700",
            "LINE 20 44 26 38 #FFD700"
        }
    };

    // ---------- Public API ----------
    public void RequestDraw(string userDescription) => StartCoroutine(_RequestDrawCoroutine(userDescription));
    public void RequestDrawWithState(string userDescription, bool sendFullCanvas = false, int maxRuns = 1200)
        => StartCoroutine(_RequestDrawWithStateCoroutine(userDescription, sendFullCanvas, maxRuns));

    public void StopApply() { stopRequested = true; paused = false; if (applyCoroutine != null) { StopCoroutine(applyCoroutine); applyCoroutine = null; } }
    public void PauseApply() => paused = true;
    public void ResumeApply() => paused = false;

    // ---------- Coroutine'ler ----------
    IEnumerator _RequestDrawCoroutine(string userDesc)
    {
        if (chatManager == null || pixelCanvas == null) yield break;

        // AI'dan sadece nesne adı iste
        string prompt = userDesc;
        string sys = @"
You are a request analyzer. User will ask to draw something.
Respond ONLY with the single object name in English (lowercase, no punctuation).
Examples:
'draw a sword' -> sword
'bir ağaç çiz' -> tree
'pixel art house' -> house
'güzel bir kılıç yap' -> sword
'çiçek çiz' -> flower
";

        string result = null;
        yield return StartCoroutine(chatManager.SendRawPrompt(prompt, sys, (s) => result = s));
        HandleAssistantResult(result);
    }

    IEnumerator _RequestDrawWithStateCoroutine(string userDesc, bool sendFullCanvas, int maxRuns)
    {
        // State ile çizim yapmak için de aynı şablon mantığını kullanabiliriz
        yield return StartCoroutine(_RequestDrawCoroutine(userDesc));
    }

    // ---------- Çıktıyı işleme ----------
    void HandleAssistantResult(string result)
    {
        if (string.IsNullOrEmpty(result))
        {
            Debug.LogWarning("[AIDrawController_Streaming] empty result");
            if (autoFallbackIfNoCommands) DrawFallbackTreeCentered(expectedTreeSize);
            return;
        }

        // AI'dan gelen cevabı temizle
        string objectName = result.Trim().ToLowerInvariant().Replace(".", "").Replace(",", "").Replace("!", "").Replace("?", "");

        Debug.Log($"[AIDrawController_Streaming] AI returned object name: {objectName}");

        // Şablonda var mı kontrol et
        if (templates.ContainsKey(objectName))
        {
            var commands = new List<string>(templates[objectName]);
            if (applyCoroutine != null) StopApply();
            stopRequested = false;
            paused = false;
            applyCoroutine = StartCoroutine(ApplyCommandsIncrementally(commands));
            return;
        }

        // Şablonda yoksa, belki AI birden fazla kelime döndü (örn: "pixel art sword")
        string[] words = objectName.Split(' ');
        foreach (string word in words)
        {
            if (templates.ContainsKey(word))
            {
                var commands = new List<string>(templates[word]);
                if (applyCoroutine != null) StopApply();
                stopRequested = false;
                paused = false;
                applyCoroutine = StartCoroutine(ApplyCommandsIncrementally(commands));
                return;
            }
        }

        // Hiçbir şey bulunamadıysa fallback
        Debug.LogWarning("[AIDrawController_Streaming] unknown object: " + objectName);
        if (autoFallbackIfNoCommands) DrawFallbackTreeCentered(expectedTreeSize);
    }

    IEnumerator ApplyCommandsIncrementally(List<string> commands)
    {
        int total = commands.Count;
        int applied = 0;
        OnApplyProgress?.Invoke(applied, total);

        for (int i = 0; i < commands.Count;)
        {
            if (stopRequested) break;
            while (paused) { yield return null; if (stopRequested) break; }
            if (stopRequested) break;

            int end = Math.Min(i + batchSize, commands.Count);
            for (int j = i; j < end; j++)
            {
                if (stopRequested) break;
                if (ExecuteSingleCommand(commands[j])) applied++;
            }

            OnApplyProgress?.Invoke(applied, total);

            if (commandDelay > 0f)
                yield return new WaitForSecondsRealtime(commandDelay);
            else
                yield return null;

            i = end;
        }

        applyCoroutine = null;
        if (applied == 0 && autoFallbackIfNoCommands)
            DrawFallbackTreeCentered(expectedTreeSize);
    }

    // ---------- Tek komut yürütme ----------
    bool ExecuteSingleCommand(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return false;
        string line = rawLine.Trim().TrimEnd('.', ';');

        bool allowOverwrite = Regex.IsMatch(line, @"\bOVERWRITE\b", RegexOptions.IgnoreCase);
        if (allowOverwrite)
            line = Regex.Replace(line, @"\bOVERWRITE\b", "", RegexOptions.IgnoreCase).Trim();

        var parts = Regex.Split(line, @"\s+");
        if (parts.Length == 0) return false;
        string cmd = parts[0].ToUpperInvariant();

        try
        {
            switch (cmd)
            {
                case "BRUSH": return true;

                case "PIXEL":
                    if (parts.Length >= 4)
                    {
                        int px = 0, py = 0;
                        string hex = null;
                        if (int.TryParse(parts[1], out int t1) && int.TryParse(parts[2], out int t2))
                        { px = t1; py = t2; hex = parts[3]; }
                        else if (TryParseHex(parts[1], out _))
                        { hex = parts[1]; if (!int.TryParse(parts[2], out px) || !int.TryParse(parts[3], out py)) return false; }
                        else return false;

                        if (TryParseHex(hex, out Color32 col))
                        {
                            ClampCoords(ref px, ref py);
                            if (allowOverwrite) pixelCanvas.DrawPixelImmediate(px, py, col);
                            else pixelCanvas.DrawPixelRespectExisting(px, py, col);
                            return true;
                        }
                    }
                    break;

                case "LINE":
                    if (parts.Length >= 6 &&
                        int.TryParse(parts[1], out int x0) && int.TryParse(parts[2], out int y0) &&
                        int.TryParse(parts[3], out int x1) && int.TryParse(parts[4], out int y1))
                    {
                        if (TryParseHex(parts[5], out Color32 lineCol))
                        {
                            ClampCoords(ref x0, ref y0); ClampCoords(ref x1, ref y1);
                            if (allowOverwrite) pixelCanvas.DrawLineImmediate(x0, y0, x1, y1, lineCol);
                            else pixelCanvas.DrawLineRespectExisting(x0, y0, x1, y1, lineCol);
                            return true;
                        }
                    }
                    break;

                case "RECT":
                    if (parts.Length >= 6 &&
                        int.TryParse(parts[1], out int rx) && int.TryParse(parts[2], out int ry) &&
                        int.TryParse(parts[3], out int rw) && int.TryParse(parts[4], out int rh))
                    {
                        if (TryParseHex(parts[5], out Color32 rectCol))
                        {
                            if (allowOverwrite) pixelCanvas.DrawRectImmediate(rx, ry, rw, rh, rectCol);
                            else pixelCanvas.DrawRectRespectExisting(rx, ry, rw, rh, rectCol);
                            return true;
                        }
                    }
                    break;

                case "CIRCLE":
                    if (parts.Length >= 5 &&
                        int.TryParse(parts[1], out int cx) && int.TryParse(parts[2], out int cy) && int.TryParse(parts[3], out int r))
                    {
                        if (TryParseHex(parts[4], out Color32 circleCol))
                        {
                            if (allowOverwrite) pixelCanvas.DrawCircleImmediate(cx, cy, r, circleCol);
                            else pixelCanvas.DrawCircleRespectExisting(cx, cy, r, circleCol);
                            return true;
                        }
                    }
                    break;

                case "FILL":
                    if (parts.Length >= 4 && int.TryParse(parts[1], out int fx) && int.TryParse(parts[2], out int fy))
                    {
                        if (TryParseHex(parts[3], out Color32 fillCol))
                        {
                            if (allowOverwrite) pixelCanvas.FloodFillAt(fx, fy, fillCol);
                            else pixelCanvas.FloodFillRespectExisting(fx, fy, fillCol);
                            return true;
                        }
                    }
                    break;
            }
        }
        catch (Exception ex) { Debug.LogWarning($"Command error: {rawLine} -> {ex}"); }
        return false;
    }

    // ---------- Yardımcılar ----------
    bool TryParseHex(string hex, out Color32 color)
    {
        color = new Color32(0, 0, 0, 255);
        if (string.IsNullOrEmpty(hex)) return false;
        string s = hex.Trim().Replace("\"", "").Replace("'", "");
        if (!s.StartsWith("#")) s = "#" + s;
        if (s.Length != 7) return false;
        try { byte r = Convert.ToByte(s.Substring(1, 2), 16); byte g = Convert.ToByte(s.Substring(3, 2), 16); byte b = Convert.ToByte(s.Substring(5, 2), 16); color = new Color32(r, g, b, 255); return true; }
        catch { return false; }
    }

    void ClampCoords(ref int x, ref int y) { if (pixelCanvas) { x = Mathf.Clamp(x, 0, pixelCanvas.width - 1); y = Mathf.Clamp(y, 0, pixelCanvas.height - 1); } }

    void DrawFallbackTreeCentered(int treeSize)
    {
        if (pixelCanvas == null) return;
        int canvasW = pixelCanvas.width;
        int canvasH = pixelCanvas.height;
        int startX = (canvasW - treeSize) / 2;
        int startY = (canvasH - treeSize) / 2;

        Color32 trunkCol = HexToColor32("#8B5A2B");
        Color32 leafDark = HexToColor32("#125B1A");
        Color32 leafLight = HexToColor32("#2FA83D");

        int trunkW = Math.Max(1, treeSize / 8);
        int trunkH = Math.Max(1, treeSize / 4);
        int trunkX = startX + (treeSize - trunkW) / 2;
        int trunkY = startY + (treeSize - trunkH);
        pixelCanvas.DrawRectImmediate(trunkX, trunkY, trunkW, trunkH, trunkCol);

        int cx = startX + treeSize / 2;
        int cy = startY + treeSize / 2 - treeSize / 8;
        int rOuter = Math.Max(0, treeSize / 2 - 2);
        int rInner = Math.Max(0, treeSize / 3);
        pixelCanvas.DrawCircleImmediate(cx, cy, rOuter, leafDark);
        pixelCanvas.DrawCircleImmediate(Math.Max(0, cx - rInner / 2), Math.Max(0, cy - rInner / 3), rInner, leafLight);
        pixelCanvas.DrawCircleImmediate(Math.Min(pixelCanvas.width - 1, cx + rInner / 3), Math.Max(0, cy - rInner / 4), rInner, leafLight);
        pixelCanvas.DrawCircleImmediate(cx, Math.Min(pixelCanvas.height - 1, cy + rInner / 4), rInner, leafLight);
        pixelCanvas.DrawRectImmediate(Math.Max(0, cx - 2), Math.Max(0, cy - 1), 4, 2, leafLight);
    }

    Color32 HexToColor32(string hex)
    {
        if (TryParseHex(hex, out Color32 c)) return c;
        return new Color32(0, 0, 0, 255);
    }
}