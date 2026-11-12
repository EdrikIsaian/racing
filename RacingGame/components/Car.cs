enum CarType
{
    SUV,
    Sedan,
    HyperCar
}

class Car
{
    public string Brand;
    public string Model;
    public CarType Type;
    public double Acceleration;
    public double MaxSpeed;

    public Car(string brand, string model, CarType type, double acceleration, double maxSpeed)
    {
        Brand = brand;
        Model = model;
        Type = type;
        Acceleration = acceleration;
        MaxSpeed = maxSpeed;
    }
}