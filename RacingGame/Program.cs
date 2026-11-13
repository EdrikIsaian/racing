using System.Globalization;
using System.Runtime;
using Raylib_cs;

// Avoid long blocking garbage collections in the middle of a race
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

// Always format numbers as 0:22.437, never 0:22,437, whatever the system locale
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// HighDpiWindow = render at full Retina resolution instead of upscaling a blurry image
Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint | ConfigFlags.VSyncHint | ConfigFlags.ResizableWindow | ConfigFlags.HighDpiWindow);
Raylib.InitWindow(Ui.BaseWidth, Ui.BaseHeight, "Racing Game");
Raylib.SetExitKey(KeyboardKey.Null); // Esc pauses instead of closing the window
// RACING_SCREENSHOTS=1: windowed, takes a few screenshots (menu, race) into the working directory, then quits
bool screenshotRun = Environment.GetEnvironmentVariable("RACING_SCREENSHOTS") == "1";
if (!screenshotRun) Raylib.ToggleBorderlessWindowed(); // start full screen
if (Environment.GetEnvironmentVariable("RACING_RT") == "0") RayTracing.Enabled = false; // start with ray tracing off
if (Environment.GetEnvironmentVariable("RACING_FX") == "0") PostFx.Enabled = false;      // start without post-processing
Ui.LoadFont();
Scene3D.Load();
Audio.Init();
Rlgl.SetClipPlanes(3, 16000); // world units are small (a car is ~40 long), the circuit is ~4000 across, mountains further

// VSync paces the frames. The FPS cap is only a safety net (e.g. when the window is hidden and
// VSync stops working), so keep it well above the refresh rate: a cap equal to the refresh rate
// makes raylib sleep at the wrong moment and miss a screen refresh, which shows as a stutter.
static void UpdateFpsCap()
{
    int hz = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
    Raylib.SetTargetFPS(Math.Max(hz, 60) * 2);
}
UpdateFpsCap();

Game game = new Game();
if (int.TryParse(Environment.GetEnvironmentVariable("RACING_CAR"), out int carNo)) game.PickCar(carNo - 1); // RACING_CAR=5: start on car 5
if (int.TryParse(Environment.GetEnvironmentVariable("RACING_TRACK"), out int trackNo)) game.PickTrack(trackNo - 1); // RACING_TRACK=2: second track
// RACING_NET=host or RACING_NET=join:<address> opens a room / joins one straight away (test runs);
// RACING_NET_START=2 makes the host start the race once that many players are in
string? netMode = Environment.GetEnvironmentVariable("RACING_NET");
if (netMode != null) game.AutoNet(netMode, int.TryParse(Environment.GetEnvironmentVariable("RACING_NET_START"), out int n) ? n : 0);
string shotPrefix = Environment.GetEnvironmentVariable("RACING_SHOT_PREFIX") ?? "";
int frame = 0;

while (!Raylib.WindowShouldClose() && !game.QuitRequested)
{
    // Single-key shortcuts, unless you're typing in a text box
    if (!TextField.AnyFocused)
    {
        // F or F11 switches between full screen and a normal window
        if (Raylib.IsKeyPressed(KeyboardKey.F) || Raylib.IsKeyPressed(KeyboardKey.F11))
        {
            Raylib.ToggleBorderlessWindowed();
            UpdateFpsCap();
        }
        if (Raylib.IsKeyPressed(KeyboardKey.F3)) PerfOverlay.Toggle();
        if (Raylib.IsKeyPressed(KeyboardKey.M)) Audio.MusicMuted = !Audio.MusicMuted;
        if (Raylib.IsKeyPressed(KeyboardKey.T)) RayTracing.Enabled = !RayTracing.Enabled;
        if (Raylib.IsKeyPressed(KeyboardKey.G)) PostFx.Enabled = !PostFx.Enabled;
    }
    PerfOverlay.Record(Raylib.GetFrameTime());

    // Time since last frame, capped so a hiccup doesn't teleport the cars
    float dt = MathF.Min(Raylib.GetFrameTime(), 1f / 30f);
    game.Update(dt);
    Audio.Update(Raylib.GetFrameTime());

    game.PreDraw();       // off-screen 3D renders (menu showroom)
    Supersample.Begin(); // smoother edges on non-Retina screens
    game.Draw();
    PerfOverlay.Draw();
    if (screenshotRun)
    {
        frame++;
        if (frame == 60) Supersample.CaptureTo = shotPrefix + "shot_menu.png";
        if (frame == 61 && netMode == null) game.StartRaceNow();
        if (frame == 150) Supersample.CaptureTo = shotPrefix + "shot_grid.png";
        if (frame == 480) Supersample.CaptureTo = shotPrefix + "shot_race.png";
        if (frame == 1100) Supersample.CaptureTo = shotPrefix + "shot_race2.png";
        if (frame == 1900) Supersample.CaptureTo = shotPrefix + "shot_race3.png";
        if (frame == 1902) break;
    }
    Supersample.End();
}

Net.Stop();
Net.StopBrowsing();
Supersample.Unload();
PostFx.Unload();
Scene3D.Unload();
Audio.Shutdown();
Ui.UnloadFont();
Raylib.CloseWindow();