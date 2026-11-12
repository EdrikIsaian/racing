using Raylib_cs;

class Button
{
    public Rectangle Bounds;
    public string Label;
    public Action OnClick;

    public Button(float x, float y, float width, float height, string label, Action onClick)
    {
        Bounds = new Rectangle(x, y, width, height);
        Label = label;
        OnClick = onClick;
    }

    public void Update()
    {
        bool hovered = Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), Bounds);
        if (hovered && Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            OnClick();
        }
    }

    public void Draw()
    {
        bool hovered = Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), Bounds);
        Color color = hovered ? Color.SkyBlue : Color.Blue;

        Raylib.DrawRectangleRec(Bounds, color);
        Raylib.DrawRectangleLinesEx(Bounds, 2, Color.White);

        int textWidth = Raylib.MeasureText(Label, 20);
        int textX = (int)(Bounds.X + (Bounds.Width - textWidth) / 2);
        int textY = (int)(Bounds.Y + (Bounds.Height - 20) / 2);
        Raylib.DrawText(Label, textX, textY, 20, Color.White);
    }
}