using Raylib_cs;

Raylib.InitWindow(800, 450, "Racing Game");
Raylib.SetTargetFPS(120);

Button startButton = new Button(200, 300, 300, 50, "Start", () =>
{
    Console.WriteLine("Start Race clicked");
});

Movement movement = new Movement();

while (!Raylib.WindowShouldClose())
{
    startButton.Update();
    movement.Update();

    Raylib.BeginDrawing();
    Raylib.ClearBackground(Color.DarkGray);
    Raylib.DrawText("Hello, racer", 20, 20, 20, Color.White);
    startButton.Draw();
    Raylib.DrawText($"Speed: {movement.CurrentSpeed:F0}", 20, 60, 20, Color.White);
    Raylib.EndDrawing();
}

Raylib.CloseWindow();