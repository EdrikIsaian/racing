// Engine + gearbox: turns road speed into engine RPM, picks gears (automatic or manual)
// and tells the physics how much drive the engine gives right now.
class Gearbox
{
    public readonly EngineSpec Spec;
    public bool Manual;

    public int Gear = 1;          // 1..Spec.Gears; shown as R while rolling backwards
    public float Rpm;
    public bool Reversing;
    public bool OnLimiter;        // bouncing off the rev limiter
    public int ShiftedThisFrame;  // +1 up, -1 down, 0 none (for sounds)
    public float DriveFactor = 1; // multiplies engine acceleration

    float shiftTimer;             // power is cut while the clutch is in
    float limiterPhase;

    const float ShiftTime = 0.16f;

    public Gearbox(EngineSpec spec) { Spec = spec; Rpm = spec.IdleRpm; }

    public bool Shifting => shiftTimer > 0;

    // Speed (px/s) at the redline in the given gear. Lower gears are packed closer together.
    float GearTopSpeed(int gear, float carTopSpeed) =>
        carTopSpeed * 1.04f * MathF.Pow(gear / (float)Spec.Gears, 0.72f);

    public void Update(float forwardSpeed, float carTopSpeed, DriverInput input, float dt)
    {
        ShiftedThisFrame = 0;
        Reversing = forwardSpeed < -5f;
        float speed = MathF.Abs(forwardSpeed);
        if (shiftTimer > 0) shiftTimer -= dt;

        if (Reversing) Gear = 1;
        else if (Manual)
        {
            if (input.ShiftUp && Gear < Spec.Gears) Shift(+1);
            if (input.ShiftDown && Gear > 1) Shift(-1);
        }
        else if (!Shifting)
        {
            float rpmNow = RpmAt(Gear, speed, carTopSpeed);
            float downAt = (input.Brake > 0 ? 0.55f : 0.40f) * Spec.RedlineRpm;
            if (rpmNow > 0.94f * Spec.RedlineRpm && input.Throttle > 0 && Gear < Spec.Gears) Shift(+1);
            else if (Gear > 1 && rpmNow < downAt && RpmAt(Gear - 1, speed, carTopSpeed) < 0.88f * Spec.RedlineRpm) Shift(-1);
        }

        // Engine speed follows the wheels; in first gear the clutch slips so you can rev it from standstill
        float target = RpmAt(Reversing ? 1 : Gear, speed, carTopSpeed);
        if (Gear == 1 || Reversing)
            target = MathF.Max(target, Spec.IdleRpm + (0.42f * Spec.RedlineRpm - Spec.IdleRpm) * input.Throttle);
        if (Shifting) target = MathF.Max(Spec.IdleRpm, target * (input.Throttle > 0 ? 0.92f : 1f));
        target = MathF.Max(target, Spec.IdleRpm + (input.Throttle > 0 ? 300 : 0));

        OnLimiter = target >= Spec.RedlineRpm && input.Throttle > 0;
        if (target > Spec.RedlineRpm)
        {
            // limiter: RPM saws just under the redline
            limiterPhase = (limiterPhase + dt * 22f) % 1f;
            target = Spec.RedlineRpm - 260f * limiterPhase;
        }
        Rpm += (target - Rpm) * (1 - MathF.Exp(-16f * dt));

        DriveFactor = Shifting || OnLimiter ? 0 : Torque(Rpm / Spec.RedlineRpm) * 1.08f;
    }

    float RpmAt(int gear, float speed, float carTopSpeed) => speed / GearTopSpeed(gear, carTopSpeed) * Spec.RedlineRpm;

    void Shift(int dir)
    {
        Gear += dir;
        ShiftedThisFrame = dir;
        shiftTimer = ShiftTime;
    }

    // Fraction of peak torque across the rev range (0..1 of the redline)
    static float Torque(float x)
    {
        if (x < 0.30f) return 0.55f + (x / 0.30f) * 0.25f;
        if (x < 0.65f) return 0.80f + (x - 0.30f) / 0.35f * 0.20f;
        if (x < 0.90f) return 1f;
        return 1f - (x - 0.90f) / 0.10f * 0.15f;
    }

    public string GearLabel => Reversing ? "R" : Gear.ToString();
}

// What the engine sounds and revs like
record EngineSpec(int Gears, float IdleRpm, float RedlineRpm, int Cylinders, float Growl)
{
    public static EngineSpec For(CarType type) => type switch
    {
        CarType.SUV => new EngineSpec(6, 750, 6400, 6, 0.55f),
        CarType.Sedan => new EngineSpec(6, 800, 6600, 4, 0.40f),
        CarType.SportsCar => new EngineSpec(7, 900, 9000, 6, 0.80f),
        _ => new EngineSpec(7, 1000, 9250, 12, 0.70f),
    };
}
