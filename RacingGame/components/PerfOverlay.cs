using System.Numerics;
using Raylib_cs;

// F3: frame-time readout, to check for lag spikes.
// Shows the average FPS and the slowest frame of the last second, plus a graph of recent frames.
static class PerfOverlay
{
    const int History = 240;
    static readonly float[] frames = new float[History];
    static int next;
    static bool visible;

    static float windowTime, windowWorst, shownWorst, shownFps;
    static int windowFrames;
    static string text = "";

    public static void Toggle() => visible = !visible;

    public static void Record(float dt)
    {
        frames[next] = dt;
        next = (next + 1) % History;

        windowTime += dt;
        windowFrames++;
        windowWorst = MathF.Max(windowWorst, dt);
        if (windowTime >= 1f)
        {
            shownFps = windowFrames / windowTime;
            shownWorst = windowWorst;
            windowTime = windowWorst = 0;
            windowFrames = 0;
            text = $"{shownFps:F0} FPS   worst {shownWorst * 1000:F1} ms";
        }
    }

    public static void Draw()
    {
        if (!visible) return;
        Ui.BeginHud();
        const float w = 240, h = 60;
        float x = Ui.Width / 2f - w / 2, y = Ui.Height - 190;
        Raylib.DrawRectangleRec(new Rectangle(x, y, w, h + 28), new Color(0, 0, 0, 170));
        Ui.DrawText(text, x + 8, y + 6, 14, Color.White, Typeface.Bold);

        // 1 px per frame; the line marks 16.7 ms (60 FPS), bars above it are dropped frames
        float scale = h / 0.05f; // 50 ms fills the graph
        float baseY = y + 24 + h;
        for (int i = 0; i < History; i++)
        {
            float dt = frames[(next + i) % History];
            float bh = MathF.Min(dt * scale, h);
            Color c = dt > 1 / 50f ? Ui.Bad : dt > 1 / 70f ? Ui.Warn : Ui.Good;
            Raylib.DrawRectangleRec(new Rectangle(x + i * (w / History), baseY - bh, w / History, bh), c);
        }
        float line = baseY - scale / 60f;
        Raylib.DrawLineEx(new Vector2(x, line), new Vector2(x + w, line), 1, new Color(255, 255, 255, 120));
        Ui.End();
    }
}
