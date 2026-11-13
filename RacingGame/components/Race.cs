using System.Numerics;
using Raylib_cs;

// One race: the track, all racers, the countdown, collisions and the camera.
class Race
{
    public readonly Track Track = new();
    public readonly List<Racer> Racers = new();
    public readonly Racer Player;
    public readonly int Laps;
    public readonly List<Racer> FinishOrder = new();

    public float Countdown = 3f;
    public float RaceTime;
    public bool Started => Countdown <= 0;
    public float LastLapCompletedAt = -10f; // for the "Lap 1: 0:23.4" popup

    readonly SkidMarks skidMarks = new();
    Camera2D camera;
    float playerSteer;

    static readonly string[] AiNames = { "Alex", "Sam", "Riley", "Jordan", "Casey" };
    static readonly Color[] AiColors =
    {
        new Color(52, 120, 246, 255),
        new Color(255, 140, 0, 255),
        new Color(160, 80, 220, 255),
        new Color(0, 190, 190, 255),
        new Color(240, 90, 160, 255),
    };

    public Race(Car car, int opponents, int laps, float skill)
    {
        Laps = laps;
        var rng = new Random();

        // Opponents fill the front of the grid, the player starts at the back.
        int slot = 0;
        for (int i = 0; i < opponents; i++)
        {
            var ai = new Racer(AiNames[i], car, AiColors[i], false);
            float lane = ((float)rng.NextDouble() - 0.5f) * Track.Width * 0.4f;
            ai.Ai = new AiDriver(skill + ((float)rng.NextDouble() - 0.5f) * 0.05f, lane);
            ai.PlaceOnGrid(Track, slot++);
            Racers.Add(ai);
        }

        Player = new Racer("You", car, car.Color, true);
        Player.PlaceOnGrid(Track, slot);
        Racers.Add(Player);

        camera = new Camera2D(Vector2.Zero, Player.Movement.Position, 0, 1f);
    }

    public void Update(float dt)
    {
        if (!Started) Countdown -= dt;
        else RaceTime += dt;

        foreach (Racer r in Racers)
        {
            DriverInput input = default;
            if (Started)
            {
                if (r.IsPlayer && !r.Finished) input = ReadPlayerInput(dt);
                else if (r.Ai != null) input = r.Ai.GetInput(r, Track, dt);
            }

            r.OnTrack = Track.IsOnTrack(r.Movement.Position, r.TrackIndex);
            r.LastSteer = input.Steer;
            r.Braking = input.Brake > 0 && r.Movement.ForwardSpeed > 5;
            r.Movement.Update(r.Car, input, r.OnTrack, dt);

            if (r.UpdateTrackPosition(Track, RaceTime, Laps) && r.IsPlayer)
                LastLapCompletedAt = RaceTime;

            if (r.Finished && !FinishOrder.Contains(r))
            {
                FinishOrder.Add(r);
                if (r.IsPlayer) r.Ai = new AiDriver(0.8f, 0); // autopilot cool-down lap
            }

            AddSkidMarks(r);
        }

        ResolveCollisions();

        if (Started && !Player.Finished && Raylib.IsKeyPressed(KeyboardKey.R))
            Player.ResetToTrack(Track);

        UpdateCamera(dt);
    }

    DriverInput ReadPlayerInput(float dt)
    {
        float steerTarget = 0;
        if (Raylib.IsKeyDown(KeyboardKey.A) || Raylib.IsKeyDown(KeyboardKey.Left)) steerTarget -= 1;
        if (Raylib.IsKeyDown(KeyboardKey.D) || Raylib.IsKeyDown(KeyboardKey.Right)) steerTarget += 1;

        var input = new DriverInput
        {
            Throttle = Raylib.IsKeyDown(KeyboardKey.W) || Raylib.IsKeyDown(KeyboardKey.Up) ? 1 : 0,
            Brake = Raylib.IsKeyDown(KeyboardKey.S) || Raylib.IsKeyDown(KeyboardKey.Down) ? 1 : 0,
            Handbrake = Raylib.IsKeyDown(KeyboardKey.Space),
        };

        // Gamepad: left stick steers, right trigger = gas, left trigger = brake, A = handbrake
        if (Raylib.IsGamepadAvailable(0))
        {
            float axis = Raylib.GetGamepadAxisMovement(0, GamepadAxis.LeftX);
            if (MathF.Abs(axis) > 0.15f) steerTarget = axis;
            if (Raylib.IsGamepadButtonDown(0, GamepadButton.RightTrigger2)) input.Throttle = 1;
            if (Raylib.IsGamepadButtonDown(0, GamepadButton.LeftTrigger2)) input.Brake = 1;
            if (Raylib.IsGamepadButtonDown(0, GamepadButton.RightFaceDown)) input.Handbrake = true;
        }

        // Smooth the keyboard steering so the car doesn't twitch
        float rate = (steerTarget == 0 ? 8f : 5f) * dt;
        playerSteer += Math.Clamp(steerTarget - playerSteer, -rate, rate);
        input.Steer = playerSteer;
        return input;
    }

    void AddSkidMarks(Racer r)
    {
        if (!r.Movement.IsSkidding || !r.OnTrack)
        {
            r.LastLeftWheel = r.LastRightWheel = null;
            return;
        }
        var (left, right) = r.RearWheels();
        if (r.LastLeftWheel is Vector2 lastL && r.LastRightWheel is Vector2 lastR)
        {
            if (Vector2.DistanceSquared(lastL, left) < 36) return; // wait until we've moved a bit
            skidMarks.Add(lastL, left);
            skidMarks.Add(lastR, right);
        }
        r.LastLeftWheel = left;
        r.LastRightWheel = right;
    }

    // Cars are treated as circles that bounce off each other a little.
    void ResolveCollisions()
    {
        const float radius = 16f;
        for (int i = 0; i < Racers.Count; i++)
        {
            for (int j = i + 1; j < Racers.Count; j++)
            {
                Movement a = Racers[i].Movement, b = Racers[j].Movement;
                Vector2 d = b.Position - a.Position;
                float dist = d.Length();
                if (dist >= radius * 2 || dist < 0.001f) continue;

                Vector2 n = d / dist;
                float overlap = radius * 2 - dist;
                a.Position -= n * overlap / 2;
                b.Position += n * overlap / 2;

                float approach = Vector2.Dot(b.Velocity - a.Velocity, n);
                if (approach < 0)
                {
                    float impulse = -(1 + 0.3f) * approach / 2;
                    a.Velocity -= n * impulse;
                    b.Velocity += n * impulse;
                }
            }
        }
    }

    void UpdateCamera(float dt)
    {
        Movement m = Player.Movement;
        float t = 1 - MathF.Exp(-5f * dt);
        camera.Offset = new Vector2(Raylib.GetScreenWidth() / 2f, Raylib.GetScreenHeight() / 2f);
        camera.Target = Vector2.Lerp(camera.Target, m.Position + m.Velocity * 0.35f, t);

        float speedRatio = m.Velocity.Length() / (Player.Car.MaxSpeed * Movement.PixelsPerKmh);
        float targetZoom = (1.05f - 0.35f * speedRatio) * Ui.Scale; // same view on any screen size
        camera.Zoom += (targetZoom - camera.Zoom) * t * 0.5f;
    }

    // Finished racers in finishing order, then everyone else by how far they've got.
    public List<Racer> Standings()
    {
        return FinishOrder
            .Concat(Racers.Where(r => !r.Finished).OrderByDescending(r => r.ProgressSamples))
            .ToList();
    }

    public bool PlayerWrongWay =>
        Started && Player.Movement.CurrentSpeed > 15 &&
        Vector2.Dot(Player.Movement.Forward, Track.DirectionAt(Player.TrackIndex)) < -0.3f;

    public void Draw()
    {
        Raylib.ClearBackground(new Color(66, 116, 58, 255));
        Raylib.BeginMode2D(camera);
        Rectangle view = VisibleWorld();
        DrawGrass();
        Track.Draw(view);
        skidMarks.Draw(view);
        foreach (Racer r in Racers) r.Draw();
        foreach (Racer r in Racers) r.DrawNameTag();
        Raylib.EndMode2D();

        Ui.Vignette(0.55f);
        Ui.BeginHud();
        Hud.Draw(this);
        Ui.End();
    }

    // The part of the world the camera can currently see
    Rectangle VisibleWorld()
    {
        Vector2 a = Raylib.GetScreenToWorld2D(Vector2.Zero, camera);
        Vector2 b = Raylib.GetScreenToWorld2D(new Vector2(Raylib.GetScreenWidth(), Raylib.GetScreenHeight()), camera);
        return new Rectangle(a.X, a.Y, b.X - a.X, b.Y - a.Y);
    }

    // Mowed-lawn plaid: light/dark stripes in both directions so you can feel your speed anywhere.
    void DrawGrass()
    {
        const int band = 150;
        Vector2 topLeft = Raylib.GetScreenToWorld2D(Vector2.Zero, camera);
        Vector2 bottomRight = Raylib.GetScreenToWorld2D(new Vector2(Raylib.GetScreenWidth(), Raylib.GetScreenHeight()), camera);
        int x0 = (int)MathF.Floor(topLeft.X / band), x1 = (int)MathF.Ceiling(bottomRight.X / band);
        int y0 = (int)MathF.Floor(topLeft.Y / band), y1 = (int)MathF.Ceiling(bottomRight.Y / band);
        int top = y0 * band, height = (y1 - y0 + 1) * band;
        int left = x0 * band, width = (x1 - x0 + 1) * band;
        Color stripe = new(0, 0, 0, 16);
        for (int x = x0; x <= x1; x++)
            if ((x & 1) == 0) Raylib.DrawRectangle(x * band, top, band, height, stripe);
        for (int y = y0; y <= y1; y++)
            if ((y & 1) == 0) Raylib.DrawRectangle(left, y * band, width, band, stripe);
    }
}

// A fixed-size ring buffer of tyre-mark line segments (oldest ones get overwritten).
class SkidMarks
{
    const int Max = 3000;
    readonly Vector2[] starts = new Vector2[Max];
    readonly Vector2[] ends = new Vector2[Max];
    int next, count;

    public void Add(Vector2 a, Vector2 b)
    {
        starts[next] = a;
        ends[next] = b;
        next = (next + 1) % Max;
        count = Math.Min(count + 1, Max);
    }

    public void Draw(Rectangle view)
    {
        Color c = new Color(20, 20, 22, 95);
        float x0 = view.X - 4, y0 = view.Y - 4, x1 = view.X + view.Width + 4, y1 = view.Y + view.Height + 4;
        for (int i = 0; i < count; i++)
        {
            Vector2 a = starts[i], b = ends[i];
            if (MathF.Max(a.X, b.X) < x0 || MathF.Min(a.X, b.X) > x1 || MathF.Max(a.Y, b.Y) < y0 || MathF.Min(a.Y, b.Y) > y1)
                continue; // off screen
            Raylib.DrawLineEx(a, b, 4, c);
        }
    }
}
