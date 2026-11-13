using System.Numerics;
using Raylib_cs;

enum Typeface
{
    Body,    // clean sans for labels and small text
    Bold,    // DIN, for numbers and values
    Display, // condensed DIN, for headlines
}

// Drawing helpers, fonts and the shared colour palette for menus and the HUD.
// Menus and the HUD are laid out for a 1280x720 screen and scaled up/down to fit any window size.
static class Ui
{
    public const int BaseWidth = 1280, BaseHeight = 720;

    public static float Scale => MathF.Min(Raylib.GetScreenWidth() / (float)BaseWidth, Raylib.GetScreenHeight() / (float)BaseHeight);

    // Size of the screen in UI units (at least 1280x720; wider screens get more width)
    public static int Width => (int)(Raylib.GetScreenWidth() / Scale);
    public static int Height => (int)(Raylib.GetScreenHeight() / Scale);

    static Camera2D current = new Camera2D(Vector2.Zero, Vector2.Zero, 0, 1);

    // HUD: stretches over the whole screen, anchored to the corners
    public static void BeginHud()
    {
        current = new Camera2D(Vector2.Zero, Vector2.Zero, 0, Scale);
        Raylib.BeginMode2D(current);
    }

    // Menus: a 1280x720 area centred on the screen
    public static void BeginMenu()
    {
        float s = Scale;
        Vector2 offset = new((Raylib.GetScreenWidth() - BaseWidth * s) / 2, (Raylib.GetScreenHeight() - BaseHeight * s) / 2);
        current = new Camera2D(offset, Vector2.Zero, 0, s);
        Raylib.BeginMode2D(current);
    }

    public static void End() => Raylib.EndMode2D();

    // Mouse position in UI units (matches the coordinates buttons are laid out in)
    public static Vector2 Mouse => Raylib.GetScreenToWorld2D(Raylib.GetMousePosition(), current);

    // ---- Palette: graphite surfaces, white type, one warm accent
    public static readonly Color Bg0 = new(7, 8, 11, 255);
    public static readonly Color Bg1 = new(15, 17, 23, 255);
    public static readonly Color Glass = new(11, 13, 18, 214);
    public static readonly Color GlassSolid = new(17, 19, 26, 248);
    public static readonly Color Stroke = new(255, 255, 255, 20);
    public static readonly Color StrokeStrong = new(255, 255, 255, 42);
    public static readonly Color TextColor = new(240, 242, 246, 255);
    public static readonly Color Muted = new(138, 146, 162, 255);
    public static readonly Color Dim = new(82, 89, 104, 255);
    public static readonly Color Accent = new(248, 190, 60, 255);
    public static readonly Color Good = new(62, 222, 140, 255);
    public static readonly Color Bad = new(255, 76, 76, 255);
    public static readonly Color Warn = new(255, 158, 54, 255);
    public static readonly Color Purple = new(178, 112, 255, 255); // personal / fastest lap, motorsport style

    // Legacy names still used in a few places
    public static Color Panel => Glass;
    public static Color PanelLight => GlassSolid;

    public static Color Alpha(Color c, float a) => new(c.R, c.G, c.B, (int)Math.Clamp(c.A * a, 0, 255));
    public static Color Lerp(Color a, Color b, float t) => new(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t), (int)(a.A + (b.A - a.A) * t));

    // ---- Fonts
    static readonly string[] BodyFonts = { "/System/Library/Fonts/SFNS.ttf", "/System/Library/Fonts/Supplemental/Arial.ttf", "C:/Windows/Fonts/segoeui.ttf", "C:/Windows/Fonts/arial.ttf" };
    static readonly string[] BoldFonts = { "/System/Library/Fonts/Supplemental/DIN Alternate Bold.ttf", "/System/Library/Fonts/Supplemental/Arial Bold.ttf", "C:/Windows/Fonts/bahnschrift.ttf", "C:/Windows/Fonts/arialbd.ttf" };
    static readonly string[] DisplayFonts = { "/System/Library/Fonts/Supplemental/DIN Condensed Bold.ttf", "/System/Library/Fonts/Supplemental/Arial Narrow Bold.ttf", "C:/Windows/Fonts/bahnschrift.ttf", "C:/Windows/Fonts/arialbd.ttf" };

    // Printable ASCII plus the few extras the UI uses (· — – ← →)
    static readonly int[] Codepoints = Enumerable.Range(32, 95).Concat(new[] { 0xB7, 0x2014, 0x2013, 0x2190, 0x2192 }).ToArray();

    static readonly Font[] fonts = new Font[3];
    static readonly bool[] loaded = new bool[3];

    public static void LoadFont()
    {
        Load(Typeface.Body, BodyFonts);
        Load(Typeface.Bold, BoldFonts);
        Load(Typeface.Display, DisplayFonts);
    }

    static void Load(Typeface face, string[] candidates)
    {
        string? path = candidates.FirstOrDefault(File.Exists);
        if (path == null)
        {
            fonts[(int)face] = Raylib.GetFontDefault();
            return;
        }
        // Load big and let the GPU scale it down smoothly (mipmaps + trilinear filtering)
        Font f = Raylib.LoadFontEx(path, 128, Codepoints, Codepoints.Length);
        Raylib.GenTextureMipmaps(ref f.Texture);
        Raylib.SetTextureFilter(f.Texture, TextureFilter.Trilinear);
        fonts[(int)face] = f;
        loaded[(int)face] = true;
    }

    public static void UnloadFont()
    {
        for (int i = 0; i < fonts.Length; i++)
            if (loaded[i]) Raylib.UnloadFont(fonts[i]);
    }

    static float Spacing(float size, Typeface face, float tracking) =>
        (loaded[(int)face] ? size * 0.01f : size / 10f) + tracking * size;

    public static void DrawText(string text, float x, float y, float size, Color color, Typeface face = Typeface.Bold, float tracking = 0)
    {
        Raylib.DrawTextEx(fonts[(int)face], text, new Vector2(MathF.Round(x), MathF.Round(y)), size, Spacing(size, face, tracking), color);
    }

    public static float MeasureText(string text, float size, Typeface face = Typeface.Bold, float tracking = 0)
    {
        return Raylib.MeasureTextEx(fonts[(int)face], text, size, Spacing(size, face, tracking)).X;
    }

    // Text with a soft drop shadow (for the HUD, where it sits over the world)
    public static void Text(string text, float x, float y, float size, Color color, Typeface face = Typeface.Bold, float tracking = 0)
    {
        float o = MathF.Max(1, size / 22f);
        DrawText(text, x, y + o, size, new Color(0, 0, 0, (int)(90 * color.A / 255f)), face, tracking);
        DrawText(text, x, y, size, color, face, tracking);
    }

    public static void TextCentered(string text, float centerX, float y, float size, Color color, Typeface face = Typeface.Bold, float tracking = 0)
    {
        DrawText(text, centerX - MeasureText(text, size, face, tracking) / 2, y, size, color, face, tracking);
    }

    public static void TextRight(string text, float rightX, float y, float size, Color color, Typeface face = Typeface.Bold, float tracking = 0)
    {
        DrawText(text, rightX - MeasureText(text, size, face, tracking), y, size, color, face, tracking);
    }

    // Small, widely tracked caps label — the "premium" texture for section headings
    public static void Label(string text, float x, float y, Color color, float size = 11)
        => DrawText(text.ToUpperInvariant(), x, y, size, color, Typeface.Body, 0.16f);

    public static void LabelRight(string text, float rightX, float y, Color color, float size = 11)
        => TextRight(text.ToUpperInvariant(), rightX, y, size, color, Typeface.Body, 0.16f);

    public static void LabelCentered(string text, float cx, float y, Color color, float size = 11)
        => TextCentered(text.ToUpperInvariant(), cx, y, size, color, Typeface.Body, 0.16f);

    public static string FormatTime(float seconds)
    {
        if (seconds <= 0) return "-:--.---";
        return $"{(int)(seconds / 60)}:{seconds % 60:00.000}";
    }

    // ---- Shapes

    // raylib's roundness is relative; this takes a corner radius in pixels instead
    public static float Roundness(Rectangle r, float radius) => Math.Clamp(radius * 2 / MathF.Min(r.Width, r.Height), 0f, 1f);

    public static void RoundRect(Rectangle r, float radius, Color c) =>
        Raylib.DrawRectangleRounded(r, Roundness(r, radius), 12, c);

    public static void RoundOutline(Rectangle r, float radius, float thick, Color c) =>
        Raylib.DrawRectangleRoundedLinesEx(r, Roundness(r, radius), 12, thick, c);

    public static Rectangle Inflate(Rectangle r, float by) => new(r.X - by, r.Y - by, r.Width + by * 2, r.Height + by * 2);

    // Soft shadow: a few stacked, growing, very transparent rounded rects
    public static void Shadow(Rectangle r, float radius, float strength = 1f)
    {
        for (int i = 1; i <= 4; i++)
        {
            Rectangle s = Inflate(r, i * 3);
            s.Y += 6;
            RoundRect(s, radius + i * 3, new Color(0, 0, 0, (int)(22 * strength)));
        }
    }

    // A frosted glass panel with a hairline border and a faint top highlight
    public static void GlassPanel(Rectangle r, float radius = 14, Color? fill = null, bool shadow = true)
    {
        if (shadow) Shadow(r, radius, 0.8f);
        RoundRect(r, radius, fill ?? Glass);
        // top sheen
        Rectangle sheen = new(r.X + 1, r.Y + 1, r.Width - 2, MathF.Min(r.Height * 0.5f, 40));
        Raylib.DrawRectangleGradientV((int)(sheen.X + radius * 0.6f), (int)sheen.Y, (int)(sheen.Width - radius * 1.2f), (int)sheen.Height,
            new Color(255, 255, 255, 10), new Color(255, 255, 255, 0));
        RoundOutline(r, radius, 1f, Stroke);
        Raylib.DrawLineEx(new Vector2(r.X + radius, r.Y + 0.5f), new Vector2(r.X + r.Width - radius, r.Y + 0.5f), 1f, new Color(255, 255, 255, 30));
    }

    // Additive radial glow
    public static void Glow(Vector2 center, float radius, Color color)
    {
        Raylib.BeginBlendMode(BlendMode.Additive);
        CircleGradient(center, radius, color, new Color(color.R, color.G, color.B, (byte)0));
        Raylib.EndBlendMode();
    }

    // raylib's own circles always use 36 sides, which shows as flat facets on big circles.
    // These pick the side count from the radius so large circles stay round.
    static int Segments(float radius) => Math.Clamp((int)(radius * 0.9f), 24, 200);

    public static void Circle(Vector2 c, float radius, Color color) =>
        Raylib.DrawCircleSector(c, radius, 0, 360, Segments(radius), color);

    public static void CircleLines(Vector2 c, float radius, Color color, float thick = 1) =>
        Raylib.DrawRing(c, radius - thick / 2, radius + thick / 2, 0, 360, Segments(radius), color);

    public static void CircleGradient(Vector2 c, float radius, Color inner, Color outer)
    {
        int n = Segments(radius);
        Rlgl.Begin(DrawMode.Triangles);
        for (int i = 0; i < n; i++)
        {
            float a0 = i * MathF.Tau / n, a1 = (i + 1) * MathF.Tau / n;
            Vector2 p0 = c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius;
            Vector2 p1 = c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius;
            // raylib skips triangles wound the "wrong" way, so emit the order it expects
            Rlgl.Color4ub(inner.R, inner.G, inner.B, inner.A); Rlgl.Vertex2f(c.X, c.Y);
            Rlgl.Color4ub(outer.R, outer.G, outer.B, outer.A); Rlgl.Vertex2f(p1.X, p1.Y);
            Rlgl.Color4ub(outer.R, outer.G, outer.B, outer.A); Rlgl.Vertex2f(p0.X, p0.Y);
        }
        Rlgl.End();
    }

    // A pill with text, e.g. status tags. Returns its width.
    public static float Pill(string text, float x, float y, Color bg, Color fg, float size = 11, float height = 22)
    {
        float w = MeasureText(text.ToUpperInvariant(), size, Typeface.Body, 0.16f) + height * 0.9f;
        RoundRect(new Rectangle(x, y, w, height), height / 2, bg);
        Label(text, x + height * 0.45f, y + (height - size) / 2 - 0.5f, fg, size);
        return w;
    }

    // A keyboard keycap followed by a description. Returns total width.
    public static float KeyHint(string key, string label, float x, float y, float alpha = 1f)
    {
        const float h = 22, size = 12;
        float kw = MathF.Max(h, MeasureText(key, size, Typeface.Bold) + 14);
        Rectangle cap = new(x, y, kw, h);
        RoundRect(new Rectangle(x, y + 2, kw, h), 5, new Color(0, 0, 0, (int)(90 * alpha)));
        RoundRect(cap, 5, new Color(38, 42, 52, (int)(235 * alpha)));
        RoundOutline(cap, 5, 1, new Color(255, 255, 255, (int)(30 * alpha)));
        TextCentered(key, x + kw / 2, y + (h - size) / 2 + 1, size, new Color(230, 232, 238, (int)(255 * alpha)), Typeface.Bold);
        float lw = MeasureText(label, 13, Typeface.Body);
        DrawText(label, x + kw + 8, y + (h - 13) / 2, 13, new Color(150, 157, 172, (int)(255 * alpha)), Typeface.Body);
        return kw + 8 + lw;
    }

    // Row of key hints centred on cx
    public static void KeyHints(float cx, float y, (string Key, string Label)[] hints, float alpha = 1f, float gap = 26)
    {
        float total = 0;
        foreach (var (k, l) in hints) total += HintWidth(k, l) + gap;
        total -= gap;
        float x = cx - total / 2;
        foreach (var (k, l) in hints) x += KeyHint(k, l, x, y, alpha) + gap;
    }

    public static void KeyHintsLeft(float x, float y, (string Key, string Label)[] hints, float alpha = 1f, float gap = 22)
    {
        foreach (var (k, l) in hints) x += KeyHint(k, l, x, y, alpha) + gap;
    }

    static float HintWidth(string key, string label) =>
        MathF.Max(22, MeasureText(key, 12, Typeface.Bold) + 14) + 8 + MeasureText(label, 13, Typeface.Body);

    // Darkened screen edges — cheap cinematic depth
    public static void Vignette(float strength = 1f)
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        int a = (int)(150 * strength);
        Color dark = new(0, 0, 0, a), clear = new(0, 0, 0, 0);
        int ev = h / 4, eh = w / 6;
        Raylib.DrawRectangleGradientV(0, 0, w, ev, dark, clear);
        Raylib.DrawRectangleGradientV(0, h - ev, w, ev, clear, dark);
        Raylib.DrawRectangleGradientH(0, 0, eh, h, dark, clear);
        Raylib.DrawRectangleGradientH(w - eh, 0, eh, h, clear, dark);
    }

    public static void Overlay(byte alpha = 150)
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        Raylib.DrawRectangle(0, 0, w, h, new Color(5, 6, 9, (int)alpha));
        Vignette(1.1f);
    }

    // Chevron arrow (direction -1 = left, 1 = right)
    public static void Chevron(Vector2 c, float size, int dir, Color color, float thick = 2.2f)
    {
        Vector2 tip = c + new Vector2(dir * size * 0.3f, 0);
        Vector2 a = c + new Vector2(-dir * size * 0.3f, -size * 0.55f);
        Vector2 b = c + new Vector2(-dir * size * 0.3f, size * 0.55f);
        Raylib.DrawLineEx(a, tip, thick, color);
        Raylib.DrawLineEx(tip, b, thick, color);
        Raylib.DrawCircleV(tip, thick / 2, color);
    }

    // Segmented rating bar (like a car configurator)
    public static void SegmentBar(float x, float y, float width, float ratio, Color fill, int segments = 12, float height = 6)
    {
        float gap = 3;
        float sw = (width - gap * (segments - 1)) / segments;
        float filled = Math.Clamp(ratio, 0, 1) * segments;
        for (int i = 0; i < segments; i++)
        {
            Rectangle seg = new(x + i * (sw + gap), y, sw, height);
            Raylib.DrawRectangleRec(seg, new Color(255, 255, 255, 18));
            float f = Math.Clamp(filled - i, 0, 1);
            if (f > 0)
            {
                seg.Width *= f;
                // brighter towards the end of the bar
                float t = i / (float)(segments - 1);
                Raylib.DrawRectangleRec(seg, Alpha(fill, 0.55f + 0.45f * t));
            }
        }
    }

    // Smooth 0..1 animation helper (frame-rate independent)
    public static float Approach(float value, float target, float speed) =>
        value + (target - value) * (1 - MathF.Exp(-speed * Raylib.GetFrameTime()));
}
