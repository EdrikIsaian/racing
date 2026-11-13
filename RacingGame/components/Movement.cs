using System.Numerics;

// What a driver (keyboard or AI) wants the car to do this frame.
struct DriverInput
{
    public float Throttle;  // 0..1
    public float Brake;     // 0..1 (reverses once the car has stopped)
    public float Steer;     // -1 (left) .. 1 (right)
    public bool Handbrake;
    public bool ShiftUp, ShiftDown; // manual gearbox, pressed this frame
}

// Top-down arcade car physics. Units are pixels and seconds.
// The car keeps some sideways velocity when it turns, which is what makes it slide/drift.
class Movement
{
    public const float PixelsPerKmh = 2f;   // 100 km/h = 200 px/s
    public const float BrakeDecel = 900f;   // px/s²
    const float Gravity = 84f * 1.4f;        // 9.8 m/s² in px/s², a bit stronger so hills are felt
    const float ReverseAccel = 300f;
    const float MaxReverse = 140f;

    public Vector2 Position;
    public Vector2 Velocity;
    public float Heading;      // radians, 0 = facing right (+X)
    public bool IsSkidding;

    public Vector2 Forward => new(MathF.Cos(Heading), MathF.Sin(Heading));
    public Vector2 Right => new(-MathF.Sin(Heading), MathF.Cos(Heading));
    public float ForwardSpeed => Vector2.Dot(Velocity, Forward); // px/s, negative when reversing
    public float CurrentSpeed => Velocity.Length() / PixelsPerKmh; // km/h

    public static float BaseTurnRate(Car car) => 1.9f + 1.6f * car.Handling; // rad/s

    // You can't turn while standing still, and you turn a bit slower at top speed.
    public static float TurnRate(Car car, float speed)
    {
        float abs = MathF.Abs(speed);
        float lowSpeed = Math.Clamp(abs / 150f, 0f, 1f);
        float speedRatio = Math.Clamp(abs / (car.MaxSpeed * PixelsPerKmh), 0f, 1f);
        return BaseTurnRate(car) * lowSpeed * (1f - 0.35f * speedRatio);
    }

    // drive: how much the engine can push right now (from the gearbox; 0 while shifting)
    // slope: how much the ground rises per unit driven forward (hills slow you down going up)
    public void Update(Car car, DriverInput input, bool onTrack, float dt, float drive = 1f, float slope = 0f)
    {
        Vector2 forward = Forward;
        Vector2 right = Right;
        float vf = Vector2.Dot(Velocity, forward); // forward speed
        float vl = Vector2.Dot(Velocity, right);   // sideways (slide) speed

        float topSpeed = car.MaxSpeed * PixelsPerKmh;
        float limit = onTrack ? topSpeed : topSpeed * 0.45f; // grass is slow

        // Engine: strong at low speed, weaker near top speed
        if (input.Throttle > 0 && vf < limit)
        {
            float ratio = Math.Clamp(vf / topSpeed, 0f, 1f);
            vf += car.Acceleration * PixelsPerKmh * input.Throttle * (1f - 0.6f * ratio * ratio) * drive * dt;
        }

        // Brake, or reverse once stopped
        if (input.Brake > 0)
        {
            if (vf > 10f) vf = MathF.Max(0, vf - BrakeDecel * input.Brake * dt);
            else vf = MathF.Max(-MaxReverse, vf - ReverseAccel * input.Brake * dt);
        }

        // Gravity along the slope
        vf -= Gravity * Math.Clamp(slope, -0.5f, 0.5f) * dt;

        // Air drag + rolling resistance (much more on grass)
        vf -= vf * (onTrack ? 0.08f : 1.2f) * dt;
        if (input.Throttle == 0 && input.Brake == 0) vf -= vf * 0.35f * dt; // engine braking
        if (vf > limit) vf -= (vf - limit) * 2.5f * dt;                      // drove onto grass too fast

        // Steering
        float turn = TurnRate(car, vf) * input.Steer * MathF.Sign(vf);

        // Grip: how fast the sideways slide is killed. Handbrake = almost no grip = drift
        float grip = (5f + 7f * car.Handling) * (onTrack ? 1f : 0.55f);
        if (input.Handbrake)
        {
            grip = 1.0f;
            vf -= vf * 0.9f * dt;
            turn *= 1.35f;
        }
        vl *= MathF.Exp(-grip * dt);

        Velocity = forward * vf + right * vl;
        Heading += turn * dt; // velocity lags behind the new heading next frame → slide
        Position += Velocity * dt;

        IsSkidding = MathF.Abs(vl) > 70f || (input.Brake > 0.5f && vf > 250f);
    }
}
