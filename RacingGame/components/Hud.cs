using System.Numerics;
using Raylib_cs;

// Everything drawn on top of the race: position, laps, times, speedometer, minimap, messages.
static class Hud
{
    public static void Draw(Race race)
    {
        int w = Ui.Width, h = Ui.Height;
        Racer p = race.Player;
        List<Racer> standings = race.Standings();
        int position = standings.IndexOf(p) + 1;

        // --- Top-left: position, lap, times
        Raylib.DrawRectangleRounded(new Rectangle(16, 16, 270, 160), 0.12f, 8, Ui.Panel);
        string pos = $"P{position}";
        Ui.Text(pos, 32, 26, 56, Ui.Accent);
        Ui.Text($"/{race.Racers.Count}", 36 + Raylib.MeasureText(pos, 56), 54, 24, Ui.Muted);
        Ui.TextRight("LAP", 270, 30, 16, Ui.Muted);
        Ui.TextRight($"{p.CurrentLap(race.Laps)}/{race.Laps}", 270, 48, 32, Color.White);

        float lapTime = race.Started && !p.Finished ? p.CurrentLapTime(race.RaceTime) : 0;
        DrawTimeRow("TIME", race.Started ? (p.Finished ? p.FinishTime : race.RaceTime) : 0, 96, Color.White);
        DrawTimeRow("LAP", lapTime, 120, Color.White);
        DrawTimeRow("BEST", p.BestLap, 144, Ui.Good);

        // --- Below it: live standings
        int y = 190;
        Raylib.DrawRectangleRounded(new Rectangle(16, y, 270, 14 + standings.Count * 24), 0.12f, 8, Ui.Panel);
        for (int i = 0; i < standings.Count; i++)
        {
            Racer r = standings[i];
            int rowY = y + 8 + i * 24;
            Raylib.DrawRectangle(32, rowY + 4, 10, 10, r.Color);
            Color c = r.IsPlayer ? Ui.Accent : Color.White;
            Ui.Text($"{i + 1}. {r.Name}", 52, rowY, 18, c);
            string status = r.Finished ? Ui.FormatTime(r.FinishTime) : $"Lap {r.CurrentLap(race.Laps)}";
            Ui.TextRight(status, 270, rowY + 2, 14, Ui.Muted);
        }

        DrawMinimap(race, new Rectangle(w - 246, 16, 230, 170));
        DrawSpeedometer(p, new Vector2(w - 120, h - 110));

        // --- Centre messages
        if (!race.Started)
        {
            int n = (int)MathF.Ceiling(race.Countdown);
            float frac = race.Countdown - MathF.Floor(race.Countdown);
            int size = (int)(110 + 60 * frac);
            Ui.TextCentered(n.ToString(), w / 2, h / 2 - size / 2 - 60, size, n == 1 ? Ui.Accent : Color.White);
        }
        else if (race.RaceTime < 1f)
        {
            Ui.TextCentered("GO!", w / 2, h / 2 - 120, 120, Ui.Good);
        }

        if (race.Started && !p.Finished)
        {
            float sinceLap = race.RaceTime - race.LastLapCompletedAt;
            if (sinceLap < 2.5f && p.LapTimes.Count > 0)
            {
                float last = p.LapTimes[^1];
                bool best = last <= p.BestLap;
                Ui.TextCentered($"Lap {p.LapTimes.Count}: {Ui.FormatTime(last)}", w / 2, 90, 30, best ? Ui.Good : Color.White);
                if (p.LapsDone == race.Laps - 1) Ui.TextCentered("FINAL LAP", w / 2, 130, 36, Ui.Accent);
            }

            bool blink = (int)(race.RaceTime * 3) % 2 == 0;
            if (race.PlayerWrongWay && blink) Ui.TextCentered("WRONG WAY", w / 2, h / 2 + 80, 44, Ui.Bad);
            else if (!p.OnTrack && blink) Ui.TextCentered("OFF TRACK  -  press R to reset", w / 2, h / 2 + 90, 24, new Color(255, 170, 60, 255));
        }

        Raylib.DrawText("W/S gas & brake   A/D steer   Space handbrake   R reset   Esc pause   F full screen", 16, h - 26, 16, new Color(255, 255, 255, 140));
    }

    static void DrawTimeRow(string label, float time, int y, Color color)
    {
        Ui.Text(label, 32, y, 18, Ui.Muted);
        Ui.TextRight(Ui.FormatTime(time), 270, y, 20, color);
    }

    static void DrawSpeedometer(Racer p, Vector2 center)
    {
        float speed = p.Movement.CurrentSpeed;
        float ratio = Math.Clamp(speed / p.Car.MaxSpeed, 0f, 1f);

        Raylib.DrawCircleV(center, 96, Ui.Panel);
        Raylib.DrawRing(center, 72, 84, 135, 405, 48, new Color(60, 66, 84, 255));
        Color fill = ratio > 0.9f ? Ui.Bad : ratio > 0.65f ? Ui.Accent : Ui.Good;
        if (ratio > 0.005f) Raylib.DrawRing(center, 72, 84, 135, 135 + 270 * ratio, 48, fill);

        Ui.TextCentered($"{speed:F0}", (int)center.X, (int)center.Y - 26, 44, Color.White);
        Ui.TextCentered("km/h", (int)center.X, (int)center.Y + 20, 16, Ui.Muted);
        if (p.Movement.ForwardSpeed < -5) Ui.TextCentered("R", (int)center.X, (int)center.Y + 42, 20, Ui.Bad);
    }

    static void DrawMinimap(Race race, Rectangle area)
    {
        Raylib.DrawRectangleRounded(area, 0.12f, 8, Ui.Panel);
        Track t = race.Track;
        const float pad = 14;
        Vector2 size = t.BoundsMax - t.BoundsMin;
        float scale = MathF.Min((area.Width - pad * 2) / size.X, (area.Height - pad * 2) / size.Y);
        Vector2 offset = new Vector2(area.X, area.Y)
                         + new Vector2(area.Width - size.X * scale, area.Height - size.Y * scale) / 2;
        Vector2 Map(Vector2 world) => offset + (world - t.BoundsMin) * scale;

        for (int i = 0; i < t.Count; i += 2)
            Raylib.DrawLineEx(Map(t.PointAt(i)), Map(t.PointAt(i + 2)), 5, new Color(120, 126, 140, 255));

        Vector2 start = Map(t.PointAt(0));
        Raylib.DrawCircleV(start, 4, Color.White);

        foreach (Racer r in race.Racers.Where(r => !r.IsPlayer))
            Raylib.DrawCircleV(Map(r.Movement.Position), 4, r.Color);
        Vector2 pp = Map(race.Player.Movement.Position);
        Raylib.DrawCircleV(pp, 6, Color.White);
        Raylib.DrawCircleV(pp, 4, race.Player.Color);
    }
}
