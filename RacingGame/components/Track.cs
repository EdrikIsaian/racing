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
    public Vector2 BoundsMin, BoundsMax;
    readonly List<(Vector2 Pos, float Size)> trees = new();

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
                trees.Add((p, 18 + (float)rng.NextDouble() * 22));
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

    public void Draw()
    {
        // Tree shadows first so they never sit on top of a neighbour
        foreach (var (pos, size) in trees)
            Raylib.DrawCircleV(pos + new Vector2(6, 8), size, new Color(0, 0, 0, 50));

        // Asphalt
        Color asphalt = new Color(62, 64, 70, 255);
        for (int i = 0; i < Count; i++)
        {
            Raylib.DrawLineEx(Points[i], PointAt(i + 1), Width, asphalt);
            Raylib.DrawCircleV(Points[i], Width / 2, asphalt);
        }

        // Edges: red/white curbs in corners, a white line on straights
        for (int i = 0; i < Count; i++)
        {
            Vector2 a = Points[i], b = PointAt(i + 1);
            Vector2 na = NormalAt(i), nb = NormalAt(i + 1);
            bool corner = CurvatureAt(i) > 0.0012f;
            for (int side = -1; side <= 1; side += 2)
            {
                if (corner)
                {
                    Color c = i % 2 == 0 ? new Color(210, 40, 40, 255) : Color.White;
                    Raylib.DrawLineEx(a + na * side * (Width / 2 + 2), b + nb * side * (Width / 2 + 2), 12, c);
                }
                else
                {
                    Raylib.DrawLineEx(a + na * side * (Width / 2 - 5), b + nb * side * (Width / 2 - 5), 3, new Color(235, 235, 235, 200));
                }
            }

            // Dashed centre line
            if (i % 4 < 2)
                Raylib.DrawLineEx(a, b, 2, new Color(255, 255, 255, 60));
        }

        DrawStartLine();

        foreach (var (pos, size) in trees)
        {
            Raylib.DrawCircleV(pos, size, new Color(34, 92, 40, 255));
            Raylib.DrawCircleV(pos - new Vector2(size * 0.2f, size * 0.25f), size * 0.65f, new Color(52, 122, 52, 255));
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
            for (int row = 0; row < 2; row++)
            {
                Vector2 c = p + nrm * (-Width / 2 + sq / 2 + k * sq) + d * (row * sq - sq / 2);
                Color col = (k + row) % 2 == 0 ? Color.White : Color.Black;
                Raylib.DrawRectanglePro(new Rectangle(c.X, c.Y, sq, sq), new Vector2(sq / 2, sq / 2), deg, col);
            }
        }
    }
}
