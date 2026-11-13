using System.Numerics;
using Raylib_cs;

// Everything drawn on top of the race: timing tower, start lights, speedometer, minimap, messages.
static class Hud
{
    static readonly (string, string)[] Controls =
    {
        ("W S", "Throttle / brake"), ("A D", "Steer"), ("SPACE", "Handbrake"), ("R", "Reset"), ("ESC", "Pause"),
    };

    public static void Draw(Race race)
    {
        int w = Ui.Width, h = Ui.Height;
        Racer p = race.Player;
        List<Racer> standings = race.Standings();
        int position = standings.IndexOf(p) + 1;

        DrawPositionCard(race, p, position, new Rectangle(20, 20, 268, 172));
        DrawTower(race, standings, 20, 204);
        DrawMinimap(race, new Rectangle(w - 260, 20, 240, 184));
        DrawSpeedometer(p, new Vector2(w - 138, h - 132));

        if (!race.Started || race.RaceTime < 1.2f) DrawStartLights(race, w);
        DrawMessages(race, p, w, h);

        // Controls fade back once you're under way
        float alpha = race.Started ? Math.Clamp(1.2f - (race.RaceTime - 6f) / 2f, 0.28f, 1f) : 1f;
        Ui.KeyHintsLeft(20, h - 40, Controls, alpha);
    }

    static void DrawPositionCard(Race race, Racer p, int position, Rectangle r)
    {
        Ui.GlassPanel(r, 14);
        // accent spine
        Raylib.DrawRectangleRounded(new Rectangle(r.X, r.Y + 16, 3, 56), 1, 4, Ui.Accent);

        Ui.Label("Position", r.X + 18, r.Y + 16, Ui.Muted);
        string pos = position.ToString();
        Ui.Text(pos, r.X + 16, r.Y + 30, 58, Ui.TextColor, Typeface.Display);
        float pw = Ui.MeasureText(pos, 58, Typeface.Display);
        Ui.DrawText($"/{race.Racers.Count}", r.X + 20 + pw, r.Y + 56, 22, Ui.Dim, Typeface.Display);

        Ui.LabelRight("Lap", r.X + r.Width - 18, r.Y + 16, Ui.Muted);
        string lap = $"{p.CurrentLap(race.Laps)}";
        float tw = Ui.MeasureText($"/{race.Laps}", 22, Typeface.Display);
        Ui.TextRight($"/{race.Laps}", r.X + r.Width - 18, r.Y + 56, 22, Ui.Dim, Typeface.Display);
        Ui.Text(lap, r.X + r.Width - 20 - tw - Ui.MeasureText(lap, 58, Typeface.Display), r.Y + 30, 58, Ui.TextColor, Typeface.Display);

        Raylib.DrawLineEx(new Vector2(r.X + 16, r.Y + 98), new Vector2(r.X + r.Width - 16, r.Y + 98), 1, Ui.Stroke);

        float lapTime = race.Started && !p.Finished ? p.CurrentLapTime(race.RaceTime) : 0;
        float total = race.Started ? (p.Finished ? p.FinishTime : race.RaceTime) : 0;
        TimeRow(r, "Race", total, r.Y + 108, Ui.TextColor);
        TimeRow(r, "Lap", lapTime, r.Y + 130, Ui.TextColor);
        TimeRow(r, "Best", p.BestLap, r.Y + 152, p.BestLap > 0 ? Ui.Purple : Ui.Dim);
    }

    static void TimeRow(Rectangle r, string label, float time, float y, Color color)
    {
        Ui.Label(label, r.X + 18, y + 3, Ui.Muted);
        Ui.TextRight(Ui.FormatTime(time), r.X + r.Width - 18, y, 17, color, Typeface.Bold);
    }

    // F1-style timing tower
    static void DrawTower(Race race, List<Racer> standings, float x, float y)
    {
        const float rowH = 28, width = 268;
        Rectangle r = new(x, y, width, 34 + standings.Count * rowH + 8);
        Ui.GlassPanel(r, 14);
        Ui.Label("Standings", x + 18, y + 14, Ui.Muted);

        Racer leader = standings[0];
        for (int i = 0; i < standings.Count; i++)
        {
            Racer racer = standings[i];
            float ry = y + 34 + i * rowH;
            if (racer.IsPlayer)
            {
                Ui.RoundRect(new Rectangle(x + 8, ry + 1, width - 16, rowH - 2), 7, Ui.Alpha(Ui.Accent, 0.16f));
                Ui.RoundOutline(new Rectangle(x + 8, ry + 1, width - 16, rowH - 2), 7, 1, Ui.Alpha(Ui.Accent, 0.4f));
            }

            Ui.TextCentered($"{i + 1}", x + 28, ry + 7, 15, racer.IsPlayer ? Ui.Accent : Ui.Muted, Typeface.Bold);
            Raylib.DrawRectangleRounded(new Rectangle(x + 44, ry + 7, 3, rowH - 14), 1, 4, racer.Color);
            Ui.DrawText(racer.Name.ToUpperInvariant(), x + 56, ry + 7, 15, Ui.TextColor, Typeface.Bold, 0.04f);

            string status;
            Color sc = Ui.Muted;
            if (racer.Finished) { status = Ui.FormatTime(racer.FinishTime); sc = Ui.Good; }
            else if (racer == leader) status = $"LAP {racer.CurrentLap(race.Laps)}";
            else
            {
                // Gap to the leader, estimated from track distance and the leader's speed
                float metres = (leader.ProgressSamples - racer.ProgressSamples) * Track.Spacing;
                if (metres > race.Track.Length) status = $"+{(int)(metres / race.Track.Length)} LAP";
                else
                {
                    float speed = MathF.Max(leader.Movement.Velocity.Length(), 220f);
                    status = $"+{metres / speed:0.0}";
                }
            }
            Ui.TextRight(status, x + width - 18, ry + 8, 13, sc, Typeface.Bold);
        }
    }

    static void DrawStartLights(Race race, int w)
    {
        const int lights = 5;
        float pod = 46, gap = 10;
        float total = lights * pod + (lights - 1) * gap + 32;
        Rectangle r = new(w / 2f - total / 2, 26, total, pod + 32);

        // fade out after the start
        float a = race.Started ? Math.Clamp(1 - (race.RaceTime - 0.6f) / 0.6f, 0, 1) : 1;
        if (a <= 0) return;

        Ui.GlassPanel(r, 16, Ui.Alpha(new Color(6, 7, 10, 235), a), shadow: a > 0.5f);
        int lit = race.Started ? lights : Math.Clamp((int)((3f - race.Countdown) / 0.55f) + 1, 0, lights);

        for (int i = 0; i < lights; i++)
        {
            Vector2 c = new(r.X + 16 + pod / 2 + i * (pod + gap), r.Y + 16 + pod / 2);
            Ui.Circle(c, pod / 2, Ui.Alpha(new Color(20, 22, 28, 255), a));
            Ui.CircleLines(c, pod / 2, Ui.Alpha(Ui.Stroke, a));
            bool on = i < lit;
            Color col = race.Started ? Ui.Good : Ui.Bad;
            if (on)
            {
                Ui.Glow(c, pod * 1.1f, Ui.Alpha(col, 0.45f * a));
                Ui.Circle(c, pod / 2 - 5, Ui.Alpha(col, a));
                Ui.Circle(c - new Vector2(5, 6), pod / 7, Ui.Alpha(new Color(255, 255, 255, 120), a));
            }
            else
            {
                Ui.Circle(c, pod / 2 - 5, Ui.Alpha(new Color(40, 16, 18, 255), a));
            }
        }
    }

    static void DrawMessages(Race race, Racer p, int w, int h)
    {
        // Big countdown numeral / GO, with a scale-pop and fade
        if (!race.Started)
        {
            int n = (int)MathF.Ceiling(race.Countdown);
            float frac = race.Countdown - MathF.Floor(race.Countdown); // 1 → 0 over each second
            float size = 96 + 34 * frac * frac;
            Color c = Ui.Alpha(Ui.TextColor, Math.Clamp(frac * 1.6f, 0, 1));
            Ui.Text(n.ToString(), w / 2f - Ui.MeasureText(n.ToString(), size, Typeface.Display) / 2, 150 - (size - 96) * 0.3f, size, c, Typeface.Display);
        }
        else if (race.RaceTime < 1.1f)
        {
            float t = race.RaceTime / 1.1f;
            float size = 110 + 40 * t;
            Color c = Ui.Alpha(Ui.Good, 1 - t * t);
            Ui.Text("GO", w / 2f - Ui.MeasureText("GO", size, Typeface.Display, 0.06f) / 2, 150 - (size - 110) * 0.3f, size, c, Typeface.Display, 0.06f);
        }

        if (!race.Started || p.Finished) return;

        float sinceLap = race.RaceTime - race.LastLapCompletedAt;
        if (sinceLap < 3f && p.LapTimes.Count > 0)
        {
            float a = Math.Clamp(MathF.Min(sinceLap * 5, (3f - sinceLap) * 2.5f), 0, 1);
            float last = p.LapTimes[^1];
            bool best = last <= p.BestLap;
            string text = $"LAP {p.LapTimes.Count}   {Ui.FormatTime(last)}";
            float tw = Ui.MeasureText(text, 26, Typeface.Display, 0.06f);
            string tag = best ? "PERSONAL BEST" : "LAP TIME";
            float tagW = Ui.MeasureText(tag, 11, Typeface.Body, 0.16f) + 20;
            float width = tw + tagW + 56;
            Rectangle r = new(w / 2f - width / 2, 120 - (1 - a) * 10, width, 48);
            Ui.GlassPanel(r, 24, Ui.Alpha(Ui.Glass, a), shadow: a > 0.6f);
            Ui.Pill(tag, r.X + 14, r.Y + 13, Ui.Alpha(best ? Ui.Purple : new Color(255, 255, 255, 40), a), Ui.Alpha(Ui.TextColor, a));
            Ui.DrawText(text, r.X + tagW + 34, r.Y + 14, 26, Ui.Alpha(best ? Ui.Purple : Ui.TextColor, a), Typeface.Display, 0.06f);

            if (p.LapsDone == race.Laps - 1)
                Banner("FINAL LAP", w / 2f, 180, Ui.Accent, new Color(24, 18, 6, 255), a);
        }

        float pulse = 0.65f + 0.35f * MathF.Sin(race.RaceTime * 8);
        if (race.PlayerWrongWay)
            Banner("WRONG WAY", w / 2f, h / 2f + 70, Ui.Bad, Color.White, pulse, 34);
        else if (!p.OnTrack)
            Banner("OFF TRACK  ·  PRESS R TO RESET", w / 2f, h / 2f + 80, Ui.Warn, new Color(30, 16, 4, 255), pulse, 22);
    }

    static void Banner(string text, float cx, float y, Color bg, Color fg, float alpha, float size = 24)
    {
        float tw = Ui.MeasureText(text, size, Typeface.Display, 0.1f);
        float h = size + 20;
        Rectangle r = new(cx - tw / 2 - 26, y, tw + 52, h);
        Ui.Shadow(r, h / 2, alpha);
        Ui.RoundRect(r, h / 2, Ui.Alpha(bg, alpha));
        Ui.DrawText(text, r.X + 26, r.Y + 10 + size * 0.08f, size, Ui.Alpha(fg, alpha), Typeface.Display, 0.1f);
    }

    static void DrawSpeedometer(Racer p, Vector2 c)
    {
        float speed = p.Movement.CurrentSpeed;
        float maxDial = MathF.Ceiling(p.Car.MaxSpeed / 50f) * 50f;
        float ratio = Math.Clamp(speed / maxDial, 0f, 1f);
        const float R = 112, start = 135, sweep = 270;

        // dial body
        for (int i = 1; i <= 4; i++) Ui.Circle(c + new Vector2(0, 6), R + 6 + i * 3, new Color(0, 0, 0, 20));
        Ui.Circle(c, R + 6, Ui.Glass);
        Ui.CircleGradient(c, R, new Color(30, 34, 44, 180), new Color(10, 12, 16, 0));
        Ui.CircleLines(c, R + 6, Ui.Stroke);

        // track + fill, coloured white → accent → red as you approach the limiter
        Raylib.DrawRing(c, R - 16, R - 8, start, start + sweep, 72, new Color(255, 255, 255, 18));
        int segs = (int)(ratio * 60);
        for (int i = 0; i < segs; i++)
        {
            float t0 = i / 60f, t1 = (i + 1) / 60f;
            Color col = t0 < 0.6f ? Ui.Lerp(new Color(220, 226, 236, 255), Ui.Accent, t0 / 0.6f)
                                  : Ui.Lerp(Ui.Accent, Ui.Bad, Math.Clamp((t0 - 0.6f) / 0.3f, 0, 1));
            Raylib.DrawRing(c, R - 16, R - 8, start + sweep * t0, start + sweep * t1 + 0.4f, 2, col);
        }
        if (ratio > 0.01f)
        {
            float ang = (start + sweep * ratio) * MathF.PI / 180f;
            Vector2 tip = c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (R - 12);
            Color tc = ratio < 0.6f ? Ui.TextColor : ratio < 0.9f ? Ui.Accent : Ui.Bad;
            Ui.Glow(tip, 20, Ui.Alpha(tc, 0.55f));
            Ui.Circle(tip, 5, Color.White);
        }

        // ticks + numerals
        int majors = (int)(maxDial / 50);
        for (int i = 0; i <= majors * 5; i++)
        {
            float t = i / (float)(majors * 5);
            float ang = (start + sweep * t) * MathF.PI / 180f;
            Vector2 d = new(MathF.Cos(ang), MathF.Sin(ang));
            bool major = i % 5 == 0;
            Color tc = t <= ratio ? new Color(255, 255, 255, 200) : new Color(255, 255, 255, major ? 90 : 40);
            Raylib.DrawLineEx(c + d * (R - (major ? 30 : 26)), c + d * (R - 22), major ? 2 : 1, tc);
            if (major && (majors <= 7 || (i / 5) % 2 == 0))
            {
                string label = ((int)(t * maxDial)).ToString();
                Vector2 lp = c + d * (R - 44);
                Ui.TextCentered(label, lp.X, lp.Y - 6, 11, Ui.Muted, Typeface.Bold);
            }
        }

        Ui.TextCentered($"{speed:F0}", c.X, c.Y - 30, 58, Ui.TextColor, Typeface.Display);
        Ui.LabelCentered("km/h", c.X, c.Y + 24, Ui.Muted);
        if (p.Movement.ForwardSpeed < -5)
            Ui.Pill("Reverse", c.X - 38, c.Y + 46, Ui.Bad, Color.White, 10, 20);
    }

    static void DrawMinimap(Race race, Rectangle area)
    {
        Ui.GlassPanel(area, 14);
        Ui.Label("Circuit", area.X + 16, area.Y + 14, Ui.Muted);
        Track t = race.Track;
        Rectangle inner = new(area.X + 16, area.Y + 34, area.Width - 32, area.Height - 48);
        Vector2 size = t.BoundsMax - t.BoundsMin;
        float scale = MathF.Min(inner.Width / size.X, inner.Height / size.Y);
        Vector2 offset = new Vector2(inner.X, inner.Y)
                         + new Vector2(inner.Width - size.X * scale, inner.Height - size.Y * scale) / 2;
        Vector2 Map(Vector2 world) => offset + (world - t.BoundsMin) * scale;

        // outline + inner line for a crisp "drawn" circuit
        for (int i = 0; i < t.Count; i += 2)
        {
            Raylib.DrawLineEx(Map(t.PointAt(i)), Map(t.PointAt(i + 2)), 7, new Color(255, 255, 255, 22));
            Ui.Circle(Map(t.PointAt(i)), 3.5f, new Color(255, 255, 255, 22));
        }
        for (int i = 0; i < t.Count; i += 2)
            Raylib.DrawLineEx(Map(t.PointAt(i)), Map(t.PointAt(i + 2)), 2, new Color(200, 206, 218, 200));

        // start/finish tick
        Vector2 s0 = Map(t.PointAt(0)), sn = t.NormalAt(0) * 6;
        Raylib.DrawLineEx(s0 - sn, s0 + sn, 2.5f, Ui.Accent);

        foreach (Racer r in race.Racers.Where(r => !r.IsPlayer))
        {
            Vector2 m = Map(r.Movement.Position);
            Ui.Circle(m, 4.5f, new Color(0, 0, 0, 140));
            Ui.Circle(m, 3.5f, r.Color);
        }
        Vector2 pp = Map(race.Player.Movement.Position);
        float pulse = 0.5f + 0.5f * MathF.Sin((float)Raylib.GetTime() * 4);
        Ui.Glow(pp, 14 + 4 * pulse, Ui.Alpha(Ui.Accent, 0.5f));
        Ui.Circle(pp, 6, Color.White);
        Ui.Circle(pp, 4, Ui.Accent);
    }
}
