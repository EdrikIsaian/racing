using System.Numerics;
using Raylib_cs;

enum RacerKind { Player, Ai, Remote }

// One car on the starting grid, front to back
record RaceEntry(string Name, Car Car, Color Color, RacerKind Kind, int NetId = -1);

// One race: the track, all racers, the countdown, collisions and the camera.
class Race
{
    public readonly Track Track;
    public TrackDef Def => Track.Def;
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

    public static readonly string[] AiNames = { "Alex", "Sam", "Riley", "Jordan", "Casey" };
    public static readonly Color[] AiColors =
    {
        new Color(52, 120, 246, 255),
        new Color(255, 140, 0, 255),
        new Color(160, 80, 220, 255),
        new Color(0, 190, 190, 255),
        new Color(240, 90, 160, 255),
    };

    // Collisions involving the player this frame (strongest impact speed), for the crash sound
    public float PlayerImpact;
    int lastLit;          // start lights already lit (for the beeps)
    float crashCooldown;
    public Camera3D Camera => camera;

    // RACING_AUTOPILOT=1: the computer drives the player's car too (for screenshot runs)
    static readonly bool Autopilot = Environment.GetEnvironmentVariable("RACING_AUTOPILOT") == "1";

    // Online race: cars driven on other machines are synced over the network
    public readonly bool Online;
    public bool InputLocked; // online pause menu: the race goes on, your car just coasts
    readonly Dictionary<int, Racer> byNetId = new();
    readonly List<CarState> netStates = new();
    readonly List<int> netRetired = new();
    float sendTimer;

    public Race(TrackDef def, Car car, int opponents, int laps, float skill, bool manualGears = false)
        : this(def, OfflineGrid(car, opponents), laps, skill, manualGears, false) { }

    // Opponents fill the front of the grid, the player starts at the back.
    static List<RaceEntry> OfflineGrid(Car car, int opponents)
    {
        var grid = new List<RaceEntry>();
        for (int i = 0; i < opponents; i++) grid.Add(new RaceEntry(AiNames[i], car, AiColors[i], RacerKind.Ai));
        grid.Add(new RaceEntry("You", car, car.Color, RacerKind.Player));
        return grid;
    }

    public Race(TrackDef def, List<RaceEntry> grid, int laps, float skill, bool manualGears, bool online)
    {
        Track = Track.Get(def);
        Track.EnsureWorld();
        Scene3D.FogStart = def.FogStart;
        Scene3D.FogEnd = def.FogEnd;
        Laps = laps;
        var rng = new Random();

        Online = online;

        for (int slot = 0; slot < grid.Count; slot++)
        {
            RaceEntry e = grid[slot];
            var r = new Racer(e.Name, e.Car, e.Color, e.Kind == RacerKind.Player) { NetId = e.NetId, Remote = e.Kind == RacerKind.Remote };
            if (e.Kind == RacerKind.Ai)
            {
                float lane = ((float)rng.NextDouble() - 0.5f) * Track.Width * 0.4f;
                r.Ai = new AiDriver(skill + ((float)rng.NextDouble() - 0.5f) * 0.05f, lane);
            }
            if (e.Kind == RacerKind.Player) r.Gearbox.Manual = manualGears;
            r.PlaceOnGrid(Track, slot);
            Racers.Add(r);
            if (e.NetId >= 0) byNetId[e.NetId] = r;
        }
        Player = Racers.First(r => r.IsPlayer);

        foreach (Racer r in Racers) r.UpdateGround(Track, 1);
        camYaw = Player.Movement.Heading;
        camera = new Camera3D(Vector3.Zero, Vector3.Zero, Vector3.UnitY, 55, CameraProjection.Perspective);
        UpdateCamera(1f);
    }

    public void Update(float dt)
    {
        if (!Started) Countdown -= dt;
        else RaceTime += dt;

        PlayerImpact = 0;
        float now = (float)Raylib.GetTime();
        if (Online) ReceiveNetwork(now);

        foreach (Racer r in Racers)
        {
            if (r.Remote)
            {
                r.UpdateRemote(dt, now);
                r.UpdateGround(Track, dt);
                AddSkidMarks(r);
                NoteFinish(r);
                continue;
            }

            DriverInput input = default;
            if (Started)
            {
                if (r.IsPlayer && !r.Finished && Autopilot) input = (r.Ai ??= new AiDriver(0.9f, 0)).GetInput(r, Track, dt);
                else if (r.IsPlayer && !r.Finished) input = ReadPlayerInput(dt);
                else if (r.Ai != null) input = r.Ai.GetInput(r, Track, dt);
            }
            else if (r.IsPlayer)
            {
                // Rev the engine on the grid (the car stays put until the lights go out)
                DriverInput rev = ReadPlayerInput(dt);
                r.Gearbox.Update(0, r.Car.MaxSpeed * Movement.PixelsPerKmh, rev, dt);
                r.LastSteer = 0;
                r.LastThrottle = rev.Throttle;
                r.UpdateGround(Track, dt);
                continue;
            }

            r.OnTrack = Track.IsOnTrack(r.Movement.Position, r.TrackIndex);
            r.LastSteer = input.Steer;
            r.LastThrottle = input.Throttle;
            r.Braking = input.Brake > 0 && r.Movement.ForwardSpeed > 5;
            r.Gearbox.Update(r.Movement.ForwardSpeed, r.Car.MaxSpeed * Movement.PixelsPerKmh, input, dt);
            r.Movement.Update(r.Car, input, r.OnTrack, dt, r.Gearbox.DriveFactor, Started ? r.Slope : 0);
            r.UpdateVisuals(input, dt);
            r.UpdateGround(Track, dt);

            if (r.UpdateTrackPosition(Track, RaceTime, Laps) && r.IsPlayer)
            {
                LastLapCompletedAt = RaceTime;
                if (r.Finished) Audio.Finish();
                else if (r.LapsDone == Laps - 1) Audio.FinalLap();
                else if (r.LapsDone > 0) Audio.Lap();
            }

            NoteFinish(r);
            AddSkidMarks(r);
        }

        ResolveCollisions();

        if (Started && !Player.Finished && !InputLocked && Raylib.IsKeyPressed(KeyboardKey.R))
            Player.ResetToTrack(Track);

        if (Online) SendNetwork(dt);

        UpdateCamera(dt);
        UpdateSounds(dt);
        Track.Scenery!.Update(dt, Player.Movement.Position);
    }

    void NoteFinish(Racer r)
    {
        if (!r.Finished || FinishOrder.Contains(r)) return;
        FinishOrder.Add(r);
        // Online, finishes can arrive a little out of order: rank by time
        if (Online) FinishOrder.Sort((a, b) => a.FinishTime.CompareTo(b.FinishTime));
        if (r.IsPlayer) r.Ai = new AiDriver(0.8f, 0); // autopilot cool-down lap
    }

    // Updates from the other machines: car positions, and cars that left the race
    void ReceiveNetwork(float now)
    {
        netStates.Clear();
        Net.TakeStates(netStates);
        foreach (CarState s in netStates)
            if (byNetId.TryGetValue(s.Id, out Racer? r) && r.Remote) r.ApplyNetState(s, now);

        netRetired.Clear();
        Net.TakeRetired(netRetired);
        foreach (int id in netRetired)
            if (byNetId.TryGetValue(id, out Racer? r) && r.Remote)
            {
                byNetId.Remove(id);
                Racers.Remove(r); // stays in FinishOrder if it had finished
            }
    }

    // ~30 updates a second for the cars this machine drives (your car; the host also drives the AI)
    void SendNetwork(float dt)
    {
        sendTimer -= dt;
        if (sendTimer > 0) return;
        sendTimer = MathF.Max(sendTimer + 1f / 30f, 0);
        foreach (Racer r in Racers)
            if (!r.Remote && r.NetId >= 0) Net.SendState(r.NetState());
    }

    // Tell everyone the cars this machine drives are leaving the race
    public void Retire()
    {
        if (!Online) return;
        foreach (Racer r in Racers)
            if (!r.Remote && r.NetId >= 0) Net.SendRetire(r.NetId);
    }

    // One-shot race sounds: start beeps, gear changes, crashes, lap chimes
    void UpdateSounds(float dt)
    {
        int lit = Started ? 6 : Math.Clamp((int)((3f - Countdown) / 0.55f) + 1, 0, 5);
        if (lit != lastLit)
        {
            if (lit == 6) Audio.Go();
            else if (lit > lastLit) Audio.Beep();
            lastLit = lit;
        }

        if (Player.Gearbox.ShiftedThisFrame != 0) Audio.Shift();

        crashCooldown -= dt;
        if (PlayerImpact > 60 && crashCooldown <= 0)
        {
            Audio.Crash(PlayerImpact / 400f);
            crashCooldown = 0.25f;
        }
    }

    // Engines, tyres and wind, placed around the camera (call every frame the race is audible)
    public void UpdateAudio()
    {
        Span<EngineVoice> list = stackalloc EngineVoice[Racers.Count];
        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));

        for (int i = 0; i < Racers.Count; i++)
        {
            Racer r = Racers[i];
            Movement m = r.Movement;
            Vector3 d = Scene3D.W(m.Position, r.Y + 8) - camera.Position;
            float dist = d.Length();
            float topSpeed = r.Car.MaxSpeed * Movement.PixelsPerKmh;
            float speedRatio = Math.Clamp(m.Velocity.Length() / topSpeed, 0, 1);
            float slide = MathF.Abs(Vector2.Dot(m.Velocity, m.Right));

            list[i] = new EngineVoice
            {
                Rpm = r.Gearbox.Rpm,
                Redline = r.Gearbox.Spec.RedlineRpm,
                Throttle = r.Gearbox.Shifting ? 0.15f : r.IsPlayer && !Started ? (Raylib.IsKeyDown(KeyboardKey.W) || Raylib.IsKeyDown(KeyboardKey.Up) ? 1 : 0) : r.LastThrottle,
                Cylinders = r.Gearbox.Spec.Cylinders,
                Growl = r.Gearbox.Spec.Growl,
                Volume = r.IsPlayer ? 0.5f : 0.55f / (1 + MathF.Pow(dist / 170f, 2)),
                Pan = r.IsPlayer ? 0 : Math.Clamp(Vector3.Dot(d / MathF.Max(dist, 1), right), -1, 1) * 0.8f,
                Skid = m.IsSkidding && r.OnTrack ? MathF.Max(0.35f, Math.Clamp((slide - 60) / 200, 0, 1)) : 0,
                Wind = r.IsPlayer ? speedRatio * speedRatio * 0.3f : 0,
                Gravel = r.IsPlayer && !r.OnTrack ? Math.Clamp(m.Velocity.Length() / 160, 0, 1) * 0.55f : 0,
            };
        }
        Audio.SetEngines(list);
    }

    DriverInput ReadPlayerInput(float dt)
    {
        if (InputLocked)
        {
            playerSteer = 0;
            return default;
        }
        float steerTarget = 0;
        if (Raylib.IsKeyDown(KeyboardKey.A) || Raylib.IsKeyDown(KeyboardKey.Left)) steerTarget -= 1;
        if (Raylib.IsKeyDown(KeyboardKey.D) || Raylib.IsKeyDown(KeyboardKey.Right)) steerTarget += 1;

        var input = new DriverInput
        {
            Throttle = Raylib.IsKeyDown(KeyboardKey.W) || Raylib.IsKeyDown(KeyboardKey.Up) ? 1 : 0,
            Brake = Raylib.IsKeyDown(KeyboardKey.S) || Raylib.IsKeyDown(KeyboardKey.Down) ? 1 : 0,
            Handbrake = Raylib.IsKeyDown(KeyboardKey.Space),
            ShiftUp = Raylib.IsKeyPressed(KeyboardKey.E) || Raylib.IsKeyPressed(KeyboardKey.LeftShift),
            ShiftDown = Raylib.IsKeyPressed(KeyboardKey.Q) || Raylib.IsKeyPressed(KeyboardKey.LeftControl),
        };

        // Gamepad: left stick steers, right trigger = gas, left trigger = brake, A = handbrake
        if (Raylib.IsGamepadAvailable(0))
        {
            float axis = Raylib.GetGamepadAxisMovement(0, GamepadAxis.LeftX);
            if (MathF.Abs(axis) > 0.15f) steerTarget = axis;
            if (Raylib.IsGamepadButtonDown(0, GamepadButton.RightTrigger2)) input.Throttle = 1;
            if (Raylib.IsGamepadButtonDown(0, GamepadButton.LeftTrigger2)) input.Brake = 1;
            if (Raylib.IsGamepadButtonDown(0, GamepadButton.RightFaceDown)) input.Handbrake = true;
            if (Raylib.IsGamepadButtonPressed(0, GamepadButton.RightTrigger1)) input.ShiftUp = true;
            if (Raylib.IsGamepadButtonPressed(0, GamepadButton.LeftTrigger1)) input.ShiftDown = true;
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
                // Online, each machine only pushes the cars it drives; a remote car acts like a wall
                bool moveA = !Racers[i].Remote, moveB = !Racers[j].Remote;
                if (!moveA && !moveB) continue;
                float shareA = moveA ? (moveB ? 0.5f : 1f) : 0f, shareB = 1 - shareA;

                Movement a = Racers[i].Movement, b = Racers[j].Movement;
                Vector2 d = b.Position - a.Position;
                float dist = d.Length();
                if (dist >= radius * 2 || dist < 0.001f) continue;

                Vector2 n = d / dist;
                float overlap = radius * 2 - dist;
                a.Position -= n * overlap * shareA;
                b.Position += n * overlap * shareB;

                float approach = Vector2.Dot(b.Velocity - a.Velocity, n);
                if (approach < 0 && (Racers[i].IsPlayer || Racers[j].IsPlayer))
                    PlayerImpact = MathF.Max(PlayerImpact, -approach);
                if (approach < 0)
                {
                    float impulse = -(1 + 0.3f) * approach;
                    a.Velocity -= n * impulse * shareA;
                    b.Velocity += n * impulse * shareB;
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
        Vector2 back = m.Position - dir * distance, ahead = m.Position + dir * 46;
        float y = Player.Y;
        // Stay above the car, and above the ground behind it in dips
        float camY = MathF.Max(y + height, Track.HeightAt(back) + 14);
        // Look a little down over crests and up into climbs
        float aheadY = y + (Track.HeightAt(m.Position + dir * 140) - y) * 0.35f;
        Vector3 wantPos = Scene3D.W(back, camY);
        Vector3 wantTarget = Scene3D.W(ahead, aheadY + 9);

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

    PostFx.Grade Look => new() { Exposure = Def.Exposure, Saturation = Def.Saturation, Bloom = Def.Bloom, Contrast = Def.Contrast, WhiteBalance = Def.WhiteBalance };

    // The 3D view, drawn off-screen for post-processing. Runs before the frame starts drawing.
    public void PreDraw()
    {
        if (!PostFx.Enabled) return;
        PostFx.BeginScene();
        var (w, h) = PostFx.SceneSize();
        DrawScene(w, h);
        PostFx.EndScene(camera, Scene3D.SunDir, Look);
    }

    // Sky, land, road, scenery and cars
    void DrawScene(int pixelWidth, int pixelHeight)
    {
        Sky.Apply(Def);
        Raylib.ClearBackground(Def.Horizon);
        Sky.Draw(camera, Def, pixelWidth, pixelHeight);
        Scenery scenery = Track.Scenery!;

        Raylib.BeginMode3D(camera);
        Scene3D.Ground = Track.Terrain;
        Scene3D.SetView(camera.Position, Scene3D.FogStart, Scene3D.FogEnd, Def.Horizon, Def.Tint);
        Vector3 forward = camera.Target - camera.Position;

        // Shapes the ray tracer casts shadows and reflections from
        RayTracing.Begin(new Color(80, 80, 83, 255), Player.Y);
        foreach (Racer r in Racers) RayTracing.AddCar(r.Car.Type, r.Movement.Position, r.Movement.Heading, r.Color, 1, r.Y);
        Track.AddRayCasters(camera.Position, forward);
        RayTracing.Upload();

        // The land (and lakes), then the road and everything flat on it
        Track.Terrain!.Draw(camera.Position, Def.Horizon);
        Scene3D.BeginGround();
        Track.DrawGround();
        skidMarks.Draw();
        foreach (Racer r in Racers) Scene3D.CarShadow(r.Movement.Position, r.Movement.Heading, r.Car.Type);
        if (Scene3D.BeginRayShadows())
        {
            Track.DrawRoadFootprint();
            Scene3D.EndRayShadows();
        }
        Scene3D.EndGround();

        Track.DrawScenery(camera.Position, forward);
        scenery.DrawModels(camera.Position, forward);
        Scene3D.BeginFlat();
        scenery.DrawFlat(camera.Position, Player.Movement.Position);
        Scene3D.EndFlat();
        foreach (Racer r in Racers) r.Draw();
        Raylib.EndMode3D();
        Supersample.Setup2D();
    }

    public void Draw()
    {
        if (PostFx.Enabled) PostFx.Present();
        else DrawScene(Supersample.PixelSize.W, Supersample.PixelSize.H);
        Supersample.Setup2D();

        DrawNameTags();

        if (!PostFx.Enabled) Ui.Vignette(0.45f);
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
            Vector3 p = Scene3D.W(r.Movement.Position, r.Y + 24);
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
