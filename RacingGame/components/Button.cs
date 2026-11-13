using System.Numerics;
using Raylib_cs;

enum ButtonStyle
{
    Primary,   // accent fill, the one call-to-action on a screen
    Secondary, // glass fill with a hairline border
    Ghost,     // text only until hovered
    Icon,      // round chevron button
}

class Button
{
    public Rectangle Bounds;
    public string Label;
    public Action OnClick;
    public int FontSize;
    public ButtonStyle Style;
    public int IconDir; // for ButtonStyle.Icon: -1 left, 1 right

    float hover;  // 0..1, animated
    float press;  // 0..1, animated

    public Button(float x, float y, float width, float height, string label, Action onClick, int fontSize = 18, ButtonStyle style = ButtonStyle.Secondary)
    {
        Bounds = new Rectangle(x, y, width, height);
        Label = label;
        OnClick = onClick;
        FontSize = fontSize;
        Style = style;
    }

    public static Button Arrow(float cx, float cy, float size, int dir, Action onClick) =>
        new(cx - size / 2, cy - size / 2, size, size, "", onClick, 0, ButtonStyle.Icon) { IconDir = dir };

    bool Hovered => Raylib.CheckCollisionPointRec(Ui.Mouse, Bounds);

    public void Update()
    {
        if (Hovered && Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            press = 1;
            OnClick();
        }
    }

    public void Draw()
    {
        bool hovered = Hovered;
        hover = Ui.Approach(hover, hovered ? 1 : 0, 14);
        press = Ui.Approach(press, 0, 10);

        // Slight press-in
        Rectangle r = Ui.Inflate(Bounds, -press * 1.5f);
        float radius = Style == ButtonStyle.Icon ? r.Height / 2 : MathF.Min(10, r.Height / 2);

        switch (Style)
        {
            case ButtonStyle.Primary:
            {
                // glow grows on hover
                for (int i = 1; i <= 4; i++)
                    Ui.RoundRect(Ui.Inflate(r, i * (2 + 2 * hover)), radius + i * 3, Ui.Alpha(Ui.Accent, (0.05f + 0.06f * hover)));
                Color top = Ui.Lerp(Ui.Accent, new Color(255, 222, 130, 255), 0.35f + 0.35f * hover);
                Ui.RoundRect(r, radius, Ui.Accent);
                // upper half sheen
                Rectangle upper = new(r.X, r.Y, r.Width, r.Height * 0.55f);
                Raylib.DrawRectangleRounded(upper, Ui.Roundness(upper, radius), 12, Ui.Alpha(top, 0.55f));
                Ui.RoundOutline(r, radius, 1, new Color(255, 236, 180, 120));
                DrawLabel(r, new Color(24, 18, 6, 255), true);
                break;
            }
            case ButtonStyle.Secondary:
            {
                Ui.RoundRect(r, radius, Ui.Lerp(new Color(255, 255, 255, 10), new Color(255, 255, 255, 26), hover));
                Ui.RoundOutline(r, radius, 1, Ui.Lerp(Ui.StrokeStrong, new Color(255, 255, 255, 90), hover));
                DrawLabel(r, Ui.TextColor, false);
                break;
            }
            case ButtonStyle.Ghost:
            {
                if (hover > 0.01f) Ui.RoundRect(r, radius, new Color(255, 255, 255, (int)(18 * hover)));
                DrawLabel(r, Ui.Lerp(Ui.Muted, Ui.TextColor, hover), false);
                break;
            }
            case ButtonStyle.Icon:
            {
                Vector2 c = new(r.X + r.Width / 2, r.Y + r.Height / 2);
                Ui.Circle(c, r.Width / 2, Ui.Lerp(new Color(255, 255, 255, 8), new Color(255, 255, 255, 30), hover));
                Ui.CircleLines(c, r.Width / 2, Ui.Lerp(Ui.StrokeStrong, Ui.Alpha(Ui.Accent, 0.9f), hover));
                Ui.Chevron(c + new Vector2(IconDir * hover * 1.5f, 0), r.Width * 0.32f, IconDir, Ui.Lerp(Ui.Muted, Ui.TextColor, hover), MathF.Max(1.6f, r.Width / 22f));
                break;
            }
        }
    }

    void DrawLabel(Rectangle r, Color color, bool primary)
    {
        float size = FontSize;
        float tracking = 0.08f;
        float tw = Ui.MeasureText(Label, size, Typeface.Display, tracking);
        // DIN Condensed sits a little high in its box; nudge it down to look optically centred
        float y = r.Y + (r.Height - size) / 2 + size * 0.1f;
        Ui.DrawText(Label, r.X + (r.Width - tw) / 2, y, size, color, Typeface.Display, tracking);
    }
}
