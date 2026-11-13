using Raylib_cs;

enum CarType
{
    SUV,
    Sedan,
    SportsCar,
    HyperCar,
    HeadCar,
    BurgerCar
}

// The "spec sheet" of a car. Several racers can share the same Car.
class Car
{
    public string Brand;
    public string Model;
    public CarType Type;
    public float Acceleration; // km/h gained per second at full throttle (from standstill)
    public float MaxSpeed;     // km/h
    public float Handling;     // 0..1 — steering speed and tyre grip
    public Color Color;

    public string Name => $"{Brand} {Model}";

    public Car(string brand, string model, CarType type, float acceleration, float maxSpeed, float handling, Color color)
    {
        Brand = brand;
        Model = model;
        Type = type;
        Acceleration = acceleration;
        MaxSpeed = maxSpeed;
        Handling = handling;
        Color = color;
    }
}
