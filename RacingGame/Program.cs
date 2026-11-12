using Raylib_cs;

Raylib.InitWindow(800, 450, "Racing Game");
Raylib.SetTargetFPS(120);

while (!Raylib.WindowShouldClose())
{
    Raylib.BeginDrawing();
    Raylib.ClearBackground(Color.DarkGray);
    Raylib.DrawText("Hello, racer", 20, 20, 20, Color.White);
    Raylib.EndDrawing();
}

Raylib.CloseWindow();