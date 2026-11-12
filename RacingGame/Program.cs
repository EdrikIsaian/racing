using Raylib_cs;

// HighDpiWindow = render at full Retina resolution instead of upscaling a blurry image
Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint | ConfigFlags.VSyncHint | ConfigFlags.ResizableWindow | ConfigFlags.HighDpiWindow);
Raylib.InitWindow(Ui.BaseWidth, Ui.BaseHeight, "Racing Game");
Raylib.SetTargetFPS(120);
Raylib.SetExitKey(KeyboardKey.Null); // Esc pauses instead of closing the window
Raylib.ToggleBorderlessWindowed();   // start full screen
Ui.LoadFont();

Game game = new Game();

while (!Raylib.WindowShouldClose() && !game.QuitRequested)
{
    // F or F11 switches between full screen and a normal window
    if (Raylib.IsKeyPressed(KeyboardKey.F) || Raylib.IsKeyPressed(KeyboardKey.F11))
        Raylib.ToggleBorderlessWindowed();

    // Time since last frame, capped so a hiccup doesn't teleport the cars
    float dt = MathF.Min(Raylib.GetFrameTime(), 1f / 30f);
    game.Update(dt);

    Raylib.BeginDrawing();
    game.Draw();
    Raylib.EndDrawing();
}

Ui.UnloadFont();
Raylib.CloseWindow();