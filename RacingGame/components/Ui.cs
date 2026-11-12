using System.Numerics;
using Raylib_cs;

// Small drawing helpers and shared colours for menus and the HUD.
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

    public static readonly Color Panel = new Color(14, 17, 26, 210);
    public static readonly Color PanelLight = new Color(32, 38, 56, 230);
    public static readonly Color Accent = new Color(255, 196, 0, 255);
    public static readonly Color Muted = new Color(160, 168, 185, 255);
    public static readonly Color Good = new Color(90, 220, 120, 255);
    public static readonly Color Bad = new Color(240, 80, 70, 255);

    // ---- Font: a smooth TrueType font instead of raylib's small pixel font
    static readonly string[] FontCandidates =
    {
        "/System/Library/Fonts/Supplemental/DIN Alternate Bold.ttf",
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
        "C:/Windows/Fonts/arialbd.ttf",
    };
    static Font font;
    static bool customFont;

    public static void LoadFont()
    {
        string? path = FontCandidates.FirstOrDefault(File.Exists);
        if (path == null)
        {
            font = Raylib.GetFontDefault();
            return;
        }
        // Load big and let the GPU scale it down smoothly (mipmaps + trilinear filtering)
        font = Raylib.LoadFontEx(path, 128, null, 0);
        Raylib.GenTextureMipmaps(ref font.Texture);
        Raylib.SetTextureFilter(font.Texture, TextureFilter.Trilinear);
        customFont = true;
    }

    public static void UnloadFont()
    {
        if (customFont) Raylib.UnloadFont(font);
    }

    static float Spacing(int size) => customFont ? size * 0.02f : size / 10f;

    public static void DrawText(string text, int x, int y, int size, Color color)
    {
        Raylib.DrawTextEx(font, text, new Vector2(x, y), size, Spacing(size), color);
    }

    public static int MeasureText(string text, int size)
    {
        return (int)Raylib.MeasureTextEx(font, text, size, Spacing(size)).X;
    }

    // Text with a soft drop shadow
    public static void Text(string text, int x, int y, int size, Color color)
    {
        int offset = Math.Max(1, size / 18);
        DrawText(text, x + offset, y + offset, size, new Color(0, 0, 0, 120));
        DrawText(text, x, y, size, color);
    }

    public static void TextCentered(string text, int centerX, int y, int size, Color color)
    {
        Text(text, centerX - MeasureText(text, size) / 2, y, size, color);
    }

    public static void TextRight(string text, int rightX, int y, int size, Color color)
    {
        Text(text, rightX - MeasureText(text, size), y, size, color);
    }

    public static string FormatTime(float seconds)
    {
        if (seconds <= 0) return "--:--.---";
        return $"{(int)(seconds / 60)}:{seconds % 60:00.000}";
    }

    public static void Overlay(byte alpha = 150)
    {
        Raylib.DrawRectangle(0, 0, Raylib.GetScreenWidth(), Raylib.GetScreenHeight(), new Color(0, 0, 0, (int)alpha));
    }
}
