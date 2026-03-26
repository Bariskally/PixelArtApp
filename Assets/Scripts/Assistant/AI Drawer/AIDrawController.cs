// AIDrawController.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// AIDrawController:
/// - RequestDraw / RequestDrawWithState / RequestDrawWithFullPixels
/// - Handles ChatManager __PROMPT_TOO_LARGE__ and empty responses with compact fallbacks
/// - Executes drawing commands while respecting existing pixels by default
/// - Preprocess ordering by vertical center to produce better layering
/// - Low-color-diversity retry + local dither fallback
/// </summary>
public class AIDrawController : MonoBehaviour
{
    [Header("Integration")]
    public ChatManager chatManager; // inspector
    public PixelCanvas pixelCanvas; // inspector

    [Header("Behavior")]
    public bool autoFallbackIfNoCommands = true;    // model parse edilemezse fallback çiz
    public bool logAssistantRawToConsole = true;    // model ham çýktýsýný konsola yaz
    public int expectedTreeSize = 32;               // fallback için
    [Tooltip("When ChatManager returns __PROMPT_TOO_LARGE__ or when canvas has too many pixels, try the compact fallback instead.")]
    public int compactMaxPixels = 2048;             // fallback için daha küçük limit

    [TextArea(6, 8)]
    public string drawingSystemPrompt = @"You are a strict Pixel Art Drawing Assistant. OUTPUT RULES:
1) Respond with ONLY drawing commands, one command per line, no extra text.
2) Commands allowed:
   PIXEL x y #RRGGBB
   LINE x0 y0 x1 y1 #RRGGBB
   RECT x y w h #RRGGBB
   CIRCLE cx cy r #RRGGBB
   FILL x y #RRGGBB
   BRUSH size
3) Coordinates are integers in canvas pixels (0,0 top-left), absolute unless a CROP header is present.
4) W and H are the provided canvas size. Use as many commands as needed but keep under 400 commands.
5) Do not include explanations or extra text — only commands and newline characters.
";

    // ------------------------------
    // Public entry points (used elsewhere)
    // ------------------------------
    public void RequestDraw(string userDescription)
    {
        StartCoroutine(_RequestDrawCoroutine(userDescription));
    }

    public void RequestDrawWithState(string userDescription, bool sendFullCanvas = false, int maxRuns = 1200)
    {
        StartCoroutine(_RequestDrawWithStateCoroutine(userDescription, sendFullCanvas, maxRuns));
    }

    public void RequestDrawWithFullPixels(string userDescription, bool includeBackground = false, bool useCropIfPossible = true, int maxPixels = 4096)
    {
        StartCoroutine(_RequestDrawWithFullPixelsCoroutine(userDescription, includeBackground, useCropIfPossible, maxPixels));
    }

    // ------------------------------
    // Core coroutines
    // ------------------------------
    IEnumerator _RequestDrawCoroutine(string userDesc)
    {
        if (chatManager == null || pixelCanvas == null)
        {
            Debug.LogWarning("[AIDrawController] chatManager or pixelCanvas not assigned.");
            yield break;
        }

        string prompt = $"CanvasSize: {pixelCanvas.width} {pixelCanvas.height}\nDraw: {userDesc}\nReturn commands only as specified.";
        string sys = drawingSystemPrompt + $"\nCanvasSize {pixelCanvas.width} {pixelCanvas.height}\n";

        string result = null;
        yield return StartCoroutine(chatManager.SendRawPrompt(prompt, sys, (s) => result = s));

        // Handle ChatManager special token for too-large prompt
        if (result == "__PROMPT_TOO_LARGE__")
        {
            // Try compact fallback: send cropped RLE (much smaller), ask to respond with commands within the crop
            string compactState = BuildCompactStateForPrompt();
            if (!string.IsNullOrEmpty(compactState))
            {
                string compactPrompt = compactState + "\n" + $"Draw: {userDesc}\nReturn only drawing commands within the provided CROP (coordinates relative to full canvas).";
                yield return StartCoroutine(chatManager.SendRawPrompt(compactPrompt, sys, (s) => result = s));
            }
        }

        if (string.IsNullOrEmpty(result))
        {
            Debug.LogWarning("[AIDrawController] empty response from model.");
            if (autoFallbackIfNoCommands) DrawFallbackTreeCentered(expectedTreeSize);
            yield break;
        }

        if (logAssistantRawToConsole)
            Debug.Log("[AIDrawController] Assistant raw output (truncated):\n" + Truncate(result, 4000));

        // Attempt to apply result
        bool appliedAny = ApplyAssistantOutput(result);

        if (!appliedAny && autoFallbackIfNoCommands)
        {
            Debug.Log("[AIDrawController] No valid commands detected — using fallback procedural tree.");
            DrawFallbackTreeCentered(expectedTreeSize);
        }

        yield break;
    }

    IEnumerator _RequestDrawWithStateCoroutine(string userDesc, bool sendFullCanvas, int maxRuns)
    {
        if (chatManager == null || pixelCanvas == null)
        {
            Debug.LogWarning("[AIDrawController] chatManager or pixelCanvas not assigned.");
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
                stateText = $"CANVAS {pixelCanvas.width} {pixelCanvas.height}\n" + pixelCanvas.ExportPaletteLine() + "\nNOTE: canvas empty (no non-background pixels)\n";
            }
        }
        else
        {
            stateText = pixelCanvas.ExportStateRLE(includeAllRows: false, maxRuns: maxRuns);
        }

        string userBlock = $"UserRequest: {userDesc}\n";
        userBlock += "Guidelines:\n" +
                     "- Use only the allowed command set (PIXEL/LINE/RECT/CIRCLE/FILL/BRUSH), one command per line.\n" +
                     "- Coordinates are absolute (0,0 top-left) relative to the full canvas unless a CROP header exists.\n" +
                     "- Do not include any explanation or extra text — only commands.\n" +
                     "- Try to reuse existing colors from PALETTE where possible.\n" +
                     "- Keep commands concise and under 400 commands total.\n";

        string prompt = stateText + "\n" + userBlock + "\nReturn only the drawing commands as specified.";
        string sys = drawingSystemPrompt + $"\nCanvasSize {pixelCanvas.width} {pixelCanvas.height}\n";

        string result = null;
        yield return StartCoroutine(chatManager.SendRawPrompt(prompt, sys, (s) => result = s));

        if (result == "__PROMPT_TOO_LARGE__")
        {
            // unlikely because we already sent compact state, but handle anyway
            Debug.LogWarning("[AIDrawController] ChatManager reported prompt too large even for compact state.");
            result = "";
        }

        if (string.IsNullOrEmpty(result))
        {
            Debug.LogWarning("[AIDrawController] empty response from model (with state).");
            if (autoFallbackIfNoCommands) DrawFallbackTreeCentered(expectedTreeSize);
            yield break;
        }

        if (logAssistantRawToConsole)
            Debug.Log("[AIDrawController] Assistant raw output (with state):\n" + Truncate(result, 8000));

        bool appliedAny = ApplyAssistantOutput(result);

        if (!appliedAny && autoFallbackIfNoCommands)
        {
            Debug.Log("[AIDrawController] No valid commands detected — using fallback procedural tree.");
            DrawFallbackTreeCentered(expectedTreeSize);
        }

        yield break;
    }

    IEnumerator _RequestDrawWithFullPixelsCoroutine(string userDesc, bool includeBackground, bool useCropIfPossible, int maxPixels)
    {
        if (chatManager == null || pixelCanvas == null)
        {
            Debug.LogWarning("[AIDrawController] chatManager or pixelCanvas not assigned.");
            yield break;
        }

        string pixelBlock = pixelCanvas.ExportFullPixelList(includeBackground, useCropIfPossible, maxPixels);

        // If the full pixel block is huge, ChatManager may reject; guard earlier
        if (pixelBlock.Length > 20000)
        {
            Debug.Log("[AIDrawController] Full pixel block large (" + pixelBlock.Length + " chars). Sending a compact JSON alternative.");
            string jsonBlock = pixelCanvas.ExportFullPixelListAsJson(includeBackground, useCropIfPossible, Math.Min(maxPixels, compactMaxPixels));
            string compactPrompt = jsonBlock + "\nUserRequest: " + userDesc + "\nGuidelines: Return drawing commands only (PIXEL/LINE...).";
            string sys = drawingSystemPrompt + $"\nCanvasSize {pixelCanvas.width} {pixelCanvas.height}\n";
            string resultJson = null;
            yield return StartCoroutine(chatManager.SendRawPrompt(compactPrompt, sys, (s) => resultJson = s));

            if (string.IsNullOrEmpty(resultJson))
            {
                Debug.LogWarning("[AIDrawController] empty response from model (fullpixels->json).");
                if (autoFallbackIfNoCommands) DrawFallbackTreeCentered(expectedTreeSize);
                yield break;
            }

            bool appliedJson = ApplyAssistantOutput(resultJson);
            if (!appliedJson && autoFallbackIfNoCommands) DrawFallbackTreeCentered(expectedTreeSize);
            yield break;
        }

        string userBlock = $"UserRequest: {userDesc}\n";
        userBlock += "Guidelines:\n" +
                     "- You received a FULLPIXELS block describing the canvas state. Do NOT overwrite pixels that are NOT background unless explicitly instructed.\n" +
                     "- Use only PIXEL/LINE/RECT/CIRCLE/FILL/BRUSH commands (one per line), coordinates absolute (0,0 top-left).\n" +
                     "- If you must overwrite existing non-background pixels, append the token OVERWRITE to the end of that command line (e.g. PIXEL 10 10 #FF0000 OVERWRITE).\n" +
                     "- Prefer reusing colors from PALETTE.\n" +
                     "- Return only commands (no explanation).\n";

        string prompt = pixelBlock + "\n" + userBlock + "\nReturn only drawing commands as specified.";
        string sysMsg = drawingSystemPrompt + $"\nCanvasSize {pixelCanvas.width} {pixelCanvas.height}\n";

        string result = null;
        yield return StartCoroutine(chatManager.SendRawPrompt(prompt, sysMsg, (s) => result = s));

        if (result == "__PROMPT_TOO_LARGE__")
        {
            // fallback to JSON compact
            string jsonBlock = pixelCanvas.ExportFullPixelListAsJson(includeBackground, useCropIfPossible, Math.Min(maxPixels, compactMaxPixels));
            string compactPrompt = jsonBlock + "\nUserRequest: " + userDesc + "\nGuidelines: Return drawing commands only (PIXEL/LINE...).";
            yield return StartCoroutine(chatManager.SendRawPrompt(compactPrompt, sysMsg, (s) => result = s));
        }

        if (string.IsNullOrEmpty(result))
        {
            Debug.LogWarning("[AIDrawController] empty response from model (full pixels).");
            if (autoFallbackIfNoCommands) DrawFallbackTreeCentered(expectedTreeSize);
            yield break;
        }

        if (logAssistantRawToConsole)
            Debug.Log("[AIDrawController] Assistant raw output (full pixels):\n" + Truncate(result, 8000));

        bool applied = ApplyAssistantOutput(result);
        if (!applied && autoFallbackIfNoCommands) DrawFallbackTreeCentered(expectedTreeSize);

        yield break;
    }

    // ------------------------------
    // Processing assistant output and applying to canvas
    // ------------------------------
    bool ApplyAssistantOutput(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;

        // Some assistants send a header "CanvasSize W H" and "Palette ..." before commands.
        // We'll strip known headers but keep the commands section for parsing.
        string processed = raw.Trim();

        // If assistant returned a short human greeting, ignore
        if (!Regex.IsMatch(processed, @"\b(PIXEL|LINE|RECT|CIRCLE|FILL|BRUSH|PALETTE|CANVAS|FULLPIXELS|CROP)\b", RegexOptions.IgnoreCase))
        {
            // Nothing to apply
            return false;
        }

        // If it contains a FULLPIXELS block (explicit pixel list), we allow parsing of those PIXEL lines.
        // We'll just feed whole text to ExecuteCommandTextWithValidation which tolerates PIXEL commands.
        int beforeAppliedCount = ExecuteCommandTextWithValidation(processed);

        return beforeAppliedCount > 0;
    }

    // ------------------------------
    // Preprocess & execution (kept and robust)
    // ------------------------------
    List<string> PreprocessAndSortCommandsByVerticalCenter(IEnumerable<string> rawLines)
    {
        var brushOrDirectives = new List<string>();
        var drawCommands = new List<(string line, float sortKey, int originalIndex)>();

        int idx = 0;
        foreach (var r in rawLines)
        {
            string line = r?.Trim();
            if (string.IsNullOrEmpty(line))
            {
                idx++;
                continue;
            }

            // Keep BRUSH or obvious directives at top
            if (Regex.IsMatch(line, @"^\s*BRUSH\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(line, @"^\s*PALETTE\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(line, @"^\s*(CANVAS|CROP|FULLPIXELS)\b", RegexOptions.IgnoreCase))
            {
                brushOrDirectives.Add(line);
                idx++;
                continue;
            }

            float avgY = pixelCanvas != null ? (float)pixelCanvas.height * 0.5f : 0f; // default mid if unknown
            bool parsed = false;

            var mPixel = Regex.Match(line, @"^\s*PIXEL\s+(-?\d+)\s+(-?\d+)", RegexOptions.IgnoreCase);
            if (mPixel.Success)
            {
                if (int.TryParse(mPixel.Groups[2].Value, out int y)) { avgY = y; parsed = true; }
            }

            var mLine = Regex.Match(line, @"^\s*LINE\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)", RegexOptions.IgnoreCase);
            if (!parsed && mLine.Success)
            {
                if (int.TryParse(mLine.Groups[2].Value, out int y0) && int.TryParse(mLine.Groups[4].Value, out int y1))
                {
                    avgY = (y0 + y1) * 0.5f;
                    parsed = true;
                }
            }

            var mRect = Regex.Match(line, @"^\s*RECT\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)", RegexOptions.IgnoreCase);
            if (!parsed && mRect.Success)
            {
                if (int.TryParse(mRect.Groups[2].Value, out int y) && int.TryParse(mRect.Groups[4].Value, out int h))
                {
                    int yEnd = y + Math.Max(0, h - 1);
                    avgY = (y + yEnd) * 0.5f;
                    parsed = true;
                }
            }

            var mCircle = Regex.Match(line, @"^\s*CIRCLE\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)", RegexOptions.IgnoreCase);
            if (!parsed && mCircle.Success)
            {
                if (int.TryParse(mCircle.Groups[2].Value, out int cy)) { avgY = cy; parsed = true; }
            }

            var mFill = Regex.Match(line, @"^\s*FILL\s+(-?\d+)\s+(-?\d+)", RegexOptions.IgnoreCase);
            if (!parsed && mFill.Success)
            {
                if (int.TryParse(mFill.Groups[2].Value, out int fy)) { avgY = fy; parsed = true; }
            }

            drawCommands.Add((line, avgY, idx));
            idx++;
        }

        drawCommands.Sort((a, b) =>
        {
            int cmp = b.sortKey.CompareTo(a.sortKey); // descending
            if (cmp != 0) return cmp;
            return a.originalIndex.CompareTo(b.originalIndex);
        });

        var result = new List<string>();
        result.AddRange(brushOrDirectives);
        foreach (var d in drawCommands) result.Add(d.line);
        return result;
    }

    int ExecuteCommandTextWithValidation(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        // split raw lines
        var rawLines = Regex.Split(text.Trim(), @"\r?\n");

        // PREPROCESS: reorder by vertical center so layering is more natural (bottom -> top)
        var preprocessedLines = PreprocessAndSortCommandsByVerticalCenter(rawLines);

        int brush = 1;
        int applied = 0;
        foreach (var raw in preprocessedLines)
        {
            string line = raw?.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // Normalize possible trailing punctuation (model sometimes adds '.')
            line = line.Trim().TrimEnd('.', ';');

            // Detect explicit overwrite request on this line
            bool allowOverwrite = Regex.IsMatch(line, @"\bOVERWRITE\b", RegexOptions.IgnoreCase);
            if (allowOverwrite)
            {
                // Remove token so parsing stays consistent
                line = Regex.Replace(line, @"\bOVERWRITE\b", "", RegexOptions.IgnoreCase).Trim();
            }

            if (!Regex.IsMatch(line, @"^(PIXEL|LINE|RECT|CIRCLE|FILL|BRUSH)\b", RegexOptions.IgnoreCase))
            {
                var tolerant = line.ToUpperInvariant();
                if (!tolerant.Contains("PIXEL") && !tolerant.Contains("LINE") && !tolerant.Contains("RECT") && !tolerant.Contains("CIRCLE") && !tolerant.Contains("FILL") && !tolerant.Contains("BRUSH"))
                {
                    continue;
                }
            }

            var parts = Regex.Split(line, @"\s+");
            if (parts.Length == 0) continue;
            string cmd = parts[0].ToUpperInvariant();

            try
            {
                switch (cmd)
                {
                    case "BRUSH":
                        if (parts.Length >= 2 && int.TryParse(parts[1], out int b)) brush = Math.Max(1, b);
                        applied++;
                        break;

                    case "PIXEL":
                        // handle either "PIXEL #HEX X Y" or "PIXEL X Y #HEX"
                        // prefer PIXEL x y #hex
                        if (parts.Length >= 4)
                        {
                            // Determine where hex is
                            int px = 0, py = 0;
                            string hex = null;
                            // common format PIXEL x y #RRGGBB
                            if (int.TryParse(parts[1], out int t1) && int.TryParse(parts[2], out int t2))
                            {
                                px = t1; py = t2; hex = parts[3];
                            }
                            else if (parts.Length >= 4 && TryParseHex(parts[1], out _))
                            {
                                // alternate format PIXEL #HEX X Y
                                hex = parts[1];
                                if (int.TryParse(parts[2], out int ta) && int.TryParse(parts[3], out int tb)) { px = ta; py = tb; }
                                else continue;
                            }
                            else continue;

                            if (TryParseHex(hex, out Color32 col))
                            {
                                ClampCoords(ref px, ref py);
                                if (allowOverwrite) pixelCanvas.DrawPixelImmediate(px, py, col);
                                else pixelCanvas.DrawPixelRespectExisting(px, py, col);
                                applied++;
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
                                if (!IsLineMostlyOffCanvas(x0, y0, x1, y1))
                                {
                                    if (allowOverwrite) pixelCanvas.DrawLineImmediate(x0, y0, x1, y1, lineCol);
                                    else pixelCanvas.DrawLineRespectExisting(x0, y0, x1, y1, lineCol);
                                    applied++;
                                }
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
                                if (rw <= 0 || rh <= 0) break;
                                if (rx < 0) rx = 0;
                                if (ry < 0) ry = 0;
                                if (rx >= pixelCanvas.width) rx = pixelCanvas.width - 1;
                                if (ry >= pixelCanvas.height) ry = pixelCanvas.height - 1;

                                if (allowOverwrite) pixelCanvas.DrawRectImmediate(rx, ry, rw, rh, rectCol);
                                else pixelCanvas.DrawRectRespectExisting(rx, ry, rw, rh, rectCol);
                                applied++;
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
                                ClampCoords(ref cx, ref cy);
                                r = Mathf.Clamp(r, 0, Math.Max(pixelCanvas.width, pixelCanvas.height));
                                if (allowOverwrite) pixelCanvas.DrawCircleImmediate(cx, cy, r, circleCol);
                                else pixelCanvas.DrawCircleRespectExisting(cx, cy, r, circleCol);
                                applied++;
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
                                ClampCoords(ref fx, ref fy);
                                if (allowOverwrite) pixelCanvas.FloodFillAt(fx, fy, fillCol);
                                else pixelCanvas.FloodFillRespectExisting(fx, fy, fillCol);
                                applied++;
                            }
                        }
                        break;

                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AIDrawController] Exception parsing/executing line: " + line + " -> " + ex);
            }
        }

        return applied;
    }

    // ------------------------------
    // Utilities / helpers
    // ------------------------------
    bool IsLineMostlyOffCanvas(int x0, int y0, int x1, int y1)
    {
        int w = pixelCanvas.width, h = pixelCanvas.height;
        int off = 0;
        if (x0 < 0 || x0 >= w || y0 < 0 || y0 >= h) off++;
        if (x1 < 0 || x1 >= w || y1 < 0 || y1 >= h) off++;
        return off == 2;
    }

    void ClampCoords(ref int x, ref int y)
    {
        x = Mathf.Clamp(x, 0, pixelCanvas.width - 1);
        y = Mathf.Clamp(y, 0, pixelCanvas.height - 1);
    }

    void DrawFallbackTreeCentered(int treeSize)
    {
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

    Color32 HexToColor32(string hex)
    {
        if (TryParseHex(hex, out Color32 c)) return c;
        return new Color32(0, 0, 0, 255);
    }

    string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "...(truncated)";
    }

    int CountUniqueHexColorsInCommands(string commandText)
    {
        if (string.IsNullOrEmpty(commandText)) return 0;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = Regex.Matches(commandText, "#[0-9A-Fa-f]{6}");
        foreach (Match m in matches) set.Add(m.Value.ToUpperInvariant());
        return set.Count;
    }

    // Build compact state (cropped RLE or small JSON) for prompt fallback
    string BuildCompactStateForPrompt()
    {
        if (pixelCanvas == null) return null;

        // Prefer cropped RLE if available
        if (pixelCanvas.GetNonBackgroundBoundingBox(out int xMin, out int yMin, out int xMax, out int yMax))
        {
            string cropped = pixelCanvas.ExportCroppedRLE(xMin, yMin, xMax, yMax, 400);
            if (!string.IsNullOrEmpty(cropped)) return $"CROP {xMin} {yMin} {xMax - xMin + 1} {yMax - yMin + 1}\n" + cropped;
        }

        // Fallback: small JSON thumbnail (compact)
        return pixelCanvas.ExportFullPixelListAsJson(includeBackground: false, useCropIfPossible: true, maxPixels: compactMaxPixels);
    }
}