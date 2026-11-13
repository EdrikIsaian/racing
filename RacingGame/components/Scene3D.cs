using System.Numerics;
using System.Text.Json;
using Raylib_cs;

// Everything 3D: the Blender models (assets/models, built by blender/build_models.py),
// the lighting/fog shaders and helpers to draw cars, scenery and flat ground geometry.
//
// The game logic stays 2D: a world point (x, y) sits at 3D (x, 0, y) with Y up.
// World units are the old "pixels"; the models are in metres and scaled by UnitsPerMetre.
static unsafe class Scene3D
{
    public const float UnitsPerMetre = 8.6f;

    // Light comes from the upper left-ish; matches the blob shadows on the ground
    public static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(0.45f, -1f, 0.32f));
    public static readonly Color SkyTop = new(92, 148, 214, 255);
    public static readonly Color Horizon = new(196, 214, 228, 255);
    public static float FogStart = 1800, FogEnd = 6500;

    record struct CarModels(Model Paint, Model Detail, Model Tail, float WheelRadius, float WheelWidth, float Wheelbase, float Track, float Length, float Width);

    static readonly Dictionary<CarType, CarModels> cars = new();
    static Model wheel, pine, round, gantry, platform;
    static Shader lit, ground, rtShadow;
    static int litViewPos, litFog, litFogColor, litReflect, litTexBlur, litTexSide, groundViewPos, groundFog, groundFogColor, rtViewPos, rtFog;
    static bool loaded;

    public static Vector3 W(Vector2 p, float height = 0) => new(p.X, height, p.Y);

    // ---------------------------------------------------------------- loading

    public static void Load()
    {
        lit = Raylib.LoadShaderFromMemory(LitVs, LitFs);
        ground = Raylib.LoadShaderFromMemory(GroundVs, GroundFs);
        rtShadow = Raylib.LoadShaderFromMemory(GroundVs, RayTracing.ShadowFs);
        litReflect = Raylib.GetShaderLocation(lit, "reflectivity");
        litTexBlur = Raylib.GetShaderLocation(lit, "texBlur");
        litTexSide = Raylib.GetShaderLocation(lit, "texSide");
        rtViewPos = Raylib.GetShaderLocation(rtShadow, "viewPos");
        rtFog = Raylib.GetShaderLocation(rtShadow, "fogRange");
        Raylib.SetShaderValue(rtShadow, Raylib.GetShaderLocation(rtShadow, "sunDir"), SunDir, ShaderUniformDataType.Vec3);
        RayTracing.Bind(lit);
        RayTracing.Bind(rtShadow);
        litViewPos = Raylib.GetShaderLocation(lit, "viewPos");
        litFog = Raylib.GetShaderLocation(lit, "fogRange");
        litFogColor = Raylib.GetShaderLocation(lit, "fogColor");
        groundViewPos = Raylib.GetShaderLocation(ground, "viewPos");
        groundFog = Raylib.GetShaderLocation(ground, "fogRange");
        groundFogColor = Raylib.GetShaderLocation(ground, "fogColor");
        Raylib.SetShaderValue(lit, Raylib.GetShaderLocation(lit, "sunDir"), SunDir, ShaderUniformDataType.Vec3);

        string dir = Path.Combine(AppContext.BaseDirectory, "assets", "models");
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "cars.json")));
        foreach (var (type, key) in new[] { (CarType.SUV, "suv"), (CarType.Sedan, "sedan"), (CarType.SportsCar, "sports"), (CarType.HyperCar, "hyper") })
        {
            JsonElement m = meta.RootElement.GetProperty(key);
            float F(string name) => m.GetProperty(name).GetSingle();
            cars[type] = new CarModels(
                LoadLit(dir, $"car_{key}_paint.glb"), LoadLit(dir, $"car_{key}_detail.glb"), LoadLit(dir, $"car_{key}_tail.glb"),
                F("wheelRadius"), F("wheelWidth"), F("wheelbase"), F("track"), F("length"), F("width"));
        }
        Model head = HeadCar.Build(dir);
        UseLitShader(ref head);
        cars[CarType.HeadCar] = new CarModels(head, default, default, HeadCar.WheelRadius, 0.30f, HeadCar.Wheelbase, 1.72f, HeadCar.Length, HeadCar.Width);
        Model burger = BurgerCar.Build(dir);
        UseLitShader(ref burger);
        cars[CarType.BurgerCar] = new CarModels(burger, default, default, BurgerCar.WheelRadius, BurgerCar.WheelWidth, BurgerCar.Wheelbase, BurgerCar.Track, BurgerCar.Length, BurgerCar.Width);
        wheel = LoadLit(dir, "wheel.glb");
        pine = LoadLit(dir, "tree_pine.glb");
        round = LoadLit(dir, "tree_round.glb");
        gantry = LoadLit(dir, "gantry.glb");

        platform = Raylib.LoadModelFromMesh(Raylib.GenMeshCylinder(1, 1, 64));
        UseLitShader(ref platform);
        loaded = true;
    }

    static Model LoadLit(string dir, string file)
    {
        Model m = Raylib.LoadModel(Path.Combine(dir, file));
        UseLitShader(ref m);
        return m;
    }

    static void UseLitShader(ref Model m)
    {
        for (int i = 0; i < m.MaterialCount; i++) m.Materials[i].Shader = lit;
    }

    public static void Unload()
    {
        if (!loaded) return;
        foreach (var c in cars.Values)
        {
            Raylib.UnloadModel(c.Paint);
            if (c.Detail.MeshCount > 0) Raylib.UnloadModel(c.Detail);
            if (c.Tail.MeshCount > 0) Raylib.UnloadModel(c.Tail);
        }
        HeadCar.Unload();
        BurgerCar.Unload();
        Raylib.UnloadModel(wheel);
        Raylib.UnloadModel(pine);
        Raylib.UnloadModel(round);
        Raylib.UnloadModel(gantry);
        Raylib.UnloadModel(platform);
        Raylib.UnloadShader(lit);
        Raylib.UnloadShader(ground);
        Raylib.UnloadShader(rtShadow);
        RayTracing.Unbind();
        Showcase.Unload();
    }

    // Call once per frame (per camera) before drawing anything 3D
    public static void SetView(Vector3 cameraPos, float fogStart, float fogEnd, Color fog)
    {
        Vector2 range = new(fogStart, fogEnd);
        Vector3 fc = new(fog.R / 255f, fog.G / 255f, fog.B / 255f);
        Raylib.SetShaderValue(lit, litViewPos, cameraPos, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(lit, litFog, range, ShaderUniformDataType.Vec2);
        Raylib.SetShaderValue(lit, litFogColor, fc, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(ground, groundViewPos, cameraPos, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(ground, groundFog, range, ShaderUniformDataType.Vec2);
        Raylib.SetShaderValue(ground, groundFogColor, fc, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(rtShadow, rtViewPos, cameraPos, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(rtShadow, rtFog, range, ShaderUniformDataType.Vec2);
    }

    // How mirror-like the next models are (ray-traced reflections; 0 = matte)
    static void Reflect(float amount) => Raylib.SetShaderValue(lit, litReflect, amount, ShaderUniformDataType.Float);

    // Ray-traced sun shadows on the ground, inside the ground pass after everything flat is drawn.
    // Covers a disc around `centre`; the scene must have been sent with RayTracing.Upload.
    public static void RayShadows(Vector2 centre, float radius)
    {
        if (!RayTracing.Enabled) return;
        Rlgl.DrawRenderBatchActive();
        Raylib.BeginShaderMode(rtShadow);
        Disc(centre, radius, radius, new Vector2(1, 0), Color.Black, 48);
        Rlgl.DrawRenderBatchActive();
        Raylib.BeginShaderMode(ground);
    }

    // ---------------------------------------------------------------- ground pass
    // Flat things on the ground (grass, track, kerbs, skid marks, shadows) are all at height 0.
    // They are drawn first, in order, without the depth buffer, so they can never flicker
    // against each other; everything standing on top is drawn afterwards with depth.

    public static void BeginGround()
    {
        Rlgl.DrawRenderBatchActive();
        Raylib.BeginShaderMode(ground);
        Rlgl.DisableDepthTest();
        Rlgl.DisableBackfaceCulling();
    }

    public static void EndGround()
    {
        Rlgl.DrawRenderBatchActive();
        Raylib.EndShaderMode();
        Rlgl.EnableDepthTest();
        Rlgl.EnableBackfaceCulling();
    }

    public static void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color, float y = 0)
    {
        Rlgl.CheckRenderBatchLimit(6);
        Rlgl.Begin(DrawMode.Triangles);
        Rlgl.Color4ub(color.R, color.G, color.B, color.A);
        V(a, y); V(b, y); V(c, y);
        V(a, y); V(c, y); V(d, y);
        Rlgl.End();
    }

    // Rectangle centred on `c`, `length` along `f`, `width` across it
    public static void Box(Vector2 c, Vector2 f, float length, float width, Color color, float y = 0)
    {
        Vector2 r = new(-f.Y, f.X);
        Vector2 hl = f * length / 2, hw = r * width / 2;
        Quad(c - hl - hw, c + hl - hw, c + hl + hw, c - hl + hw, color, y);
    }

    public static void Disc(Vector2 c, float rx, float ry, Vector2 f, Color color, int segments = 20, float y = 0)
    {
        Vector2 r = new(-f.Y, f.X);
        Rlgl.CheckRenderBatchLimit(segments * 3);
        Rlgl.Begin(DrawMode.Triangles);
        Rlgl.Color4ub(color.R, color.G, color.B, color.A);
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
            V(c, y);
            V(c + f * MathF.Cos(a0) * rx + r * MathF.Sin(a0) * ry, y);
            V(c + f * MathF.Cos(a1) * rx + r * MathF.Sin(a1) * ry, y);
        }
        Rlgl.End();
    }

    static void V(Vector2 p, float y) => Rlgl.Vertex3f(p.X, y, p.Y);

    // Soft blob shadow under a car, nudged away from the sun.
    // With ray tracing the sun shadow is traced, so only the dark contact patch under the car stays.
    public static void CarShadow(Vector2 pos, float heading, CarType type, float scale = 1)
    {
        CarModels m = cars[type];
        Vector2 f = new(MathF.Cos(heading), MathF.Sin(heading));
        float L = m.Length * UnitsPerMetre * scale, Wd = m.Width * UnitsPerMetre * scale;
        if (RayTracing.Enabled)
        {
            Box(pos, f, L + 3 * scale, Wd + 3 * scale, new Color(0, 0, 0, 30));
            Box(pos, f, L - 4 * scale, Wd - 3 * scale, new Color(0, 0, 0, 40));
            return;
        }
        Vector2 c = pos + new Vector2(SunDir.X, SunDir.Z) * 6f * scale;
        Box(c, f, L + 8 * scale, Wd + 7 * scale, new Color(0, 0, 0, 34));
        Box(c, f, L + 2 * scale, Wd + 1 * scale, new Color(0, 0, 0, 60));
    }

    // ---------------------------------------------------------------- models

    public struct CarPose
    {
        public Vector2 Position;
        public float Heading;     // radians, game convention
        public float Steer;       // -1..1
        public float WheelSpin;   // radians
        public float Roll, Pitch; // body lean, radians
        public bool Braking;
    }

    public static float WheelRadiusUnits(CarType type) => cars[type].WheelRadius * UnitsPerMetre;

    public static void DrawCar(CarType type, CarPose pose, Color color, float scale = 1f)
    {
        CarModels m = cars[type];
        float s = UnitsPerMetre * scale;
        Vector3 pos = W(pose.Position);
        Vector3 up = Vector3.UnitY;
        float yaw = -pose.Heading * 180f / MathF.PI;
        Vector3 scl = new(s, s, s);

        // Body lean: rotate about a pivot half a metre up so it rolls on its springs
        Matrix4x4 lean = Raymath.MatrixMultiply(
            Raymath.MatrixMultiply(Raymath.MatrixTranslate(0, -0.5f, 0), Raymath.MatrixMultiply(Raymath.MatrixRotateX(pose.Roll), Raymath.MatrixRotateZ(pose.Pitch))),
            Raymath.MatrixTranslate(0, 0.5f, 0));

        Model paint = m.Paint, detail = m.Detail, tail = m.Tail;
        paint.Transform = detail.Transform = tail.Transform = lean;
        if (type == CarType.HeadCar)
        {
            // The photo is the paint job: skin keeps its own colour, with a faint sheen
            Reflect(0.08f);
            float a = -pose.Heading;
            Raylib.SetShaderValue(lit, litTexSide, new Vector3(MathF.Sin(a), 0, MathF.Cos(a)), ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(lit, litTexBlur, HeadCar.EdgeBlur, ShaderUniformDataType.Float);
            Raylib.DrawModelEx(paint, pos, up, yaw, scl, Color.White);
            Raylib.SetShaderValue(lit, litTexBlur, 0f, ShaderUniformDataType.Float);
        }
        else if (type == CarType.BurgerCar)
        {
            // Bread, lettuce and cartoon riders: matte, with only a hint of sheen
            Reflect(0.05f);
            Raylib.DrawModelEx(paint, pos, up, yaw, scl, Color.White);
        }
        else
        {
            Reflect(1.0f);
            Raylib.DrawModelEx(paint, pos, up, yaw, scl, color);
            Reflect(0.7f); // glass, trim and chrome
            Raylib.DrawModelEx(detail, pos, up, yaw, scl, Color.White);
            Reflect(0.4f);
            Raylib.DrawModelEx(tail, pos, up, yaw, scl, pose.Braking ? new Color(255, 40, 36, 255) : new Color(120, 16, 22, 255));
        }
        Reflect(0.15f);

        // Wheels: front pair steers, all spin with distance travelled
        Vector2 f = new(MathF.Cos(pose.Heading), MathF.Sin(pose.Heading));
        Vector2 r = new(-f.Y, f.X);
        Model w = wheel;
        w.Transform = Raymath.MatrixRotateZ(-pose.WheelSpin);
        Vector3 wScale = new(m.WheelRadius * s, m.WheelRadius * s, m.WheelWidth * s);
        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2;
            float side = i % 2 == 0 ? -1 : 1;
            Vector2 p = pose.Position + f * (front ? 1 : -1) * m.Wheelbase / 2 * s + r * side * m.Track / 2 * s;
            float wheelYaw = -(pose.Heading + (front ? pose.Steer * 0.42f : 0)) * 180f / MathF.PI;
            Raylib.DrawModelEx(w, W(p, m.WheelRadius * s), up, wheelYaw, wScale, Color.White);
        }
    }

    public static void DrawTree(Vector2 pos, float size, float shade, int variant)
    {
        Model m = variant % 2 == 0 ? pine : round;
        float s = size / 26f * UnitsPerMetre;
        byte k = (byte)Math.Clamp(255 * shade, 0, 255);
        Reflect(0);
        Raylib.DrawModelEx(m, W(pos), Vector3.UnitY, variant * 47 % 360, new Vector3(s, s, s), new Color(k, k, k, (byte)255));
    }

    public static void DrawGantry(Vector2 pos, float heading)
    {
        float s = UnitsPerMetre;
        Reflect(0.25f);
        Raylib.DrawModelEx(gantry, W(pos), Vector3.UnitY, -heading * 180f / MathF.PI, new Vector3(s, s, s), Color.White);
    }

    public static void DrawPlatform(Vector3 pos, float radius, float height, Color color)
    {
        Reflect(0);
        Raylib.DrawModelEx(platform, pos, Vector3.UnitY, 0, new Vector3(radius, height, radius), color);
    }

    // ---------------------------------------------------------------- shaders

    const string LitVs = @"#version 330
in vec3 vertexPosition;
in vec3 vertexNormal;
in vec2 vertexTexCoord;
uniform mat4 mvp;
uniform mat4 matModel;
uniform mat4 matNormal;
out vec3 fragPos;
out vec3 fragNormal;
out vec2 fragTexCoord;
void main() {
    fragTexCoord = vertexTexCoord;
    fragPos = vec3(matModel * vec4(vertexPosition, 1.0));
    fragNormal = normalize(vec3(matNormal * vec4(vertexNormal, 0.0)));
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}";

    // Sun (with ray-traced soft shadows) + sky/ground ambient, a little specular and a cool rim light,
    // ray-traced reflections weighted by Fresnel, then distance fog
    const string LitFs = @"#version 330
in vec3 fragPos;
in vec3 fragNormal;
in vec2 fragTexCoord;
uniform sampler2D texture0; // 1x1 white for the untextured Blender models
uniform vec4 colDiffuse;
uniform vec3 sunDir;
uniform vec3 viewPos;
uniform vec3 fogColor;
uniform vec2 fogRange;
uniform float reflectivity;
uniform float texBlur;  // head car: blur the side-projected photo where the surface turns away from the side
uniform vec3 texSide;   // the car's sideways axis
" + RayTracing.Glsl + RayTracing.ReflectGlsl + @"
out vec4 finalColor;
void main() {
    vec3 n = normalize(fragNormal);
    vec3 l = -sunDir;
    vec3 v = normalize(viewPos - fragPos);
    float diff = max(dot(n, l), 0.0);
    if (rtOn == 1 && diff > 0.0) diff *= 1.0 - rtShadow(fragPos, n);
    float hemi = 0.5 + 0.5 * n.y;
    vec3 ambient = mix(vec3(0.26, 0.27, 0.24), vec3(0.50, 0.56, 0.66), hemi);
    float facing = abs(dot(n, texSide));
    vec3 albedo = colDiffuse.rgb * texture(texture0, fragTexCoord, texBlur * (1.0 - smoothstep(0.1, 0.75, facing))).rgb;
    vec3 col = albedo * (ambient + vec3(1.0, 0.95, 0.86) * diff * 0.80);
    vec3 h = normalize(l + v);
    col += pow(max(dot(n, h), 0.0), 48.0) * 0.30 * diff;
    col += pow(1.0 - max(dot(n, v), 0.0), 3.0) * 0.10 * vec3(0.75, 0.85, 1.0);
    if (rtOn == 1 && reflectivity > 0.0) {
        float fresnel = 0.12 + 0.88 * pow(1.0 - max(dot(n, v), 0.0), 5.0);
        col = mix(col, rtReflection(fragPos + n * 0.06, reflect(-v, n)), reflectivity * fresnel);
    }
    float f = clamp((length(viewPos - fragPos) - fogRange.x) / (fogRange.y - fogRange.x), 0.0, 1.0);
    finalColor = vec4(mix(col, fogColor, f), colDiffuse.a);
}";

    // Ground pass: immediate-mode vertices are already in world space
    const string GroundVs = @"#version 330
in vec3 vertexPosition;
in vec4 vertexColor;
uniform mat4 mvp;
out vec4 fragColor;
out vec3 worldPos;
void main() {
    fragColor = vertexColor;
    worldPos = vertexPosition;
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}";

    const string GroundFs = @"#version 330
in vec4 fragColor;
in vec3 worldPos;
uniform vec3 viewPos;
uniform vec3 fogColor;
uniform vec2 fogRange;
out vec4 finalColor;
void main() {
    float f = clamp((length(viewPos - worldPos) - fogRange.x) / (fogRange.y - fogRange.x), 0.0, 1.0);
    finalColor = vec4(mix(fragColor.rgb, fogColor, f), fragColor.a);
}";
}

// The rotating car on the menu, rendered in 3D into its own image and then placed on the menu.
static class Showcase
{
    static RenderTexture2D target;

    public static Texture2D Texture => target.Texture;

    public static void Render(CarType type, Color color, float spin, float scale, int pixelWidth, int pixelHeight)
    {
        pixelWidth = Math.Clamp(pixelWidth, 64, 4096);
        pixelHeight = Math.Clamp(pixelHeight, 64, 4096);
        if (target.Texture.Width != pixelWidth || target.Texture.Height != pixelHeight)
        {
            if (target.Id != 0) Raylib.UnloadRenderTexture(target);
            target = Raylib.LoadRenderTexture(pixelWidth, pixelHeight);
            Raylib.SetTextureFilter(target.Texture, TextureFilter.Bilinear);
        }

        float u = Scene3D.UnitsPerMetre;
        var cam = new Camera3D(new Vector3(0, 2.7f * u, 7.4f * u), new Vector3(0, 0.55f * u, 0), Vector3.UnitY, 30, CameraProjection.Perspective);

        Raylib.BeginTextureMode(target);
        Raylib.ClearBackground(new Color(0, 0, 0, 0));
        Raylib.BeginMode3D(cam);
        Scene3D.SetView(cam.Position, 1e6f, 2e6f, Scene3D.Horizon);
        RayTracing.Begin(new Color(70, 76, 92, 255)); // turntable top, as seen lit
        RayTracing.AddCar(type, Vector2.Zero, spin, color, scale);
        RayTracing.Upload();

        // Turntable
        Scene3D.DrawPlatform(new Vector3(0, -0.12f * u, 0), 3.15f * u, 0.12f * u, new Color(40, 44, 54, 255));
        Scene3D.DrawPlatform(new Vector3(0, -0.10f * u, 0), 3.0f * u, 0.105f * u, new Color(58, 63, 76, 255));

        Scene3D.BeginGround();
        Scene3D.CarShadow(Vector2.Zero, spin, type, scale);
        Scene3D.EndGround();

        var pose = new Scene3D.CarPose { Heading = spin, Steer = MathF.Sin(spin * 2.3f) * 0.4f };
        Scene3D.DrawCar(type, pose, color, scale);
        Raylib.EndMode3D();
        Raylib.EndTextureMode();
    }

    public static void Unload()
    {
        if (target.Id != 0) Raylib.UnloadRenderTexture(target);
    }
}
