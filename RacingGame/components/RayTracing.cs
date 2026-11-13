using System.Numerics;
using Raylib_cs;

// Real-time ray tracing in the shaders (T toggles it).
//
// macOS OpenGL has no ray-tracing hardware access, so instead of tracing the full triangle meshes
// every frame we upload a small "proxy" scene of exact shapes that closely wrap the models:
// each car is a few boxes (body, cabin, rear wing), the gantry is boxes, the nearest trees are
// cones / spheres / cylinders that match their Blender parts. The fragment shaders then cast rays
// against these shapes for:
//   - soft sun shadows: several rays per pixel spread over the sun's disc, on the ground and on models
//   - reflections: car paint and glass reflect the other cars, trees, ground and sky (with its own
//     ray-traced shadow at the hit point), weighted by Fresnel
// Far trees that don't fit in the proxy scene keep their old painted blob shadows.
static class RayTracing
{
    public const int MaxBoxes = 32, MaxTrees = 64;

    public static bool Enabled = true;

    static readonly Vector4[] boxA = new Vector4[MaxBoxes]; // centre xyz, cos(yaw)
    static readonly Vector4[] boxB = new Vector4[MaxBoxes]; // half size xyz, -sin(yaw)
    static readonly Vector4[] boxC = new Vector4[MaxBoxes]; // colour rgb
    static readonly Vector4[] trees = new Vector4[MaxTrees]; // x, z, scale (negative = pine), yaw
    static int boxCount, treeCount;
    static Vector3 groundColor;

    record struct Bound(Shader Shader, int On, int BoxCount, int BoxA, int BoxB, int BoxC, int TreeCount, int Trees, int Ground);
    static readonly List<Bound> shaders = new();

    // Proxy sizes in metres, from CARS in blender/build_models.py
    record struct CarProxy(float L, float W, float Clearance, float Top, float CabX0, float CabX1, float Roof, float Rake, float Taper, float WingHeight, float WingChord);
    static readonly Dictionary<CarType, CarProxy> proxies = new()
    {
        [CarType.SUV] = new(4.60f, 1.95f, 0.24f, 1.00f, -1.55f, 0.95f, 1.66f, 0.40f, 0.10f, 0, 0),
        [CarType.Sedan] = new(4.70f, 1.84f, 0.15f, 0.80f, -1.30f, 0.85f, 1.42f, 0.60f, 0.14f, 0, 0),
        [CarType.SportsCar] = new(4.50f, 1.88f, 0.11f, 0.74f, -1.05f, 0.55f, 1.28f, 0.70f, 0.17f, 0.95f, 0.30f),
        [CarType.HyperCar] = new(4.75f, 2.00f, 0.09f, 0.70f, -0.80f, 0.70f, 1.13f, 0.80f, 0.22f, 1.10f, 0.38f),
    };

    static readonly Color Glass = new(28, 36, 50, 255), Trim = new(22, 22, 26, 255), Steel = new(70, 74, 82, 255), Banner = new(18, 20, 26, 255);

    // ---------------------------------------------------------------- scene building (once per frame, per camera)

    public static void Bind(Shader s)
    {
        shaders.Add(new Bound(s,
            Raylib.GetShaderLocation(s, "rtOn"),
            Raylib.GetShaderLocation(s, "boxCount"), Raylib.GetShaderLocation(s, "boxA"),
            Raylib.GetShaderLocation(s, "boxB"), Raylib.GetShaderLocation(s, "boxC"),
            Raylib.GetShaderLocation(s, "treeCount"), Raylib.GetShaderLocation(s, "trees"),
            Raylib.GetShaderLocation(s, "rtGround")));
    }

    public static void Unbind() => shaders.Clear();

    // `ground`: what the floor looks like in reflections
    public static void Begin(Color ground)
    {
        boxCount = treeCount = 0;
        groundColor = new Vector3(ground.R, ground.G, ground.B) / 255f;
    }

    public static bool TreesFull => treeCount >= MaxTrees;

    // A box turned about the vertical axis by `yaw` (radians, same as the model it wraps)
    static void AddBox(Vector3 centre, Vector3 half, float yaw, Color color)
    {
        if (boxCount >= MaxBoxes) return;
        boxA[boxCount] = new Vector4(centre, MathF.Cos(yaw));
        boxB[boxCount] = new Vector4(half, -MathF.Sin(yaw));
        boxC[boxCount] = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, 0);
        boxCount++;
    }

    // Box given in a model's own space (metres; x forward, y up, z right), placed like DrawModelEx would
    static void AddModelBox(Vector2 pos, float yaw, float scale, Vector3 centre, Vector3 half, Color color)
    {
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        Vector3 world = new(pos.X + (centre.X * c + centre.Z * s) * scale, centre.Y * scale, pos.Y + (-centre.X * s + centre.Z * c) * scale);
        AddBox(world, half * scale, yaw, color);
    }

    public static void AddCar(CarType type, Vector2 pos, float heading, Color paint, float scale = 1)
    {
        float s = Scene3D.UnitsPerMetre * scale, yaw = -heading;
        if (type == CarType.HeadCar)
        {
            foreach (var (centre, half) in HeadCar.Proxy) AddModelBox(pos, yaw, s, centre, half, HeadCar.Skin);
            return;
        }
        if (type == CarType.BurgerCar)
        {
            foreach (var (centre, half, colour) in BurgerCar.Proxy) AddModelBox(pos, yaw, s, centre, half, colour);
            return;
        }
        CarProxy p = proxies[type];
        // Lower body (starts at the ground clearance so the ground under the car is outside it)
        AddModelBox(pos, yaw, s, new(0, (p.Clearance + p.Top - 0.03f) / 2, 0), new(p.L / 2, (p.Top - 0.03f - p.Clearance) / 2, p.W / 2), paint);
        // Cabin: the roof's footprint, which sits inside the sloped glass
        float x0 = p.CabX0 + p.Rake * 0.45f, x1 = p.CabX1 - p.Rake * 0.55f, zBase = p.Top - 0.08f;
        float halfW = p.W / 2 - 0.12f - p.Taper;
        AddModelBox(pos, yaw, s, new((x0 + x1) / 2, (zBase + p.Roof) / 2, 0), new((x1 - x0) / 2, (p.Roof - zBase) / 2, halfW), Glass);
        if (p.WingHeight > 0)
        {
            float span = type == CarType.HyperCar ? p.W - 0.06f : p.W - 0.2f;
            AddModelBox(pos, yaw, s, new(-p.L / 2 + 0.25f, p.WingHeight, 0), new(p.WingChord / 2, 0.03f, span / 2), Trim);
        }
    }

    // Matches build_gantry() in blender/build_models.py
    public static void AddGantry(Vector2 pos, float heading)
    {
        float s = Scene3D.UnitsPerMetre, yaw = -heading;
        const float span = 19f, height = 6.6f;
        AddModelBox(pos, yaw, s, new(0, height / 2, -span / 2), new(0.3f, height / 2, 0.3f), Steel);
        AddModelBox(pos, yaw, s, new(0, height / 2, span / 2), new(0.3f, height / 2, 0.3f), Steel);
        AddModelBox(pos, yaw, s, new(0, height, 0), new(0.35f, 0.35f, span / 2 + 0.3f), Steel);
        AddModelBox(pos, yaw, s, new(0, height - 0.95f, 0), new(0.125f, 0.6f, span * 0.31f), Banner);
    }

    // Same placement as Scene3D.DrawTree
    public static void AddTree(Vector2 pos, float size, int variant)
    {
        if (treeCount >= MaxTrees) return;
        float s = size / 26f * Scene3D.UnitsPerMetre;
        float yaw = variant * 47 % 360 * MathF.PI / 180f;
        trees[treeCount++] = new Vector4(pos.X, pos.Y, variant % 2 == 0 ? -s : s, yaw);
    }

    public static void Upload()
    {
        foreach (Bound b in shaders)
        {
            Raylib.SetShaderValue(b.Shader, b.On, Enabled ? 1 : 0, ShaderUniformDataType.Int);
            Raylib.SetShaderValue(b.Shader, b.BoxCount, boxCount, ShaderUniformDataType.Int);
            Raylib.SetShaderValue(b.Shader, b.TreeCount, treeCount, ShaderUniformDataType.Int);
            if (b.Ground >= 0) Raylib.SetShaderValue(b.Shader, b.Ground, groundColor, ShaderUniformDataType.Vec3);
            if (boxCount > 0)
            {
                Raylib.SetShaderValueV(b.Shader, b.BoxA, boxA, ShaderUniformDataType.Vec4, boxCount);
                Raylib.SetShaderValueV(b.Shader, b.BoxB, boxB, ShaderUniformDataType.Vec4, boxCount);
                Raylib.SetShaderValueV(b.Shader, b.BoxC, boxC, ShaderUniformDataType.Vec4, boxCount);
            }
            if (treeCount > 0) Raylib.SetShaderValueV(b.Shader, b.Trees, trees, ShaderUniformDataType.Vec4, treeCount);
        }
    }

    // ---------------------------------------------------------------- GLSL

    // Shared ray-tracing code, pasted into the lit shader and the ground shadow shader.
    // Needs `uniform vec3 sunDir` declared before it.
    public const string Glsl = @"
#define MAX_BOXES 32
#define MAX_TREES 64
uniform int rtOn;
uniform int boxCount;
uniform vec4 boxA[MAX_BOXES];
uniform vec4 boxB[MAX_BOXES];
uniform vec4 boxC[MAX_BOXES];
uniform int treeCount;
uniform vec4 trees[MAX_TREES];

const float NO_HIT = 1e9;
const float SUN_RADIUS = 0.03; // radians; bigger than the real sun for nicer soft shadows
const float SOFT_MARGIN = 4.0; // world units; covers the widest penumbra of the tallest tree

// Rotate a horizontal vector into an object's frame (c = cos(yaw), s = -sin(yaw)) and back
vec2 rtRot(vec2 v, float c, float s) { return vec2(v.x * c + v.y * s, v.y * c - v.x * s); }
vec2 rtUnrot(vec2 v, float c, float s) { return vec2(v.x * c - v.y * s, v.x * s + v.y * c); }

// All hit tests return the distance to where the ray enters the shape, or NO_HIT.
// A ray that starts inside a shape ignores it: surfaces of a model sit on or inside their own proxy.

float rtBox(vec3 ro, vec3 rd, int i, inout vec3 nrm) {
    vec4 A = boxA[i], B = boxB[i];
    vec3 o = ro - A.xyz;
    vec2 oxz = rtRot(o.xz, A.w, B.w), dxz = rtRot(rd.xz, A.w, B.w);
    vec3 lo = vec3(oxz.x, o.y, oxz.y), ld = vec3(dxz.x, rd.y, dxz.y);
    vec3 sg = vec3(greaterThanEqual(ld, vec3(0.0))) * 2.0 - 1.0;
    vec3 inv = sg / max(abs(ld), vec3(1e-7));
    vec3 t1 = (-B.xyz - lo) * inv, t2 = (B.xyz - lo) * inv;
    vec3 tn3 = min(t1, t2), tf3 = max(t1, t2);
    float tn = max(max(tn3.x, tn3.y), tn3.z), tf = min(min(tf3.x, tf3.y), tf3.z);
    if (tn > tf || tn <= 0.0) return NO_HIT;
    vec3 ln = tn == tn3.x ? vec3(-sg.x, 0.0, 0.0) : tn == tn3.y ? vec3(0.0, -sg.y, 0.0) : vec3(0.0, 0.0, -sg.z);
    vec2 wn = rtUnrot(ln.xz, A.w, B.w);
    nrm = vec3(wn.x, ln.y, wn.y);
    return tn;
}

float rtSphere(vec3 ro, vec3 rd, vec3 c, float r, inout vec3 nrm) {
    vec3 oc = ro - c;
    float b = dot(oc, rd), cc = dot(oc, oc) - r * r;
    float h = b * b - cc;
    if (cc < 0.0 || h < 0.0) return NO_HIT;
    float t = -b - sqrt(h);
    if (t <= 0.0) return NO_HIT;
    nrm = (oc + rd * t) / r;
    return t;
}

// Upright cylinder on the origin, from y = 0 to `hgt` (sides only: it's a trunk)
float rtTrunk(vec3 ro, vec3 rd, float r, float hgt, inout vec3 nrm) {
    float a = dot(rd.xz, rd.xz);
    float b = dot(ro.xz, rd.xz), c = dot(ro.xz, ro.xz) - r * r;
    float h = b * b - a * c;
    if (a < 1e-8 || c < 0.0 || h < 0.0) return NO_HIT;
    float t = (-b - sqrt(h)) / a, y = ro.y + rd.y * t;
    if (t <= 0.0 || y < 0.0 || y > hgt) return NO_HIT;
    nrm = vec3(ro.x + rd.x * t, 0.0, ro.z + rd.z * t) / r;
    return t;
}

// Upright cone on the origin axis: base radius r at height yb, tip at yb + hgt
float rtCone(vec3 ro, vec3 rd, float yb, float hgt, float r, inout vec3 nrm) {
    float k2 = (r / hgt) * (r / hgt);
    vec3 p = ro - vec3(0.0, yb + hgt, 0.0); // relative to the tip
    if (p.y < 0.0 && p.y > -hgt && dot(p.xz, p.xz) < k2 * p.y * p.y) return NO_HIT;
    float best = NO_HIT;
    if (abs(rd.y) > 1e-6) {
        float t = (-hgt - p.y) / rd.y;
        vec2 q = p.xz + rd.xz * t;
        if (t > 0.0 && dot(q, q) < r * r) { best = t; nrm = vec3(0.0, -1.0, 0.0); }
    }
    float a = dot(rd.xz, rd.xz) - k2 * rd.y * rd.y;
    float b = dot(p.xz, rd.xz) - k2 * p.y * rd.y;
    float c = dot(p.xz, p.xz) - k2 * p.y * p.y;
    float h = b * b - a * c;
    if (h >= 0.0 && abs(a) > 1e-8) {
        float sq = sqrt(h);
        for (int j = 0; j < 2; j++) {
            float t = (-b + (j == 0 ? -sq : sq)) / a;
            vec3 q = p + rd * t;
            if (t > 0.0 && t < best && q.y < 0.0 && q.y > -hgt) {
                best = t;
                nrm = normalize(vec3(q.x, -k2 * q.y, q.z));
            }
        }
    }
    return best;
}

// Does a ray come within `r` of the upright column at c (heights y0..y1)? Cheap rejection test.
bool rtColumn(vec3 ro, vec3 rd, vec2 c, float y0, float y1, float r) {
    float t0 = 0.0, t1 = 1e7;
    if (abs(rd.y) > 1e-6) {
        float a = (y0 - ro.y) / rd.y, b = (y1 - ro.y) / rd.y;
        t0 = max(t0, min(a, b));
        t1 = min(t1, max(a, b));
    } else if (ro.y < y0 || ro.y > y1) return false;
    if (t0 > t1) return false;
    vec2 o = ro.xz - c, d = rd.xz;
    float dd = dot(d, d);
    float t = dd > 1e-8 ? clamp(-dot(o, d) / dd, t0, t1) : t0;
    vec2 q = o + d * t;
    return dot(q, q) < r * r;
}

float rtTreeRadius(vec4 T) { return (T.z < 0.0 ? 2.65 : 2.9) * abs(T.z); }
float rtTreeHeight(vec4 T) { return (T.z < 0.0 ? 9.45 : 7.9) * abs(T.z); }
float rtBoxRadius(int i) { return length(boxB[i].xz); }

// Tree i, built from the same parts as build_trees() in blender/build_models.py
float rtTree(vec3 ro, vec3 rd, int i, inout vec3 nrm, inout vec3 alb) {
    vec4 T = trees[i];
    if (!rtColumn(ro, rd, T.xy, 0.0, rtTreeHeight(T), rtTreeRadius(T))) return NO_HIT;
    float s = abs(T.z), c = cos(T.w), sn = -sin(T.w);
    vec3 o = (ro - vec3(T.x, 0.0, T.y)) / s;
    vec2 oxz = rtRot(o.xz, c, sn), dxz = rtRot(rd.xz, c, sn);
    vec3 lo = vec3(oxz.x, o.y, oxz.y), ld = vec3(dxz.x, rd.y, dxz.y);
    float best = NO_HIT, t;
    vec3 n = vec3(0.0, 1.0, 0.0);
    vec3 bark = vec3(0.33, 0.24, 0.16);
    if (T.z < 0.0) {
        vec3 leaf = vec3(0.13, 0.33, 0.18);
        t = rtTrunk(lo, ld, 0.28, 2.4, n); if (t < best) { best = t; nrm = n; alb = bark; }
        t = rtCone(lo, ld, 1.8, 3.6, 2.6, n); if (t < best) { best = t; nrm = n; alb = leaf; }
        t = rtCone(lo, ld, 3.8, 3.2, 2.0, n); if (t < best) { best = t; nrm = n; alb = leaf; }
        t = rtCone(lo, ld, 5.6, 2.8, 1.4, n); if (t < best) { best = t; nrm = n; alb = leaf; }
        t = rtCone(lo, ld, 7.2, 2.2, 0.8, n); if (t < best) { best = t; nrm = n; alb = leaf; }
    } else {
        vec3 leaf = vec3(0.23, 0.47, 0.21);
        t = rtTrunk(lo, ld, 0.27, 3.4, n); if (t < best) { best = t; nrm = n; alb = bark; }
        // Blender (x, y, z) is (x, z, -y) here
        t = rtSphere(lo, ld, vec3(0.0, 4.6, 0.0), 2.5, n); if (t < best) { best = t; nrm = n; alb = leaf; }
        t = rtSphere(lo, ld, vec3(0.9, 5.6, -0.6), 1.7, n); if (t < best) { best = t; nrm = n; alb = leaf; }
        t = rtSphere(lo, ld, vec3(-0.9, 5.3, 0.5), 1.8, n); if (t < best) { best = t; nrm = n; alb = leaf; }
        t = rtSphere(lo, ld, vec3(0.2, 6.4, 0.9), 1.4, n); if (t < best) { best = t; nrm = n; alb = leaf; }
        t = rtSphere(lo, ld, vec3(-0.4, 6.5, -0.8), 1.3, n); if (t < best) { best = t; nrm = n; alb = leaf; }
    }
    if (best >= NO_HIT) return NO_HIT;
    vec2 wn = rtUnrot(nrm.xz, c, sn);
    nrm = vec3(wn.x, nrm.y, wn.y);
    return best * s;
}

// Hard shadow ray: is anything between `ro` and the sky along `rd`?
bool rtOccluded(vec3 ro, vec3 rd) {
    vec3 n, a;
    for (int i = 0; i < boxCount; i++) if (rtBox(ro, rd, i, n) < NO_HIT) return true;
    for (int i = 0; i < treeCount; i++) if (rtTree(ro, rd, i, n, a) < NO_HIT) return true;
    return false;
}

// Interleaved gradient noise: a different sample pattern per pixel, which averages into smooth penumbras
float rtNoise() { return fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))); }

// Soft sun shadow at p (normal n): 0 = fully lit, 1 = fully shadowed.
// First a cheap test finds which shapes could be anywhere near the sun direction (bit masks),
// then only those are traced, with rays spread over the sun's disc.
float rtShadow(vec3 p, vec3 n) {
    vec3 L = -sunDir;
    vec3 ro = p + n * 0.06;
    uint boxMask = 0u, treeLo = 0u, treeHi = 0u;
    for (int i = 0; i < boxCount; i++) {
        vec4 A = boxA[i], B = boxB[i];
        if (rtColumn(ro, L, A.xz, A.y - B.y, A.y + B.y, rtBoxRadius(i) + SOFT_MARGIN)) boxMask |= 1u << uint(i);
    }
    for (int i = 0; i < treeCount; i++) {
        vec4 T = trees[i];
        if (rtColumn(ro, L, T.xy, 0.0, rtTreeHeight(T), rtTreeRadius(T) + SOFT_MARGIN)) {
            if (i < 32) treeLo |= 1u << uint(i); else treeHi |= 1u << uint(i - 32);
        }
    }
    if (boxMask == 0u && treeLo == 0u && treeHi == 0u) return 0.0;

    vec3 u = normalize(cross(L, vec3(0.0, 1.0, 0.0)));
    vec3 v = cross(L, u);
    float spin = rtNoise() * 6.2831853;
    float occ = 0.0;
    int taken = 0;
    for (int k = 0; k < 12; k++) {
        // After 4 rays, stop if they all agree (fully lit or fully in shadow)
        if (k == 4 && (occ == 0.0 || occ == 4.0)) break;
        float r = sqrt((float(k % 4) + 0.5) / 4.0) * SUN_RADIUS * (k < 4 ? 1.0 : k < 8 ? 0.7 : 0.4);
        float ang = float(k) * 2.3999632 + spin;
        vec3 d = normalize(L + (u * cos(ang) + v * sin(ang)) * r);
        vec3 nn, aa;
        bool hit = false;
        for (int i = 0; i < boxCount && !hit; i++)
            if ((boxMask & (1u << uint(i))) != 0u && rtBox(ro, d, i, nn) < NO_HIT) hit = true;
        for (int i = 0; i < treeCount && !hit; i++) {
            uint m = i < 32 ? treeLo : treeHi;
            if ((m & (1u << uint(i & 31))) != 0u && rtTree(ro, d, i, nn, aa) < NO_HIT) hit = true;
        }
        if (hit) occ += 1.0;
        taken++;
    }
    return occ / float(taken);
}
";

    // Reflection tracing; pasted into the lit shader after Glsl (uses its fog uniforms).
    public const string ReflectGlsl = @"
uniform vec3 rtGround;

vec3 rtSky(vec3 d) {
    vec3 sky = mix(vec3(0.769, 0.839, 0.894), vec3(0.361, 0.580, 0.839), smoothstep(0.0, 0.55, d.y));
    return sky + vec3(1.0, 0.95, 0.85) * pow(max(dot(d, -sunDir), 0.0), 900.0) * 3.0;
}

// What a mirror ray from ro sees: the nearest car / tree / gantry / ground, lit and shadowed, or the sky
vec3 rtReflection(vec3 ro, vec3 rd) {
    float best = NO_HIT, t;
    vec3 nrm = vec3(0.0, 1.0, 0.0), alb = vec3(0.0), n, a;
    for (int i = 0; i < boxCount; i++) {
        t = rtBox(ro, rd, i, n);
        if (t < best) { best = t; nrm = n; alb = boxC[i].rgb; }
    }
    for (int i = 0; i < treeCount; i++) {
        t = rtTree(ro, rd, i, n, a);
        if (t < best) { best = t; nrm = n; alb = a; }
    }
    bool ground = false;
    if (rd.y < -1e-4) {
        t = -ro.y / rd.y;
        if (t > 0.0 && t < best) { best = t; nrm = vec3(0.0, 1.0, 0.0); alb = rtGround; ground = true; }
    }
    if (best >= NO_HIT) return rtSky(rd);

    vec3 p = ro + rd * best;
    vec3 L = -sunDir;
    float diff = max(dot(nrm, L), 0.0);
    if (diff > 0.0 && rtOccluded(p + nrm * 0.06, L)) diff = 0.0;
    vec3 col;
    if (ground) col = alb * (diff > 0.0 ? 1.0 : 0.6); // the ground is drawn unlit, only darkened by shadows
    else {
        vec3 amb = mix(vec3(0.26, 0.27, 0.24), vec3(0.50, 0.56, 0.66), 0.5 + 0.5 * nrm.y);
        col = alb * (amb + vec3(1.0, 0.95, 0.86) * diff * 0.80);
    }
    float f = clamp((best - fogRange.x) / (fogRange.y - fogRange.x), 0.0, 1.0);
    return mix(col, fogColor, f);
}
";

    // Ground shadow pass: one disc over the already-drawn ground, darkening where the sun is blocked
    public const string ShadowFs = @"#version 330
in vec4 fragColor;
in vec3 worldPos;
uniform vec3 sunDir;
uniform vec3 viewPos;
uniform vec2 fogRange;
" + Glsl + @"
out vec4 finalColor;
void main() {
    float f = clamp((length(viewPos - worldPos) - fogRange.x) / (fogRange.y - fogRange.x), 0.0, 1.0);
    if (rtOn == 0 || f >= 1.0) discard;
    float s = rtShadow(worldPos, vec3(0.0, 1.0, 0.0));
    if (s <= 0.0) discard;
    finalColor = vec4(0.0, 0.0, 0.0, s * 0.40 * (1.0 - f));
}";
}
