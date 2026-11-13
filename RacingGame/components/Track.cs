using System.Numerics;
using Raylib_cs;

// A closed circuit built from a few control points, smoothed with a Catmull-Rom spline
// and resampled into evenly spaced points along the centre line.
class Track
{
    public const float Width = 130f;
    public const float Spacing = 20f; // distance between centre-line samples

    const float Scale = 1.3f;
    static readonly Vector2[] ControlPoints =
    {
        new(900, 300),   // start / finish (top straight)
        new(1700, 300),
        new(2400, 380),
        new(2850, 700),
        new(2850, 1150),
        new(2450, 1400),
        new(1950, 1250),
        new(1550, 1450),
        new(1650, 1850),
        new(1250, 2100),
        new(600, 2000),
        new(300, 1500),
        new(350, 800),
        new(500, 400),
    };

    public Vector2[] Points = Array.Empty<Vector2>();
    float[] curvature = Array.Empty<float>();
    Vector2[] joinNormals = Array.Empty<Vector2>(); // average of the two segment normals at each point, so bands join without gaps
    bool[] isCorner = Array.Empty<bool>();
    public Vector2 BoundsMin, BoundsMax;
    readonly List<(Vector2 Pos, float Size, float Shade)> trees = new();

    public int Count => Points.Length;
    public float Length => Count * Spacing;

    public Track()
    {
        Build();
        PlantTrees();
    }

    void Build()
    {
        // 1. Densely sample the spline
        var dense = new List<Vector2>();
        int n = ControlPoints.Length;
        for (int i = 0; i < n; i++)
        {
            Vector2 p0 = ControlPoints[(i - 1 + n) % n] * Scale;
            Vector2 p1 = ControlPoints[i] * Scale;
            Vector2 p2 = ControlPoints[(i + 1) % n] * Scale;
            Vector2 p3 = ControlPoints[(i + 2) % n] * Scale;
            for (int s = 0; s < 60; s++)
                dense.Add(CatmullRom(p0, p1, p2, p3, s / 60f));
        }

        // 2. Resample so points are exactly `Spacing` apart
        var pts = new List<Vector2> { dense[0] };
        float sinceLast = 0;
        for (int i = 1; i <= dense.Count; i++)
        {
            Vector2 a = dense[i - 1], b = dense[i % dense.Count];
            float segLen = Vector2.Distance(a, b);
            float pos = 0;
            while (sinceLast + (segLen - pos) >= Spacing)
            {
                pos += Spacing - sinceLast;
                pts.Add(Vector2.Lerp(a, b, pos / segLen));
                sinceLast = 0;
            }
            sinceLast += segLen - pos;
        }
        if (Vector2.Distance(pts[^1], pts[0]) < Spacing * 0.5f) pts.RemoveAt(pts.Count - 1);
        Points = pts.ToArray();

        // 3. Curvature (how sharp the bend is) at each point — used by the AI and for curbs
        curvature = new float[Count];
        for (int i = 0; i < Count; i++)
        {
            Vector2 a = PointAt(i) - PointAt(i - 3);
            Vector2 b = PointAt(i + 3) - PointAt(i);
            float angle = MathF.Abs(WrapAngle(MathF.Atan2(b.Y, b.X) - MathF.Atan2(a.Y, a.X)));
            curvature[i] = angle / (3 * Spacing);
        }

        // 4. Cached per-point drawing data (the track never changes, so work it out once)
        joinNormals = new Vector2[Count];
        isCorner = new bool[Count];
        for (int i = 0; i < Count; i++)
        {
            joinNormals[i] = Vector2.Normalize(NormalAt(i - 1) + NormalAt(i));
            isCorner[i] = curvature[i] > 0.0012f;
        }

        BoundsMin = new Vector2(Points.Min(p => p.X), Points.Min(p => p.Y));
        BoundsMax = new Vector2(Points.Max(p => p.X), Points.Max(p => p.Y));
    }

    void PlantTrees()
    {
        var rng = new Random(7); // fixed seed → same scenery every time
        for (int i = 0; i < 400 && trees.Count < 180; i++)
        {
            var p = new Vector2(
                BoundsMin.X - 500 + (float)rng.NextDouble() * (BoundsMax.X - BoundsMin.X + 1000),
                BoundsMin.Y - 500 + (float)rng.NextDouble() * (BoundsMax.Y - BoundsMin.Y + 1000));
            float minDist = Points.Min(c => Vector2.DistanceSquared(c, p));
            if (minDist > MathF.Pow(Width / 2 + 120, 2))
                trees.Add((p, 18 + (float)rng.NextDouble() * 22, 0.82f + (float)rng.NextDouble() * 0.3f));
        }
    }

    static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
    }

    public static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= 2 * MathF.PI;
        while (a < -MathF.PI) a += 2 * MathF.PI;
        return a;
    }

    public int Wrap(int i) => ((i % Count) + Count) % Count;
    public Vector2 PointAt(int i) => Points[Wrap(i)];
    public float CurvatureAt(int i) => curvature[Wrap(i)];
    public Vector2 DirectionAt(int i) => Vector2.Normalize(PointAt(i + 1) - PointAt(i));
    public Vector2 NormalAt(int i) { Vector2 d = DirectionAt(i); return new Vector2(-d.Y, d.X); }
    public float HeadingAt(int i) { Vector2 d = DirectionAt(i); return MathF.Atan2(d.Y, d.X); }

    // Nearest centre-line point, searching only near `hint` so cars can't "jump" across the infield.
    public int FindNearest(Vector2 p, int hint, int window = 12)
    {
        int best = Wrap(hint);
        float bestDist = float.MaxValue;
        for (int k = -window; k <= window; k++)
        {
            int i = Wrap(hint + k);
            float d = Vector2.DistanceSquared(Points[i], p);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    public float DistanceFromCenter(Vector2 p, int index)
    {
        return MathF.Min(
            DistanceToSegment(p, PointAt(index - 1), PointAt(index)),
            DistanceToSegment(p, PointAt(index), PointAt(index + 1)));
    }

    public bool IsOnTrack(Vector2 p, int index) => DistanceFromCenter(p, index) <= Width / 2 + 4;

    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    // Grid slot behind the start line, two cars per row.
    public (Vector2 Position, float Heading, int Index) GridSlot(int slot)
    {
        int row = slot / 2;
        int index = Wrap(-3 - row * 4);
        float side = slot % 2 == 0 ? -1 : 1;
        Vector2 pos = PointAt(index) + NormalAt(index) * side * Width * 0.22f
                      - DirectionAt(index) * (slot % 2) * 25f; // stagger
        return (pos, HeadingAt(index), index);
    }

    // Only what's inside `view` (world coordinates) gets drawn; the rest of the circuit is skipped.
    public void Draw(Rectangle view)
    {
        // Widest thing drawn around the centre line is the grass verge
        const float margin = (Width + 90) / 2 + 10;
        Rectangle segView = Grow(view, margin);

        // Tree shadows first so they never sit on top of a neighbour
        foreach (var (pos, size, _) in trees)
            if (Visible(pos, size * 1.1f + 20, view))
                Ui.Circle(pos + new Vector2(10, 14), size * 1.05f, new Color(0, 0, 0, 46));

        // Layers from the outside in: darker grass verge, kerb-side shoulder, asphalt, rubbered line
        Band(Width + 90, new Color(55, 98, 50, 255), segView);
        Band(Width + 26, new Color(36, 38, 43, 255), segView);
        Band(Width, new Color(57, 59, 65, 255), segView);
        Band(Width * 0.38f, new Color(52, 54, 60, 255), segView);

        // Edges: red/white kerbs in corners, a white line on straights
        Color kerbRed = new(206, 36, 44, 255), kerbWhite = new(242, 242, 244, 255);
        Color lip = new(30, 30, 34, 255), edgeLine = new(228, 230, 234, 255);
        for (int i = 0; i < Count; i++)
        {
            if (!SegmentVisible(i, segView)) continue;
            int j = (i + 1) % Count;
            for (int side = -1; side <= 1; side += 2)
            {
                if (isCorner[i])
                {
                    Strip(i, j, side * (Width / 2 - 4), side * (Width / 2 + 11), i % 2 == 0 ? kerbRed : kerbWhite);
                    Strip(i, j, side * (Width / 2 + 11), side * (Width / 2 + 13), lip); // thin dark lip on the outside of the kerb
                }
                else
                {
                    Strip(i, j, side * (Width / 2 - 8), side * (Width / 2 - 4), edgeLine);
                }
            }
        }

        if (Visible(Points[0], Width * 2, view))
        {
            DrawGridBoxes();
            DrawStartLine();
        }

        foreach (var (pos, size, k) in trees)
        {
            if (!Visible(pos, size, view)) continue;
            Ui.Circle(pos, size, Tint(new Color(26, 70, 36, 255), k));
            Ui.Circle(pos - new Vector2(size * 0.12f, size * 0.15f), size * 0.8f, Tint(new Color(38, 94, 44, 255), k));
            Ui.Circle(pos - new Vector2(size * 0.26f, size * 0.3f), size * 0.5f, Tint(new Color(58, 124, 56, 255), k));
            Ui.Circle(pos - new Vector2(size * 0.36f, size * 0.42f), size * 0.2f, Tint(new Color(92, 156, 78, 255), k));
        }
    }

    static Rectangle Grow(Rectangle r, float by) => new(r.X - by, r.Y - by, r.Width + by * 2, r.Height + by * 2);

    static bool Visible(Vector2 p, float radius, Rectangle view) =>
        p.X + radius >= view.X && p.X - radius <= view.X + view.Width &&
        p.Y + radius >= view.Y && p.Y - radius <= view.Y + view.Height;

    // Segment i → i+1 touches the (already grown) view rectangle
    bool SegmentVisible(int i, Rectangle view)
    {
        Vector2 a = Points[i], b = Points[(i + 1) % Count];
        return MathF.Max(a.X, b.X) >= view.X && MathF.Min(a.X, b.X) <= view.X + view.Width &&
               MathF.Max(a.Y, b.Y) >= view.Y && MathF.Min(a.Y, b.Y) <= view.Y + view.Height;
    }

    // A continuous band of the given width along the centre line, as one quad per segment.
    // (Drawing it as thick lines plus a circle at every point covered each pixel ~10 times over.)
    void Band(float width, Color color, Rectangle view)
    {
        for (int i = 0; i < Count; i++)
            if (SegmentVisible(i, view))
                Strip(i, (i + 1) % Count, -width / 2, width / 2, color);
    }

    // Quad between two offsets (signed distances along the normal) from point i to point j
    void Strip(int i, int j, float from, float to, Color color)
    {
        Vector2 pi = Points[i], pj = Points[j], ni = joinNormals[i], nj = joinNormals[j];
        Quad(pi + ni * from, pj + nj * from, pj + nj * to, pi + ni * to, color);
    }

    static void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
    {
        Rlgl.CheckRenderBatchLimit(6);
        Rlgl.Begin(DrawMode.Triangles);
        Rlgl.Color4ub(color.R, color.G, color.B, color.A);
        Tri(a, b, c);
        Tri(a, c, d);
        Rlgl.End();
    }

    // raylib skips triangles wound the "wrong" way; flip them instead of drawing everything twice
    static void Tri(Vector2 a, Vector2 b, Vector2 c)
    {
        float cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (cross > 0) (b, c) = (c, b);
        Rlgl.Vertex2f(a.X, a.Y);
        Rlgl.Vertex2f(b.X, b.Y);
        Rlgl.Vertex2f(c.X, c.Y);
    }

    static Color Tint(Color c, float k) => new((int)Math.Min(255, c.R * k), (int)Math.Min(255, c.G * k), (int)Math.Min(255, c.B * k), 255);

    // White grid-slot brackets behind the start line
    void DrawGridBoxes()
    {
        Color line = new(236, 238, 242, 210);
        for (int slot = 0; slot < 6; slot++)
        {
            var (pos, heading, _) = GridSlot(slot);
            Vector2 f = new(MathF.Cos(heading), MathF.Sin(heading));
            Vector2 n = new(-f.Y, f.X);
            Vector2 front = pos + f * 24;
            Raylib.DrawLineEx(front - n * 14, front + n * 14, 3, line);
            Raylib.DrawLineEx(front - n * 14, front - n * 14 - f * 14, 3, line);
            Raylib.DrawLineEx(front + n * 14, front + n * 14 - f * 14, 3, line);
        }
    }

    void DrawStartLine()
    {
        Vector2 p = Points[0], d = DirectionAt(0), nrm = NormalAt(0);
        float deg = HeadingAt(0) * 180f / MathF.PI;
        const float sq = 10f;
        int cols = (int)(Width / sq);
        for (int k = 0; k < cols; k++)
        {
            for (int row = 0; row < 3; row++)
            {
                Vector2 c = p + nrm * (-Width / 2 + sq / 2 + k * sq + (Width - cols * sq) / 2) + d * (row * sq - sq);
                Color col = (k + row) % 2 == 0 ? new Color(242, 242, 244, 255) : new Color(18, 18, 20, 255);
                Raylib.DrawRectanglePro(new Rectangle(c.X, c.Y, sq, sq), new Vector2(sq / 2, sq / 2), deg, col);
            }
        }
    }
}
