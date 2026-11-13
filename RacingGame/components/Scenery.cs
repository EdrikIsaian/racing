using System.Numerics;
using Raylib_cs;

// Everything that makes a circuit feel lived in: a grandstand with a crowd that jumps when you
// pass, flags flapping in the wind, birds circling, hot-air balloons, wind turbines, rocks,
// cacti and tumbleweeds rolling across the road. (Clouds are part of the sky: Sky.cs.)
// The layout is fixed per track (seeded); only the motion changes.
class Scenery
{
    readonly Track track;
    readonly Terrain terrain;
    readonly TrackDef def;
    readonly Random rng;

    public static readonly Vector2 Wind = Vector2.Normalize(new Vector2(0.9f, 0.35f));

    record struct Prop(Vector2 Pos, float Y, float Size, float Yaw, Color Color, int Kind);
    readonly List<Prop> rocks = new(), cacti = new(), turbines = new();
    readonly List<(Vector2 Centre, float Radius, float Alt, float Phase, Color Color, Color Trim)> balloons = new();
    readonly List<(Vector2 Centre, float Radius, float Alt, int Count, float Speed, float Phase, bool Eagle)> flocks = new();
    readonly List<(Vector2 Pos, float Y, float Phase, Color Color)> flags = new();

    struct Weed { public Vector2 Pos, Vel; public float Size, Spin, Phase; }
    readonly Weed[] weeds;

    // Grandstand, in its own frame: u along the straight, v away from the road
    readonly Vector2 standCentre, standU, standV;
    readonly float standY, standLength = 420;
    const int StandRows = 6;
    const float RowDepth = 16, RowHeight = 9, StandOffset = Track.Width / 2 + 120;
    readonly List<(float U, int Row, Color Shirt, Color Skin, float Phase)> crowd = new();

    public static Vector2 GrandstandCentre(Track t) =>
        t.PointAt(0) + t.DirectionAt(0) * 120 + t.NormalAt(0) * (StandOffset + StandRows * RowDepth / 2);

    public Scenery(Track track, Terrain terrain, TrackDef def)
    {
        this.track = track;
        this.terrain = terrain;
        this.def = def;
        rng = new Random(def.Seed * 7 + 1);

        standU = track.DirectionAt(0);
        standV = track.NormalAt(0);
        standCentre = track.PointAt(0) + standU * 120 + standV * StandOffset;
        standY = MathF.Min(terrain.HeightAt(standCentre), terrain.HeightAt(standCentre + standV * StandRows * RowDepth));
        BuildCrowd();
        PlaceFlags();
        PlaceProps();
        weeds = new Weed[def.Tumbleweeds];
        for (int i = 0; i < weeds.Length; i++) SpawnWeed(ref weeds[i], track.PointAt(i * track.Count / Math.Max(1, weeds.Length)), true);
    }

    float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);

    Vector2 RandomNearTrack(float spread) => new(
        R(track.BoundsMin.X - spread, track.BoundsMax.X + spread),
        R(track.BoundsMin.Y - spread, track.BoundsMax.Y + spread));

    // ---------------------------------------------------------------- layout

    static readonly Color[] Shirts =
    {
        new(220, 50, 50, 255), new(250, 250, 250, 255), new(40, 110, 220, 255), new(250, 200, 40, 255),
        new(40, 170, 90, 255), new(30, 30, 36, 255), new(240, 120, 30, 255), new(160, 70, 200, 255),
    };
    static readonly Color[] Skins = { new(240, 200, 170, 255), new(200, 150, 110, 255), new(140, 96, 64, 255), new(96, 64, 44, 255) };

    void BuildCrowd()
    {
        for (int row = 0; row < StandRows; row++)
            for (float u = -standLength / 2 + 8; u < standLength / 2 - 8; u += 8.5f)
            {
                if (rng.NextDouble() < 0.18) continue; // empty seats
                crowd.Add((u + R(-1.5f, 1.5f), row, Shirts[rng.Next(Shirts.Length)], Skins[rng.Next(Skins.Length)], R(0, MathF.Tau)));
            }
    }

    void PlaceFlags()
    {
        for (int i = 0; i < track.Count && flags.Count < 34; i += 6)
        {
            if (track.CurvatureAt(i) < 0.0018f) continue;
            Vector2 d0 = track.DirectionAt(i - 3), d1 = track.DirectionAt(i + 3);
            float turn = d0.X * d1.Y - d0.Y * d1.X;
            Vector2 p = track.PointAt(i) - track.NormalAt(i) * MathF.Sign(turn) * (Track.Width / 2 + 62);
            flags.Add((p, terrain.HeightAt(p), R(0, MathF.Tau), def.FlagColors[flags.Count % def.FlagColors.Length]));
            i += 18; // spread them out
        }
    }

    void PlaceProps()
    {
        // Rocks: scattered, a few clusters
        for (int i = 0; i < def.Rocks * 10 && rocks.Count < def.Rocks; i++)
        {
            Vector2 p = RandomNearTrack(2400);
            float size = R(8, 46) * (rng.NextDouble() < 0.15 ? 1.8f : 1);
            if (!track.CanPlace(p, Track.Width / 2 + 60 + size)) continue;
            int shade = rng.Next(-14, 14);
            Color c = new(Math.Clamp(def.Rock.R + shade, 0, 255), Math.Clamp(def.Rock.G + shade, 0, 255), Math.Clamp(def.Rock.B + shade, 0, 255), 255);
            rocks.Add(new Prop(p, terrain.HeightAt(p), size, R(0, MathF.Tau), c, rng.Next(3)));
        }

        for (int i = 0; i < def.Cacti * 10 && cacti.Count < def.Cacti; i++)
        {
            Vector2 p = RandomNearTrack(2400);
            if (!track.CanPlace(p, Track.Width / 2 + 110)) continue;
            Color c = new(64 + rng.Next(20), 112 + rng.Next(26), 58 + rng.Next(16), 255);
            cacti.Add(new Prop(p, terrain.HeightAt(p), R(0.75f, 1.35f), R(0, MathF.Tau), c, rng.Next(3)));
        }

        // Wind turbines on the highest free ground
        if (def.Turbines > 0)
        {
            var spots = new List<(Vector2 P, float H)>();
            for (int i = 0; i < 600; i++)
            {
                Vector2 p = RandomNearTrack(3200);
                if (track.DistanceToCentreLine(p) < 900 || !track.CanPlace(p, 300)) continue;
                spots.Add((p, terrain.HeightAt(p)));
            }
            foreach (var (p, h) in spots.OrderByDescending(s => s.H))
            {
                if (turbines.Count >= def.Turbines) break;
                if (turbines.Any(t => Vector2.Distance(t.Pos, p) < 600)) continue;
                turbines.Add(new Prop(p, h, 1, MathF.Atan2(Wind.Y, -Wind.X), Color.White, 0));
            }
        }

        Color[] envelopes = { new(230, 60, 50, 255), new(250, 190, 40, 255), new(60, 140, 230, 255), new(120, 200, 90, 255), new(200, 80, 200, 255) };
        for (int i = 0; i < def.Balloons; i++)
        {
            Vector2 c = RandomNearTrack(-200);
            balloons.Add((c, R(500, 1300), R(520, 900), R(0, MathF.Tau), envelopes[i % envelopes.Length], envelopes[(i + 2) % envelopes.Length]));
        }

        for (int i = 0; i < def.Flocks; i++)
            flocks.Add((RandomNearTrack(0), R(300, 800), R(180, 340), rng.Next(5, 10), R(0.08f, 0.14f) * (rng.NextDouble() < 0.5 ? -1 : 1), R(0, MathF.Tau), def.Biome != Biome.Meadow));
    }

    // ---------------------------------------------------------------- motion

    public void Update(float dt, Vector2 player)
    {
        float t = (float)Raylib.GetTime();
        for (int i = 0; i < weeds.Length; i++)
        {
            ref Weed w = ref weeds[i];
            // Gusts: speed swells and drops
            float gust = 0.7f + 0.5f * MathF.Sin(t * 0.8f + w.Phase);
            w.Pos += w.Vel * gust * dt;
            w.Spin += w.Vel.Length() * gust / w.Size * dt;
            if (Vector2.Distance(w.Pos, player) > 2400) SpawnWeed(ref w, player, false);
        }
    }

    // A tumbleweed upwind of `around`, so it rolls past
    void SpawnWeed(ref Weed w, Vector2 around, bool anywhere)
    {
        Vector2 side = new(-Wind.Y, Wind.X);
        w.Pos = around - Wind * (anywhere ? R(-800, 800) : R(1200, 2000)) + side * R(-1200, 1200);
        w.Vel = Wind * R(55, 95) + side * R(-12, 12);
        w.Size = R(6, 11);
        w.Phase = R(0, MathF.Tau);
    }

    // ---------------------------------------------------------------- drawing

    // Models (lit shader): rocks, cacti, turbines, balloons, tumbleweeds
    public void DrawModels(Vector3 cameraPos, Vector3 cameraForward)
    {
        Vector2 cam = new(cameraPos.X, cameraPos.Z);
        Vector2 fwd = Vector2.Normalize(new Vector2(cameraForward.X, cameraForward.Z));
        float far = Scene3D.FogEnd + 200;
        bool Visible(Vector2 p, float radius)
        {
            Vector2 d = p - cam;
            float dist = d.Length();
            if (dist > far + radius) return false;
            return dist < 300 + radius || Vector2.Dot(d / dist, fwd) > 0.1f;
        }
        float t = (float)Raylib.GetTime();

        foreach (Prop r in rocks)
        {
            if (!Visible(r.Pos, r.Size)) continue;
            Vector3 scale = r.Kind switch { 0 => new Vector3(1.3f, 0.7f, 1f), 1 => new Vector3(1f, 0.9f, 0.85f), _ => new Vector3(1.6f, 0.5f, 1.1f) } * r.Size;
            Scene3D.DrawShape(Scene3D.Rock, Scene3D.Xf(Raymath.MatrixScale(scale.X, scale.Y, scale.Z), Raymath.MatrixRotateY(r.Yaw),
                Raymath.MatrixTranslate(r.Pos.X, r.Y - r.Size * 0.15f, r.Pos.Y)), r.Color);
        }

        foreach (Prop c in cacti)
        {
            if (!Visible(c.Pos, 80)) continue;
            DrawCactus(c);
        }

        foreach (Prop w in turbines)
        {
            if (!Visible(w.Pos, 400)) continue;
            DrawTurbine(w, t);
        }

        foreach (var (centre, radius, alt, phase, color, trim) in balloons)
        {
            float a = phase + t * 0.012f;
            Vector2 p = centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius + Wind * MathF.Sin(t * 0.05f + phase) * 200;
            float y = terrain.HeightAt(centre) + alt + MathF.Sin(t * 0.4f + phase) * 14;
            if (!Visible(p, 120)) continue;
            Scene3D.DrawShape(Scene3D.Sphere, Scene3D.Xf(Raymath.MatrixScale(58, 66, 58), Raymath.MatrixTranslate(p.X, y, p.Y)), color, 0.1f);
            Scene3D.DrawShape(Scene3D.Sphere, Scene3D.Xf(Raymath.MatrixScale(59, 20, 59), Raymath.MatrixTranslate(p.X, y + 4, p.Y)), trim, 0.1f);
            Scene3D.DrawShape(Scene3D.Cone, Scene3D.Xf(Raymath.MatrixScale(40, 46, 40), Raymath.MatrixRotateX(MathF.PI), Raymath.MatrixTranslate(p.X, y - 40, p.Y)), color);
            Scene3D.DrawShape(Scene3D.Cube, Scene3D.Xf(Raymath.MatrixScale(14, 11, 14), Raymath.MatrixTranslate(p.X, y - 100, p.Y)), new Color(120, 82, 46, 255));
        }

        foreach (Weed w in weeds)
        {
            if (!Visible(w.Pos, 20)) continue;
            float bounce = MathF.Abs(MathF.Sin(t * 2.6f + w.Phase)) * 14;
            float yaw = MathF.Atan2(-w.Vel.Y, w.Vel.X);
            Vector3 at = new(w.Pos.X, terrain.HeightAt(w.Pos) + w.Size + bounce, w.Pos.Y);
            Matrix4x4 roll = Raymath.MatrixRotateZ(-w.Spin);
            Scene3D.DrawShape(Scene3D.Rock, Scene3D.Xf(Raymath.MatrixScale(w.Size, w.Size, w.Size), roll, Raymath.MatrixRotateY(yaw), Raymath.MatrixTranslate(at.X, at.Y, at.Z)), new Color(150, 112, 66, 255));
            Scene3D.DrawShape(Scene3D.Rock, Scene3D.Xf(Raymath.MatrixScale(w.Size * 0.8f, w.Size * 1.05f, w.Size * 0.9f), Raymath.MatrixRotateX(0.8f), roll, Raymath.MatrixRotateY(yaw), Raymath.MatrixTranslate(at.X, at.Y, at.Z)), new Color(118, 86, 50, 255));
        }
    }

    static void DrawCactus(Prop c)
    {
        float s = c.Size;
        Matrix4x4 place = Scene3D.Xf(Raymath.MatrixRotateY(c.Yaw), Raymath.MatrixTranslate(c.Pos.X, c.Y - 2, c.Pos.Y));
        float h = 52 * s, r = 5.2f * s;
        Scene3D.DrawShape(Scene3D.Cylinder, Scene3D.Xf(Raymath.MatrixScale(r, h, r), place), c.Color);
        Scene3D.DrawShape(Scene3D.Sphere, Scene3D.Xf(Raymath.MatrixScale(r, r, r), Raymath.MatrixTranslate(0, h, 0), place), c.Color);
        for (int arm = 0; arm < 1 + c.Kind % 2 + (c.Kind == 2 ? 0 : 0); arm++)
        {
            float side = arm == 0 ? 1 : -1;
            float y0 = h * (arm == 0 ? 0.42f : 0.55f), reach = 10 * s, ar = r * 0.7f, ah = h * 0.32f;
            Scene3D.DrawShape(Scene3D.Cylinder, Scene3D.Xf(Raymath.MatrixScale(ar, reach, ar), Raymath.MatrixRotateZ(-MathF.PI / 2 * side), Raymath.MatrixTranslate(0, y0, 0), place), c.Color);
            Scene3D.DrawShape(Scene3D.Sphere, Scene3D.Xf(Raymath.MatrixScale(ar, ar, ar), Raymath.MatrixTranslate(reach * side, y0, 0), place), c.Color);
            Scene3D.DrawShape(Scene3D.Cylinder, Scene3D.Xf(Raymath.MatrixScale(ar, ah, ar), Raymath.MatrixTranslate(reach * side, y0, 0), place), c.Color);
            Scene3D.DrawShape(Scene3D.Sphere, Scene3D.Xf(Raymath.MatrixScale(ar, ar, ar), Raymath.MatrixTranslate(reach * side, y0 + ah, 0), place), c.Color);
        }
    }

    static void DrawTurbine(Prop w, float t)
    {
        const float height = 520, blade = 250;
        Color white = new(236, 238, 242, 255);
        Matrix4x4 place = Scene3D.Xf(Raymath.MatrixRotateY(w.Yaw), Raymath.MatrixTranslate(w.Pos.X, w.Y - 6, w.Pos.Y));
        Scene3D.DrawShape(Scene3D.Cylinder, Scene3D.Xf(Raymath.MatrixScale(9, height, 9), place), white, 0.1f);
        Scene3D.DrawShape(Scene3D.Cube, Scene3D.Xf(Raymath.MatrixScale(46, 18, 18), Raymath.MatrixTranslate(6, height + 6, 0), place), white, 0.2f);
        Scene3D.DrawShape(Scene3D.Sphere, Scene3D.Xf(Raymath.MatrixScale(10, 10, 10), Raymath.MatrixTranslate(32, height + 6, 0), place), white, 0.2f);
        float spin = t * 1.1f + w.Pos.X * 0.01f;
        for (int k = 0; k < 3; k++)
            Scene3D.DrawShape(Scene3D.Cube, Scene3D.Xf(Raymath.MatrixScale(3, blade, 15), Raymath.MatrixTranslate(0, blade / 2 + 6, 0),
                Raymath.MatrixRotateX(spin + k * MathF.Tau / 3), Raymath.MatrixTranslate(36, height + 6, 0), place), white, 0.15f);
    }

    // Flat-coloured geometry (ground shader, depth on): grandstand + crowd, flags, birds
    public void DrawFlat(Vector3 cameraPos, Vector2 player)
    {
        float t = (float)Raylib.GetTime();
        Rlgl.DisableBackfaceCulling();
        if (Vector2.Distance(new Vector2(cameraPos.X, cameraPos.Z), standCentre) < Scene3D.FogEnd) DrawGrandstand(t, player);
        DrawFlags(t, cameraPos);
        DrawBirds(t);
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableBackfaceCulling();
    }

    Vector3 StandPoint(float u, float v, float y) { Vector2 p = standCentre + standU * u + standV * v; return new Vector3(p.X, standY + y, p.Y); }

    void DrawGrandstand(float t, Vector2 player)
    {
        Color concrete = new(176, 176, 182, 255), seats = Ui.Lerp(def.FlagColors[0], new Color(60, 60, 70, 255), 0.35f);
        float L = standLength / 2;
        float top = 8 + StandRows * RowHeight;
        for (int row = 0; row < StandRows; row++)
        {
            float v0 = row * RowDepth, v1 = v0 + RowDepth, y = 8 + (row + 1) * RowHeight;
            Box(-L, L, v0, v1, -40, y, row % 2 == 0 ? seats : Shade(seats, 0.88f), concrete);
        }
        // Back wall and roof
        float back = StandRows * RowDepth;
        Box(-L, L, back, back + 5, -40, top + 52, Shade(concrete, 0.8f), concrete);
        Quad(StandPoint(-L - 6, back + 6, top + 56), StandPoint(L + 6, back + 6, top + 56), StandPoint(L + 6, -12, top + 44), StandPoint(-L - 6, -12, top + 44), def.FlagColors[^1]);
        for (float u = -L; u <= L + 1; u += L / 3)
            Box(u - 1.5f, u + 1.5f, -10, -7, 8, top + 44, new Color(80, 82, 90, 255), new Color(80, 82, 90, 255));
        // Front advertising board
        Box(-L, L, -6, -4, -10, 16, new Color(24, 26, 34, 255), Ui.Accent);

        // Crowd: everyone bounces a little, and jumps when the player goes by
        float near = Vector2.Distance(player, standCentre);
        float excite = 0.25f + 0.75f * Math.Clamp(1 - (near - 150) / 500, 0, 1);
        foreach (var (u, row, shirt, skin, phase) in crowd)
        {
            float v = row * RowDepth + RowDepth * 0.55f, y = 8 + (row + 1) * RowHeight;
            float hop = MathF.Pow(MathF.Max(0, MathF.Sin(t * (5 + phase % 2) + phase)), 2) * 4.5f * excite;
            Vector3 b0 = StandPoint(u - 2.4f, v, y + hop), b1 = StandPoint(u + 2.4f, v, y + hop);
            Vector3 up = new(0, 9, 0), head = new(0, 4, 0);
            Quad(b0, b1, b1 + up, b0 + up, shirt);
            Vector3 h0 = StandPoint(u - 1.8f, v - 0.1f, y + hop + 9), h1 = StandPoint(u + 1.8f, v - 0.1f, y + hop + 9);
            Quad(h0, h1, h1 + head, h0 + head, skin);
            if (excite > 0.6f && phase > 3.5f)
            {
                // arms in the air
                Vector3 a0 = StandPoint(u - 2.6f, v - 0.2f, y + hop + 7), a1 = StandPoint(u - 1.6f, v - 0.2f, y + hop + 7);
                Quad(a0, a1, a1 + new Vector3(0, 8, 0), a0 + new Vector3(0, 8, 0), shirt);
            }
        }
    }

    void DrawFlags(float t, Vector3 cameraPos)
    {
        Vector2 side = new(-Wind.Y, Wind.X);
        Color pole = new(200, 202, 208, 255);
        foreach (var (p, y, phase, color) in flags)
        {
            if (Vector2.DistanceSquared(new Vector2(cameraPos.X, cameraPos.Z), p) > 3000 * 3000) continue;
            Vector3 b = new(p.X, y, p.Y);
            const float h = 70, len = 34, cloth = 20;
            // Pole: two crossed strips
            Quad(b + new Vector3(-1.2f, 0, 0), b + new Vector3(1.2f, 0, 0), b + new Vector3(1.2f, h, 0), b + new Vector3(-1.2f, h, 0), pole);
            Quad(b + new Vector3(0, 0, -1.2f), b + new Vector3(0, 0, 1.2f), b + new Vector3(0, h, 1.2f), b + new Vector3(0, h, -1.2f), pole);
            const int segs = 8;
            for (int k = 0; k < segs; k++)
            {
                float u0 = k / (float)segs, u1 = (k + 1) / (float)segs;
                Vector3 P(float u, float dy)
                {
                    float wave = MathF.Sin(u * 5 - t * 7 + phase) * u * 5;
                    Vector2 q = p + Wind * u * len + side * wave;
                    return new Vector3(q.X, y + h - dy - u * 2, q.Y);
                }
                Color c = k % 2 == 0 ? color : Shade(color, 0.86f);
                Quad(P(u0, 0), P(u1, 0), P(u1, cloth), P(u0, cloth), c);
            }
        }
    }

    void DrawBirds(float t)
    {
        Color body = new(34, 34, 40, 255);
        foreach (var (centre, radius, alt, count, speed, phase, eagle) in flocks)
        {
            float baseY = terrain.HeightAt(centre) + alt;
            float span = eagle ? 15 : 8;
            for (int k = 0; k < count; k++)
            {
                float a = phase + t * speed + k * 0.11f * MathF.Sign(speed);
                float rr = radius + MathF.Sin(k * 2.3f) * 40;
                Vector2 p = centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rr;
                Vector2 dir = new Vector2(-MathF.Sin(a), MathF.Cos(a)) * MathF.Sign(speed);
                Vector2 right = new(-dir.Y, dir.X);
                float y = baseY + MathF.Sin(t * 0.7f + k) * 18 + k * 3;
                // Eagles glide with the odd flap, small birds flap all the time
                float flapRate = eagle ? 3 : 10;
                float flap = MathF.Sin(t * flapRate + k * 1.7f) * (eagle ? 0.5f + 0.5f * MathF.Max(0, MathF.Sin(t * 0.5f + k)) : 1);
                Vector3 nose = new Vector3(p.X, y, p.Y) + new Vector3(dir.X, 0, dir.Y) * span * 0.35f;
                Vector3 tail = new Vector3(p.X, y, p.Y) - new Vector3(dir.X, 0, dir.Y) * span * 0.35f;
                for (int s = -1; s <= 1; s += 2)
                {
                    Vector2 tip2 = p + right * s * span - dir * span * 0.15f;
                    Vector3 tip = new(tip2.X, y + flap * span * 0.4f, tip2.Y);
                    Tri(nose, tail, tip, body);
                }
            }
        }
    }

    // ---------------------------------------------------------------- immediate geometry helpers

    static Color Shade(Color c, float k) => new((int)(c.R * k), (int)(c.G * k), (int)(c.B * k), (int)c.A);

    // Box in the grandstand frame, with simple fixed shading per face
    void Box(float u0, float u1, float v0, float v1, float y0, float y1, Color topColor, Color sideColor)
    {
        Vector3 P(float u, float v, float y) => StandPoint(u, v, y);
        Quad(P(u0, v0, y1), P(u1, v0, y1), P(u1, v1, y1), P(u0, v1, y1), topColor);
        Quad(P(u0, v0, y0), P(u1, v0, y0), P(u1, v0, y1), P(u0, v0, y1), Shade(sideColor, 0.82f)); // facing the road
        Quad(P(u0, v1, y0), P(u1, v1, y0), P(u1, v1, y1), P(u0, v1, y1), Shade(sideColor, 0.6f));
        Quad(P(u0, v0, y0), P(u0, v1, y0), P(u0, v1, y1), P(u0, v0, y1), Shade(sideColor, 0.7f));
        Quad(P(u1, v0, y0), P(u1, v1, y0), P(u1, v1, y1), P(u1, v0, y1), Shade(sideColor, 0.7f));
    }

    static void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
    {
        Rlgl.CheckRenderBatchLimit(6);
        Rlgl.Begin(DrawMode.Triangles);
        Rlgl.Color4ub(color.R, color.G, color.B, color.A);
        Rlgl.Vertex3f(a.X, a.Y, a.Z); Rlgl.Vertex3f(b.X, b.Y, b.Z); Rlgl.Vertex3f(c.X, c.Y, c.Z);
        Rlgl.Vertex3f(a.X, a.Y, a.Z); Rlgl.Vertex3f(c.X, c.Y, c.Z); Rlgl.Vertex3f(d.X, d.Y, d.Z);
        Rlgl.End();
    }

    static void Tri(Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        Rlgl.CheckRenderBatchLimit(3);
        Rlgl.Begin(DrawMode.Triangles);
        Rlgl.Color4ub(color.R, color.G, color.B, color.A);
        Rlgl.Vertex3f(a.X, a.Y, a.Z); Rlgl.Vertex3f(b.X, b.Y, b.Z); Rlgl.Vertex3f(c.X, c.Y, c.Z);
        Rlgl.End();
    }
}
