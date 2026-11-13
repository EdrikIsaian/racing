using System.Numerics;
using Raylib_cs;

// One car on the track: the player or an AI driver.
class Racer
{
    public string Name;
    public Car Car;
    public Color Color;
    public bool IsPlayer;
    public AiDriver? Ai;
    public Movement Movement = new();

    // Track progress, counted in centre-line samples. Negative = behind the start line.
    public int TrackIndex;
    public int ProgressSamples;
    public int LapsDone;
    public List<float> LapTimes = new();
    float lapStart;

    public bool Finished;
    public float FinishTime;
    public bool OnTrack = true;
    public float LastSteer;
    public bool Braking;
    public Vector2? LastLeftWheel, LastRightWheel; // for skid marks

    public Racer(string name, Car car, Color color, bool isPlayer)
    {
        Name = name;
        Car = car;
        Color = color;
        IsPlayer = isPlayer;
    }

    public float BestLap => LapTimes.Count > 0 ? LapTimes.Min() : 0;
    public float CurrentLapTime(float raceTime) => raceTime - lapStart;
    public int CurrentLap(int totalLaps) => Math.Clamp(LapsDone + 1, 1, totalLaps);

    public void PlaceOnGrid(Track track, int slot)
    {
        var (pos, heading, index) = track.GridSlot(slot);
        Movement.Position = pos;
        Movement.Heading = heading;
        Movement.Velocity = Vector2.Zero;
        TrackIndex = index;
        ProgressSamples = index - track.Count; // just behind the line
    }

    public void ResetToTrack(Track track)
    {
        Movement.Position = track.PointAt(TrackIndex);
        Movement.Heading = track.HeadingAt(TrackIndex);
        Movement.Velocity = Vector2.Zero;
        LastLeftWheel = LastRightWheel = null;
    }

    // Returns true on the frame a lap is completed.
    public bool UpdateTrackPosition(Track track, float raceTime, int totalLaps)
    {
        int newIndex = track.FindNearest(Movement.Position, TrackIndex);
        int delta = newIndex - TrackIndex;
        if (delta > track.Count / 2) delta -= track.Count;
        if (delta < -track.Count / 2) delta += track.Count;
        ProgressSamples += delta;
        TrackIndex = newIndex;

        // Floor division so -1 samples counts as "lap -1" (not crossed the line yet)
        int completed = (int)MathF.Floor(ProgressSamples / (float)track.Count);
        if (completed > LapsDone)
        {
            LapsDone = completed;
            LapTimes.Add(raceTime - lapStart);
            lapStart = raceTime;
            if (LapsDone >= totalLaps && !Finished)
            {
                Finished = true;
                FinishTime = raceTime;
            }
            return true;
        }
        return false;
    }

    public (Vector2 Left, Vector2 Right) RearWheels()
    {
        Vector2 f = Movement.Forward, r = Movement.Right;
        Vector2 rear = Movement.Position - f * 12;
        return (rear - r * 9, rear + r * 9);
    }

    public void Draw()
    {
        DrawCar(Movement.Position, Movement.Heading, Color, 1f, LastSteer, Car.Type, Braking);
    }

    // Floating name tag over AI cars (drawn after all cars so tags never hide under a car)
    public void DrawNameTag()
    {
        if (IsPlayer) return;
        const float size = 11;
        float w = Ui.MeasureText(Name, size, Typeface.Body) + 14;
        Vector2 p = Movement.Position + new Vector2(0, -36);
        Rectangle r = new(p.X - w / 2, p.Y - 9, w, 18);
        Ui.RoundRect(r, 9, new Color(8, 10, 14, 170));
        Raylib.DrawRectangleRec(new Rectangle(r.X + 6, r.Y + 5, 2, 8), Color);
        Ui.DrawText(Name, r.X + 11, r.Y + 3.5f, size, new Color(235, 238, 244, 230), Typeface.Body);
    }

    // Body proportions per class (length, width, cabin length, cabin offset, spoiler)
    static (float L, float W, float Cabin, float CabinOff, bool Spoiler, float Corner) Shape(CarType type) => type switch
    {
        CarType.SUV => (41f, 21f, 22f, -3f, false, 5f),
        CarType.Sedan => (40f, 19f, 18f, -1.5f, false, 5.5f),
        CarType.SportsCar => (37f, 19.5f, 14f, -2f, true, 6f),
        _ => (40f, 21f, 12f, 0f, true, 7f), // hypercar: long, wide, tiny canopy
    };

    // Draws a top-down car. Also used for the showroom in the menu (scale > 1).
    public static void DrawCar(Vector2 pos, float heading, Color color, float scale, float steer, CarType type = CarType.SportsCar, bool braking = false)
    {
        float deg = heading * 180f / MathF.PI;
        Vector2 f = new(MathF.Cos(heading), MathF.Sin(heading));
        Vector2 r = new(-MathF.Sin(heading), MathF.Cos(heading));
        var (L, W, cabin, cabinOff, spoiler, corner) = Shape(type);
        float s = scale;

        // Soft shadow (light from the top-left)
        Vector2 sh = pos + new Vector2(3f, 4.5f) * s;
        for (int i = 0; i < 4; i++)
            RoundBox(sh, f, r, (L + 7 - i * 2.2f) * s, (W + 7 - i * 2.2f) * s, (corner + 3.5f - i) * s, deg, new Color(0, 0, 0, 30));

        // Wheels, peeking out just past the body
        float wx = L * 0.30f, wy = W / 2 - 1.2f;
        foreach (float ax in Sides)
            foreach (float ay in Sides)
            {
                float sx = ax * wx, sy = ay * wy;
                float wheelDeg = deg + (sx > 0 ? steer * 25f : 0);
                Rect(pos + (f * sx + r * sy) * s, 9.5f * s, 5f * s, wheelDeg, new Color(14, 14, 16, 255));
                Rect(pos + (f * sx + r * sy) * s, 5f * s, 5f * s, wheelDeg, new Color(36, 36, 40, 255));
            }

        // Body: darker base, then a slightly inset lighter top → reads as a curved surface
        Color dark = Shade(color, 0.62f);
        Color mid = Shade(color, 0.86f);
        RoundBox(pos, f, r, L * s, W * s, corner * s, deg, dark);
        RoundBox(pos, f, r, (L - 1.6f) * s, (W - 3.2f) * s, (corner - 0.8f) * s, deg, mid);
        RoundBox(pos + f * 0.5f * s, f, r, (L - 6f) * s, (W - 8f) * s, (corner - 2f) * s, deg, color);

        // Hood crease / bonnet highlight
        Rect(pos + f * (L * 0.27f) * s, L * 0.28f * s, 1.6f * s, deg, Ui.Alpha(Lighten(color, 0.45f), 0.8f));

        // Side intakes on the hypercar
        if (type == CarType.HyperCar)
            foreach (float side in Sides)
                Rect(pos + (f * -4f + r * side * (W / 2 - 3.2f)) * s, 8f * s, 2.2f * s, deg, new Color(18, 18, 22, 255));

        // Cabin: glass greenhouse with the roof panel inside
        Vector2 cab = pos + f * cabinOff * s;
        float cw = W - 5.5f;
        RoundBox(cab, f, r, cabin * s, cw * s, 3.2f * s, deg, new Color(30, 38, 52, 255));
        // roof sits towards the back: big windscreen in front, slim rear window behind
        RoundBox(cab - f * cabin * 0.1f * s, f, r, cabin * 0.52f * s, (cw - 1.4f) * s, 2.4f * s, deg, Shade(color, 0.95f));
        // windscreen reflection
        Rect(cab + f * (cabin / 2 - 1.6f) * s + r * (-cw * 0.18f) * s, 1.1f * s, cw * 0.38f * s, deg, new Color(255, 255, 255, 70));

        // Roof rails on the SUV
        if (type == CarType.SUV)
            foreach (float side in Sides)
                Rect(cab - f * 1.2f * s + r * side * (cw / 2 - 1.2f) * s, (cabin - 6f) * s, 1f * s, deg, new Color(30, 30, 34, 255));

        // Mirrors
        foreach (float side in Sides)
            Rect(pos + (f * (cabinOff + cabin / 2 - 1.5f) + r * side * (W / 2 + 0.6f)) * s, 2.2f * s, 2.4f * s, deg, dark);

        // Rear wing
        if (spoiler)
        {
            float wingW = type == CarType.HyperCar ? W + 1 : W - 1;
            foreach (float side in Sides)
                Rect(pos + (f * -(L / 2 - 4f) + r * side * 4f) * s, 3f * s, 1.2f * s, deg, new Color(20, 20, 24, 255));
            Rect(pos - f * (L / 2 - 1.8f) * s, 3.4f * s, wingW * s, deg, new Color(22, 22, 26, 255));
            Rect(pos - f * (L / 2 - 1.2f) * s, 1f * s, wingW * s, deg, Ui.Alpha(color, 0.9f));
        }

        // Lights
        Color head = new(255, 248, 226, 255);
        Color tail = braking ? new Color(255, 52, 52, 255) : new Color(150, 22, 30, 255);
        foreach (float side in Sides)
        {
            Rect(pos + (f * (L / 2 - 1.3f) + r * side * (W / 2 - 3.5f)) * s, 1.8f * s, 4.2f * s, deg, head);
            Rect(pos + (f * -(L / 2 - 0.9f) + r * side * (W / 2 - 3.2f)) * s, 1.4f * s, 4.4f * s, deg, tail);
        }
        if (braking)
            foreach (float side in Sides)
                Ui.Glow(pos + (f * -(L / 2 + 1f) + r * side * (W / 2 - 3.2f)) * s, 9f * s, new Color(255, 40, 40, 110));
    }

    static readonly float[] Sides = { -1f, 1f }; // left/right, reused so drawing a car allocates nothing

    // Rotated rounded rectangle (rlgl transform around raylib's rounded rect)
    static void RoundBox(Vector2 c, Vector2 f, Vector2 r, float length, float width, float radius, float deg, Color color)
    {
        Rectangle box = new(-length / 2, -width / 2, length, width);
        Rlgl.PushMatrix();
        Rlgl.Translatef(c.X, c.Y, 0);
        Rlgl.Rotatef(deg, 0, 0, 1);
        Raylib.DrawRectangleRounded(box, Ui.Roundness(box, radius), 8, color);
        Rlgl.PopMatrix();
    }

    static void Rect(Vector2 center, float length, float width, float deg, Color color)
    {
        Raylib.DrawRectanglePro(new Rectangle(center.X, center.Y, length, width), new Vector2(length / 2, width / 2), deg, color);
    }

    static Color Shade(Color c, float k) => new((int)Math.Clamp(c.R * k, 0, 255), (int)Math.Clamp(c.G * k, 0, 255), (int)Math.Clamp(c.B * k, 0, 255), (int)c.A);
    static Color Lighten(Color c, float t) => new((int)(c.R + (255 - c.R) * t), (int)(c.G + (255 - c.G) * t), (int)(c.B + (255 - c.B) * t), (int)c.A);
}
