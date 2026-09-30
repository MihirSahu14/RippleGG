// GameGold DialoguePlayer v6
// GameGold DialoguePlayer — plays a GameGold narrative dialogue JSON in Play mode.
// Setup: put this on any GameObject, save the dialogue JSON as
// Assets/Resources/GameGold/dialogue.json, backgrounds in Resources/GameGold/Backgrounds/<bg>,
// portraits in Resources/GameGold/Portraits/portrait_<speaker lowercase>, optional sound
// effects in Resources/GameGold/Sfx/<sfx>. Press Play. It builds its own UI (legacy uGUI Text).
// Format: { "variables": {..}, "start": "id", "nodes": [{ id, speaker, text, bg, chapter, sfx,
// expr, choices: [{ text, next, effects: {var: delta} }], branches: [{ when, next }], next, ending }] }
// Branch "when": "<var> [+ <var>...] <op> <int>" (op: < <= > >= ==) or "else".
// Optional Resources/GameGold/player_settings.json (written by GameGold's "Sync settings") overrides the
// Inspector: { look: plain|halftone|duotone, textSpeedCps, wordmarkTitle, ambience, volume,
// chapterColors: [{ chapter, color: "#rrggbb" }], twoCharacterStaging, characterSides: [{ speaker, side: left|right }],
// choiceRipple, originalBackgrounds: ["bg", ...] }.
// No file = plain look, no sound, Inspector values.
// Staging (v2): left/right portrait slots per scene (a new bg = new scene); the speaker is lit and forward.
// Keys (v2): Space/Enter/Right advance, hold Space/Ctrl to skip, 1-4 or Up/Down + Enter for choices, Esc pauses.
// Nameplate (v3): shown only for spoken dialogue (text starts with a quote mark); a speaker's unquoted line
// (inner thought) hides the tab and renders in italics — the portrait still stages/lights normally.
// End screen (v3): never names the ending (no choice is labeled good/bad) — only the final lines, then a
// quiet "Play again" (Space/Enter). Choice ripple (v3): one identical soft ring + water-drop cue after every
// choice, tinted by the chapter colour, gated by player_settings.json's choiceRipple.
// Original art (v4): backgrounds listed in originalBackgrounds are shown exactly as drawn — no print, no tint.
// Art cards (v5): a one-word ALL-CAPS line on an original-art background hides the textbox — the art is the card.
// Cover fit (v6): backgrounds keep their aspect ratio and fill the screen (edges trimmed), never squashed.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

public class DialoguePlayer : MonoBehaviour
{
    [Serializable]
    public class ChapterColor
    {
        public string chapter;
        public Color color = Color.white;
    }

    public enum Look { Plain, Halftone, Duotone }

    [Serializable]
    public class SpeakerSide
    {
        public string speaker;
        [Tooltip("left or right")] public string side = "left";
    }

    // Shape of player_settings.json (JsonUtility can't read dictionaries, so chapter colours are a list).
    [Serializable]
    class Settings
    {
        public string look = "plain";
        public float textSpeedCps = 40f;
        public bool wordmarkTitle;
        public bool ambience;
        public float volume = 0.5f;
        public List<ChapterHex> chapterColors = new List<ChapterHex>();
        public bool twoCharacterStaging = true;
        public List<SpeakerSide> characterSides = new List<SpeakerSide>();
        public bool choiceRipple = true;
        public List<string> originalBackgrounds = new List<string>();
    }

    [Serializable]
    class ChapterHex
    {
        public string chapter;
        public string color;
    }

    [Tooltip("Resources path of the dialogue JSON TextAsset, without extension")]
    public string dialoguePath = "GameGold/dialogue";
    [Tooltip("Resources path of GameGold's player settings JSON; overrides the fields below when present")]
    public string settingsPath = "GameGold/player_settings";
    public float charsPerSecond = 40f;
    [Tooltip("Tint per chapter id. Chapters not listed get a colour from the default palette.")]
    public List<ChapterColor> chapterColors = new List<ChapterColor>();
    public Color neutralTint = new Color(0.85f, 0.85f, 0.85f);
    [Tooltip("Halftone/Duotone re-print each background once on the CPU, inked in the chapter colour")]
    public Look look = Look.Plain;
    [Tooltip("A one-word ALL-CAPS line on the 'title' background shows as a big centred wordmark")]
    public bool wordmarkTitle;
    [Tooltip("Procedural ambience per chapter + typewriter blips (starts on the first click)")]
    public bool ambience;
    [Range(0f, 1f)] public float volume = 0.5f;
    [Tooltip("Left/right portrait slots with the speaker in focus; off = one portrait for the current speaker")]
    public bool twoCharacterStaging = true;
    [Tooltip("Fixed stage side per speaker; everyone else takes the free / opposite slot")]
    public List<SpeakerSide> characterSides = new List<SpeakerSide>();
    [Tooltip("A soft ring + water-drop cue after every choice, identical regardless of which one was picked")]
    public bool choiceRipple = true;
    [Tooltip("Backgrounds shown exactly as drawn (designer art): no halftone/duotone print, no chapter tint")]
    public List<string> originalBackgrounds = new List<string>();

    static readonly Color[] Palette =
    {
        new Color32(0x4e, 0xa8, 0xff, 255), new Color32(0xff, 0x52, 0x77, 255), new Color32(0xb4, 0x8c, 0xff, 255),
        new Color32(0x5e, 0xe0, 0xa0, 255), new Color32(0xff, 0xc8, 0x57, 255),
    };

    // Story state
    readonly Dictionary<string, Dictionary<string, object>> nodes = new Dictionary<string, Dictionary<string, object>>();
    readonly Dictionary<string, int> initialVars = new Dictionary<string, int>();
    readonly Dictionary<string, int> vars = new Dictionary<string, int>();
    readonly Dictionary<string, Color> autoChapterColors = new Dictionary<string, Color>();
    string startId;
    Dictionary<string, object> current;
    string fullText = "";
    float shown;
    bool typing, choosing, ended;
    int hops;
    Color tint;
    string currentBg;
    Sprite rawBackground;
    readonly Dictionary<string, Sprite> printed = new Dictionary<string, Sprite>();
    float wordmarkAlpha = -1f; // < 0: no wordmark showing

    // UI
    Font font;
    Image background, accent;
    AspectRatioFitter backgroundFit;
    Text nameText, bodyText, endTitle, endSubtitle, wordmark, hint, volumeText;
    GameObject textbox;
    RectTransform choiceBox;
    GameObject endPanel, pausePanel;
    AudioSource audioSource;
    readonly List<Image> choiceImages = new List<Image>();
    readonly List<Dictionary<string, object>> shownChoices = new List<Dictionary<string, object>>();
    Image[] pauseImages;
    int choiceFocus, pauseFocus;
    bool paused, hintDone;
    float prevTimeScale = 1f, holdTime, skipTimer;
    static readonly Color ButtonColor = new Color(0.08f, 0.1f, 0.15f, 0.95f);
    static readonly Color FocusColor = new Color(0.17f, 0.3f, 0.5f, 0.98f);

    void Start()
    {
        LoadSettings();
        font = LoadFont();
        audioSource = gameObject.AddComponent<AudioSource>();
        BuildUI();
        if (Load()) Restart();
    }

    void Update()
    {
        AnimateStage();
        HandleKeys();
        if (wordmarkAlpha >= 0f && wordmarkAlpha < 1f)
        {
            wordmarkAlpha = Mathf.Min(1f, wordmarkAlpha + Time.deltaTime / 1.5f);
            wordmark.color = new Color(1f, 1f, 1f, wordmarkAlpha);
        }
        if (!typing) return;
        shown += Time.deltaTime * Mathf.Max(1f, charsPerSecond);
        int n = Mathf.Min(fullText.Length, (int)shown);
        for (int i = bodyText.text.Length; i < n; i++) if (!char.IsWhiteSpace(fullText[i])) Blip(nameText.text);
        bodyText.text = fullText.Substring(0, n);
        if (n >= fullText.Length) typing = false;
    }

    void LoadSettings()
    {
        var asset = Resources.Load<TextAsset>(settingsPath);
        if (asset == null) return; // no file: keep the Inspector values (plain by default)
        Settings s;
        try { s = JsonUtility.FromJson<Settings>(asset.text); }
        catch (Exception e)
        {
            Debug.LogError($"[DialoguePlayer] player_settings.json is invalid: {e.Message}");
            return;
        }
        if (s == null) return;
        look = s.look == "halftone" ? Look.Halftone : s.look == "duotone" ? Look.Duotone : Look.Plain;
        charsPerSecond = Mathf.Clamp(s.textSpeedCps, 10f, 120f);
        wordmarkTitle = s.wordmarkTitle;
        ambience = s.ambience;
        volume = Mathf.Clamp01(s.volume);
        foreach (var c in s.chapterColors ?? new List<ChapterHex>())
        {
            if (string.IsNullOrEmpty(c.chapter) || !ColorUtility.TryParseHtmlString(c.color, out var color)) continue;
            chapterColors.RemoveAll(x => string.Equals(x.chapter, c.chapter, StringComparison.OrdinalIgnoreCase));
            chapterColors.Add(new ChapterColor { chapter = c.chapter, color = color });
        }
        twoCharacterStaging = s.twoCharacterStaging;
        if (s.characterSides != null) characterSides = s.characterSides;
        choiceRipple = s.choiceRipple;
        if (s.originalBackgrounds != null) originalBackgrounds = s.originalBackgrounds;
    }

    // ─── Story ────────────────────────────────────────────────────────────────

    bool Load()
    {
        var asset = Resources.Load<TextAsset>(dialoguePath);
        if (asset == null)
        {
            Debug.LogError($"[DialoguePlayer] No dialogue at Resources/{dialoguePath}.json");
            ShowEnd(null, "No dialogue found");
            return false;
        }
        Dictionary<string, object> root;
        try { root = MiniJson.Parse(asset.text) as Dictionary<string, object>; }
        catch (Exception e)
        {
            Debug.LogError($"[DialoguePlayer] Dialogue JSON is invalid: {e.Message}");
            root = null;
        }
        if (root == null)
        {
            ShowEnd(null, "Dialogue JSON is invalid");
            return false;
        }
        foreach (var o in List(root, "nodes"))
        {
            if (o is Dictionary<string, object> node && Str(node, "id") is string id)
            {
                nodes[id] = node;
                if (startId == null) startId = id;
            }
        }
        startId = Str(root, "start") ?? startId;
        foreach (var kv in Dict(root, "variables")) initialVars[kv.Key] = ToInt(kv.Value);
        return true;
    }

    public void Restart()
    {
        hops = 0;
        vars.Clear();
        foreach (var kv in initialVars) vars[kv.Key] = kv.Value;
        endPanel.SetActive(false);
        ended = false;
        holdTime = 0f;
        ResetStage();
        SetChapter(null);
        SetBackground(null);
        Go(startId);
    }

    void Go(string id)
    {
        ClearChoices();
        if (id == null) { ShowEnd(null, null); return; }
        if (++hops > 1000) { Debug.LogError("[DialoguePlayer] Too many silent hops — is there a branch loop?"); ShowEnd(null, null); return; }
        if (!nodes.TryGetValue(id, out current))
        {
            Debug.LogError($"[DialoguePlayer] Unknown node id '{id}'");
            ShowEnd(null, null);
            return;
        }

        if (Str(current, "chapter") is string chapter) SetChapter(chapter);
        if (Str(current, "bg") is string bg) SetBackground(bg);
        if (Str(current, "sfx") is string sfx)
        {
            var clip = Resources.Load<AudioClip>("GameGold/Sfx/" + sfx);
            if (clip != null) audioSource.PlayOneShot(clip);
        }

        fullText = Str(current, "text") ?? "";
        if (fullText.Length == 0) { Continue(); return; } // silent node: route straight on
        hops = 0;
        bool wordmarkLine = IsWordmark(fullText);
        if (wordmarkLine && IsOriginal(currentBg))
        {
            ShowWordmark(fullText, artCard: true);
            return;
        }
        if (wordmarkTitle && currentBg == "title" && wordmarkLine)
        {
            ShowWordmark(fullText);
            return;
        }
        textbox.SetActive(true);
        hint.gameObject.SetActive(!hintDone); // first line only
        hintDone = true;
        var speaker = Str(current, "speaker") ?? "";
        bool spoken = IsNarration(speaker) || IsQuoted(fullText); // narration is never a "thought"; only an unquoted speaker line is
        nameText.text = !IsNarration(speaker) && spoken ? speaker : ""; // hide the tab for unquoted (inner-thought) lines
        bodyText.fontStyle = !IsNarration(speaker) && !spoken ? FontStyle.Italic : FontStyle.Normal;
        SetPortrait(speaker, Str(current, "expr")); // portrait still stages/lights normally either way
        bodyText.text = "";
        shown = 0;
        typing = true;
    }

    // Spoken dialogue starts with a quote mark; anything else with a speaker is an inner thought (gap 54).
    static readonly char[] QuoteMarks = { '"', '“', '\'' };
    static bool IsQuoted(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.Length > 0 && Array.IndexOf(QuoteMarks, trimmed[0]) >= 0;
    }

    // After a line is fully read: choices, branches, next, or the ending.
    void Continue()
    {
        var choices = List(current, "choices");
        if (choices.Count > 0) { ShowChoices(choices); return; }
        var branches = List(current, "branches");
        if (branches.Count > 0)
        {
            foreach (var o in branches)
            {
                if (o is Dictionary<string, object> b && Eval(Str(b, "when")))
                {
                    Go(Str(b, "next"));
                    return;
                }
            }
            Debug.LogError($"[DialoguePlayer] No branch matched in node '{Str(current, "id")}' (add an \"else\")");
            ShowEnd(null, null);
            return;
        }
        if (Str(current, "next") is string next) { Go(next); return; }
        ShowEnd(Str(current, "ending"), null);
    }

    void OnClick()
    {
        StartAudio(); // WebGL only allows audio after a user gesture
        if (ended || choosing) return;
        if (wordmarkAlpha >= 0f && wordmarkAlpha < 1f)
        {
            wordmarkAlpha = 1f;
            wordmark.color = Color.white;
            return;
        }
        if (typing)
        {
            typing = false;
            bodyText.text = fullText;
            return;
        }
        Continue();
    }

    void Choose(Dictionary<string, object> choice)
    {
        StartAudio();
        foreach (var kv in Dict(choice, "effects"))
        {
            vars.TryGetValue(kv.Key, out var v);
            vars[kv.Key] = v + ToInt(kv.Value);
        }
        if (choiceRipple) PlayRippleCue(); // one identical cue regardless of choice/effects (gap 56) — never a meter
        Go(Str(choice, "next"));
    }

    // ─── Choice ripple cue: one soft ring + water-drop sound, identical every time ────────────

    Image rippleImage;
    Coroutine rippleRoutine;

    void PlayRippleCue()
    {
        audioSource.PlayOneShot(Clip("ripple"), volume); // respects the volume/mute setting, ambience or not
        if (rippleRoutine != null) StopCoroutine(rippleRoutine);
        rippleRoutine = StartCoroutine(RippleCue());
    }

    IEnumerator RippleCue()
    {
        const float duration = 0.8f;
        rippleImage.rectTransform.anchoredPosition = Vector2.zero; // screen centre, regardless of which button fired it
        for (float t = 0f; t < duration; t += Time.unscaledDeltaTime)
        {
            float k = Mathf.Clamp01(t / duration);
            rippleImage.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.35f, 1.6f, k);
            rippleImage.color = new Color(tint.r, tint.g, tint.b, Mathf.Lerp(0.3f, 0f, k));
            yield return null;
        }
        rippleImage.color = Color.clear;
        rippleRoutine = null;
    }

    Sprite ringSprite;

    // A soft, symmetric ring (transparent centre and edges): built once on the CPU, no shader.
    Sprite RingSprite()
    {
        if (ringSprite != null) return ringSprite;
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size - 0.5f, dy = (y + 0.5f) / size - 0.5f;
                float dist = Mathf.Sqrt(dx * dx + dy * dy) * 2f; // 0 centre .. 1 edge
                float ring = Mathf.Exp(-Mathf.Pow((dist - 0.55f) * 6f, 2f)); // soft band around r=0.55
                byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(ring) * 255f);
                px[y * size + x] = new Color32(255, 255, 255, a);
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return ringSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    bool Eval(string when)
    {
        if (string.IsNullOrWhiteSpace(when)) return false;
        when = when.Trim();
        if (when == "else") return true;
        foreach (var op in new[] { "<=", ">=", "==", "<", ">" })
        {
            int i = when.IndexOf(op, StringComparison.Ordinal);
            if (i < 0) continue;
            if (!int.TryParse(when.Substring(i + op.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rhs)) break;
            int sum = 0;
            foreach (var name in when.Substring(0, i).Split('+'))
            {
                if (!vars.TryGetValue(name.Trim(), out var v)) Debug.LogError($"[DialoguePlayer] Unknown variable '{name.Trim()}'");
                sum += v;
            }
            switch (op)
            {
                case "<=": return sum <= rhs;
                case ">=": return sum >= rhs;
                case "==": return sum == rhs;
                case "<": return sum < rhs;
                default: return sum > rhs;
            }
        }
        Debug.LogError($"[DialoguePlayer] Bad branch expression '{when}'");
        return false;
    }

    // ─── Presentation ─────────────────────────────────────────────────────────

    void SetChapter(string chapter)
    {
        tint = neutralTint;
        if (!string.IsNullOrEmpty(chapter))
        {
            var custom = chapterColors.Find(c => string.Equals(c.chapter, chapter, StringComparison.OrdinalIgnoreCase));
            if (custom != null) tint = custom.color;
            else
            {
                if (!autoChapterColors.ContainsKey(chapter)) autoChapterColors[chapter] = Palette[autoChapterColors.Count % Palette.Length];
                tint = autoChapterColors[chapter];
            }
        }
        accent.color = tint;
        nameText.color = tint;
        SetBed(BedFor(chapter));
        ApplyBackground(); // halftone/duotone ink follows the chapter colour
    }

    void SetBackground(string bg)
    {
        if (string.IsNullOrEmpty(bg)) bg = null;
        if (bg != "title") HideWordmark(); // the wordmark stays up until the title card leaves
        if (bg != currentBg) ResetStage(); // new background = new scene
        currentBg = bg;
        rawBackground = null;
        if (bg != null)
        {
            rawBackground = Resources.Load<Sprite>("GameGold/Backgrounds/" + bg);
            if (rawBackground == null) Debug.LogWarning($"[DialoguePlayer] Missing background Resources/GameGold/Backgrounds/{bg} — skipped");
        }
        ApplyBackground();
    }

    void ApplyBackground()
    {
        if (rawBackground != null)
            backgroundFit.aspectRatio = rawBackground.rect.width / Mathf.Max(1f, rawBackground.rect.height);
        if (rawBackground == null)
        {
            background.sprite = null;
            background.color = Color.Lerp(Color.black, tint, 0.2f);
        }
        else if (IsOriginal(currentBg))
        {
            background.sprite = rawBackground;
            background.color = Color.white;
        }
        else if (look == Look.Plain)
        {
            background.sprite = rawBackground;
            background.color = Color.Lerp(Color.white, tint, 0.15f);
        }
        else
        {
            background.sprite = Printed(currentBg, rawBackground);
            background.color = Color.white; // the tint is already in the ink
        }
    }

    bool IsOriginal(string bg) =>
        bg != null && originalBackgrounds.Exists(b => string.Equals(b, bg, StringComparison.OrdinalIgnoreCase));

    // "RIPPLE", "AVERY": one all-caps word.
    static bool IsWordmark(string line)
    {
        line = line.Trim();
        if (line.Length < 2) return false;
        foreach (var c in line) if (!char.IsUpper(c)) return false;
        return true;
    }

    // artCard: the designer's art already carries the title — show it alone, no overlay text.
    void ShowWordmark(string word, bool artCard = false)
    {
        textbox.SetActive(false);
        stage.gameObject.SetActive(false);
        if (artCard)
        {
            wordmarkAlpha = 1f; // nothing fading in: the next click advances
            wordmark.gameObject.SetActive(false);
            return;
        }
        wordmark.text = string.Join("  ", word.Trim().ToCharArray()); // legacy Text has no letter-spacing
        wordmarkAlpha = 0f;
        wordmark.color = Color.clear;
        wordmark.gameObject.SetActive(true);
    }

    void HideWordmark()
    {
        wordmarkAlpha = -1f;
        if (wordmark != null) wordmark.gameObject.SetActive(false);
    }

    // ─── Halftone / duotone (CPU, once per background + ink; no shader files, WebGL-safe) ──────

    static readonly Color Paper = new Color32(0xef, 0xe8, 0xdc, 255);

    // Shadow ink = a dark, desaturated version of the chapter colour.
    Color Ink()
    {
        Color.RGBToHSV(tint, out var h, out var s, out _);
        return Color.HSVToRGB(h, s * 0.7f, 0.2f);
    }

    Sprite Printed(string bg, Sprite source)
    {
        var ink = Ink();
        var key = $"{bg}|{look}|{ColorUtility.ToHtmlStringRGB(ink)}";
        if (printed.TryGetValue(key, out var cached)) return cached;
        var tex = ReadableCopy(source);
        Print(tex, look == Look.Halftone, ink);
        var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        printed[key] = sprite;
        return sprite;
    }

    // Imported sprites usually aren't CPU-readable: copy through a RenderTexture.
    static Texture2D ReadableCopy(Sprite sprite)
    {
        var src = sprite.texture;
        var r = sprite.textureRect;
        var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(src, rt);
        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D((int)r.width, (int)r.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(r.x, r.y, r.width, r.height), 0, 0);
        tex.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        return tex;
    }

    // Duotone: luminance mapped ink -> paper. Halftone: ink dots on paper, bigger where darker, on a 45 degree
    // grid of ~7px at 1080p. Both blend ~35% of the original back so shapes stay readable, plus grain + vignette.
    static void Print(Texture2D tex, bool dots, Color ink)
    {
        const float photoBlend = 0.35f, grain = 0.06f, vignette = 0.55f;
        int w = tex.width, h = tex.height;
        float cell = Mathf.Max(3f, 7f * h / 1080f);
        var px = tex.GetPixels32();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                Color c = px[i];
                float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                Color col;
                if (dots)
                {
                    float u = (x + y) * 0.7071f / cell, v = (y - x) * 0.7071f / cell;
                    float dx = u - Mathf.Floor(u) - 0.5f, dy = v - Mathf.Floor(v) - 0.5f;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float radius = Mathf.Sqrt(1f - lum) * 0.72f;
                    float coverage = Mathf.Clamp01((radius - dist) * cell + 0.5f); // ~1px antialiased edge
                    col = Color.Lerp(Paper, ink, coverage);
                }
                else col = Color.Lerp(ink, Paper, lum);
                var photo = Color.Lerp(Color.Lerp(ink, Paper, lum), c, 0.5f);
                col = Color.Lerp(col, photo, photoBlend);
                float noise = (Hash(x, y) - 0.5f) * grain;
                float vx = (float)x / w - 0.5f, vy = (float)y / h - 0.5f;
                float shade = 1f - vignette * (vx * vx + vy * vy) * 1.6f;
                px[i] = new Color(
                    Mathf.Clamp01((col.r + noise) * shade),
                    Mathf.Clamp01((col.g + noise) * shade),
                    Mathf.Clamp01((col.b + noise) * shade),
                    c.a);
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, true); // upload, then drop the CPU copy
    }

    static float Hash(int x, int y)
    {
        uint n = unchecked((uint)(x * 374761393 + y * 668265263));
        n = unchecked((n ^ (n >> 13)) * 1274126177u);
        return (n & 0xffff) / 65535f;
    }

    // ─── Ambience + typewriter (baked with AudioClip.Create: WebGL has no OnAudioFilterRead) ───

    const int Rate = 22050;
    const float LoopSeconds = 16f;
    static readonly string[] Beds = { "sea", "room", "pad" };
    readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
    readonly Dictionary<string, string> chapterBeds = new Dictionary<string, string>();
    AudioSource bedA, bedB, blipSource;
    string bed = "sea";
    bool audioStarted;
    int blipCount;
    float lowpass; // filter state for the noise voices (clips are baked one at a time)

    float Master => volume * 0.25f;

    // No chapter -> sea wash; chapters take sea / room tone / pad in order of first appearance.
    string BedFor(string chapter)
    {
        if (string.IsNullOrEmpty(chapter)) return "sea";
        if (!chapterBeds.TryGetValue(chapter, out var b)) chapterBeds[chapter] = b = Beds[chapterBeds.Count % Beds.Length];
        return b;
    }

    void StartAudio()
    {
        if (!ambience || audioStarted) return;
        audioStarted = true;
        bedA = LoopSource();
        bedB = LoopSource();
        blipSource = gameObject.AddComponent<AudioSource>();
        StartCoroutine(Crossfade(bed));
    }

    AudioSource LoopSource()
    {
        var s = gameObject.AddComponent<AudioSource>();
        s.loop = true;
        s.playOnAwake = false;
        return s;
    }

    void SetBed(string id)
    {
        if (id == bed) return;
        bed = id;
        if (audioStarted) StartCoroutine(Crossfade(id));
    }

    IEnumerator Crossfade(string id)
    {
        var incoming = bedB;
        bedB = bedA;
        bedA = incoming;
        bedA.clip = Clip(id);
        bedA.volume = 0f;
        bedA.Play();
        float from = bedB.volume;
        for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime / 2f)
        {
            if (bed != id) yield break; // a newer crossfade took over
            bedA.volume = t * Master;
            bedB.volume = (1f - t) * from;
            yield return null;
        }
        bedA.volume = Master;
        bedB.Stop();
    }

    // One soft click every 2 characters, pitched per speaker.
    void Blip(string speaker)
    {
        if (!audioStarted || ++blipCount % 2 != 0) return;
        bool narration = string.IsNullOrEmpty(speaker) || string.Equals(speaker, "narrator", StringComparison.OrdinalIgnoreCase);
        float pitch = narration ? 0.9f : 0.75f + (speaker.Length * 7 + speaker[0]) % 60 / 100f;
        blipSource.pitch = pitch * UnityEngine.Random.Range(0.94f, 1.06f);
        blipSource.PlayOneShot(Clip("blip"), Master * (narration ? 0.6f : 1.6f));
    }

    AudioClip Clip(string id)
    {
        if (clips.TryGetValue(id, out var clip)) return clip;
        float[] data;
        switch (id)
        {
            case "sea": data = Loop(t => Sea(t) + Drone(t)); break;
            case "room": data = Loop(t => Room() + 0.02f * Mathf.Sin(2f * Mathf.PI * 60f * t)); break;
            case "pad": data = Loop(t => Sea(t) * 0.5f + Pad(t)); break;
            case "ripple": data = Shot(0.5f, Drop); break; // water-drop cue after a choice
            default: data = Shot(0.025f, t => Mathf.Sin(2f * Mathf.PI * 1400f * t) * Mathf.Exp(-t * 260f)); break; // blip
        }
        clip = AudioClip.Create(id, data.Length, 1, Rate, false);
        clip.SetData(data, 0);
        return clips[id] = clip;
    }

    static float Noise() => UnityEngine.Random.value * 2f - 1f;

    float Sea(float t) // low-passed noise with a slow ~8 s swell
    {
        lowpass += (Noise() - lowpass) * 0.04f;
        float swell = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * t / 8f);
        return lowpass * 2.5f * (0.25f + 0.75f * swell * swell);
    }

    float Room()
    {
        lowpass += (Noise() - lowpass) * 0.01f;
        return lowpass * 3f;
    }

    static float Drone(float t) =>
        0.05f * Mathf.Sin(2f * Mathf.PI * 55f * t) + 0.03f * Mathf.Sin(2f * Mathf.PI * 82.5f * t) * (0.6f + 0.4f * Mathf.Sin(2f * Mathf.PI * t / 16f));

    // A single soft water-drop "plink": a quick downward pitch sweep plus a light noise tick, pre-rendered once.
    static float Drop(float t) =>
        Mathf.Sin(2f * Mathf.PI * (900f - 650f * Mathf.Min(1f, t * 4f)) * t) * Mathf.Exp(-t * 9f)
        + 0.15f * Noise() * Mathf.Exp(-t * 40f);

    static float Pad(float t) // Cmaj7, very soft
    {
        float s = 0f;
        foreach (var f in new[] { 130.81f, 164.81f, 196f, 246.94f }) s += Mathf.Sin(2f * Mathf.PI * f * t) + 0.3f * Mathf.Sin(4f * Mathf.PI * f * t);
        return s * 0.012f * (0.7f + 0.3f * Mathf.Sin(2f * Mathf.PI * t / 8f));
    }

    static float[] Shot(float seconds, Func<float, float> f)
    {
        var d = new float[(int)(seconds * Rate)];
        for (int i = 0; i < d.Length; i++) d[i] = f(i / (float)Rate);
        return d;
    }

    // Bakes LoopSeconds seamlessly: renders one extra second and crossfades it into the start.
    static float[] Loop(Func<float, float> f)
    {
        int n = (int)(LoopSeconds * Rate), fadeN = Rate;
        var d = new float[n];
        var tail = new float[fadeN];
        for (int i = 0; i < n + fadeN; i++)
        {
            float v = f(i / (float)Rate);
            if (i < n) d[i] = v; else tail[i - n] = v;
        }
        for (int i = 0; i < fadeN; i++)
        {
            float k = i / (float)fadeN;
            d[i] = d[i] * k + tail[i] * (1f - k);
        }
        return d;
    }

    void ShowChoices(List<object> choices)
    {
        choosing = true;
        foreach (var o in choices)
        {
            if (!(o is Dictionary<string, object> choice)) continue;
            int index = shownChoices.Count;
            var label = (index < 4 ? $"{index + 1}.  " : "") + (Str(choice, "text") ?? "…");
            var button = MakeButton(choiceBox, label, 30);
            button.onClick.AddListener(() => Choose(choice));
            var hover = button.gameObject.AddComponent<EventTrigger>(); // hovering moves the keyboard focus
            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ => FocusChoice(index));
            hover.triggers.Add(enter);
            shownChoices.Add(choice);
            choiceImages.Add(button.image);
        }
        FocusChoice(0);
    }

    void ClearChoices()
    {
        choosing = false;
        shownChoices.Clear();
        choiceImages.Clear();
        for (int i = choiceBox.childCount - 1; i >= 0; i--) Destroy(choiceBox.GetChild(i).gameObject);
    }

    void ShowEnd(string ending, string message)
    {
        ClearChoices();
        typing = false;
        ended = true;
        endTitle.text = message ?? "THE END";
        endSubtitle.text = ""; // "ending" (good/bad/neutral) is kept only for analytics — never shown (pillar 1, gap 55)
        endPanel.SetActive(true);
    }

    // ─── Stage: left/right portrait slots, speaker in focus ──────────────────

    class Slot
    {
        public RectTransform rect;
        public Image color, gray; // gray sits on top; its alpha = how desaturated
        public AspectRatioFitter fit;
        public float f, fFrom, fTo, a, aFrom, aTo, t = 1f; // f: 1 speaking, 0.5 narration, 0 listening; a: alpha
    }

    const float EaseSeconds = 0.2f;
    RectTransform stage;
    readonly Slot[] slots = new Slot[2];
    readonly string[] occupants = new string[2];
    readonly int[] lastSpoke = new int[2];
    readonly Dictionary<Sprite, Sprite> grays = new Dictionary<Sprite, Sprite>();

    static bool IsNarration(string speaker) =>
        string.IsNullOrEmpty(speaker) || string.Equals(speaker, "narrator", StringComparison.OrdinalIgnoreCase);

    // Which slot (0 left, 1 right) `speaker` stands in; updates slots/lastSpoke in place. Someone already on
    // stage stays put. A fixed side always wins (a non-fixed character standing there steps across if the other
    // slot is free). Anyone else takes a free slot (left first, i.e. opposite whoever is on stage), else replaces
    // the non-fixed occupant, else the least-recently-speaking one.
    public static int AssignSlot(string[] slots, int[] lastSpoke, string speaker, IDictionary<string, string> fixedSides)
    {
        int Side(string who) =>
            who != null && fixedSides != null && fixedSides.TryGetValue(who, out var s)
                ? (string.Equals(s, "right", StringComparison.OrdinalIgnoreCase) ? 1 : string.Equals(s, "left", StringComparison.OrdinalIgnoreCase) ? 0 : -1)
                : -1;
        bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        int slot = Same(slots[0], speaker) ? 0 : Same(slots[1], speaker) ? 1 : -1;
        if (slot < 0)
        {
            int side = Side(speaker);
            if (side >= 0)
            {
                var displaced = slots[side];
                if (displaced != null && Side(displaced) < 0 && slots[1 - side] == null)
                {
                    slots[1 - side] = displaced;
                    lastSpoke[1 - side] = lastSpoke[side];
                }
                slot = side;
            }
            else if (slots[0] == null || slots[1] == null) slot = slots[0] == null ? 0 : 1;
            else
            {
                bool fixed0 = Side(slots[0]) >= 0, fixed1 = Side(slots[1]) >= 0;
                slot = fixed0 != fixed1 ? (fixed0 ? 1 : 0) : (lastSpoke[0] <= lastSpoke[1] ? 0 : 1);
            }
            slots[slot] = speaker;
        }
        lastSpoke[slot] = Mathf.Max(lastSpoke[0], lastSpoke[1]) + 1;
        return slot;
    }

    Sprite LoadPortrait(string speaker, string expr)
    {
        if (IsNarration(speaker)) return null;
        var lower = speaker.ToLowerInvariant();
        var names = new List<string>();
        if (!string.IsNullOrEmpty(expr)) names.Add($"portrait_{lower}_{expr}");
        names.Add("portrait_" + lower);
        names.Add(speaker);
        foreach (var n in names)
        {
            var sprite = Resources.Load<Sprite>("GameGold/Portraits/" + n);
            if (sprite != null) return sprite;
        }
        return null;
    }

    void SetPortrait(string speaker, string expr)
    {
        stage.gameObject.SetActive(true);
        var sprite = LoadPortrait(speaker, expr);
        if (!twoCharacterStaging)
        {
            if (!string.Equals(occupants[0], speaker, StringComparison.OrdinalIgnoreCase)) slots[0].a = 0f; // new face fades in
            occupants[0] = sprite != null ? speaker : null;
            SetSlotSprite(0, sprite);
            Retarget(0, 1f, sprite != null ? 1f : 0f);
            return;
        }
        if (sprite == null) // narration, or a speaker with no portrait: the stage stays, everyone listens
        {
            for (int i = 0; i < 2; i++) Retarget(i, 0.5f, occupants[i] != null ? 1f : 0f);
            return;
        }
        var before = (string[])occupants.Clone();
        var beforeSprites = new[] { slots[0].color.sprite, slots[1].color.sprite };
        var sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in characterSides) if (s != null && !string.IsNullOrEmpty(s.speaker)) sides[s.speaker] = s.side;
        int slot = AssignSlot(occupants, lastSpoke, speaker, sides);
        for (int i = 0; i < 2; i++)
        {
            if (string.Equals(occupants[i], before[i], StringComparison.OrdinalIgnoreCase)) continue;
            slots[i].a = 0f; // someone new here: fade in
            SetSlotSprite(i, i == slot ? sprite : beforeSprites[1 - i]); // the only other change is a step across
        }
        SetSlotSprite(slot, sprite); // the expression may have changed
        Retarget(slot, 1f, 1f);
        Retarget(1 - slot, 0f, occupants[1 - slot] != null ? 1f : 0f);
        slots[slot].rect.SetAsLastSibling(); // speaker drawn in front
    }

    void ResetStage()
    {
        for (int i = 0; i < 2; i++)
        {
            occupants[i] = null;
            lastSpoke[i] = 0;
            if (slots[i] == null) continue;
            SetSlotSprite(i, null);
            slots[i].a = slots[i].aTo = 0f;
        }
    }

    void SetSlotSprite(int i, Sprite sprite)
    {
        var s = slots[i];
        s.color.sprite = sprite;
        s.gray.sprite = Gray(sprite);
        if (sprite != null) s.fit.aspectRatio = sprite.rect.width / Mathf.Max(1f, sprite.rect.height);
        s.rect.gameObject.SetActive(sprite != null);
    }

    void Retarget(int i, float f, float a)
    {
        var s = slots[i];
        s.fFrom = s.f;
        s.aFrom = s.a;
        s.fTo = f;
        s.aTo = a;
        s.t = 0f;
    }

    void AnimateStage()
    {
        for (int i = 0; i < 2; i++)
        {
            var s = slots[i];
            if (s == null) continue;
            s.t = Mathf.Min(1f, s.t + Time.unscaledDeltaTime / EaseSeconds);
            float e = Mathf.SmoothStep(0f, 1f, s.t);
            s.f = Mathf.Lerp(s.fFrom, s.fTo, e);
            s.a = Mathf.Lerp(s.aFrom, s.aTo, e);
            float bright = 0.45f + 0.55f * s.f, forward = Mathf.Max(0f, s.f * 2f - 1f); // narration (0.5): ~70%, not forward
            s.color.color = new Color(bright, bright, bright, s.a);
            s.gray.color = new Color(bright, bright, bright, s.a * 0.85f * (1f - s.f));
            s.rect.localScale = Vector3.one * (0.92f + 0.08f * s.f);
            s.rect.anchoredPosition = new Vector2((i == 0 ? 24f : -24f) * forward, 12f * forward);
        }
    }

    // Desaturated copy for the "listening" look (CPU, once per sprite).
    Sprite Gray(Sprite source)
    {
        if (source == null) return null;
        if (grays.TryGetValue(source, out var cached)) return cached;
        var tex = ReadableCopy(source);
        var px = tex.GetPixels32();
        for (int i = 0; i < px.Length; i++)
        {
            var c = px[i];
            byte l = (byte)(0.299f * c.r + 0.587f * c.g + 0.114f * c.b);
            px[i] = new Color32(l, l, l, c.a);
        }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return grays[source] = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
    }

    Slot MakeSlot(string name, float centerX)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(AspectRatioFitter));
        var rect = (RectTransform)go.transform;
        rect.SetParent(stage, false);
        rect.anchorMin = new Vector2(centerX, 0.3f); // stands on the textbox, 63% of screen height
        rect.anchorMax = new Vector2(centerX, 0.93f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        var fit = go.GetComponent<AspectRatioFitter>();
        fit.aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
        var slot = new Slot { rect = rect, fit = fit, color = MakeImage(rect, "Portrait", Vector2.zero, Vector2.one, Color.clear) };
        slot.gray = MakeImage(rect, "Listening", Vector2.zero, Vector2.one, Color.clear);
        slot.color.raycastTarget = slot.gray.raycastTarget = false;
        go.SetActive(false);
        return slot;
    }

    // ─── Keyboard (new Input System or legacy Input Manager, whichever the project uses) ───

    enum Btn { Space, Enter, Right, Left, Up, Down, W, S, Esc, Ctrl, D1, D2, D3, D4 }

#if ENABLE_INPUT_SYSTEM
    static bool Key(Btn b, bool held)
    {
        var kb = Keyboard.current;
        if (kb == null) return false;
        bool K(KeyControl k) => held ? k.isPressed : k.wasPressedThisFrame;
        switch (b)
        {
            case Btn.Space: return K(kb.spaceKey);
            case Btn.Enter: return K(kb.enterKey) || K(kb.numpadEnterKey);
            case Btn.Right: return K(kb.rightArrowKey);
            case Btn.Left: return K(kb.leftArrowKey);
            case Btn.Up: return K(kb.upArrowKey);
            case Btn.Down: return K(kb.downArrowKey);
            case Btn.W: return K(kb.wKey);
            case Btn.S: return K(kb.sKey);
            case Btn.Esc: return K(kb.escapeKey);
            case Btn.Ctrl: return K(kb.leftCtrlKey) || K(kb.rightCtrlKey);
            case Btn.D1: return K(kb.digit1Key) || K(kb.numpad1Key);
            case Btn.D2: return K(kb.digit2Key) || K(kb.numpad2Key);
            case Btn.D3: return K(kb.digit3Key) || K(kb.numpad3Key);
            default: return K(kb.digit4Key) || K(kb.numpad4Key);
        }
    }
#elif ENABLE_LEGACY_INPUT_MANAGER
    static bool Key(Btn b, bool held)
    {
        bool K(KeyCode k) => held ? Input.GetKey(k) : Input.GetKeyDown(k);
        switch (b)
        {
            case Btn.Space: return K(KeyCode.Space);
            case Btn.Enter: return K(KeyCode.Return) || K(KeyCode.KeypadEnter);
            case Btn.Right: return K(KeyCode.RightArrow);
            case Btn.Left: return K(KeyCode.LeftArrow);
            case Btn.Up: return K(KeyCode.UpArrow);
            case Btn.Down: return K(KeyCode.DownArrow);
            case Btn.W: return K(KeyCode.W);
            case Btn.S: return K(KeyCode.S);
            case Btn.Esc: return K(KeyCode.Escape);
            case Btn.Ctrl: return K(KeyCode.LeftControl) || K(KeyCode.RightControl);
            case Btn.D1: return K(KeyCode.Alpha1) || K(KeyCode.Keypad1);
            case Btn.D2: return K(KeyCode.Alpha2) || K(KeyCode.Keypad2);
            case Btn.D3: return K(KeyCode.Alpha3) || K(KeyCode.Keypad3);
            default: return K(KeyCode.Alpha4) || K(KeyCode.Keypad4);
        }
    }
#else
    static bool Key(Btn b, bool held) => false;
#endif

    static bool Pressed(Btn b) => Key(b, false);
    static bool Confirm => Pressed(Btn.Space) || Pressed(Btn.Enter);
    static int Move => Pressed(Btn.Up) || Pressed(Btn.W) ? -1 : Pressed(Btn.Down) || Pressed(Btn.S) ? 1 : 0;

    void HandleKeys()
    {
        // A clicked Button stays "selected" and the UI module would re-click it on Space/Enter: never keep one.
        var es = EventSystem.current;
        if (es != null && es.currentSelectedGameObject != null) es.SetSelectedGameObject(null);

        if (Pressed(Btn.Esc)) { SetPaused(!paused); return; }
        if (paused)
        {
            pauseFocus = (pauseFocus + Move + pauseImages.Length) % pauseImages.Length;
            if (pauseFocus == 2 && (Pressed(Btn.Left) || Pressed(Btn.Right))) SetVolume(volume + (Pressed(Btn.Left) ? -0.1f : 0.1f));
            Highlight(pauseImages, pauseFocus);
            if (Confirm && pauseFocus == 0) SetPaused(false);
            else if (Confirm && pauseFocus == 1) { SetPaused(false); Restart(); }
            return;
        }
        if (ended)
        {
            if (Confirm) Restart();
            return;
        }
        if (choosing)
        {
            for (int i = 0; i < 4 && i < shownChoices.Count; i++) if (Pressed(Btn.D1 + i)) { Choose(shownChoices[i]); return; }
            if (Confirm && choiceFocus < shownChoices.Count) { Choose(shownChoices[choiceFocus]); return; }
            if (Move != 0 && shownChoices.Count > 0) FocusChoice((choiceFocus + Move + shownChoices.Count) % shownChoices.Count);
            return;
        }
        if (Confirm || Pressed(Btn.Right)) OnClick();

        // Hold Space/Ctrl: fast-skip lines (stops at choices, never picks one).
        if (Key(Btn.Space, true) || Key(Btn.Ctrl, true))
        {
            holdTime += Time.unscaledDeltaTime;
            skipTimer -= Time.unscaledDeltaTime;
            if (holdTime > 0.4f && skipTimer <= 0f && !choosing && !ended)
            {
                skipTimer = 0.05f;
                OnClick();
            }
        }
        else holdTime = skipTimer = 0f;
    }

    void FocusChoice(int i)
    {
        choiceFocus = i;
        Highlight(choiceImages, i);
    }

    static void Highlight(IList<Image> images, int focus)
    {
        for (int i = 0; i < images.Count; i++) images[i].color = i == focus ? FocusColor : ButtonColor;
    }

    void SetPaused(bool on)
    {
        if (on == paused) return;
        paused = on;
        if (on)
        {
            prevTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            pauseFocus = 0;
            Highlight(pauseImages, 0);
        }
        else Time.timeScale = prevTimeScale;
        pausePanel.SetActive(on);
    }

    void SetVolume(float v)
    {
        volume = Mathf.Clamp01(Mathf.Round(v * 10f) / 10f);
        volumeText.text = $"Volume {Mathf.RoundToInt(volume * 100f)}%";
        if (audioStarted && bedA != null) bedA.volume = Master;
    }

    void OnDestroy()
    {
        if (paused) Time.timeScale = prevTimeScale;
    }

    // ─── UI construction (all in code) ────────────────────────────────────────

    void BuildUI()
    {
        if (FindAnyObjectByType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem", typeof(EventSystem));
            // Prefer the Input System's UI module (projects with the new Input System only would throw on
            // StandaloneInputModule); fall back to the legacy module when the package isn't installed.
            // Match the project's Active Input Handling so clicks work in both.
#if ENABLE_INPUT_SYSTEM
            var module = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
#else
            Type module = null;
#endif
            if (module != null) es.AddComponent(module);
            else es.AddComponent<StandaloneInputModule>();
        }

        var canvasGo = new GameObject("GameGold Dialogue UI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        var root = (RectTransform)canvasGo.transform;

        background = MakeImage(root, "Background", Vector2.zero, Vector2.one, Color.black);
        backgroundFit = background.gameObject.AddComponent<AspectRatioFitter>();
        backgroundFit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent; // cover: fill the screen, keep the shape
        backgroundFit.aspectRatio = 16f / 9f;
        var clickCatcher = MakeImage(root, "Click To Advance", Vector2.zero, Vector2.one, Color.clear);
        clickCatcher.gameObject.AddComponent<Button>().onClick.AddListener(OnClick);

        stage = new GameObject("Stage", typeof(RectTransform)).GetComponent<RectTransform>();
        stage.SetParent(root, false);
        Stretch(stage, Vector2.zero, Vector2.one);
        slots[0] = MakeSlot("Left", 0.2f); // staging off: the single portrait lives here
        slots[1] = MakeSlot("Right", 0.8f);

        wordmark = MakeText(root, "Wordmark", new Vector2(0.05f, 0.35f), new Vector2(0.95f, 0.65f), 120, FontStyle.Bold);
        wordmark.alignment = TextAnchor.MiddleCenter;
        wordmark.gameObject.SetActive(false);

        var box = MakeImage(root, "Textbox", new Vector2(0.04f, 0.03f), new Vector2(0.96f, 0.3f), new Color(0.04f, 0.05f, 0.08f, 0.88f));
        box.raycastTarget = false; // clicks fall through to the click catcher
        textbox = box.gameObject;
        accent = MakeImage((RectTransform)box.transform, "Accent", new Vector2(0f, 0.97f), Vector2.one, neutralTint);
        accent.raycastTarget = false;
        nameText = MakeText((RectTransform)box.transform, "Speaker", new Vector2(0.03f, 0.74f), new Vector2(0.97f, 0.94f), 34, FontStyle.Bold);
        bodyText = MakeText((RectTransform)box.transform, "Line", new Vector2(0.03f, 0.08f), new Vector2(0.97f, 0.74f), 32, FontStyle.Normal);
        hint = MakeText((RectTransform)box.transform, "Hint", new Vector2(0.5f, 0.02f), new Vector2(0.985f, 0.14f), 20, FontStyle.Normal);
        hint.alignment = TextAnchor.LowerRight;
        hint.color = new Color(1f, 1f, 1f, 0.35f);
        hint.text = "Space / Enter to continue  ·  1–4 to choose";
        hint.gameObject.SetActive(false);

        var choices = new GameObject("Choices", typeof(RectTransform), typeof(VerticalLayoutGroup));
        choiceBox = (RectTransform)choices.transform;
        choiceBox.SetParent(root, false);
        Stretch(choiceBox, new Vector2(0.2f, 0.33f), new Vector2(0.8f, 0.9f));
        var layout = choices.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 14;
        layout.childAlignment = TextAnchor.LowerCenter;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;

        rippleImage = MakeImage(root, "Ripple Cue", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Color.clear);
        rippleImage.raycastTarget = false;
        rippleImage.sprite = RingSprite();
        rippleImage.rectTransform.sizeDelta = new Vector2(320f, 320f);

        var end = MakeImage(root, "End", Vector2.zero, Vector2.one, new Color(0f, 0f, 0f, 0.85f));
        endPanel = end.gameObject;
        endTitle = MakeText((RectTransform)end.transform, "Title", new Vector2(0.1f, 0.55f), new Vector2(0.9f, 0.7f), 64, FontStyle.Bold);
        endTitle.alignment = TextAnchor.MiddleCenter;
        endSubtitle = MakeText((RectTransform)end.transform, "Subtitle", new Vector2(0.1f, 0.45f), new Vector2(0.9f, 0.55f), 36, FontStyle.Italic);
        endSubtitle.alignment = TextAnchor.MiddleCenter;
        var again = new GameObject("Play Again", typeof(RectTransform), typeof(VerticalLayoutGroup));
        var againRect = (RectTransform)again.transform;
        againRect.SetParent(end.transform, false);
        Stretch(againRect, new Vector2(0.4f, 0.28f), new Vector2(0.6f, 0.36f));
        again.GetComponent<VerticalLayoutGroup>().childControlHeight = true;
        MakeButton(againRect, "Play again", 32).onClick.AddListener(Restart);
        endPanel.SetActive(false);

        var pause = MakeImage(root, "Pause", Vector2.zero, Vector2.one, new Color(0f, 0f, 0f, 0.75f)); // blocks clicks
        pausePanel = pause.gameObject;
        var menu = new GameObject("Menu", typeof(RectTransform), typeof(VerticalLayoutGroup));
        var menuRect = (RectTransform)menu.transform;
        menuRect.SetParent(pause.transform, false);
        Stretch(menuRect, new Vector2(0.38f, 0.36f), new Vector2(0.62f, 0.64f));
        var menuLayout = menu.GetComponent<VerticalLayoutGroup>();
        menuLayout.spacing = 14;
        menuLayout.childAlignment = TextAnchor.MiddleCenter;
        menuLayout.childControlHeight = menuLayout.childControlWidth = true;
        menuLayout.childForceExpandHeight = false;
        var resume = MakeButton(menuRect, "Resume", 32);
        resume.onClick.AddListener(() => SetPaused(false));
        var restart = MakeButton(menuRect, "Restart", 32);
        restart.onClick.AddListener(() => { SetPaused(false); Restart(); });
        var row = new GameObject("Volume", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        var rowRect = (RectTransform)row.transform;
        rowRect.SetParent(menuRect, false);
        row.GetComponent<LayoutElement>().minHeight = 70;
        var rowLayout = row.GetComponent<HorizontalLayoutGroup>();
        rowLayout.spacing = 10;
        rowLayout.childControlHeight = rowLayout.childControlWidth = true;
        MakeButton(rowRect, "-", 32).onClick.AddListener(() => SetVolume(volume - 0.1f));
        var volumeButton = MakeButton(rowRect, "", 28);
        volumeButton.GetComponent<LayoutElement>().flexibleWidth = 3;
        volumeText = volumeButton.GetComponentInChildren<Text>();
        MakeButton(rowRect, "+", 32).onClick.AddListener(() => SetVolume(volume + 0.1f));
        pauseImages = new[] { resume.image, restart.image, volumeButton.image };
        SetVolume(volume);
        pausePanel.SetActive(false);
    }

    Button MakeButton(RectTransform parent, string label, int size)
    {
        var img = MakeImage(parent, "Choice", Vector2.zero, Vector2.one, ButtonColor);
        img.gameObject.AddComponent<LayoutElement>().minHeight = 70;
        var text = MakeText((RectTransform)img.transform, "Label", new Vector2(0.03f, 0f), new Vector2(0.97f, 1f), size, FontStyle.Normal);
        text.alignment = TextAnchor.MiddleCenter;
        text.text = label;
        var button = img.gameObject.AddComponent<Button>();
        var colors = button.colors;
        colors.highlightedColor = new Color(0.75f, 0.85f, 1f);
        button.colors = colors;
        return button;
    }

    Image MakeImage(RectTransform parent, string name, Vector2 min, Vector2 max, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        Stretch(rect, min, max);
        var img = go.GetComponent<Image>();
        img.color = color;
        return img;
    }

    Text MakeText(RectTransform parent, string name, Vector2 min, Vector2 max, int size, FontStyle style)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        Stretch(rect, min, max);
        var text = go.GetComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = Color.white;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        return text;
    }

    static void Stretch(RectTransform rect, Vector2 min, Vector2 max)
    {
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    static Font LoadFont()
    {
        // Unity 2022.2+ ships LegacyRuntime.ttf; older versions only Arial.ttf.
        try { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
        catch (Exception) { return Resources.GetBuiltinResource<Font>("Arial.ttf"); }
    }

    // ─── JSON helpers ─────────────────────────────────────────────────────────

    static string Str(Dictionary<string, object> d, string key) =>
        d != null && d.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;

    static List<object> List(Dictionary<string, object> d, string key) =>
        d != null && d.TryGetValue(key, out var v) && v is List<object> l ? l : new List<object>();

    static Dictionary<string, object> Dict(Dictionary<string, object> d, string key) =>
        d != null && d.TryGetValue(key, out var v) && v is Dictionary<string, object> o ? o : new Dictionary<string, object>();

    static int ToInt(object v) => v is double d ? (int)Math.Round(d) : 0;

    // Minimal JSON reader (JsonUtility can't read dictionaries like "effects").
    static class MiniJson
    {
        public static object Parse(string json)
        {
            int i = 0;
            var value = Value(json, ref i);
            Skip(json, ref i);
            if (i != json.Length) throw new FormatException($"Unexpected '{json[i]}' at {i}");
            return value;
        }

        static void Skip(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                var obj = new Dictionary<string, object>();
                i++;
                Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                while (true)
                {
                    Skip(s, ref i);
                    var key = String(s, ref i);
                    Skip(s, ref i);
                    Expect(s, ref i, ':');
                    obj[key] = Value(s, ref i);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return obj;
                }
            }
            if (c == '[')
            {
                var list = new List<object>();
                i++;
                Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value(s, ref i));
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return list;
                }
            }
            if (c == '"') return String(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException($"Unexpected '{c}' at {i}");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException($"Expected '{c}' at {i}");
            i++;
        }

        static string String(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            Expect(s, ref i, '"');
            return sb.ToString();
        }
    }
}
