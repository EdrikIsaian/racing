using Raylib_cs;

Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint | ConfigFlags.VSyncHint);
Raylib.InitWindow(1280, 720, "Racing Game");
Raylib.SetTargetFPS(120);
Raylib.SetExitKey(KeyboardKey.Null); // Esc pauses instead of closing the window

Game game = new Game();

while (!Raylib.WindowShouldClose() && !game.QuitRequested)
{
    // Time since last frame, capped so a hiccup doesn't teleport the cars
    float dt = MathF.Min(Raylib.GetFrameTime(), 1f / 30f);
    game.Update(dt);

    Raylib.BeginDrawing();
    game.Draw();
    Raylib.EndDrawing();
}

Raylib.CloseWindow();
