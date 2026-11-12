using Raylib_cs;

class Movement
{
    public double CurrentSpeed;
    public bool Accelerate;
    public bool Brake;

    private const double BrakeForce = 0.5;

    public Movement()
    {
        CurrentSpeed = 0;
        Accelerate = false;
        Brake = false;
    }

    public void Update(Car car)
    {
        Accelerate = Raylib.IsKeyDown(KeyboardKey.W);
        Brake = Raylib.IsKeyDown(KeyboardKey.S);

        if (Brake)
        {
            CurrentSpeed = Math.Max(0, CurrentSpeed - BrakeForce);
        }
        else if (Accelerate)
        {
            CurrentSpeed = Math.Min(car.MaxSpeed, CurrentSpeed + car.Acceleration);
        }
    }
}