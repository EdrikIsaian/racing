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
Raylib.ToggleBorderlessWindowed();   // start full screen
Ui.LoadFont();

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

while (!Raylib.WindowShouldClose() && !game.QuitRequested)
{
    // F or F11 switches between full screen and a normal window
    if (Raylib.IsKeyPressed(KeyboardKey.F) || Raylib.IsKeyPressed(KeyboardKey.F11))
    {
        Raylib.ToggleBorderlessWindowed();
        UpdateFpsCap();
    }
    if (Raylib.IsKeyPressed(KeyboardKey.F3)) PerfOverlay.Toggle();
    PerfOverlay.Record(Raylib.GetFrameTime());

    // Time since last frame, capped so a hiccup doesn't teleport the cars
    float dt = MathF.Min(Raylib.GetFrameTime(), 1f / 30f);
    game.Update(dt);

    Supersample.Begin(); // smoother edges on non-Retina screens
    game.Draw();
    PerfOverlay.Draw();
    Supersample.End();
}

Supersample.Unload();
Ui.UnloadFont();
Raylib.CloseWindow();