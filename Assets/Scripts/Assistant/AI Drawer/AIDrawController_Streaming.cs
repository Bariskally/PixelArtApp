// AIDrawController_Streaming.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// AIDrawController (streaming-friendly)
/// - RequestDraw / RequestDrawWithState / RequestDrawWithFullPixels (kýsa fallback mantýðý býrakýldý)
/// - Eðer streamApply true ise assistant çýktýsýný satýr satýr uygular ve ilerlemeyi anlýk gösterir.
/// - Pause/Resume/Stop kontrolü, batch uygulama, gecikme ayarlarý.
/// - PixelCanvas (senin verdiðin sýnýf) ile doðrudan çalýþýr.
/// </summary>
public class AIDrawController_Streaming : MonoBehaviour
{
    [Header("Integration")]
    public ChatManager chatManager;
    public PixelCanvas pixelCanvas;

    [Header("Realtime / streaming settings")]
    [Tooltip("If true, apply assistant commands incrementally so you see drawing progress.")]
    public bool streamApply = true;
    [Tooltip("Seconds to wait after each batch. 0 = no wait (fast). Use small positive like 0.01 for visible progress.")]
    public float commandDelay = 0.02f;
    [Tooltip("Apply this many commands in a tight loop before yielding to the engine (reduces per-line overhead).")]
    public int batchSize = 8;

    [Header("Fallback / safety")]
    [Tooltip("If assistant returns empty or no commands, draw fallback procedural shape.")]
    public bool autoFallbackIfNoCommands = true;
    public int expectedTreeSize = 24;

    // Progress event: (appliedCommands, totalCommands)
    public event Action<int, int> OnApplyProgress;

    // Internal control
    Coroutine applyCoroutine = null;
    bool stopRequested = false;
    bool paused = false;

    // ----------------------
    // Public API (call from UI)
    // ----------------------
    public void RequestDraw(string userDescription)
    {
        StartCoroutine(_RequestDrawCoroutine(userDescription));
    }

    public void RequestDrawWithState(string userDescription, bool sendFullCanvas = false, int maxRuns = 1200)
    {
        StartCoroutine(_RequestDrawWithStateCoroutine(userDescription, sendFullCanvas, maxRuns));
    }

    public void StopApply()
    {
        stopRequested = true;
        paused = false;
        if (applyCoroutine != null)
        {
            StopCoroutine(applyCoroutine);
            applyCoroutine = null;
        }
    }

    public void PauseApply()
    {
        paused = true;
    }

    public void ResumeApply()
    {
        paused = false;
    }

    // ----------------------
    // Coroutines that ask ChatManager
    // ----------------------
    IEnumerator _RequestDrawCoroutine(string userDesc)
    {
        if (chatManager == null || pixelCanvas == null)
        {
            Debug.LogWarning("[AIDrawController_Streaming] chatManager or pixelCanvas not assigned.");
            yield break;
        }

        string prompt = $"CanvasSize: {pixelCanvas.width} {pixelCanvas.height}\nDraw: {userDesc}\nReturn only drawing commands.";
        string sys = "You are a strict Pixel Art Drawing Assistant. Output only drawing commands.";

        string result = null;
        yield return StartCoroutine(chatManager.SendRawPrompt(prompt, sys, (s) => result = s));

        HandleAssistantResult(result);
    }

    IEnumerator _RequestDrawWithStateCoroutine(string userDesc, bool sendFullCanvas, int maxRuns)
    {
        if (chatManager == null || pixelCanvas == null)
        {
            Debug.LogWarning("[AIDrawController_Streaming] chatManager or pixelCanvas not assigned.");
            yield break;
        }

        string stateText;
        if (!sendFullCanvas)
        {
            if (pixelCanvas.GetNonBackgroundBoundingBox(out int xMin, out int yMin, out int xMax, out int yMax))
            {
                stateText = $"CROP {xMin} {yMin} {xMax - xMin + 1} {yMax - yMin + 1}\n";
                stateText += pixelCanvas.ExportCroppedRLE(xMin, yMin, xMax, yMax, maxRuns);
            }
            else
            {
                stateText = $"CANVAS {pixelCanvas.width} {pixelCanvas.height}\n" + pixelCanvas.ExportPaletteLine() + "\nNOTE: canvas empty\n";
            }
        }
        else
        {
            stateText = pixelCanvas.ExportStateRLE(includeAllRows: false, maxRuns: maxRuns);
        }

        string userBlock = $"UserRequest: {userDesc}\nGuidelines: Return only drawing commands (PIXEL/LINE/RECT/CIRCLE/FILL/BRUSH).";
        string prompt = stateText + "\n" + userBlock;
        string sys = "You are a strict Pixel Art Drawing Assistant. Output only drawing commands.";

        string result = null;
        yield return StartCoroutine(chatManager.SendRawPrompt(prompt, sys, (s) => result = s));

        HandleAssistantResult(result);
    }

    // ----------------------
    // Handle assistant output (choose incremental or batch)
    // ----------------------
    void HandleAssistantResult(string result)
    {
        if (string.IsNullOrEmpty(result))
        {
            Debug.LogWarning("[AIDrawController_Streaming] empty assistant result.");
            if (autoFallbackIfNoCommands) DrawFallbackTreeCentered(expectedTreeSize);
            return;
        }

        // If already running, stop previous
        if (applyCoroutine != null) StopApply();

        stopRequested = false;
        paused = false;

        // Parse into commands (preprocess ordering helps layering)
        var rawLines = Regex.Split(result.Trim(), @"\r?\n");
        var commands = PreprocessAndSortCommandsByVerticalCenter(rawLines);

        if (commands.Count == 0)
        {
            if (autoFallbackIfNoCommands) DrawFallbackTreeCentered(expectedTreeSize);
            return;
        }

        if (streamApply)
        {
            applyCoroutine = StartCoroutine(ApplyCommandsIncrementally(commands));
        }
        else
        {
            // immediate apply (OLD behavior) — düzeltilmiþ: ExecuteSingleCommand döngüsü kullanýlýyor
            int applied = 0;
            foreach (var line in commands)
            {
                if (ExecuteSingleCommand(line)) applied++;
            }
            OnApplyProgress?.Invoke(applied, commands.Count);
            applyCoroutine = null;
        }
    }

    // ----------------------
    // Incremental application coroutine
    // ----------------------
    IEnumerator ApplyCommandsIncrementally(List<string> commands)
    {
        int total = commands.Count;
        int applied = 0;

        OnApplyProgress?.Invoke(applied, total);

        // Process in batches to avoid blocking
        for (int i = 0; i < commands.Count;)
        {
            if (stopRequested) break;

            // pause support
            while (paused)
            {
                yield return null;
                if (stopRequested) break;
            }
            if (stopRequested) break;

            int end = Math.Min(i + batchSize, commands.Count);
            for (int j = i; j < end; j++)
            {
                if (stopRequested) break;
                string line = commands[j];
                bool ok = ExecuteSingleCommand(line);
                if (ok) applied++;
            }

            // Let PixelCanvas update its texture (it uses dirty flag and updates in Update)
            OnApplyProgress?.Invoke(applied, total);

            // yield to next frame so UI updates show the changes
            if (commandDelay > 0f)
                yield return new WaitForSecondsRealtime(commandDelay);
            else
                yield return null;

            i = end;
        }

        // finished
        applyCoroutine = null;

        if (applied == 0 && autoFallbackIfNoCommands)
        {
            Debug.Log("[AIDrawController_Streaming] no commands applied -> fallback.");
            DrawFallbackTreeCentered(expectedTreeSize);
        }
    }

    // ----------------------
    // Single-line executor (returns true when an actual draw op applied)
    // ----------------------
    bool ExecuteSingleCommand(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return false;

        string line = rawLine.Trim().TrimEnd('.', ';');

        // Detect explicit overwrite
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
                case "BRUSH":
                    // brush only influence local UI; we ignore it for now
                    return true;

                case "PIXEL":
                    // support either "PIXEL x y #HEX" or "PIXEL #HEX x y"
                    if (parts.Length >= 4)
                    {
                        int px = 0, py = 0;
                        string hex = null;
                        if (int.TryParse(parts[1], out int t1) && int.TryParse(parts[2], out int t2))
                        {
                            px = t1; py = t2; hex = parts[3];
                        }
                        else if (TryParseHex(parts[1], out _))
                        {
                            hex = parts[1];
                            if (!int.TryParse(parts[2], out px) || !int.TryParse(parts[3], out py)) return false;
                        }
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
                        int.TryParse(parts[1], out int x0) &&
                        int.TryParse(parts[2], out int y0) &&
                        int.TryParse(parts[3], out int x1) &&
                        int.TryParse(parts[4], out int y1))
                    {
                        if (TryParseHex(parts[5], out Color32 lineCol))
                        {
                            ClampCoords(ref x0, ref y0);
                            ClampCoords(ref x1, ref y1);
                            if (Math.Abs(x0 - x1) + Math.Abs(y0 - y1) == 0) return false;
                            if (allowOverwrite) pixelCanvas.DrawLineImmediate(x0, y0, x1, y1, lineCol);
                            else pixelCanvas.DrawLineRespectExisting(x0, y0, x1, y1, lineCol);
                            return true;
                        }
                    }
                    break;

                case "RECT":
                    if (parts.Length >= 6 &&
                        int.TryParse(parts[1], out int rx) &&
                        int.TryParse(parts[2], out int ry) &&
                        int.TryParse(parts[3], out int rw) &&
                        int.TryParse(parts[4], out int rh))
                    {
                        if (TryParseHex(parts[5], out Color32 rectCol))
                        {
                            if (rw <= 0 || rh <= 0) return false;
                            if (allowOverwrite) pixelCanvas.DrawRectImmediate(rx, ry, rw, rh, rectCol);
                            else pixelCanvas.DrawRectRespectExisting(rx, ry, rw, rh, rectCol);
                            return true;
                        }
                    }
                    break;

                case "CIRCLE":
                    if (parts.Length >= 5 &&
                        int.TryParse(parts[1], out int cx) &&
                        int.TryParse(parts[2], out int cy) &&
                        int.TryParse(parts[3], out int r))
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
                    if (parts.Length >= 4 &&
                        int.TryParse(parts[1], out int fx) &&
                        int.TryParse(parts[2], out int fy))
                    {
                        if (TryParseHex(parts[3], out Color32 fillCol))
                        {
                            if (allowOverwrite) pixelCanvas.FloodFillAt(fx, fy, fillCol);
                            else pixelCanvas.FloodFillRespectExisting(fx, fy, fillCol);
                            return true;
                        }
                    }
                    break;

                default:
                    // unknown directive or PALETTE/CANVAS headers - ignore for drawing
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[AIDrawController_Streaming] Exception executing command: " + rawLine + " -> " + ex);
            return false;
        }

        return false;
    }

    // ----------------------
    // Helper parsing / ordering (same approach as full controller)
    // ----------------------
    List<string> PreprocessAndSortCommandsByVerticalCenter(IEnumerable<string> rawLines)
    {
        var brushOrDirectives = new List<string>();
        var drawCommands = new List<(string line, float sortKey, int originalIndex)>();
        int idx = 0;

        foreach (var r in rawLines)
        {
            string line = r?.Trim();
            if (string.IsNullOrEmpty(line)) { idx++; continue; }

            if (Regex.IsMatch(line, @"^\s*BRUSH\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(line, @"^\s*PALETTE\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(line, @"^\s*(CANVAS|CROP|FULLPIXELS)\b", RegexOptions.IgnoreCase))
            {
                brushOrDirectives.Add(line);
                idx++; continue;
            }

            float avgY = pixelCanvas != null ? (float)pixelCanvas.height * 0.5f : 0f;
            bool parsed = false;

            var mPixel = Regex.Match(line, @"^\s*PIXEL\s+(-?\d+)\s+(-?\d+)", RegexOptions.IgnoreCase);
            if (mPixel.Success && int.TryParse(mPixel.Groups[2].Value, out int py)) { avgY = py; parsed = true; }

            var mLine = Regex.Match(line, @"^\s*LINE\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)", RegexOptions.IgnoreCase);
            if (!parsed && mLine.Success && int.TryParse(mLine.Groups[2].Value, out int y0) && int.TryParse(mLine.Groups[4].Value, out int y1))
            {
                avgY = (y0 + y1) * 0.5f; parsed = true;
            }

            var mRect = Regex.Match(line, @"^\s*RECT\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)", RegexOptions.IgnoreCase);
            if (!parsed && mRect.Success && int.TryParse(mRect.Groups[2].Value, out int ry) && int.TryParse(mRect.Groups[4].Value, out int rh))
            {
                int yEnd = ry + Math.Max(0, rh - 1);
                avgY = (ry + yEnd) * 0.5f; parsed = true;
            }

            var mCircle = Regex.Match(line, @"^\s*CIRCLE\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)", RegexOptions.IgnoreCase);
            if (!parsed && mCircle.Success && int.TryParse(mCircle.Groups[2].Value, out int cy)) { avgY = cy; parsed = true; }

            var mFill = Regex.Match(line, @"^\s*FILL\s+(-?\d+)\s+(-?\d+)", RegexOptions.IgnoreCase);
            if (!parsed && mFill.Success && int.TryParse(mFill.Groups[2].Value, out int fy)) { avgY = fy; parsed = true; }

            drawCommands.Add((line, avgY, idx));
            idx++;
        }

        drawCommands.Sort((a, b) =>
        {
            int cmp = b.sortKey.CompareTo(a.sortKey);
            if (cmp != 0) return cmp;
            return a.originalIndex.CompareTo(b.originalIndex);
        });

        var result = new List<string>();
        result.AddRange(brushOrDirectives);
        foreach (var d in drawCommands) result.Add(d.line);
        return result;
    }

    // ----------------------
    // Utilities
    // ----------------------
    bool TryParseHex(string hex, out Color32 color)
    {
        color = new Color32(0, 0, 0, 255);
        if (string.IsNullOrEmpty(hex)) return false;
        string s = hex.Trim().Replace("\"", "").Replace("'", "");
        if (!s.StartsWith("#")) s = "#" + s;
        if (s.Length != 7) return false;
        try
        {
            byte r = Convert.ToByte(s.Substring(1, 2), 16);
            byte g = Convert.ToByte(s.Substring(3, 2), 16);
            byte b = Convert.ToByte(s.Substring(5, 2), 16);
            color = new Color32(r, g, b, 255);
            return true;
        }
        catch { return false; }
    }

    void ClampCoords(ref int x, ref int y)
    {
        if (pixelCanvas == null) return;
        x = Mathf.Clamp(x, 0, pixelCanvas.width - 1);
        y = Mathf.Clamp(y, 0, pixelCanvas.height - 1);
    }

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