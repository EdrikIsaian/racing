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
        DrawCar(Movement.Position, Movement.Heading, Color, 1f, LastSteer);
        if (!IsPlayer)
        {
            int w = Raylib.MeasureText(Name, 14);
            Raylib.DrawText(Name, (int)Movement.Position.X - w / 2, (int)Movement.Position.Y - 34, 14, new Color(255, 255, 255, 190));
        }
    }

    // Draws a little top-down car. Also used for the previews in the menu (scale > 1).
    public static void DrawCar(Vector2 pos, float heading, Color color, float scale, float steer)
    {
        float deg = heading * 180f / MathF.PI;
        Vector2 f = new(MathF.Cos(heading), MathF.Sin(heading));
        Vector2 r = new(-MathF.Sin(heading), MathF.Cos(heading));
        Color glass = new Color(40, 55, 80, 255);

        Rect(pos + new Vector2(3, 4) * scale, 38 * scale, 20 * scale, deg, new Color(0, 0, 0, 80)); // shadow

        foreach (float sx in new[] { 12f, -12f })
            foreach (float sy in new[] { -10f, 10f })
            {
                float wheelDeg = deg + (sx > 0 ? steer * 25f : 0);
                Rect(pos + (f * sx + r * sy) * scale, 9 * scale, 5 * scale, wheelDeg, new Color(20, 20, 20, 255));
            }

        Rect(pos, 38 * scale, 20 * scale, deg, color);                                // body
        Rect(pos - f * 17 * scale, 4 * scale, 22 * scale, deg, Darken(color, 0.55f)); // spoiler
        Rect(pos - f * 2 * scale, 15 * scale, 15 * scale, deg, Darken(color, 0.75f)); // roof
        Rect(pos + f * 6 * scale, 4 * scale, 14 * scale, deg, glass);                 // windscreen
        Rect(pos - f * 9 * scale, 3 * scale, 13 * scale, deg, glass);                 // rear window
        Color light = new Color(255, 250, 200, 255);
        Rect(pos + (f * 18 + r * 6) * scale, 2 * scale, 4 * scale, deg, light);
        Rect(pos + (f * 18 - r * 6) * scale, 2 * scale, 4 * scale, deg, light);
    }

    static void Rect(Vector2 center, float length, float width, float deg, Color color)
    {
        Raylib.DrawRectanglePro(new Rectangle(center.X, center.Y, length, width), new Vector2(length / 2, width / 2), deg, color);
    }

    static Color Darken(Color c, float f) => new Color((int)(c.R * f), (int)(c.G * f), (int)(c.B * f), (int)c.A);
}
