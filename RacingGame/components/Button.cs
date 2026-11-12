using Raylib_cs;

class Button
{
    public Rectangle Bounds;
    public string Label;
    public Action OnClick;
    public int FontSize;
    public bool Primary; // highlighted call-to-action style

    public Button(float x, float y, float width, float height, string label, Action onClick, int fontSize = 20, bool primary = false)
    {
        Bounds = new Rectangle(x, y, width, height);
        Label = label;
        OnClick = onClick;
        FontSize = fontSize;
        Primary = primary;
    }

    bool Hovered => Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), Bounds);

    public void Update()
    {
        if (Hovered && Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            OnClick();
        }
    }

    public void Draw()
    {
        bool hovered = Hovered;
        Color fill = Primary
            ? (hovered ? new Color(255, 214, 70, 255) : Ui.Accent)
            : (hovered ? new Color(58, 68, 98, 255) : Ui.PanelLight);
        Color text = Primary ? new Color(20, 20, 20, 255) : Color.White;

        Raylib.DrawRectangleRounded(Bounds, 0.25f, 8, fill);
        if (!Primary) Raylib.DrawRectangleRoundedLinesEx(Bounds, 0.25f, 8, 2, hovered ? Ui.Accent : new Color(80, 90, 120, 255));

        int textWidth = Raylib.MeasureText(Label, FontSize);
        int textX = (int)(Bounds.X + (Bounds.Width - textWidth) / 2);
        int textY = (int)(Bounds.Y + (Bounds.Height - FontSize) / 2);
        Raylib.DrawText(Label, textX, textY, FontSize, text);
    }
}
