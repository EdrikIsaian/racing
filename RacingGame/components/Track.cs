using System.Numerics;
using Raylib_cs;

class Track
{
    public List<Vector2> Points = new List<Vector2>
    {
        new Vector2(150, 120),
        new Vector2(650, 120),
        new Vector2(730, 220),
        new Vector2(650, 340),
        new Vector2(150, 340),
        new Vector2(70, 220)
    };

    public void Draw()
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Vector2 start = Points[i];
            Vector2 end = Points[(i+1) % Points.Count];

            Raylib.DrawLineEx(start, end, 40, Color.Gray);
        }

        foreach (Vector2 point in Points)
        {
            Raylib.DrawCircleV(point, 20, Color.Gray);
        }
    }
}