using Raylib_cs;

// Small drawing helpers and shared colours for menus and the HUD.
static class Ui
{
    public static readonly Color Panel = new Color(14, 17, 26, 210);
    public static readonly Color PanelLight = new Color(32, 38, 56, 230);
    public static readonly Color Accent = new Color(255, 196, 0, 255);
    public static readonly Color Muted = new Color(160, 168, 185, 255);
    public static readonly Color Good = new Color(90, 220, 120, 255);
    public static readonly Color Bad = new Color(240, 80, 70, 255);

    public static void Text(string text, int x, int y, int size, Color color)
    {
        Raylib.DrawText(text, x + 2, y + 2, size, new Color(0, 0, 0, 120));
        Raylib.DrawText(text, x, y, size, color);
    }

    public static void TextCentered(string text, int centerX, int y, int size, Color color)
    {
        Text(text, centerX - Raylib.MeasureText(text, size) / 2, y, size, color);
    }

    public static void TextRight(string text, int rightX, int y, int size, Color color)
    {
        Text(text, rightX - Raylib.MeasureText(text, size), y, size, color);
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
