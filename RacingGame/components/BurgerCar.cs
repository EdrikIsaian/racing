using System.Numerics;
using System.Text.Json;
using Raylib_cs;

// The sixth car: a burger on wheels with two cartoon riders. blender/build_burgercar.py builds the whole
// model (mesh, texture atlas, sizes and ray-tracing proxy boxes); this just loads it.
// Model space matches the Blender cars: metres, x forward, y up, z right, origin between the axles.
static unsafe class BurgerCar
{
    public static Texture2D Texture { get; private set; }
    public static Texture2D Icon { get; private set; } // the picture, for the menu
    public static float WheelRadius, WheelWidth, Wheelbase, Track, Length, Width;

    // Ray-tracing proxy: boxes inside the bun, the patty and each rider (model space, metres)
    public static readonly List<(Vector3 Centre, Vector3 Half, Color Colour)> Proxy = new();

    public static Model Build(string dir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "burgercar.json")));
        JsonElement j = doc.RootElement;
        float F(string name) => j.GetProperty(name).GetSingle();
        WheelRadius = F("wheelRadius"); WheelWidth = F("wheelWidth");
        Wheelbase = F("wheelbase"); Track = F("track");
        Length = F("length"); Width = F("width");
        Proxy.Clear();
        foreach (JsonElement b in j.GetProperty("proxy").EnumerateArray())
        {
            float[] c = b.GetProperty("centre").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            float[] h = b.GetProperty("half").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            int[] col = b.GetProperty("colour").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            Proxy.Add((new Vector3(c[0], c[1], c[2]), new Vector3(h[0], h[1], h[2]), new Color(col[0], col[1], col[2], 255)));
        }

        using var r = new BinaryReader(File.OpenRead(Path.Combine(dir, "burgercar.bin")));
        int vertexCount = r.ReadInt32(), indexCount = r.ReadInt32();
        if (vertexCount > ushort.MaxValue) throw new InvalidDataException("burgercar.bin: too many vertices for one mesh");
        float[] Floats(int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = r.ReadSingle(); return a; }
        float[] pos = Floats(vertexCount * 3), nrm = Floats(vertexCount * 3), uv = Floats(vertexCount * 2);
        var idx = new ushort[indexCount];
        for (int i = 0; i < indexCount; i++) idx[i] = (ushort)r.ReadUInt32();

        Mesh mesh = new() { VertexCount = vertexCount, TriangleCount = indexCount / 3 };
        mesh.AllocVertices();
        mesh.AllocNormals();
        mesh.AllocTexCoords();
        mesh.AllocIndices();
        pos.CopyTo(mesh.VerticesAs<float>());
        nrm.CopyTo(mesh.NormalsAs<float>());
        uv.CopyTo(mesh.TexCoordsAs<float>());
        idx.CopyTo(mesh.IndicesAs<ushort>());
        Raylib.UploadMesh(ref mesh, false);

        Texture2D tex = Raylib.LoadTexture(Path.Combine(dir, "burgercar.png"));
        Raylib.GenTextureMipmaps(ref tex);
        Raylib.SetTextureFilter(tex, TextureFilter.Trilinear);
        Raylib.SetTextureWrap(tex, TextureWrap.Clamp);
        Texture = tex;

        Icon = Raylib.LoadTexture(Path.Combine(dir, "burgercar_icon.png"));
        Texture2D icon = Icon;
        Raylib.GenTextureMipmaps(ref icon);
        Raylib.SetTextureFilter(icon, TextureFilter.Trilinear);
        Icon = icon;

        Model model = Raylib.LoadModelFromMesh(mesh);
        model.Materials[0].Maps[(int)MaterialMapIndex.Albedo].Texture = Texture;
        return model;
    }

    public static void Unload()
    {
        if (Texture.Id != 0) Raylib.UnloadTexture(Texture);
        if (Icon.Id != 0) Raylib.UnloadTexture(Icon);
    }
}
