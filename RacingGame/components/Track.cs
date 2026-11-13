using System.Numerics;
using Raylib_cs;

// A closed circuit built from a few control points, smoothed with a Catmull-Rom spline
// and resampled into evenly spaced points along the centre line. Each point also has a road height;
// the land around it (Terrain) and the scenery are built the first time a race is started on it.
class Track
{
    public const float Width = 130f;
    public const float Spacing = 20f; // distance between centre-line samples

    public readonly TrackDef Def;
    public Vector2[] Points = Array.Empty<Vector2>();
    public float[] Heights = Array.Empty<float>();
    float[] curvature = Array.Empty<float>();
    Vector2[] joinNormals = Array.Empty<Vector2>(); // average of the two segment normals at each point, so bands join without gaps
    bool[] isCorner = Array.Empty<bool>();
    public Vector2 BoundsMin, BoundsMax;
    readonly List<(Vector2 Pos, float Size, float Shade, float Y, int Variant)> trees = new();

    public Terrain? Terrain { get; private set; }
    public Scenery? Scenery { get; private set; }

    public int Count => Points.Length;
    public float Length => Count * Spacing;
    public float LengthKm => Length / Scene3D.UnitsPerMetre / 1000f;
    public float ClimbMetres => (Heights.Max() - Heights.Min()) / Scene3D.UnitsPerMetre;

    static readonly Dictionary<TrackDef, Track> cache = new();

    // Tracks are built once and kept: the centre line is cheap, the land is built on first race
    public static Track Get(TrackDef def)
    {
        if (!cache.TryGetValue(def, out Track? t)) cache[def] = t = new Track(def);
        return t;
    }

    Track(TrackDef def)
    {
        Def = def;
        Build();
    }

    // Builds the land and scenery (a fraction of a second, only the first time)
    public void EnsureWorld()
    {
        if (Terrain != null) return;
        Terrain = new Terrain(this, Def);
        PlantTrees();
        Scenery = new Scenery(this, Terrain, Def);
    }

    public float HeightAt(Vector2 p) => Terrain?.HeightAt(p) ?? 0;

    void Build()
    {
        // 1. Densely sample the spline
        var dense = new List<Vector2>();
        var denseH = new List<float>();
        Vector2[] cp = Def.Points;
        float[] el = Def.Elevation;
        int n = cp.Length;
        for (int i = 0; i < n; i++)
        {
            Vector2 p0 = cp[(i - 1 + n) % n] * Def.Scale;
            Vector2 p1 = cp[i] * Def.Scale;
            Vector2 p2 = cp[(i + 1) % n] * Def.Scale;
            Vector2 p3 = cp[(i + 2) % n] * Def.Scale;
            for (int s = 0; s < 60; s++)
            {
                float t = s / 60f;
                dense.Add(CatmullRom(p0, p1, p2, p3, t));
                denseH.Add(CatmullRom1(el[(i - 1 + n) % n], el[i], el[(i + 1) % n], el[(i + 2) % n], t));
            }
        }

        // 2. Resample so points are exactly `Spacing` apart
        var pts = new List<Vector2> { dense[0] };
        var hs = new List<float> { denseH[0] };
        float sinceLast = 0;
        for (int i = 1; i <= dense.Count; i++)
        {
            Vector2 a = dense[i - 1], b = dense[i % dense.Count];
            float ha = denseH[i - 1], hb = denseH[i % dense.Count];
            float segLen = Vector2.Distance(a, b);
            float pos = 0;
            while (sinceLast + (segLen - pos) >= Spacing)
            {
                pos += Spacing - sinceLast;
                pts.Add(Vector2.Lerp(a, b, pos / segLen));
                hs.Add(ha + (hb - ha) * pos / segLen);
                sinceLast = 0;
            }
            sinceLast += segLen - pos;
        }
        if (Vector2.Distance(pts[^1], pts[0]) < Spacing * 0.5f) { pts.RemoveAt(pts.Count - 1); hs.RemoveAt(hs.Count - 1); }
        Points = pts.ToArray();
        Heights = hs.ToArray();

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
        Terrain t = Terrain!;
        var rng = new Random(Def.Seed); // fixed seed → same scenery every time
        float spread = Def.Biome == Biome.Alpine ? 3200 : 2600;
        Vector2 stand = Scenery.GrandstandCentre(this);
        for (int i = 0; i < Def.Trees * 8 && trees.Count < Def.Trees; i++)
        {
            var p = new Vector2(
                BoundsMin.X - spread + (float)rng.NextDouble() * (BoundsMax.X - BoundsMin.X + spread * 2),
                BoundsMin.Y - spread + (float)rng.NextDouble() * (BoundsMax.Y - BoundsMin.Y + spread * 2));
            if (!CanPlace(p, Width / 2 + 120)) continue;
            if (Vector2.Distance(p, stand) < 420) continue;
            // Woods: trees bunch together where the noise says so
            if (Noise.Fbm(p / 700f, Def.Seed + 9, 2) < -0.15f && rng.NextDouble() < 0.7) continue;
            bool pine = rng.NextDouble() < Def.PineShare;
            int variant = rng.Next(1000) * 2 + (pine ? 0 : 1);
            trees.Add((p, 18 + (float)rng.NextDouble() * 22, 0.82f + (float)rng.NextDouble() * 0.3f, t.HeightAt(p), variant));
        }
    }

    // Free ground for scenery: away from the road, above the water, not too steep, below the snow
    public bool CanPlace(Vector2 p, float clearance)
    {
        Terrain t = Terrain!;
        if (t.DistanceToTrack(p) < clearance + 60) return false;
        if (DistanceToCentreLine(p) < clearance) return false;
        float h = t.HeightAt(p);
        if (t.HasWater && h < t.WaterLevel + 8) return false;
        if (h > Def.SnowLine - 120) return false;
        return t.SlopeAt(p) < 0.55f;
    }

    public float DistanceToCentreLine(Vector2 p)
    {
        float best = float.MaxValue;
        for (int i = 0; i < Count; i += 2) best = MathF.Min(best, Vector2.DistanceSquared(Points[i], p));
        return MathF.Sqrt(best);
    }

    static float CatmullRom1(float p0, float p1, float p2, float p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
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
        // Tree shadows (painted blobs only for trees the ray tracer isn't handling)
        for (int i = 0; i < trees.Count; i++)
        {
            var (pos, size, _, _, _) = trees[i];
            if (RayTracing.Enabled && traced.Length == trees.Count && traced[i]) continue;
            Scene3D.Disc(pos + new Vector2(Scene3D.SunDir.X, Scene3D.SunDir.Z) * size * 1.2f, size * 1.25f, size * 1.0f, new Vector2(1, 0), new Color(0, 0, 0, 52), 14);
        }

        // Layers from the outside in: darker grass verge, kerb-side shoulder, asphalt, rubbered line
        Band(Width + 90, Def.Verge);
        Band(Width + 26, new Color(50, 50, 52, 255));
        Band(Width, new Color(80, 80, 83, 255));
        Band(Width * 0.38f, new Color(70, 70, 73, 255));

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

    float[] treeDist = Array.Empty<float>();
    int[] treeOrder = Array.Empty<int>();
    bool[] traced = Array.Empty<bool>();

    // Puts the gantry and the trees nearest to what the camera looks at into the ray-traced scene
    public void AddRayCasters(Vector3 cameraPos, Vector3 cameraForward)
    {
        Vector2 gantry = Points[0] + DirectionAt(0) * 14;
        RayTracing.AddGantry(gantry, HeadingAt(0), HeightAt(gantry));
        if (traced.Length != trees.Count)
        {
            treeDist = new float[trees.Count];
            treeOrder = new int[trees.Count];
            traced = new bool[trees.Count];
        }
        Vector2 fwd = Vector2.Normalize(new Vector2(cameraForward.X, cameraForward.Z));
        Vector2 focus = new Vector2(cameraPos.X, cameraPos.Z) + fwd * 700;
        for (int i = 0; i < trees.Count; i++)
        {
            treeDist[i] = Vector2.DistanceSquared(trees[i].Pos, focus);
            treeOrder[i] = i;
            traced[i] = false;
        }
        Array.Sort(treeDist, treeOrder);
        for (int k = 0; k < trees.Count && !RayTracing.TreesFull; k++)
        {
            int i = treeOrder[k];
            RayTracing.AddTree(trees[i].Pos, trees[i].Size, trees[i].Variant, trees[i].Y);
            traced[i] = true;
        }
    }

    // Trees and the start gantry. Skips trees behind the camera or lost in the fog.
    public void DrawScenery(Vector3 cameraPos, Vector3 cameraForward)
    {
        Vector2 cam = new(cameraPos.X, cameraPos.Z);
        Vector2 fwd = Vector2.Normalize(new Vector2(cameraForward.X, cameraForward.Z));
        float maxDist = Scene3D.FogEnd + 200;
        for (int i = 0; i < trees.Count; i++)
        {
            var (pos, size, k, y, variant) = trees[i];
            Vector2 d = pos - cam;
            float dist = d.Length();
            if (dist > maxDist) continue;
            if (dist > 300 && Vector2.Dot(d / dist, fwd) < 0.2f) continue; // behind us
            Scene3D.DrawTree(pos, size, k, variant, y);
        }
        Vector2 gantry = Points[0] + DirectionAt(0) * 14;
        Scene3D.DrawGantry(gantry, HeadingAt(0), HeightAt(gantry));
    }

    // The widest road layer once more, for the ray-traced shadow pass over the road
    public void DrawRoadFootprint() => Band(Width + 90, Color.Black);

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
