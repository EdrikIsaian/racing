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

    // ---- 3D drawing. Ground layers are flat (see Scene3D.BeginGround); trees and the gantry are models.

    public void DrawGround()
    {
        // Grass with mowed stripes in both directions around the circuit, so you can feel your speed
        Vector2 lo = BoundsMin - new Vector2(9000), hi = BoundsMax + new Vector2(9000);
        Scene3D.Quad(lo, new Vector2(hi.X, lo.Y), hi, new Vector2(lo.X, hi.Y), new Color(70, 120, 60, 255));
        const int band = 150;
        Color stripe = new(0, 0, 0, 14);
        int x0 = (int)((BoundsMin.X - 2400) / band), x1 = (int)((BoundsMax.X + 2400) / band);
        int y0 = (int)((BoundsMin.Y - 2400) / band), y1 = (int)((BoundsMax.Y + 2400) / band);
        for (int x = x0; x <= x1; x += 2)
            Scene3D.Quad(new(x * band, y0 * band), new((x + 1) * band, y0 * band), new((x + 1) * band, y1 * band), new(x * band, y1 * band), stripe);
        for (int y = y0; y <= y1; y += 2)
            Scene3D.Quad(new(x0 * band, y * band), new(x1 * band, y * band), new(x1 * band, (y + 1) * band), new(x0 * band, (y + 1) * band), stripe);

        // Tree shadows
        foreach (var (pos, size, _) in trees)
            Scene3D.Disc(pos + new Vector2(Scene3D.SunDir.X, Scene3D.SunDir.Z) * size * 1.2f, size * 1.25f, size * 1.0f, new Vector2(1, 0), new Color(0, 0, 0, 52), 14);

        // Layers from the outside in: darker grass verge, kerb-side shoulder, asphalt, rubbered line
        Band(Width + 90, new Color(58, 102, 52, 255));
        Band(Width + 26, new Color(36, 38, 43, 255));
        Band(Width, new Color(62, 64, 70, 255));
        Band(Width * 0.38f, new Color(56, 58, 64, 255));

        // Edges: red/white kerbs in corners, a white line on straights
        Color kerbRed = new(206, 36, 44, 255), kerbWhite = new(242, 242, 244, 255);
        Color lip = new(30, 30, 34, 255), edgeLine = new(228, 230, 234, 255);
        for (int i = 0; i < Count; i++)
        {
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

        DrawGridBoxes();
        DrawStartLine();
    }

    // Trees and the start gantry. Skips trees behind the camera or lost in the fog.
    public void DrawScenery(Vector3 cameraPos, Vector3 cameraForward)
    {
        Vector2 cam = new(cameraPos.X, cameraPos.Z);
        Vector2 fwd = Vector2.Normalize(new Vector2(cameraForward.X, cameraForward.Z));
        float maxDist = Scene3D.FogEnd + 200;
        for (int i = 0; i < trees.Count; i++)
        {
            var (pos, size, k) = trees[i];
            Vector2 d = pos - cam;
            float dist = d.Length();
            if (dist > maxDist) continue;
            if (dist > 300 && Vector2.Dot(d / dist, fwd) < 0.2f) continue; // behind us
            Scene3D.DrawTree(pos, size, k, i);
        }
        Scene3D.DrawGantry(Points[0] + DirectionAt(0) * 14, HeadingAt(0));
    }

    // A continuous band of the given width along the centre line, as one quad per segment
    void Band(float width, Color color)
    {
        for (int i = 0; i < Count; i++)
            Strip(i, (i + 1) % Count, -width / 2, width / 2, color);
    }

    // Quad between two offsets (signed distances along the normal) from point i to point j
    void Strip(int i, int j, float from, float to, Color color)
    {
        Vector2 pi = Points[i], pj = Points[j], ni = joinNormals[i], nj = joinNormals[j];
        Scene3D.Quad(pi + ni * from, pj + nj * from, pj + nj * to, pi + ni * to, color);
    }

    // White grid-slot brackets behind the start line
    void DrawGridBoxes()
    {
        Color line = new(236, 238, 242, 220);
        for (int slot = 0; slot < 6; slot++)
        {
            var (pos, heading, _) = GridSlot(slot);
            Vector2 f = new(MathF.Cos(heading), MathF.Sin(heading));
            Vector2 n = new(-f.Y, f.X);
            Vector2 front = pos + f * 26;
            Scene3D.Box(front, f, 3, 34, line);
            Scene3D.Box(front - n * 15.5f - f * 8, f, 16, 3, line);
            Scene3D.Box(front + n * 15.5f - f * 8, f, 16, 3, line);
        }
    }

    void DrawStartLine()
    {
        Vector2 p = Points[0], d = DirectionAt(0), nrm = NormalAt(0);
        const float sq = 10f;
        int cols = (int)(Width / sq);
        for (int k = 0; k < cols; k++)
        {
            for (int row = 0; row < 3; row++)
            {
                Vector2 c = p + nrm * (-Width / 2 + sq / 2 + k * sq + (Width - cols * sq) / 2) + d * (row * sq - sq);
                Color col = (k + row) % 2 == 0 ? new Color(242, 242, 244, 255) : new Color(18, 18, 20, 255);
                Scene3D.Box(c, d, sq, sq, col);
            }
        }
    }
}
