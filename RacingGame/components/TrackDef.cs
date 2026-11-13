using System.Numerics;
using Raylib_cs;

enum Biome
{
    Meadow, // rolling green hills, lakes, wind turbines, balloons
    Alpine, // valley between snowy peaks, pines, a lake
    Desert, // dunes, flat-topped mesas, cacti, tumbleweeds
}

// Everything that makes one circuit look and feel different: its shape, its height profile
// and the land around it.
class TrackDef
{
    public string Name = "";
    public string Tagline = "";
    public Biome Biome;
    public int Seed;

    // Centre line: control points (smoothed with a Catmull-Rom spline) and the road height at each one
    public Vector2[] Points = Array.Empty<Vector2>();
    public float[] Elevation = Array.Empty<float>();
    public float Scale = 1.3f;

    // Land
    public float HillAmp, HillScale = 900;       // rolling hills right next to the track
    public float MountainAmp, MountainStart;      // big relief further away
    public float MesaHeight;                      // desert table mountains
    public float SnowLine = float.MaxValue, RockLine = float.MaxValue;
    public float WaterLevel = float.NaN;          // NaN = no water
    public (Vector2 Centre, float Radius, float Depth)[] Lakes = Array.Empty<(Vector2, float, float)>();

    // Colours
    public Color Grass, GrassDark, Rock, Snow, Sand, Verge;
    public Color Water = new(40, 96, 128, 255);
    public Color SkyTop, Horizon, Cloud = new(250, 251, 255, 255);
    public Vector3 Tint = Vector3.One;            // multiplies all lighting (warm sunset, ...)
    public float FogStart = 1800, FogEnd = 6500;
    public float Stripes;                         // mowed-grass stripes strength (0 = none)

    // Scenery amounts
    public int Trees, Rocks, Cacti, Turbines, Balloons, Flocks, Tumbleweeds;
    public float PineShare = 0.5f;
    public Color[] FlagColors = Array.Empty<Color>();

    public static readonly TrackDef[] All =
    {
        new TrackDef
        {
            Name = "Greenfield Park",
            Tagline = "Rolling meadow circuit",
            Biome = Biome.Meadow,
            Seed = 7,
            Points = new Vector2[]
            {
                new(900, 300), new(1700, 300), new(2400, 380), new(2850, 700), new(2850, 1150),
                new(2450, 1400), new(1950, 1250), new(1550, 1450), new(1650, 1850), new(1250, 2100),
                new(600, 2000), new(300, 1500), new(350, 800), new(500, 400),
            },
            Elevation = new float[] { 0, 2, 14, 38, 60, 52, 34, 22, 6, -14, -24, -16, -4, 0 },
            Scale = 1.3f,
            HillAmp = 70, HillScale = 950,
            MountainAmp = 380, MountainStart = 1800,
            RockLine = 520,
            WaterLevel = -46,
            Lakes = new[] { (new Vector2(1250, 1250), 360f, 120f), (new Vector2(3500, 2300), 600f, 140f) },
            Grass = new(72, 124, 60, 255), GrassDark = new(56, 104, 50, 255),
            Rock = new(122, 116, 100, 255), Snow = new(236, 240, 246, 255), Sand = new(170, 160, 112, 255),
            Verge = new(58, 102, 52, 255),
            Water = new(46, 104, 134, 255),
            SkyTop = new(92, 148, 214, 255), Horizon = new(196, 214, 228, 255),
            FogStart = 1800, FogEnd = 7000,
            Stripes = 0.055f,
            Trees = 230, Rocks = 25, Turbines = 5, Balloons = 4, Flocks = 2, PineShare = 0.4f,
            FlagColors = new Color[] { new(220, 40, 48, 255), new(250, 250, 250, 255), new(40, 110, 220, 255) },
        },
        new TrackDef
        {
            Name = "Alpine Pass",
            Tagline = "Mountain valley, big climbs",
            Biome = Biome.Alpine,
            Seed = 21,
            Points = new Vector2[]
            {
                new(800, 400), new(1700, 350), new(2500, 500), new(3100, 900), new(3200, 1500),
                new(2800, 1900), new(2200, 1800), new(1800, 1400), new(1300, 1500), new(1100, 2000),
                new(600, 2300), new(200, 1900), new(150, 1200), new(350, 700),
            },
            Elevation = new float[] { 0, 30, 100, 190, 280, 320, 270, 210, 150, 90, 40, -10, -30, -16 },
            Scale = 1.4f,
            HillAmp = 110, HillScale = 800,
            MountainAmp = 1500, MountainStart = 700,
            RockLine = 560, SnowLine = 940,
            WaterLevel = -60,
            Lakes = new[] { (new Vector2(-700, 1300), 700f, 260f) },
            Grass = new(70, 114, 58, 255), GrassDark = new(48, 88, 50, 255),
            Rock = new(118, 114, 108, 255), Snow = new(238, 242, 248, 255), Sand = new(140, 132, 110, 255),
            Verge = new(54, 92, 50, 255),
            Water = new(38, 92, 120, 255),
            SkyTop = new(70, 130, 210, 255), Horizon = new(204, 220, 236, 255),
            FogStart = 2400, FogEnd = 9500,
            Trees = 340, Rocks = 70, Flocks = 3, PineShare = 0.92f,
            FlagColors = new Color[] { new(220, 30, 40, 255), new(250, 250, 250, 255) },
        },
        new TrackDef
        {
            Name = "Sunset Mesa",
            Tagline = "Desert canyon at dusk",
            Biome = Biome.Desert,
            Seed = 42,
            Points = new Vector2[]
            {
                new(600, 500), new(1500, 450), new(2300, 600), new(2700, 1100), new(2400, 1600),
                new(1900, 1500), new(1500, 1100), new(1050, 1250), new(1000, 1800), new(1500, 2200),
                new(2300, 2300), new(2600, 2700), new(2000, 3000), new(900, 2900), new(300, 2300),
                new(200, 1300),
            },
            Elevation = new float[] { 0, 8, 40, 96, 150, 128, 70, 20, -40, -100, -80, -10, 60, 90, 54, 18 },
            Scale = 1.35f,
            HillAmp = 45, HillScale = 700,
            MountainAmp = 600, MountainStart = 2200,
            MesaHeight = 260,
            Grass = new(214, 170, 110, 255), GrassDark = new(198, 150, 96, 255),
            Rock = new(172, 92, 58, 255), Snow = new(196, 126, 82, 255), Sand = new(222, 182, 124, 255),
            Verge = new(184, 140, 92, 255),
            SkyTop = new(64, 86, 156, 255), Horizon = new(248, 178, 120, 255), Cloud = new(255, 214, 190, 255),
            Tint = new Vector3(1.08f, 0.96f, 0.86f),
            FogStart = 2000, FogEnd = 8500,
            Trees = 0, Rocks = 90, Cacti = 170, Flocks = 2, Tumbleweeds = 10,
            FlagColors = new Color[] { new(250, 120, 30, 255), new(30, 30, 34, 255), new(250, 220, 60, 255) },
        },
    };
}
