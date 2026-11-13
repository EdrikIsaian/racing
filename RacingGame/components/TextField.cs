using System.Numerics;
using Raylib_cs;

// A one-line text box (player name, host address). Click to type, Enter or click away to finish.
class TextField
{
    public Rectangle Bounds;
    public string Text;
    public string Placeholder;
    public int MaxLength;
    public Func<char, bool> Allow;
    public Action? OnSubmit;

    static TextField? focused;
    // While someone is typing, single-key shortcuts (F, M, T…) are off
    public static bool AnyFocused => focused != null;
    public bool Focused => focused == this;

    float hover;

    public TextField(Rectangle bounds, string text, string placeholder, int maxLength, Func<char, bool> allow)
    {
        Bounds = bounds;
        Text = text;
        Placeholder = placeholder;
        MaxLength = maxLength;
        Allow = allow;
    }

    public static void Blur() => focused = null;

    public void Update()
    {
        if (Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            if (Raylib.CheckCollisionPointRec(Ui.Mouse, Bounds)) focused = this;
            else if (focused == this) focused = null;
        }
        if (focused != this) return;

        for (int c = Raylib.GetCharPressed(); c > 0; c = Raylib.GetCharPressed())
            if (Text.Length < MaxLength && c < 0x10000 && Allow((char)c)) Text += (char)c;
        if ((Raylib.IsKeyPressed(KeyboardKey.Backspace) || Raylib.IsKeyPressedRepeat(KeyboardKey.Backspace)) && Text.Length > 0)
            Text = Text[..^1];
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter))
        {
            focused = null;
            OnSubmit?.Invoke();
        }
        else if (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.Tab))
            focused = null;
    }

    public void Draw()
    {
        bool on = Focused;
        hover = Ui.Approach(hover, Raylib.CheckCollisionPointRec(Ui.Mouse, Bounds) || on ? 1 : 0, 14);
        Ui.RoundRect(Bounds, 10, Ui.Lerp(new Color(255, 255, 255, 8), new Color(255, 255, 255, 16), hover));
        Ui.RoundOutline(Bounds, 10, 1, on ? Ui.Alpha(Ui.Accent, 0.85f) : Ui.Lerp(Ui.StrokeStrong, new Color(255, 255, 255, 70), hover));

        const float size = 20;
        float x = Bounds.X + 16, y = Bounds.Y + (Bounds.Height - size) / 2;
        if (Text.Length == 0 && !on) Ui.DrawText(Placeholder, x, y, size, Ui.Dim, Typeface.Body);
        else Ui.DrawText(Text, x, y, size, Ui.TextColor, Typeface.Body);

        if (on && (Raylib.GetTime() % 1.0) < 0.55)
        {
            float cx = x + Ui.MeasureText(Text, size, Typeface.Body) + 2;
            Raylib.DrawRectangleRec(new Rectangle(cx, y + 1, 2, size), Ui.Accent);
        }
    }
}
