using System.Numerics;
using Raylib_cs;

// The sky and the air: a per-pixel sky dome (blue overhead fading to a hazy horizon, a bright sun
// with its glow, and drifting procedural clouds), plus the shared GLSL every 3D shader uses for
// sky colour, sunlight and aerial-perspective fog, so models, land, road and reflections all sit
// in the same atmosphere.
static class Sky
{
    record struct Bound(Shader Shader, int SunDir, int Zenith, int Horizon, int SunColor);
    static readonly List<Bound> shaders = new();

    static Shader sky;
    static int locRes, locFwd, locRight, locUp, locTanHalf, locAspect, locTime, locCover, locCloud, locView;

    // Default look (menu showroom): the meadow's afternoon sun
    public static readonly Vector3 DefaultSunColor = new(1.0f, 0.95f, 0.86f);

    public static Vector3 SunDirection(float elevationDeg, float azimuthDeg)
    {
        float e = elevationDeg * MathF.PI / 180, a = azimuthDeg * MathF.PI / 180;
        // Direction the light travels: from the sun towards the ground
        return -new Vector3(MathF.Cos(e) * MathF.Cos(a), MathF.Sin(e), MathF.Cos(e) * MathF.Sin(a));
    }

    public static void Load()
    {
        sky = Raylib.LoadShaderFromMemory(null, SkyFs);
        locRes = Raylib.GetShaderLocation(sky, "resolution");
        locFwd = Raylib.GetShaderLocation(sky, "camFwd");
        locRight = Raylib.GetShaderLocation(sky, "camRight");
        locUp = Raylib.GetShaderLocation(sky, "camUp");
        locTanHalf = Raylib.GetShaderLocation(sky, "tanHalf");
        locAspect = Raylib.GetShaderLocation(sky, "aspect");
        locTime = Raylib.GetShaderLocation(sky, "time");
        locCover = Raylib.GetShaderLocation(sky, "cloudCover");
        locCloud = Raylib.GetShaderLocation(sky, "cloudColor");
        locView = Raylib.GetShaderLocation(sky, "viewPos");
        Bind(sky);
    }

    public static void Unload()
    {
        if (sky.Id != 0) Raylib.UnloadShader(sky);
        shaders.Clear();
    }

    // Shaders that paste in Glsl register here to receive the sun and sky colours
    public static void Bind(Shader s)
    {
        shaders.Add(new Bound(s, Raylib.GetShaderLocation(s, "sunDir"), Raylib.GetShaderLocation(s, "skyZenith"),
            Raylib.GetShaderLocation(s, "skyHorizon"), Raylib.GetShaderLocation(s, "sunColor")));
    }

    static Vector3 V(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    // Sets the sun and sky for everything drawn after it (cheap; called once per frame per camera)
    public static void Apply(Color zenith, Color horizon, Vector3 sunDir, Vector3 sunColor)
    {
        Scene3D.SunDir = sunDir;
        Vector3 z = V(zenith), h = V(horizon);
        foreach (Bound b in shaders)
        {
            Raylib.SetShaderValue(b.Shader, b.SunDir, sunDir, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(b.Shader, b.Zenith, z, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(b.Shader, b.Horizon, h, ShaderUniformDataType.Vec3);
            Raylib.SetShaderValue(b.Shader, b.SunColor, sunColor, ShaderUniformDataType.Vec3);
        }
    }

    public static void Apply(TrackDef def) =>
        Apply(def.SkyTop, def.Horizon, SunDirection(def.SunElevation, def.SunAzimuth), def.SunColor);

    // Fills the whole current target with the sky as seen from `cam`. Call before the 3D pass.
    public static void Draw(Camera3D cam, TrackDef def, int pixelWidth, int pixelHeight)
    {
        Vector3 fwd = Vector3.Normalize(cam.Target - cam.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(fwd, cam.Up));
        Vector3 up = Vector3.Cross(right, fwd);
        Raylib.SetShaderValue(sky, locRes, new Vector2(pixelWidth, pixelHeight), ShaderUniformDataType.Vec2);
        Raylib.SetShaderValue(sky, locFwd, fwd, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(sky, locRight, right, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(sky, locUp, up, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(sky, locTanHalf, MathF.Tan(cam.FovY * MathF.PI / 360f), ShaderUniformDataType.Float);
        Raylib.SetShaderValue(sky, locAspect, pixelWidth / (float)pixelHeight, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(sky, locTime, (float)Raylib.GetTime(), ShaderUniformDataType.Float);
        Raylib.SetShaderValue(sky, locCover, def.CloudCover, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(sky, locCloud, V(def.Cloud), ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(sky, locView, cam.Position, ShaderUniformDataType.Vec3);

        Raylib.BeginShaderMode(sky);
        Raylib.DrawRectangle(0, 0, Raylib.GetScreenWidth(), Raylib.GetScreenHeight(), Color.White);
        Raylib.EndShaderMode();
    }

    // ---------------------------------------------------------------- GLSL

    // Shared atmosphere code. Needs `sunDir`, `viewPos`, `fogColor` and `fogRange` uniforms declared before it.
    // Colours are in the usual display (gamma) space but may go above 1: the post-processing turns
    // them into light, so a bright sun glow blooms instead of clipping.
    public const string Glsl = @"
uniform vec3 skyZenith;
uniform vec3 skyHorizon;
uniform vec3 sunColor;

// The sun's glow in the air around it (strong at the horizon where the light crosses more air)
vec3 sunScatter(vec3 d) {
    float mu = max(dot(d, -sunDir), 0.0);
    float low = 1.0 - 0.6 * clamp(-sunDir.y * 1.4, 0.0, 1.0);
    return sunColor * (pow(mu, 5.0) * 0.20 + pow(mu, 40.0) * 0.40 + pow(mu, 400.0) * 0.8) * low;
}

// Sky colour in direction d, without clouds or the sun's disc
vec3 skyColor(vec3 d) {
    float y = d.y;
    float up = clamp(y, 0.0, 1.0);
    vec3 col = mix(skyHorizon, skyZenith, pow(up, 0.55));
    // a thin bright band right at the horizon, and hazier air just below it
    col += skyHorizon * 0.06 * exp(-abs(y) * 30.0);
    if (y < 0.0) col = mix(col, skyHorizon * 0.92, clamp(-y * 6.0, 0.0, 1.0));
    return col + sunScatter(d) * (1.0 - 0.6 * up);
}

// How much air is between the eye and a point dist away: 0 = clear, 1 = all haze.
// Starts gently, thickens with distance, and is complete at fogRange.y (where scenery stops being drawn).
float fogAmount(float dist) {
    float x = max(dist - fogRange.x * 0.5, 0.0) / (fogRange.y - fogRange.x * 0.5);
    float f = 1.0 - exp(-x * x * 2.6 - x * 0.45);
    return mix(f, 1.0, smoothstep(0.82, 1.0, x));
}

// Aerial perspective: distant things fade into the horizon colour, warmed towards the sun
vec3 applyFog(vec3 col, vec3 p) {
    vec3 d = p - viewPos;
    float dist = length(d);
    float f = fogAmount(dist);
    if (f <= 0.0) return col;
    vec3 dir = d / dist;
    // a little thinner looking up at high peaks
    f *= 1.0 - 0.35 * smoothstep(0.02, 0.25, dir.y);
    vec3 haze = fogColor + sunScatter(dir) * 0.45;
    return mix(col, haze, f);
}
";

    const string SkyFs = @"#version 330
in vec2 fragTexCoord;
in vec4 fragColor;
uniform vec2 resolution;
uniform vec3 camFwd;
uniform vec3 camRight;
uniform vec3 camUp;
uniform float tanHalf;
uniform float aspect;
uniform float time;
uniform float cloudCover;
uniform vec3 cloudColor;
uniform vec3 viewPos;
uniform vec3 sunDir;
uniform vec3 fogColor;
uniform vec2 fogRange;
" + Glsl + @"
out vec4 finalColor;

float hash(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
float vnoise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y);
}
// Fractal noise; `detail` (0..1) fades out the finest octaves where they would only shimmer
float fbm(vec2 p, float detail) {
    float s = 0.0, a = 0.5, w = 0.0;
    mat2 rot = mat2(0.8, 0.6, -0.6, 0.8);
    for (int i = 0; i < 6; i++) {
        float k = i < 3 ? 1.0 : detail;
        s += vnoise(p) * a * k;
        w += a * k;
        p = rot * p * 2.03 + vec2(17.1, 9.2);
        a *= 0.5;
    }
    return s / w;
}

void main() {
    vec2 ndc = gl_FragCoord.xy / resolution * 2.0 - 1.0;
    vec3 d = normalize(camFwd + ndc.x * aspect * tanHalf * camRight + ndc.y * tanHalf * camUp);
    vec3 col = skyColor(d);
    float mu = dot(d, -sunDir);

    // The sun's disc, far brighter than anything else (it blooms in post-processing)
    float disc = smoothstep(0.99984, 0.99993, mu);

    // Clouds: a layer of fractal noise high above, seen in perspective, lit from the sun's side
    float cloud = 0.0;
    if (d.y > 0.0 && cloudCover > 0.0) {
        const float height = 6000.0;
        float t = height / max(d.y, 0.03);
        vec2 wind = vec2(0.9, 0.35) * time * 22.0;
        vec2 p = (viewPos.xz + d.xz * t + wind) / 6500.0;
        float detail = clamp(1.6 - t / 50000.0, 0.0, 1.0);
        float n = fbm(p, detail);
        // fbm sits around 0.5 +- 0.15: the threshold sets how much of the sky is cloud
        float edge = 0.70 - cloudCover * 0.40;
        float dens = smoothstep(edge, edge + 0.16, n);
        if (dens > 0.0) {
            // Thicker cloud towards the sun = its far side is in its own shadow
            vec2 toSun = normalize(-sunDir.xz + 1e-4) * 0.06;
            float n2 = fbm(p + toSun, detail * 0.5);
            float lit = clamp(0.62 + (n - n2) * 3.2 - (n - edge) * 0.55, 0.0, 1.0);
            vec3 shadowCol = mix(cloudColor * vec3(0.56, 0.60, 0.70), skyZenith * 0.8, 0.25);
            vec3 cc = mix(shadowCol, cloudColor * 1.04, lit);
            // bright silver edges where the sun shines through the thin parts
            cc += sunColor * pow(max(mu, 0.0), 10.0) * (1.0 - dens) * 1.2;
            cc += sunScatter(d) * 0.35;
            // far clouds sink into the horizon haze
            float far = 1.0 - exp(-t / 110000.0);
            cc = mix(cc, skyColor(d), far * 0.85);
            cloud = dens * smoothstep(0.0, 0.08, d.y);
            col = mix(col, cc, cloud);
        }
    }
    col += sunColor * disc * 6.0 * (1.0 - cloud * 0.95);
    finalColor = vec4(col, 1.0);
}";
}
