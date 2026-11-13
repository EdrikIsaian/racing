using System.Numerics;
using System.Runtime.InteropServices;
using Raylib_cs;

// Camera-like post-processing for the race view (G toggles it).
//
// The 3D scene is drawn into an off-screen HDR image (16-bit float, 4x multisampled where the
// driver allows) instead of straight to the screen, so light brighter than "white" survives:
// the sun, glints on paint and glass. Then:
//   - bloom: bright light bleeds softly into its surroundings, built from a chain of ever
//     smaller blurred copies of the image
//   - sun rays: the bright sky around the sun streaks out between trees, cars and hills
//   - tone mapping (Khronos PBR Neutral): highlights roll off smoothly like film instead of clipping
//   - colour grading per track (exposure, white balance, saturation), a lens vignette, a hint of
//     chromatic aberration at the edges, and fine grain that also hides colour banding
static unsafe class PostFx
{
    public static bool Enabled = true;

    const int Levels = 6;
    const int Samples = 4;

    static int width, height;
    static RenderTexture2D scene; // the resolved HDR image
    static uint msaaFbo, msaaColor, msaaDepth;
    static readonly RenderTexture2D[] down = new RenderTexture2D[Levels], up = new RenderTexture2D[Levels];
    static Shader downFs, upFs, finalFs;
    static int locDownTexel, locDownFirst, locUpTexel, locUpBase;
    static int locBloom, locRays, locSunUV, locSunVis, locExposure, locBalance, locSat, locBloomAmt, locTime, locVignette, locContrast;
    static bool loaded;

    public struct Grade
    {
        public float Exposure, Saturation, Bloom, Contrast;
        public Vector3 WhiteBalance;
    }

    static Grade grade;
    static Vector2 sunUV;
    static float sunVis;

    // ---------------------------------------------------------------- set-up

    static void Load()
    {
        downFs = Raylib.LoadShaderFromMemory(null, DownFs);
        upFs = Raylib.LoadShaderFromMemory(null, UpFs);
        finalFs = Raylib.LoadShaderFromMemory(null, FinalFs);
        locDownTexel = Raylib.GetShaderLocation(downFs, "texel");
        locDownFirst = Raylib.GetShaderLocation(downFs, "first");
        locUpTexel = Raylib.GetShaderLocation(upFs, "texel");
        locUpBase = Raylib.GetShaderLocation(upFs, "baseTex");
        locBloom = Raylib.GetShaderLocation(finalFs, "bloomTex");
        locRays = Raylib.GetShaderLocation(finalFs, "raysTex");
        locSunUV = Raylib.GetShaderLocation(finalFs, "sunUV");
        locSunVis = Raylib.GetShaderLocation(finalFs, "sunVis");
        locExposure = Raylib.GetShaderLocation(finalFs, "exposure");
        locBalance = Raylib.GetShaderLocation(finalFs, "balance");
        locSat = Raylib.GetShaderLocation(finalFs, "saturation");
        locBloomAmt = Raylib.GetShaderLocation(finalFs, "bloomAmount");
        locTime = Raylib.GetShaderLocation(finalFs, "time");
        locVignette = Raylib.GetShaderLocation(finalFs, "vignette");
        locContrast = Raylib.GetShaderLocation(finalFs, "contrast");
        Gl.Load();
        loaded = true;
    }

    // Float colour image with a bilinear filter, optionally with its own depth buffer
    static RenderTexture2D LoadTarget(int w, int h, bool depth)
    {
        RenderTexture2D rt = default;
        rt.Id = Rlgl.LoadFramebuffer();
        Rlgl.EnableFramebuffer(rt.Id);
        uint tex = Rlgl.LoadTexture(null, w, h, PixelFormat.UncompressedR16G16B16A16, 1);
        rt.Texture = new Texture2D { Id = tex, Width = w, Height = h, Mipmaps = 1, Format = PixelFormat.UncompressedR16G16B16A16 };
        Rlgl.FramebufferAttach(rt.Id, tex, FramebufferAttachType.ColorChannel0, FramebufferAttachTextureType.Texture2D, 0);
        if (depth)
        {
            uint d = Rlgl.LoadTextureDepth(w, h, true);
            rt.Depth = new Texture2D { Id = d, Width = w, Height = h, Mipmaps = 1 };
            Rlgl.FramebufferAttach(rt.Id, d, FramebufferAttachType.Depth, FramebufferAttachTextureType.Renderbuffer, 0);
        }
        Rlgl.FramebufferComplete(rt.Id);
        Rlgl.DisableFramebuffer();
        Raylib.SetTextureFilter(rt.Texture, TextureFilter.Bilinear);
        Raylib.SetTextureWrap(rt.Texture, TextureWrap.Clamp);
        return rt;
    }

    static void Resize(int w, int h)
    {
        Free();
        width = w;
        height = h;
        bool msaa = Gl.Available && CreateMsaa(w, h);
        scene = LoadTarget(w, h, !msaa);
        int lw = w, lh = h;
        for (int i = 0; i < Levels; i++)
        {
            lw = Math.Max(1, lw / 2);
            lh = Math.Max(1, lh / 2);
            down[i] = LoadTarget(lw, lh, false);
            up[i] = LoadTarget(lw, lh, false);
        }
    }

    // A multisampled float colour + depth framebuffer that the scene is drawn into, then resolved
    static bool CreateMsaa(int w, int h)
    {
        int max;
        Gl.GetIntegerv(Gl.MAX_SAMPLES, &max);
        int samples = Math.Min(Samples, max);
        if (samples < 2) return false;
        uint fbo, rb0, rb1;
        Gl.GenFramebuffers(1, &fbo);
        Gl.GenRenderbuffers(1, &rb0);
        Gl.GenRenderbuffers(1, &rb1);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
        Gl.BindRenderbuffer(Gl.RENDERBUFFER, rb0);
        Gl.RenderbufferStorageMultisample(Gl.RENDERBUFFER, samples, Gl.RGBA16F, w, h);
        Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.RENDERBUFFER, rb0);
        Gl.BindRenderbuffer(Gl.RENDERBUFFER, rb1);
        Gl.RenderbufferStorageMultisample(Gl.RENDERBUFFER, samples, Gl.DEPTH_COMPONENT24, w, h);
        Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.RENDERBUFFER, rb1);
        bool ok = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) == Gl.FRAMEBUFFER_COMPLETE;
        Gl.BindRenderbuffer(Gl.RENDERBUFFER, 0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        msaaFbo = fbo; msaaColor = rb0; msaaDepth = rb1;
        if (!ok) FreeMsaa();
        return ok;
    }

    static void FreeMsaa()
    {
        if (msaaFbo == 0) return;
        uint fbo = msaaFbo, rb0 = msaaColor, rb1 = msaaDepth;
        Gl.DeleteFramebuffers(1, &fbo);
        Gl.DeleteRenderbuffers(1, &rb0);
        Gl.DeleteRenderbuffers(1, &rb1);
        msaaFbo = msaaColor = msaaDepth = 0;
    }

    static void Free()
    {
        FreeMsaa();
        if (scene.Id != 0) Raylib.UnloadRenderTexture(scene);
        for (int i = 0; i < Levels; i++)
        {
            if (down[i].Id != 0) Raylib.UnloadRenderTexture(down[i]);
            if (up[i].Id != 0) Raylib.UnloadRenderTexture(up[i]);
            down[i] = up[i] = default;
        }
        scene = default;
    }

    public static void Unload()
    {
        if (!loaded) return;
        Free();
        Raylib.UnloadShader(downFs);
        Raylib.UnloadShader(upFs);
        Raylib.UnloadShader(finalFs);
    }

    // ---------------------------------------------------------------- per frame

    // Size of the image the scene is drawn into, in pixels. Multisampling smooths the edges;
    // without it, 1x screens draw at double size like Supersample does.
    public static (int W, int H) SceneSize()
    {
        int w = Raylib.GetRenderWidth(), h = Raylib.GetRenderHeight();
        if (msaaFbo == 0 && !Gl.Available && Raylib.GetWindowScaleDPI().X < 1.5f) { w *= 2; h *= 2; }
        return (Math.Max(w, 1), Math.Max(h, 1));
    }

    // Starts drawing the scene off-screen. 2D drawing is in screen points, like everywhere else.
    public static void BeginScene()
    {
        if (!loaded) Load();
        var (w, h) = SceneSize();
        if (w != width || h != height) Resize(w, h);

        if (msaaFbo != 0)
        {
            // raylib only needs the id and size to draw into a framebuffer
            RenderTexture2D target = default;
            target.Id = msaaFbo;
            target.Texture.Width = w;
            target.Texture.Height = h;
            Raylib.BeginTextureMode(target);
        }
        else Raylib.BeginTextureMode(scene);
        Raylib.ClearBackground(Color.Black);
        Supersample.Setup2D();
    }

    // Finishes the scene: resolves the multisampling and builds the bloom images.
    // `cam` locates the sun on screen for the sun rays.
    public static void EndScene(Camera3D cam, Vector3 sunDir, Grade look)
    {
        Raylib.EndTextureMode();
        grade = look;
        if (msaaFbo != 0)
        {
            Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, msaaFbo);
            Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, scene.Id);
            Gl.BlitFramebuffer(0, 0, width, height, 0, 0, width, height, Gl.COLOR_BUFFER_BIT, Gl.NEAREST);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        }

        // Where the sun is on screen (0..1, y up like the image) and how much of it to show
        Vector3 fwd = Vector3.Normalize(cam.Target - cam.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(fwd, cam.Up));
        Vector3 upv = Vector3.Cross(right, fwd);
        Vector3 toSun = -sunDir;
        float z = Vector3.Dot(toSun, fwd);
        float tanHalf = MathF.Tan(cam.FovY * MathF.PI / 360f), aspect = width / (float)height;
        sunVis = 0;
        if (z > 0.05f)
        {
            sunUV = new Vector2(0.5f + 0.5f * Vector3.Dot(toSun, right) / (z * tanHalf * aspect), 0.5f + 0.5f * Vector3.Dot(toSun, upv) / (z * tanHalf));
            // fades out as the sun leaves the screen
            float edge = MathF.Max(MathF.Abs(sunUV.X - 0.5f), MathF.Abs(sunUV.Y - 0.5f));
            sunVis = Math.Clamp((1.1f - edge) / 0.5f, 0, 1);
        }

        Rlgl.DisableColorBlend();
        // Downsample: each level half the size of the one before; the first keeps only bright light
        Texture2D src = scene.Texture;
        for (int i = 0; i < Levels; i++)
        {
            Raylib.BeginTextureMode(down[i]);
            Raylib.BeginShaderMode(downFs);
            Raylib.SetShaderValue(downFs, locDownTexel, new Vector2(1f / src.Width, 1f / src.Height), ShaderUniformDataType.Vec2);
            Raylib.SetShaderValue(downFs, locDownFirst, i == 0 ? 1 : 0, ShaderUniformDataType.Int);
            Blit(src, down[i].Texture.Width, down[i].Texture.Height);
            Raylib.EndShaderMode();
            Raylib.EndTextureMode();
            src = down[i].Texture;
        }
        // Upsample: each level = its own blur + the smoother level below, softly enlarged
        src = down[Levels - 1].Texture;
        for (int i = Levels - 2; i >= 0; i--)
        {
            Raylib.BeginTextureMode(up[i]);
            Raylib.BeginShaderMode(upFs);
            Raylib.SetShaderValue(upFs, locUpTexel, new Vector2(1f / src.Width, 1f / src.Height), ShaderUniformDataType.Vec2);
            Raylib.SetShaderValueTexture(upFs, locUpBase, down[i].Texture);
            Blit(src, up[i].Texture.Width, up[i].Texture.Height);
            Raylib.EndShaderMode();
            Raylib.EndTextureMode();
            src = up[i].Texture;
        }
        Rlgl.EnableColorBlend();
    }

    // Draws `tex` over a w x h target, keeping it the right way up
    static void Blit(Texture2D tex, float w, float h)
    {
        Raylib.DrawTexturePro(tex, new Rectangle(0, 0, tex.Width, -tex.Height), new Rectangle(0, 0, w, h), Vector2.Zero, 0, Color.White);
    }

    // Puts the finished picture on the screen (or the supersampling image), filling the window
    public static void Present()
    {
        Rlgl.DrawRenderBatchActive();
        Rlgl.DisableColorBlend();
        Raylib.BeginShaderMode(finalFs);
        Raylib.SetShaderValueTexture(finalFs, locBloom, up[0].Texture);
        Raylib.SetShaderValueTexture(finalFs, locRays, down[1].Texture);
        Raylib.SetShaderValue(finalFs, locSunUV, sunUV, ShaderUniformDataType.Vec2);
        Raylib.SetShaderValue(finalFs, locSunVis, sunVis, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(finalFs, locExposure, grade.Exposure, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(finalFs, locBalance, grade.WhiteBalance, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(finalFs, locSat, grade.Saturation, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(finalFs, locBloomAmt, grade.Bloom, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(finalFs, locTime, (float)(Raylib.GetTime() % 1000), ShaderUniformDataType.Float);
        Raylib.SetShaderValue(finalFs, locVignette, 0.32f, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(finalFs, locContrast, grade.Contrast, ShaderUniformDataType.Float);
        Blit(scene.Texture, Raylib.GetScreenWidth(), Raylib.GetScreenHeight());
        Raylib.EndShaderMode();
        Rlgl.EnableColorBlend();
    }

    // ---------------------------------------------------------------- shaders
    // The scene's colours are display (gamma) values that may exceed 1; the passes work in linear light.

    // 13-tap downsample (as in Jimenez, "Next generation post processing in Call of Duty"): smooth,
    // no flicker. The first pass turns colours into light, keeps only what's bright, and tames
    // single blazing pixels so they don't flash.
    const string DownFs = @"#version 330
in vec2 fragTexCoord;
uniform sampler2D texture0;
uniform vec2 texel;
uniform int first;
out vec4 finalColor;
vec3 tap(vec2 o) {
    vec3 c = texture(texture0, fragTexCoord + o * texel).rgb;
    if (first == 1) {
        c = pow(max(c, 0.0), vec3(2.2));
        c = min(c, vec3(60.0));
        float l = max(c.r, max(c.g, c.b));
        // soft threshold: nothing below 0.75, fully above 1.6
        float k = clamp((l - 0.75) / 0.85, 0.0, 1.0);
        c *= k * k * (3.0 - 2.0 * k);
        c /= 1.0 + 0.04 * l;
    }
    return c;
}
void main() {
    vec3 a = tap(vec2(-2, 2)), b = tap(vec2(0, 2)), c = tap(vec2(2, 2));
    vec3 d = tap(vec2(-2, 0)), e = tap(vec2(0, 0)), f = tap(vec2(2, 0));
    vec3 g = tap(vec2(-2, -2)), h = tap(vec2(0, -2)), i = tap(vec2(2, -2));
    vec3 j = tap(vec2(-1, 1)), k = tap(vec2(1, 1)), l = tap(vec2(-1, -1)), m = tap(vec2(1, -1));
    vec3 col = e * 0.125 + (a + c + g + i) * 0.03125 + (b + d + f + h) * 0.0625 + (j + k + l + m) * 0.125;
    finalColor = vec4(col, 1.0);
}";

    // 9-tap tent upsample of the smaller level, added to this level's own blur
    const string UpFs = @"#version 330
in vec2 fragTexCoord;
uniform sampler2D texture0;
uniform sampler2D baseTex;
uniform vec2 texel;
out vec4 finalColor;
void main() {
    vec2 uv = fragTexCoord;
    vec3 s = texture(texture0, uv).rgb * 4.0;
    s += (texture(texture0, uv + vec2(-1, 0) * texel).rgb + texture(texture0, uv + vec2(1, 0) * texel).rgb +
          texture(texture0, uv + vec2(0, -1) * texel).rgb + texture(texture0, uv + vec2(0, 1) * texel).rgb) * 2.0;
    s += texture(texture0, uv + vec2(-1, -1) * texel).rgb + texture(texture0, uv + vec2(1, -1) * texel).rgb +
         texture(texture0, uv + vec2(-1, 1) * texel).rgb + texture(texture0, uv + vec2(1, 1) * texel).rgb;
    finalColor = vec4(texture(baseTex, uv).rgb + s / 16.0, 1.0);
}";

    const string FinalFs = @"#version 330
in vec2 fragTexCoord;
uniform sampler2D texture0;
uniform sampler2D bloomTex;
uniform sampler2D raysTex;
uniform vec2 sunUV;
uniform float sunVis;
uniform float exposure;
uniform vec3 balance;
uniform float saturation;
uniform float bloomAmount;
uniform float time;
uniform float vignette;
uniform float contrast;
out vec4 finalColor;

vec3 toLinear(vec3 c) { return pow(max(c, 0.0), vec3(2.2)); }

// Khronos PBR Neutral tone mapping: colours below ~0.75 stay as designed, brighter light rolls off
// smoothly towards white (and desaturates a little, like film) instead of clipping
vec3 neutral(vec3 c) {
    const float start = 0.76, desat = 0.15;
    float x = min(c.r, min(c.g, c.b));
    float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
    c -= offset;
    float peak = max(c.r, max(c.g, c.b));
    if (peak < start) return c;
    const float d = 1.0 - start;
    float newPeak = 1.0 - d * d / (peak + d - start);
    c *= newPeak / peak;
    float g = 1.0 - 1.0 / (desat * (peak - newPeak) + 1.0);
    return mix(c, vec3(newPeak), g);
}

float hash(vec2 p) { vec3 q = fract(vec3(p.xyx) * 0.1031); q += dot(q, q.yzx + 33.33); return fract((q.x + q.y) * q.z); }

void main() {
    vec2 uv = fragTexCoord;
    vec2 dc = uv - 0.5;
    // a whisper of lens colour fringing towards the corners
    vec2 ca = dc * dot(dc, dc) * 0.006;
    vec3 c = vec3(texture(texture0, uv - ca).r, texture(texture0, uv).g, texture(texture0, uv + ca).b);
    vec3 col = toLinear(c);

    col += texture(bloomTex, uv).rgb * bloomAmount;

    // Sun rays: smear the bright sky towards the sun; whatever stands in front of it cuts the rays
    if (sunVis > 0.0) {
        vec2 delta = (sunUV - uv) / 28.0;
        vec2 p = uv;
        float w = 1.0;
        vec3 rays = vec3(0.0);
        for (int i = 0; i < 28; i++) {
            p += delta;
            rays += texture(raysTex, p).rgb * w;
            w *= 0.94;
        }
        float fall = 1.0 - smoothstep(0.0, 0.9, length((uv - sunUV) * vec2(1.6, 1.0)));
        col += rays / 28.0 * sunVis * fall * 0.5;
    }

    col *= exposure * balance;
    float l = dot(col, vec3(0.2126, 0.7152, 0.0722));
    col = max(mix(vec3(l), col, saturation), 0.0);
    col = neutral(col);
    col = pow(clamp(col, 0.0, 1.0), vec3(1.0 / 2.2));
    // a gentle film-like S-curve for punch
    col = mix(col, col * col * (3.0 - 2.0 * col), contrast);

    float r = length(dc * vec2(1.0, 0.78));
    col *= 1.0 - vignette * smoothstep(0.25, 0.85, r);
    col += (hash(gl_FragCoord.xy + fract(time) * 517.0) - 0.5) * (2.0 / 255.0);
    finalColor = vec4(col, 1.0);
}";

    // ---------------------------------------------------------------- OpenGL calls raylib doesn't wrap
    // (multisampled renderbuffers and the blit that resolves them)
    static class Gl
    {
        public const uint FRAMEBUFFER = 0x8D40, READ_FRAMEBUFFER = 0x8CA8, DRAW_FRAMEBUFFER = 0x8CA9, RENDERBUFFER = 0x8D41;
        public const uint COLOR_ATTACHMENT0 = 0x8CE0, DEPTH_ATTACHMENT = 0x8D00, RGBA16F = 0x881A, DEPTH_COMPONENT24 = 0x81A6;
        public const uint COLOR_BUFFER_BIT = 0x4000, NEAREST = 0x2600, FRAMEBUFFER_COMPLETE = 0x8CD5, MAX_SAMPLES = 0x8D57;

        public static bool Available;
        public static delegate* unmanaged<int, uint*, void> GenFramebuffers, GenRenderbuffers, DeleteFramebuffers, DeleteRenderbuffers;
        public static delegate* unmanaged<uint, uint, void> BindFramebuffer, BindRenderbuffer;
        public static delegate* unmanaged<uint, int, uint, int, int, void> RenderbufferStorageMultisample;
        public static delegate* unmanaged<uint, uint, uint, uint, void> FramebufferRenderbuffer;
        public static delegate* unmanaged<uint, uint> CheckFramebufferStatus;
        public static delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void> BlitFramebuffer;
        public static delegate* unmanaged<uint, int*, void> GetIntegerv;

        // These are plain exports of the system OpenGL library on macOS and Linux.
        // Elsewhere (Windows) the scene is drawn without multisampling instead.
        public static void Load()
        {
            string? lib = OperatingSystem.IsMacOS() ? "/System/Library/Frameworks/OpenGL.framework/OpenGL"
                : OperatingSystem.IsLinux() ? "libGL.so.1" : null;
            if (lib == null || !NativeLibrary.TryLoad(lib, out IntPtr h)) return;
            IntPtr F(string name) => NativeLibrary.TryGetExport(h, name, out IntPtr p) ? p : IntPtr.Zero;
            IntPtr[] p =
            {
                F("glGenFramebuffers"), F("glGenRenderbuffers"), F("glDeleteFramebuffers"), F("glDeleteRenderbuffers"),
                F("glBindFramebuffer"), F("glBindRenderbuffer"), F("glRenderbufferStorageMultisample"),
                F("glFramebufferRenderbuffer"), F("glCheckFramebufferStatus"), F("glBlitFramebuffer"), F("glGetIntegerv"),
            };
            if (p.Any(x => x == IntPtr.Zero)) return;
            GenFramebuffers = (delegate* unmanaged<int, uint*, void>)p[0];
            GenRenderbuffers = (delegate* unmanaged<int, uint*, void>)p[1];
            DeleteFramebuffers = (delegate* unmanaged<int, uint*, void>)p[2];
            DeleteRenderbuffers = (delegate* unmanaged<int, uint*, void>)p[3];
            BindFramebuffer = (delegate* unmanaged<uint, uint, void>)p[4];
            BindRenderbuffer = (delegate* unmanaged<uint, uint, void>)p[5];
            RenderbufferStorageMultisample = (delegate* unmanaged<uint, int, uint, int, int, void>)p[6];
            FramebufferRenderbuffer = (delegate* unmanaged<uint, uint, uint, uint, void>)p[7];
            CheckFramebufferStatus = (delegate* unmanaged<uint, uint>)p[8];
            BlitFramebuffer = (delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void>)p[9];
            GetIntegerv = (delegate* unmanaged<uint, int*, void>)p[10];
            Available = true;
        }
    }
}
