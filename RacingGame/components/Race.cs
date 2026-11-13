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
    Camera3D camera;
    float camYaw;       // smoothed direction the chase camera looks along
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

        camYaw = Player.Movement.Heading;
        camera = new Camera3D(Vector3.Zero, Vector3.Zero, Vector3.UnitY, 55, CameraProjection.Perspective);
        UpdateCamera(1f);
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
            r.UpdateVisuals(input, dt);

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

    // Third-person chase camera: sits behind and above the player and swings round smoothly,
    // so drifts show the car sliding sideways instead of the whole world snapping round.
    void UpdateCamera(float dt)
    {
        Movement m = Player.Movement;
        float speedRatio = Math.Clamp(m.Velocity.Length() / (Player.Car.MaxSpeed * Movement.PixelsPerKmh), 0, 1);

        camYaw += Track.WrapAngle(m.Heading - camYaw) * (1 - MathF.Exp(-5.5f * dt));
        Vector2 dir = new(MathF.Cos(camYaw), MathF.Sin(camYaw));

        float distance = 74 + 26 * speedRatio;
        float height = 29 + 5 * speedRatio;
        Vector3 wantPos = Scene3D.W(m.Position - dir * distance, height);
        Vector3 wantTarget = Scene3D.W(m.Position + dir * 46, 9);

        float t = 1 - MathF.Exp(-14f * dt);
        camera.Position = Vector3.Lerp(camera.Position, wantPos, t);
        camera.Target = Vector3.Lerp(camera.Target, wantTarget, t);
        camera.FovY += (54 + 14 * speedRatio - camera.FovY) * (1 - MathF.Exp(-3f * dt)); // widen with speed
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
        // Sky
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        Raylib.ClearBackground(Scene3D.Horizon);
        Raylib.DrawRectangleGradientV(0, 0, sw, sh / 2, Scene3D.SkyTop, Scene3D.Horizon);

        Raylib.BeginMode3D(camera);
        Scene3D.SetView(camera.Position, Scene3D.FogStart, Scene3D.FogEnd, Scene3D.Horizon);

        Scene3D.BeginGround();
        Track.DrawGround();
        skidMarks.Draw();
        foreach (Racer r in Racers) Scene3D.CarShadow(r.Movement.Position, r.Movement.Heading, r.Car.Type);
        Scene3D.EndGround();

        Track.DrawScenery(camera.Position, camera.Target - camera.Position);
        foreach (Racer r in Racers) r.Draw();
        Raylib.EndMode3D();
        Supersample.Setup2D();

        DrawNameTags();

        Ui.Vignette(0.45f);
        Ui.BeginHud();
        Hud.Draw(this);
        Ui.End();
    }

    // Name tags float above the AI cars; projected from 3D to the screen
    void DrawNameTags()
    {
        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        foreach (Racer r in Racers)
        {
            if (r.IsPlayer) continue;
            Vector3 p = Scene3D.W(r.Movement.Position, 24);
            Vector3 d = p - camera.Position;
            float dist = d.Length();
            if (Vector3.Dot(d, forward) < 10 || dist > 2600) continue; // behind the camera or too far
            Vector2 screen = Raylib.GetWorldToScreen(p, camera);
            float alpha = Math.Clamp((2600 - dist) / 600, 0, 1);
            r.DrawNameTag(screen, Ui.Scale * Math.Clamp(260 / dist, 0.7f, 1.15f), alpha);
        }
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

    public void Draw()
    {
        Color c = new Color(20, 20, 22, 95);
        for (int i = 0; i < count; i++)
        {
            Vector2 a = starts[i], b = ends[i], d = b - a;
            float len = d.Length();
            if (len < 0.01f) continue;
            Scene3D.Box((a + b) / 2, d / len, len, 4, c);
        }
    }
}
