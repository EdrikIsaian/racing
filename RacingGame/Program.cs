using Raylib_cs;

Raylib.InitWindow(800, 450, "Racing Game");
Raylib.SetTargetFPS(120);

Car GLC = new Car("Mercedes-Benz", "GLC", CarType.SUV, 0.2, 120);
Car Octavia = new Car("Skoda", "Octavia", CarType.Sedan, 0.25, 140);
Car LaFerrari = new Car("Ferrari", "LaFerrari", CarType.HyperCar, 0.5, 300);

Car selectedCar = GLC;

Button glcButton = new Button(20, 120, 220, 40, "GLC", () => selectedCar = GLC);
Button octaviaButton = new Button(20, 170, 220, 40, "Octavia", () => selectedCar = Octavia);
Button laFerrariButton = new Button(20, 220, 220, 40, "LaFerrari", () => selectedCar = LaFerrari);

Button startButton = new Button(200, 300, 300, 50, "Start", () =>
{
    Console.WriteLine($"Starting race with {selectedCar.Brand} {selectedCar.Model}");
});

Movement movement = new Movement();
Track track = new Track();

while (!Raylib.WindowShouldClose())
{
    glcButton.Update();
    octaviaButton.Update();
    laFerrariButton.Update();
    startButton.Update();
    movement.Update(selectedCar);

    Raylib.BeginDrawing();
    Raylib.ClearBackground(Color.DarkGreen);

    track.Draw();

    Raylib.DrawText("Hello, racer", 20, 20, 20, Color.White);
    Raylib.DrawText($"Speed: {movement.CurrentSpeed:F0}", 20, 60, 20, Color.White);
    Raylib.DrawText($"Car: {selectedCar.Brand} {selectedCar.Model}", 20, 90, 20, Color.White);

    glcButton.Draw();
    octaviaButton.Draw();
    laFerrariButton.Draw();
    startButton.Draw();

    Raylib.EndDrawing();
}

Raylib.CloseWindow();