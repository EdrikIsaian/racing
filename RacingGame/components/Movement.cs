using Raylib_cs;

class Movement
{
    public double CurrentSpeed;
    public bool Accelerate;
    public bool Brake;
    
    public Movement()
    {
        CurrentSpeed = 0;
        Accelerate = false;
        Brake = false;
    }

    public void Update()
    {
        Accelerate = Raylib.IsKeyDown(KeyboardKey.W);
        Brake = Raylib.IsKeyDown(KeyboardKey.S);
        Console.WriteLine($"Z: {Accelerate}  S: {Brake}");

        if (Brake)
        {
            CurrentSpeed = Math.Max(0, CurrentSpeed - 0.1);
        }

        else if (Accelerate)
        {
            CurrentSpeed += 0.1;
        }

    }

}