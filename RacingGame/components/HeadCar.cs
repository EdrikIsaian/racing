using System.Numerics;
using System.Text.Json;
using Raylib_cs;

// The fifth car: a giant head on wheels, built at load time from a photo.
//
// blender/build_headcar.py cleans the photo (assets/models/headcar.png) and traces the head's side
// profile (assets/models/headcar.json). Here that profile becomes a 3D body: every pixel column is a
// rounded cross-section as tall as the head at that point, and narrower where the head is short
// (the nose, the back of the skull). The photo is projected straight through from the side, so both
// sides show the face and the top picks up the hair.
//
// Model space matches the Blender cars: metres, x forward, y up, z right, ground at y = 0,
// origin halfway between the axles (so Scene3D can place the wheels like any other car).
static unsafe class HeadCar
{
    const int Ring = 40;              // vertices around each cross-section
    const int Step = 2;               // pixel columns per ring
    const float HalfWidth = 1.0f;     // metres, at the head's tallest
    const float Roundness = 3.2f;     // superellipse exponent of the cross-section (2 = ellipse)

    public static Texture2D Texture { get; private set; }
    public static Texture2D Icon { get; private set; }      // the original photo, transparent background
    public static Rectangle PhotoArea { get; private set; } // the car inside the photo, for the menu icon
    public static float WheelRadius, Wheelbase, Length, Width;
    public static Color Skin;

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

        // Cross-sections
        int cols = (top.Length - 1) / Step + 1;
        float maxH = 0;
        for (int c = 0; c < top.Length; c++) maxH = Math.Max(maxH, bottom[c] - top[c]);

        int vertexCount = cols * Ring + 2;
        int triCount = (cols - 1) * Ring * 2 + Ring * 2;
        var pos = new Vector3[vertexCount];
        var uv = new Vector2[vertexCount];
        var halfW = new float[cols];

        for (int c = 0; c < cols; c++)
        {
            int k = Math.Min(c * Step, top.Length - 1);
            float px = x0 + k, t = top[k], b = bottom[k];
            float h = Math.Max(b - t, 0.5f);
            float x = (px - originX) * mpp;
            float yc = (groundY - (t + b) / 2) * mpp, hh = h / 2 * mpp;
            // Short parts of the profile (nose, back of the skull) are narrow; sqrt keeps the ends round
            float hw = HalfWidth * MathF.Sqrt(Math.Clamp(h / (0.65f * maxH), 0.02f, 1f));
            halfW[c] = hw;
            for (int i = 0; i < Ring; i++)
            {
                float a = i * MathF.Tau / Ring;
                float z = hw * SignedPow(MathF.Cos(a), 2 / Roundness);
                float y = yc + hh * SignedPow(MathF.Sin(a), 2 / Roundness);
                pos[c * Ring + i] = new Vector3(x, y, z);
                // Straight side projection, kept inside the outline: the top picks up the hair rather than
                // the anti-aliased edge, the back of the head the skin rather than the photo's last column
                float py = Math.Clamp(groundY - y / mpp, t + Math.Min(10, h * 0.4f), b - Math.Min(3, h * 0.2f));
                uv[c * Ring + i] = new Vector2(Math.Clamp(px, x0 + 22, x0 + top.Length - 8) / imgW, py / imgH);
            }
        }

        // End caps: a point just past each end, in the middle of the last cross-section
        int back = cols * Ring, front = back + 1;
        for (int e = 0; e < 2; e++)
        {
            int c = e == 0 ? 0 : cols - 1;
            Vector3 centre = Vector3.Zero;
            for (int i = 0; i < Ring; i++) centre += pos[c * Ring + i];
            centre /= Ring;
            centre.X += (e == 0 ? -1 : 1) * 0.6f * halfW[c] * 0.3f;
            pos[back + e] = centre;
            int k = Math.Min(c * Step, top.Length - 1);
            uv[back + e] = new Vector2((x0 + k + (e == 0 ? 22 : -8)) / (float)imgW, (top[k] + bottom[k]) / 2 / imgH);
        }

        var idx = new List<ushort>(triCount * 3);
        void Tri(int a, int b, int c) { idx.Add((ushort)a); idx.Add((ushort)b); idx.Add((ushort)c); }
        for (int c = 0; c < cols - 1; c++)
            for (int i = 0; i < Ring; i++)
            {
                int a = c * Ring + i, b = c * Ring + (i + 1) % Ring;
                int d = a + Ring, e = b + Ring;
                Tri(a, b, e);
                Tri(a, e, d);
            }
        for (int i = 0; i < Ring; i++)
        {
            Tri(back, (i + 1) % Ring, i);
            int l = (cols - 1) * Ring;
            Tri(front, l + i, l + (i + 1) % Ring);
        }

        // Face the triangles outwards (check one on the side of the middle section)
        {
            int c = cols / 2, a = c * Ring, b = a + 1, e = b + Ring;
            Vector3 n = Vector3.Cross(pos[b] - pos[a], pos[e] - pos[a]);
            if (n.Z < 0)
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

    // Four boxes along the length, each wrapping its stretch of the body
    static void BuildProxy(Vector3[] pos, int cols)
    {
        Proxy.Clear();
        const int parts = 4;
        for (int p = 0; p < parts; p++)
        {
            int c0 = p * (cols - 1) / parts, c1 = (p + 1) * (cols - 1) / parts;
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            for (int c = c0; c <= c1; c++)
                for (int i = 0; i < Ring; i++)
                {
                    lo = Vector3.Min(lo, pos[c * Ring + i]);
                    hi = Vector3.Max(hi, pos[c * Ring + i]);
                }
            if (p == 0) lo.X = pos[cols * Ring].X;
            if (p == parts - 1) hi.X = pos[cols * Ring + 1].X;
            Vector3 half = (hi - lo) / 2 + new Vector3(0.01f);
            Proxy.Add(((lo + hi) / 2, half));
        }
    }

    static float SignedPow(float v, float e) => MathF.Sign(v) * MathF.Pow(MathF.Abs(v), e);

    public static void Unload()
    {
        if (Texture.Id != 0) Raylib.UnloadTexture(Texture);
        if (Icon.Id != 0) Raylib.UnloadTexture(Icon);
    }
}
