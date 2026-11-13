using System.Numerics;
using Raylib_cs;

// The land around a circuit: one height grid that everything stands on.
//
// The road is cut into the land: within a strip around the centre line the surface is exactly
// the road's height (flat from side to side), and further out it blends into hills, mountains,
// mesas or lake beds depending on the biome. The drawn mesh is sunk a few units under the road,
// so the road layers (drawn on top without writing depth) can never flicker against it.
//
// HeightAt uses the same two triangles per cell as the mesh, so whatever is placed with it
// (cars, trees, decals) sits exactly on the drawn ground.
unsafe class Terrain
{
    const float RoadZone = Track.Width / 2 + 75; // flat road surface out to here
    const float Blend = 380;                     // then blends into the natural land over this distance
    const float Sink = 5;                        // mesh is this far below the road surface under the road
    const int TileCells = 120;                   // cells per mesh tile side (vertices must fit 16-bit indices)

    public readonly Vector2 Origin;
    public readonly float Cell;
    public readonly int Nx, Ny; // vertices per side
    public readonly float WaterLevel;
    public bool HasWater => !float.IsNaN(WaterLevel);
    public Vector2 Min => Origin;
    public Vector2 Max => Origin + new Vector2((Nx - 1) * Cell, (Ny - 1) * Cell);

    readonly float[] surface;   // ground height at each grid vertex
    readonly float[] trackDist; // distance from the centre line (approximate far away)
    readonly TrackDef def;
    readonly List<(Model Model, Vector3 Lo, Vector3 Hi)> tiles = new();

    static Shader shader;
    static int locViewPos, locFog, locFogColor, locMode, locTime, locWater, locStripes, locTint;
    static readonly List<Terrain> loaded = new();

    public Terrain(Track track, TrackDef def)
    {
        this.def = def;
        WaterLevel = def.WaterLevel;
        float extent = def.FogEnd + 900;
        Vector2 lo = track.BoundsMin - new Vector2(extent), hi = track.BoundsMax + new Vector2(extent);
        Vector2 size = hi - lo;
        Cell = MathF.Max(56, MathF.Max(size.X, size.Y) / 330f);
        Nx = (int)MathF.Ceiling(size.X / Cell) + 1;
        Ny = (int)MathF.Ceiling(size.Y / Cell) + 1;
        Origin = lo;
        surface = new float[Nx * Ny];
        trackDist = new float[Nx * Ny];

        BuildHeights(track);
        BuildMesh();
        loaded.Add(this);
    }

    // ---------------------------------------------------------------- heights

    void BuildHeights(Track track)
    {
        Vector2[] pts = track.Points;
        float[] th = track.Heights;
        int n = pts.Length;

        // 1. Coarse "regional" height: a soft average of the road heights, so the land follows
        //    the road's climbs from far away instead of every climb being a cutting.
        const float cc = 320;
        int cx = (int)MathF.Ceiling((Nx - 1) * Cell / cc) + 2, cy = (int)MathF.Ceiling((Ny - 1) * Cell / cc) + 2;
        var regH = new float[cx * cy];
        var regD = new float[cx * cy];
        for (int j = 0; j < cy; j++)
            for (int i = 0; i < cx; i++)
            {
                Vector2 p = Origin + new Vector2(i * cc, j * cc);
                double ws = 0, hs = 0;
                float best = float.MaxValue;
                for (int k = 0; k < n; k += 2)
                {
                    float d2 = Vector2.DistanceSquared(p, pts[k]);
                    best = MathF.Min(best, d2);
                    double w = 1.0 / (d2 + 450.0 * 450.0);
                    ws += w;
                    hs += w * th[k];
                }
                regH[j * cx + i] = (float)(hs / ws);
                regD[j * cx + i] = MathF.Sqrt(best);
            }
        float Coarse(float[] a, Vector2 p)
        {
            float fx = (p.X - Origin.X) / cc, fy = (p.Y - Origin.Y) / cc;
            int ix = Math.Clamp((int)fx, 0, cx - 2), iy = Math.Clamp((int)fy, 0, cy - 2);
            float tx = Math.Clamp(fx - ix, 0, 1), ty = Math.Clamp(fy - iy, 0, 1);
            float a0 = a[iy * cx + ix] + (a[iy * cx + ix + 1] - a[iy * cx + ix]) * tx;
            float a1 = a[(iy + 1) * cx + ix] + (a[(iy + 1) * cx + ix + 1] - a[(iy + 1) * cx + ix]) * tx;
            return a0 + (a1 - a0) * ty;
        }

        // 2. Near the road: exact distance to the centre line and the road height there
        //    (inverse-distance weighted, so two nearby stretches at different heights don't make a cliff)
        int count = Nx * Ny;
        var dmin = new float[count];
        var wsum = new double[count];
        var hsum = new double[count];
        Array.Fill(dmin, float.MaxValue);
        float reach = RoadZone + Blend;
        for (int k = 0; k < n; k++)
        {
            Vector2 a = pts[k], b = pts[(k + 1) % n];
            float ha = th[k], hb = th[(k + 1) % n];
            Vector2 ab = b - a;
            float abLen2 = MathF.Max(ab.LengthSquared(), 1e-4f);
            int i0 = Math.Max(0, (int)((MathF.Min(a.X, b.X) - reach - Origin.X) / Cell));
            int i1 = Math.Min(Nx - 1, (int)((MathF.Max(a.X, b.X) + reach - Origin.X) / Cell) + 1);
            int j0 = Math.Max(0, (int)((MathF.Min(a.Y, b.Y) - reach - Origin.Y) / Cell));
            int j1 = Math.Min(Ny - 1, (int)((MathF.Max(a.Y, b.Y) + reach - Origin.Y) / Cell) + 1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    Vector2 p = Origin + new Vector2(i * Cell, j * Cell);
                    float t = Math.Clamp(Vector2.Dot(p - a, ab) / abLen2, 0, 1);
                    float d = Vector2.Distance(p, a + ab * t);
                    if (d > reach) continue;
                    int idx = j * Nx + i;
                    if (d < dmin[idx]) dmin[idx] = d;
                    double dd = d * d;
                    double w = 1.0 / (dd * dd + 1.0);
                    wsum[idx] += w;
                    hsum[idx] += w * (ha + (hb - ha) * t);
                }
        }

        // 3. Natural land, blended into the road strip
        for (int j = 0; j < Ny; j++)
            for (int i = 0; i < Nx; i++)
            {
                int idx = j * Nx + i;
                Vector2 p = Origin + new Vector2(i * Cell, j * Cell);
                float dist = MathF.Min(dmin[idx], Coarse(regD, p));
                float nat = Natural(p, Coarse(regH, p), dist);
                float h = nat;
                if (wsum[idx] > 0)
                {
                    float road = (float)(hsum[idx] / wsum[idx]);
                    h = road + (nat - road) * Smooth(RoadZone, reach, dmin[idx]);
                }
                surface[idx] = h;
                trackDist[idx] = dist;
            }
    }

    float Natural(Vector2 p, float regional, float dist)
    {
        float near = Smooth(120, 900, dist);
        float h = regional + Noise.Fbm(p / def.HillScale, def.Seed, 4) * def.HillAmp * (0.3f + 0.7f * near);

        if (def.MountainAmp > 0)
        {
            float m = Smooth(def.MountainStart, def.MountainStart + 2800, dist);
            float ridge = 1 - MathF.Abs(Noise.Fbm(p / 2300f, def.Seed + 11, 5));
            h += def.MountainAmp * m * (0.25f + ridge * ridge);
        }

        if (def.MesaHeight > 0)
        {
            // Table mountains: flat tops, steep sides, a few layers
            float n = Noise.Fbm(p / 1500f, def.Seed + 3, 3) * 0.5f + 0.5f;
            float keep = Smooth(350, 1100, dist);
            h += def.MesaHeight * (Smooth(0.56f, 0.6f, n) * 0.6f + Smooth(0.66f, 0.69f, n) * 0.4f) * keep;
            // wind-blown dunes
            h += 22 * MathF.Sin(p.X / 170f + 2.2f * MathF.Sin(p.Y / 430f)) * near;
        }

        foreach (var (centre, radius, depth) in def.Lakes)
        {
            float d = Vector2.Distance(p, centre * def.Scale);
            h -= depth * (1 - Smooth(radius * 0.35f, radius, d));
        }
        return h;
    }

    static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    float S(int i, int j) => surface[Math.Clamp(j, 0, Ny - 1) * Nx + Math.Clamp(i, 0, Nx - 1)];

    // Ground height at any point (the road surface on the road)
    public float HeightAt(Vector2 p)
    {
        float fx = (p.X - Origin.X) / Cell, fy = (p.Y - Origin.Y) / Cell;
        int ix = Math.Clamp((int)MathF.Floor(fx), 0, Nx - 2), iy = Math.Clamp((int)MathF.Floor(fy), 0, Ny - 2);
        float tx = Math.Clamp(fx - ix, 0, 1), ty = Math.Clamp(fy - iy, 0, 1);
        float h00 = surface[iy * Nx + ix], h10 = surface[iy * Nx + ix + 1];
        float h01 = surface[(iy + 1) * Nx + ix], h11 = surface[(iy + 1) * Nx + ix + 1];
        // Same split as the mesh: triangles (00,10,11) and (00,11,01)
        return tx > ty
            ? h00 + (h10 - h00) * tx + (h11 - h10) * ty
            : h00 + (h11 - h01) * tx + (h01 - h00) * ty;
    }

    public float DistanceToTrack(Vector2 p)
    {
        int i = Math.Clamp((int)MathF.Round((p.X - Origin.X) / Cell), 0, Nx - 1);
        int j = Math.Clamp((int)MathF.Round((p.Y - Origin.Y) / Cell), 0, Ny - 1);
        return trackDist[j * Nx + i];
    }

    // Steepness (rise per unit) at p
    public float SlopeAt(Vector2 p)
    {
        float e = Cell * 0.5f;
        float dx = HeightAt(p + new Vector2(e, 0)) - HeightAt(p - new Vector2(e, 0));
        float dz = HeightAt(p + new Vector2(0, e)) - HeightAt(p - new Vector2(0, e));
        return MathF.Sqrt(dx * dx + dz * dz) / (2 * e);
    }

    // ---------------------------------------------------------------- mesh

    float MeshHeight(int i, int j)
    {
        int idx = Math.Clamp(j, 0, Ny - 1) * Nx + Math.Clamp(i, 0, Nx - 1);
        float sink = (1 - Smooth(RoadZone - 10, RoadZone + 50, trackDist[idx])) * Sink;
        return surface[idx] - sink;
    }

    Color GroundColor(int i, int j, Vector3 normal)
    {
        int idx = j * Nx + i;
        float h = surface[idx], dist = trackDist[idx];
        Vector2 p = Origin + new Vector2(i * Cell, j * Cell);
        float steep = 1 - normal.Y; // 0 flat .. 1 vertical
        float n = Noise.Fbm(p / 260f, def.Seed + 5, 3) * 0.5f + 0.5f;

        Vector3 c = Mix(V(def.Grass), V(def.GrassDark), Math.Clamp(n * 1.4f - 0.2f, 0, 1));
        if (def.Biome == Biome.Desert)
        {
            float rock = Smooth(0.08f, 0.2f, steep);
            Vector3 strata = V(def.Rock) * (0.9f + 0.12f * MathF.Sin(h / 13f));
            c = Mix(c, strata, rock);
            c = Mix(c, V(def.Snow), Smooth(190, 260, h) * (1 - rock) * 0.8f); // mesa tops
        }
        else
        {
            c = Mix(c, V(def.Rock), Math.Max(Smooth(0.18f, 0.32f, steep), Smooth(def.RockLine - 60, def.RockLine + 80, h + n * 90)));
            c = Mix(c, V(def.Snow), Smooth(def.SnowLine - 40, def.SnowLine + 60, h + n * 140) * (1 - Smooth(0.45f, 0.6f, steep) * 0.6f));
            if (HasWater) c = Mix(c, V(def.Sand), 1 - Smooth(WaterLevel + 4, WaterLevel + 18, h));
        }
        // Under and right beside the road: the verge colour, so the road edge looks tidy
        c = Mix(V(def.Verge), c, Smooth(RoadZone - 30, RoadZone + 40, dist));
        float mowed = def.Stripes > 0 ? 1 - Smooth(1600, 2200, dist) : 0;
        return new Color((int)Math.Clamp(c.X * 255, 0, 255), (int)Math.Clamp(c.Y * 255, 0, 255), (int)Math.Clamp(c.Z * 255, 0, 255), (int)(mowed * 255));
    }

    static Vector3 V(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);
    static Vector3 Mix(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    void BuildMesh()
    {
        EnsureShader();
        for (int tj = 0; tj < Ny - 1; tj += TileCells)
            for (int ti = 0; ti < Nx - 1; ti += TileCells)
            {
                int w = Math.Min(TileCells, Nx - 1 - ti) + 1, h = Math.Min(TileCells, Ny - 1 - tj) + 1;
                Mesh mesh = new() { VertexCount = w * h, TriangleCount = (w - 1) * (h - 1) * 2 };
                mesh.AllocVertices();
                mesh.AllocNormals();
                mesh.AllocColors();
                mesh.AllocIndices();
                Span<Vector3> pos = mesh.VerticesAs<Vector3>();
                Span<Vector3> nrm = mesh.NormalsAs<Vector3>();
                Span<Color> col = mesh.ColorsAs<Color>();
                Span<ushort> idx = mesh.IndicesAs<ushort>();
                Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        int gi = ti + i, gj = tj + j;
                        float y = MeshHeight(gi, gj);
                        Vector3 v = new(Origin.X + gi * Cell, y, Origin.Y + gj * Cell);
                        Vector3 nv = Vector3.Normalize(new Vector3(
                            MeshHeight(gi - 1, gj) - MeshHeight(gi + 1, gj), 2 * Cell,
                            MeshHeight(gi, gj - 1) - MeshHeight(gi, gj + 1)));
                        pos[j * w + i] = v;
                        nrm[j * w + i] = nv;
                        col[j * w + i] = GroundColor(gi, gj, nv);
                        lo = Vector3.Min(lo, v);
                        hi = Vector3.Max(hi, v);
                    }
                int k = 0;
                for (int j = 0; j < h - 1; j++)
                    for (int i = 0; i < w - 1; i++)
                    {
                        ushort a = (ushort)(j * w + i), b = (ushort)(j * w + i + 1);
                        ushort c = (ushort)((j + 1) * w + i + 1), d = (ushort)((j + 1) * w + i);
                        idx[k++] = a; idx[k++] = c; idx[k++] = b;
                        idx[k++] = a; idx[k++] = d; idx[k++] = c;
                    }
                Raylib.UploadMesh(ref mesh, false);
                Model model = Raylib.LoadModelFromMesh(mesh);
                model.Materials[0].Shader = shader;
                tiles.Add((model, lo, hi));
            }
    }

    // ---------------------------------------------------------------- drawing

    static void EnsureShader()
    {
        if (shader.Id != 0) return;
        shader = Raylib.LoadShaderFromMemory(Vs, Fs);
        locViewPos = Raylib.GetShaderLocation(shader, "viewPos");
        locFog = Raylib.GetShaderLocation(shader, "fogRange");
        locFogColor = Raylib.GetShaderLocation(shader, "fogColor");
        locMode = Raylib.GetShaderLocation(shader, "mode");
        locTime = Raylib.GetShaderLocation(shader, "time");
        locWater = Raylib.GetShaderLocation(shader, "waterColor");
        locStripes = Raylib.GetShaderLocation(shader, "stripes");
        locTint = Raylib.GetShaderLocation(shader, "tint");
        Sky.Bind(shader);
        RayTracing.Bind(shader);
    }

    // The land (depth-tested, writes depth). Call before the ground decals.
    public void Draw(Vector3 cameraPos, Color fog)
    {
        Rlgl.DrawRenderBatchActive();
        Vector3 fc = new(fog.R / 255f, fog.G / 255f, fog.B / 255f);
        Raylib.SetShaderValue(shader, locViewPos, cameraPos, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(shader, locFog, new Vector2(Scene3D.FogStart, Scene3D.FogEnd), ShaderUniformDataType.Vec2);
        Raylib.SetShaderValue(shader, locFogColor, fc, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(shader, locTime, (float)Raylib.GetTime(), ShaderUniformDataType.Float);
        Raylib.SetShaderValue(shader, locStripes, def.Stripes, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(shader, locTint, def.Tint, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(shader, locWater, V(def.Water), ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(shader, locMode, 0, ShaderUniformDataType.Int);

        Rlgl.DisableBackfaceCulling();
        float far = Scene3D.FogEnd + 200;
        foreach (var (model, lo, hi) in tiles)
        {
            // Skip tiles completely lost in the fog
            Vector3 closest = Vector3.Clamp(cameraPos, lo, hi);
            if (Vector3.Distance(closest, cameraPos) > far) continue;
            Raylib.DrawModel(model, Vector3.Zero, 1, Color.White);
        }

        if (HasWater)
        {
            // One big sheet at the water level: the land above it hides it, so lakes appear where the land dips
            Raylib.SetShaderValue(shader, locMode, 1, ShaderUniformDataType.Int);
            Raylib.BeginShaderMode(shader);
            Rlgl.DisableDepthMask();
            Vector2 a = Min, b = Max;
            float y = WaterLevel;
            Rlgl.Begin(DrawMode.Triangles);
            Rlgl.Color4ub(255, 255, 255, 255);
            Rlgl.Normal3f(0, 1, 0);
            Rlgl.Vertex3f(a.X, y, a.Y); Rlgl.Vertex3f(b.X, y, b.Y); Rlgl.Vertex3f(b.X, y, a.Y);
            Rlgl.Vertex3f(a.X, y, a.Y); Rlgl.Vertex3f(a.X, y, b.Y); Rlgl.Vertex3f(b.X, y, b.Y);
            Rlgl.End();
            Rlgl.DrawRenderBatchActive();
            Rlgl.EnableDepthMask();
            Raylib.EndShaderMode();
        }
        Rlgl.EnableBackfaceCulling();
    }

    public void Unload()
    {
        foreach (var t in tiles) Raylib.UnloadModel(t.Model);
        tiles.Clear();
    }

    public static void UnloadAll()
    {
        foreach (Terrain t in loaded) t.Unload();
        loaded.Clear();
        if (shader.Id != 0) Raylib.UnloadShader(shader);
        shader = default;
    }

    const string Vs = @"#version 330
in vec3 vertexPosition;
in vec3 vertexNormal;
in vec4 vertexColor;
uniform mat4 mvp;
out vec4 fragColor;
out vec3 worldPos;
out vec3 fragNormal;
void main() {
    fragColor = vertexColor;
    worldPos = vertexPosition;
    fragNormal = vertexNormal;
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}";

    // Land: vertex colours + small-scale variation, mowed stripes, sun with ray-traced shadows, fog.
    // Water (mode 1): moving ripples, sky reflection by Fresnel, sun glints.
    const string Fs = @"#version 330
in vec4 fragColor;
in vec3 worldPos;
in vec3 fragNormal;
uniform vec3 sunDir;
uniform vec3 viewPos;
uniform vec3 fogColor;
uniform vec2 fogRange;
uniform vec3 tint;
uniform vec3 waterColor;
uniform float stripes;
uniform float time;
uniform int mode;
" + Sky.Glsl + RayTracing.Glsl + @"
out vec4 finalColor;
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y);
}
void main() {
    float dist = length(viewPos - worldPos);
    float fog = fogAmount(dist);
    vec3 col;
    float alpha = 1.0;
    if (mode == 1) {
        vec2 q = worldPos.xz;
        float w1 = sin(q.x * 0.021 + time * 1.3) * sin(q.y * 0.017 - time * 1.1);
        float w2 = sin((q.x + q.y) * 0.047 + time * 2.4) * 0.5 + sin((q.x - q.y) * 0.081 - time * 3.1) * 0.3;
        vec3 n = normalize(vec3(0.06 * w1 + 0.05 * w2, 1.0, 0.05 * w2 - 0.04 * w1));
        vec3 v = normalize(viewPos - worldPos);
        float fres = 0.02 + 0.98 * pow(1.0 - max(dot(n, v), 0.0), 5.0);
        vec3 r = reflect(-v, n);
        // deep water colour, with the sky mirrored in it at grazing angles
        col = mix(waterColor * (0.85 + 0.25 * max(dot(n, -sunDir), 0.0)), skyColor(r), clamp(0.15 + fres * 0.9, 0.0, 0.9));
        col *= 0.96 + 0.06 * w2;
        float spec = pow(max(dot(r, -sunDir), 0.0), 220.0) * 3.0 + pow(max(dot(r, -sunDir), 0.0), 30.0) * 0.25;
        col += sunColor * spec;
        alpha = 0.9;
    } else {
        vec3 n = normalize(fragNormal);
        vec3 base = fragColor.rgb;
        // on steep ground let height play into the pattern too, so cliffs don't get stretched streaks
        vec2 q = worldPos.xz + worldPos.y * vec2(0.25, 0.2) * (1.0 - smoothstep(0.6, 0.9, n.y));
        // Bumpy relief the mesh is too coarse for: ridges and gullies from noise slopes,
        // strongest on steep ground (cliffs, mountain sides), gentle on the grass
        float steep = 1.0 - smoothstep(0.55, 0.9, n.y);
        vec2 e = vec2(6.0, 0.0);
        vec2 qa = q / 95.0, qb = q / 26.0;
        float h0 = vnoise(qa) + vnoise(qb) * 0.35;
        float hx = vnoise(qa + e.xy / 95.0) + vnoise(qb + e.xy / 26.0) * 0.35;
        float hz = vnoise(qa + e.yx / 95.0) + vnoise(qb + e.yx / 26.0) * 0.35;
        vec2 grad = vec2(hx - h0, hz - h0) / 6.0 * 95.0;
        n = normalize(n + vec3(-grad.x, 0.0, -grad.y) * (0.05 + 0.30 * steep));
        // Snow only settles on gentler slopes: rock shows through on the steep faces
        float lumB = dot(base, vec3(0.333));
        float snowy = smoothstep(0.78, 0.88, lumB) * (1.0 - smoothstep(0.03, 0.08, max(base.r, max(base.g, base.b)) - min(base.r, min(base.g, base.b))));
        float bare = snowy * smoothstep(0.72, 0.52, n.y + (h0 - 0.65) * 0.25);
        base = mix(base, vec3(0.40, 0.39, 0.38) * (0.85 + 0.3 * h0), bare);
        // detail fades out with distance so far hills don't shimmer
        float fine = 1.0 - smoothstep(150.0, 900.0, dist);
        float mid = 1.0 - smoothstep(900.0, 4000.0, dist);
        float g = vnoise(q / 41.0) * 0.6 + vnoise(q / 11.0) * 0.4;
        float macro = vnoise(q / 420.0) * 0.6 + vnoise(q / 160.0) * 0.4;
        base *= 0.9 + 0.18 * mix(0.5, g, mid) + 0.14 * (macro - 0.5);
        // grassy colours get drier, yellower patches and greener damp hollows
        float grassy = smoothstep(0.02, 0.08, base.g - max(base.r, base.b));
        float dry = smoothstep(0.45, 0.8, vnoise(q / 300.0 + 3.7) * 0.7 + vnoise(q / 70.0) * 0.3);
        base = mix(base, base * vec3(1.25, 1.08, 0.72), dry * grassy * 0.6);
        // clumps: darker, bluer-green tufts against lighter, yellower blades
        float clump = vnoise(q / 5.5) * 0.6 + vnoise(q / 17.0) * 0.4;
        base = mix(base, base * mix(vec3(0.82, 0.88, 0.86), vec3(1.10, 1.06, 0.92), clump), grassy * mix(0.25, 0.7, fine));
        // blades and pebbles up close
        float blades = (vnoise(q / 1.3) - 0.5) * 0.6 + (vnoise(q / 3.7) - 0.5) * 0.4;
        base *= 1.0 + blades * 0.16 * fine;
        if (stripes > 0.0) {
            float sx = mod(floor(worldPos.x / 150.0), 2.0), sz = mod(floor(worldPos.z / 150.0), 2.0);
            base *= 1.0 - stripes * fragColor.a * (sx + sz);
        }
        // Sun light, scaled so flat ground looks as designed whatever the sun's height,
        // plus sky light that is bluer in the shade
        float flatLit = max(-sunDir.y, 0.15);
        float ndl = max(dot(n, -sunDir), 0.0);
        if (rtOn == 1 && ndl > 0.0 && fog < 1.0) ndl *= 1.0 - 0.9 * rtShadow(worldPos, n);
        float sunLit = min(ndl / flatLit, 1.7);
        vec3 skyAmb = mix(skyHorizon, skyZenith, 0.55);
        vec3 amb = mix(vec3(1.0), skyAmb / max(dot(skyAmb, vec3(0.333)), 0.05), 0.35) * (0.48 + 0.12 * n.y);
        col = base * (amb + 0.45 * sunLit * sunColor / max(dot(sunColor, vec3(0.333)), 0.05));
    }
    col *= tint;
    finalColor = vec4(applyFog(col, worldPos), alpha);
}";
}

// Smooth value noise, used to shape the land and scatter scenery (same result every run)
static class Noise
{
    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xffffff) / (float)0xffffff;
        }
    }

    // -1..1
    public static float Value(Vector2 p, int seed)
    {
        int xi = (int)MathF.Floor(p.X), yi = (int)MathF.Floor(p.Y);
        float fx = p.X - xi, fy = p.Y - yi;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
        return (a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy) * 2 - 1;
    }

    // Several octaves of value noise, roughly -1..1
    public static float Fbm(Vector2 p, int seed, int octaves)
    {
        float sum = 0, amp = 0.5f, norm = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += Value(p, seed + o * 31) * amp;
            norm += amp;
            amp *= 0.5f;
            p = p * 2.03f + new Vector2(17.3f, -9.1f);
        }
        return sum / norm;
    }
}
