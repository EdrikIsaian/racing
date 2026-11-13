using System.Numerics;
using System.Text.Json;
using Raylib_cs;

// The fifth car: a giant head on wheels, built at load time from a photo.
//
// blender/build_headcar.py cleans the photo (assets/models/headcar.png) and traces the head's side
// profile (assets/models/headcar.json). Here that profile is inflated into a 3D body: the further a
// point is from the outline, the wider the body is there, so it rounds over on every edge and thin
// parts (the nose, the chin) stay thin. The photo is projected straight through from the side; where
// the surface turns away from the side, Scene3D blurs it so stretched pixels don't show as stripes.
//
// Model space matches the Blender cars: metres, x forward, y up, z right, ground at y = 0,
// origin halfway between the axles (so Scene3D can place the wheels like any other car).
static unsafe class HeadCar
{
    const int Ring = 48;              // vertices around each cross-section
    const int Step = 2;               // pixel columns per ring
    const float HalfWidth = 1.0f;     // metres, also how far in from the outline the sides are full width
    const float BottomRound = 0.22f;  // metres: radius of the rounded edge along the sill
    const float LowerEdge = 2f;       // the head's lower edges (jaw, chin) round over this much more gently
    const float ThinWidth = 1.1f;     // short parts of the profile are this wide for their height
    const float SillLine = 330;       // photo y: lower edges below this are the car's sill and arches

    public static Texture2D Texture { get; private set; }
    public static Texture2D Icon { get; private set; }      // the original photo, transparent background
    public static Rectangle PhotoArea { get; private set; } // the car inside the photo, for the menu icon
    public static float WheelRadius, Wheelbase, Length, Width;
    public static Color Skin;
    public const float EdgeBlur = 3.5f; // mipmap levels of blur on surfaces facing forwards, back or up

    // Ray-tracing proxy: a few boxes along the car that enclose the head (model space, metres)
    public static readonly List<(Vector3 Centre, Vector3 Half)> Proxy = new();

    public static Model Build(string dir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "headcar.json")));
        JsonElement j = doc.RootElement;
        int imgW = j.GetProperty("imageWidth").GetInt32(), imgH = j.GetProperty("imageHeight").GetInt32();
        float groundY = j.GetProperty("groundY").GetSingle();
        float mpp = j.GetProperty("metresPerPixel").GetSingle();
        int x0 = j.GetProperty("x0").GetInt32();
        float[] top = j.GetProperty("top").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        float[] bottom = j.GetProperty("bottom").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var wheels = j.GetProperty("wheels").EnumerateArray().Select(w => (X: w.GetProperty("x").GetSingle(), R: w.GetProperty("r").GetSingle())).ToArray();
        int[] skin = j.GetProperty("skin").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Skin = new Color(skin[0], skin[1], skin[2], 255);

        float originX = (wheels[0].X + wheels[1].X) / 2;
        WheelRadius = (wheels[0].R + wheels[1].R) / 2 * mpp;
        Wheelbase = Math.Abs(wheels[1].X - wheels[0].X) * mpp;
        Length = (top.Length - 1) * mpp;
        Width = HalfWidth * 2;
        float minTop = top.Min();
        PhotoArea = new Rectangle(x0, minTop, top.Length, groundY - minTop);

        // The silhouette's outline, for the inflation below. Under the wheel arches and along the sill the
        // body should stay full like a car's, so only the head's own lower edges (jaw, chin, under the nose)
        // count there.
        int n = top.Length;
        // Smooth out the tracing's wiggles (they would ripple the whole side), then round off both ends
        // so the back of the skull and the nose close in a dome instead of a vertical edge
        top = Smooth(top, 6); bottom = Smooth(bottom, 4);
        foreach (bool front in new[] { false, true })
        {
            int end = front ? n - 1 : 0;
            float reach = (bottom[end] - top[end]) / 2;
            for (int step = 0; step < reach && step < n / 4; step++)
            {
                int k = front ? n - 1 - step : step;
                float mid = (top[k] + bottom[k]) / 2, f = Quarter(step / reach);
                top[k] = mid - (mid - top[k]) * f;
                bottom[k] = mid + (bottom[k] - mid) * f;
            }
        }
        var outline = new List<Vector3>(); // x, y, and how much a distance to it counts
        for (int k = 0; k < n; k++)
        {
            float px = x0 + k;
            outline.Add(new Vector3(px, top[k], 1));
            bool arch = wheels.Any(w => Math.Abs(px - w.X) < w.R + 8);
            if (bottom[k] < SillLine && !arch) outline.Add(new Vector3(px, bottom[k], LowerEdge));
        }
        foreach (int k in new[] { 0, n - 1 })
            for (float y = top[k]; y <= bottom[k]; y += 1) outline.Add(new Vector3(x0 + k, y, 1));
        Vector3[] outlinePts = outline.ToArray();

        // How tall the head is at each column, for how tightly it rounds over (below). Wheel arches
        // don't count, and it changes slowly, or the body would bulge in and out over the wheels.
        var height = new float[n];
        for (int k = 0; k < n; k++)
        {
            bool arch = wheels.Any(w => Math.Abs(x0 + k - w.X) < w.R + 8);
            height[k] = (arch ? SillLine : Math.Min(bottom[k], SillLine)) - top[k];
        }
        height = Smooth(height, 6);

        // Thickness: the silhouette is "inflated" like a balloon. A point's half-width grows with its
        // distance from the outline along a quarter circle, so every edge rounds over (seen from the front,
        // the top or the back) and thin parts of the profile, like the nose, stay thin.
        float roundPx = HalfWidth / mpp, bottomPx = BottomRound / mpp;
        float HalfWidthAt(int k, float px, float py, float b)
        {
            float best = float.MaxValue;
            foreach (Vector3 o in outlinePts)
            {
                float dx = (o.X - px) * o.Z;
                if (dx * dx >= best) continue;
                float dy = (o.Y - py) * o.Z;
                best = Math.Min(best, dx * dx + dy * dy);
            }
            // Where the profile is short (nose, chin, the back of the skull) the rounding is tighter, so
            // those parts end up about as wide as they are tall rather than ballooning out
            float r = Math.Max(Math.Min(roundPx, ThinWidth * height[k] / 2), 0.5f);
            return r * mpp * Quarter(MathF.Sqrt(best) / r) * Quarter((b - py) / bottomPx);
        }

        int cols = (n - 1) / Step + 1;
        int vertexCount = cols * Ring;
        var pos = new Vector3[vertexCount];
        var uv = new Vector2[vertexCount];
        for (int c = 0; c < cols; c++)
        {
            int k = Math.Min(c * Step, n - 1);
            float px = x0 + k, t = top[k], b = Math.Max(bottom[k], top[k] + 0.5f);
            float x = (px - originX) * mpp;
            for (int i = 0; i < Ring; i++)
            {
                // Round the ring from the top (i = 0) down the right side, along the bottom and back up the
                // left; cosine spacing puts more vertices where the surface turns over the top and bottom
                float a = i * MathF.Tau / Ring;
                float py = (t + b) / 2 - (b - t) / 2 * MathF.Cos(a);
                pos[c * Ring + i] = new Vector3(x, (groundY - py) * mpp, HalfWidthAt(k, px, py, b));
                // The photo, projected straight through from the side (just inside the anti-aliased outline)
                uv[c * Ring + i] = new Vector2(Math.Clamp(px, x0 + 2, x0 + n - 3) / imgW, Math.Clamp(py, t + Math.Min(2, (b - t) * 0.4f), b - Math.Min(1, (b - t) * 0.2f)) / imgH);
            }
        }

        // Even out the width along the length (the nearest outline point jumps around, which ripples the
        // sides), then put each vertex on its side
        var width = new float[cols];
        for (int i = 0; i < Ring; i++)
        {
            for (int c = 0; c < cols; c++) width[c] = pos[c * Ring + i].Z;
            width = Smooth(width, 6);
            float sin = MathF.Sin(i * MathF.Tau / Ring);
            float side = MathF.Abs(sin) < 1e-4f ? 0 : MathF.Sign(sin);
            for (int c = 0; c < cols; c++) pos[c * Ring + i].Z = side * width[c];
        }

        var idx = new List<ushort>((cols - 1) * Ring * 6);
        void Tri(int a, int b, int c) { idx.Add((ushort)a); idx.Add((ushort)b); idx.Add((ushort)c); }
        for (int c = 0; c < cols - 1; c++)
            for (int i = 0; i < Ring; i++)
            {
                int a = c * Ring + i, b = c * Ring + (i + 1) % Ring;
                int d = a + Ring, e = b + Ring;
                Tri(a, b, e);
                Tri(a, e, d);
            }

        // Face the triangles outwards (check one on the right side of the middle section)
        {
            int c = cols / 2, a = c * Ring + Ring / 4, b = a + 1, e = b + Ring;
            Vector3 nn = Vector3.Cross(pos[b] - pos[a], pos[e] - pos[a]);
            if (nn.Z < 0)
                for (int i = 0; i < idx.Count; i += 3) (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
        }

        // Smooth normals
        var nrm = new Vector3[vertexCount];
        for (int i = 0; i < idx.Count; i += 3)
        {
            Vector3 fn = Vector3.Cross(pos[idx[i + 1]] - pos[idx[i]], pos[idx[i + 2]] - pos[idx[i]]);
            nrm[idx[i]] += fn; nrm[idx[i + 1]] += fn; nrm[idx[i + 2]] += fn;
        }
        for (int i = 0; i < vertexCount; i++) nrm[i] = nrm[i].LengthSquared() > 0 ? Vector3.Normalize(nrm[i]) : Vector3.UnitY;

        Mesh mesh = new() { VertexCount = vertexCount, TriangleCount = idx.Count / 3 };
        mesh.AllocVertices();
        mesh.AllocNormals();
        mesh.AllocTexCoords();
        mesh.AllocIndices();
        pos.CopyTo(mesh.VerticesAs<Vector3>());
        nrm.CopyTo(mesh.NormalsAs<Vector3>());
        uv.CopyTo(mesh.TexCoordsAs<Vector2>());
        idx.ToArray().CopyTo(mesh.IndicesAs<ushort>());
        Raylib.UploadMesh(ref mesh, false);

        Texture = Raylib.LoadTexture(Path.Combine(dir, "headcar.png"));
        Texture2D tex = Texture;
        Raylib.GenTextureMipmaps(ref tex);
        Texture = tex;
        Raylib.SetTextureFilter(Texture, TextureFilter.Trilinear);
        Raylib.SetTextureWrap(Texture, TextureWrap.Clamp);

        Icon = Raylib.LoadTexture(Path.Combine(dir, "headcar_icon.png"));
        Raylib.SetTextureFilter(Icon, TextureFilter.Bilinear);

        Model model = Raylib.LoadModelFromMesh(mesh);
        model.Materials[0].Maps[(int)MaterialMapIndex.Albedo].Texture = Texture;

        BuildProxy(pos, cols);
        return model;
    }

    // Four boxes along the length, each fitted just inside its stretch of the body. Inside, not around:
    // a shadow ray leaving the body's surface must not hit the car's own boxes, or the steps between
    // them throw stripes of shadow over the roof.
    static void BuildProxy(Vector3[] pos, int cols)
    {
        Proxy.Clear();
        // Per cross-section: a rectangle that fits inside it with a margin to spare: 60% of the full
        // width, and the heights where the body is at least that wide plus the margin
        const float margin = 0.12f;
        var inner = new (float X, float Lo, float Hi, float Half)[cols];
        for (int c = 0; c < cols; c++)
        {
            float maxZ = 0;
            for (int i = 0; i < Ring; i++) maxZ = Math.Max(maxZ, pos[c * Ring + i].Z);
            float half = 0.6f * maxZ, lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < Ring; i++)
            {
                Vector3 v = pos[c * Ring + i];
                if (v.Z < half + margin) continue;
                lo = Math.Min(lo, v.Y); hi = Math.Max(hi, v.Y);
            }
            inner[c] = (pos[c * Ring].X, lo + margin, hi - margin, half);
        }
        // Leave out the narrow tips (nose, back of the skull)
        int first = Array.FindIndex(inner, s => s.Half > 0.45f && s.Hi > s.Lo);
        int last = Array.FindLastIndex(inner, s => s.Half > 0.45f && s.Hi > s.Lo);
        const int parts = 4;
        for (int p = 0; p < parts; p++)
        {
            int c0 = first + p * (last - first) / parts, c1 = first + (p + 1) * (last - first) / parts;
            var seg = inner[c0..(c1 + 1)];
            // Inside every cross-section it spans
            float lo = seg.Max(s => s.Lo), hi = seg.Min(s => s.Hi), half = seg.Min(s => s.Half);
            if (hi <= lo) continue;
            Proxy.Add((new Vector3((seg[0].X + seg[^1].X) / 2, (lo + hi) / 2, 0),
                       new Vector3((seg[^1].X - seg[0].X) / 2, (hi - lo) / 2, half)));
        }
    }

    // Gaussian blur of a 1D array (edges held)
    static float[] Smooth(float[] v, float sigma)
    {
        int r = (int)MathF.Ceiling(sigma * 2.5f);
        var w = Enumerable.Range(-r, 2 * r + 1).Select(j => MathF.Exp(-j * j / (2 * sigma * sigma))).ToArray();
        var o = new float[v.Length];
        for (int k = 0; k < v.Length; k++)
        {
            float sum = 0, ws = 0;
            for (int j = -r; j <= r; j++) { sum += v[Math.Clamp(k + j, 0, v.Length - 1)] * w[j + r]; ws += w[j + r]; }
            o[k] = sum / ws;
        }
        return o;
    }

    // A quarter circle: 0 at the edge, rising steeply, levelling off at 1
    static float Quarter(float s)
    {
        s = Math.Clamp(s, 0, 1);
        return MathF.Sqrt(1 - (1 - s) * (1 - s));
    }

    public static void Unload()
    {
        if (Texture.Id != 0) Raylib.UnloadTexture(Texture);
        if (Icon.Id != 0) Raylib.UnloadTexture(Icon);
    }
}
