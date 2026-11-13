using System.Numerics;
using Raylib_cs;

enum GameState
{
    Menu,
    Racing,
    Paused,
    Results
}

// Switches between the menu, the race, the pause screen and the results.
class Game
{
    public bool QuitRequested;

    GameState state = GameState.Menu;
    Race? race;
    float resultsDelay;

    readonly List<Car> cars = new()
    {
        new Car("Mercedes-Benz", "GLC", CarType.SUV, 38, 220, 0.55f, new Color(190, 195, 205, 255)),
        new Car("Skoda", "Octavia", CarType.Sedan, 45, 240, 0.70f, new Color(78, 169, 50, 255)),
        new Car("Porsche", "911 GT3", CarType.SportsCar, 70, 318, 0.95f, new Color(250, 210, 30, 255)),
        new Car("Ferrari", "LaFerrari", CarType.HyperCar, 85, 350, 0.82f, new Color(210, 30, 30, 255)),
        new Car("Meme Motors", "Big Head", CarType.HeadCar, 62, 300, 0.78f, new Color(214, 120, 62, 255)),
        new Car("Krusty Krab", "Patty Wagon", CarType.BurgerCar, 55, 260, 0.85f, new Color(228, 162, 89, 255)),
    };
    int selectedCar;
    int selectedTrack;
    int laps = 3;
    int opponents = 3;
    int difficulty = 1;
    bool manualGears;
    static readonly string[] DifficultyNames = { "Easy", "Normal", "Hard" };
    static readonly float[] DifficultySkill = { 0.72f, 0.88f, 0.98f };

    // Best lap per car and track for this session
    readonly Dictionary<(Car, TrackDef), float> bestLaps = new();

    readonly List<Button> menuButtons = new();
    readonly List<Button> pauseButtons = new();
    readonly List<Button> resultsButtons = new();

    // Menu layout (1280x720 design space)
    static readonly Vector2 Turntable = new(440, 404);
    const float StripX = 80, StripY = 596, StripW = 710, StripH = 66, StripGap = 10;
    static readonly Rectangle SidePanel = new(850, 96, 380, 550);
    const float TrackCardY = 96 + 186, TrackCardH = 100;
    const float SettingsY = 96 + 334, SettingsRowH = 34;

    float showcaseT = 1;   // 0 → 1 slide-in when you switch car
    int showcaseDir = 1;
    float overlayT;        // fade-in for pause/results
    GameState lastState;

    public Game()
    {
        float right = SidePanel.X + SidePanel.Width - 24;
        AddSelector(right, SettingsY, () => laps = Math.Max(1, laps - 1), () => laps = Math.Min(10, laps + 1));
        AddSelector(right, SettingsY + SettingsRowH, () => opponents = Math.Max(0, opponents - 1), () => opponents = Math.Min(5, opponents + 1));
        AddSelector(right, SettingsY + SettingsRowH * 2, () => difficulty = Math.Max(0, difficulty - 1), () => difficulty = Math.Min(2, difficulty + 1));
        AddSelector(right, SettingsY + SettingsRowH * 3, () => manualGears = !manualGears, () => manualGears = !manualGears);
        menuButtons.Add(Button.Arrow(SidePanel.X + 24 + 16, TrackCardY + TrackCardH / 2, 28, -1, () => SelectTrack(-1)));
        menuButtons.Add(Button.Arrow(right - 16, TrackCardY + TrackCardH / 2, 28, 1, () => SelectTrack(1)));
        menuButtons.Add(new Button(SidePanel.X + 24, SidePanel.Y + SidePanel.Height - 70, SidePanel.Width - 48, 52, "START RACE", StartRace, 26, ButtonStyle.Primary));
        menuButtons.Add(new Button(1146, 28, 84, 34, "QUIT", () => QuitRequested = true, 17, ButtonStyle.Ghost));
        menuButtons.Add(Button.Arrow(112, Turntable.Y, 46, -1, () => SelectCar(-1)));
        menuButtons.Add(Button.Arrow(768, Turntable.Y, 46, 1, () => SelectCar(1)));

        pauseButtons.Add(new Button(514, 336, 252, 48, "RESUME", () => state = GameState.Racing, 20, ButtonStyle.Primary));
        pauseButtons.Add(new Button(514, 394, 252, 48, "RESTART", StartRace, 20));
        pauseButtons.Add(new Button(514, 452, 252, 48, "MAIN MENU", () => state = GameState.Menu, 20));

        resultsButtons.Add(new Button(434, 622, 200, 54, "RACE AGAIN", StartRace, 22, ButtonStyle.Primary));
        resultsButtons.Add(new Button(646, 622, 200, 54, "MAIN MENU", () => state = GameState.Menu, 22));
    }

    void AddSelector(float rightX, float rowY, Action minus, Action plus)
    {
        menuButtons.Add(Button.Arrow(rightX - 136, rowY + 14, 26, -1, minus));
        menuButtons.Add(Button.Arrow(rightX - 15, rowY + 14, 26, 1, plus));
    }

    void SelectTrack(int delta)
    {
        int n = TrackDef.All.Length;
        selectedTrack = (selectedTrack + delta + n) % n;
        Audio.Whoosh();
    }

    public void PickTrack(int i) => selectedTrack = Math.Clamp(i, 0, TrackDef.All.Length - 1);

    void SelectCar(int delta)
    {
        selectedCar = (selectedCar + delta + cars.Count) % cars.Count;
        Audio.Whoosh();
        showcaseT = 0;
        showcaseDir = delta;
    }

    public void StartRaceNow() => StartRace();
    public void PickCar(int i) => selectedCar = Math.Clamp(i, 0, cars.Count - 1);

    void StartRace()
    {
        race = new Race(TrackDef.All[selectedTrack], cars[selectedCar], opponents, laps, DifficultySkill[difficulty], manualGears);
        resultsDelay = 0;
        state = GameState.Racing;
    }

    public void Update(float dt)
    {
        if (state == GameState.Menu) Audio.PlayMenuMusic();
        else Audio.PlayRaceMusic();

        switch (state)
        {
            case GameState.Menu:
                UpdateMenu();
                break;

            case GameState.Racing:
                race!.Update(dt);
                race.UpdateAudio();
                if (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.P))
                    state = GameState.Paused;
                if (race.Player.Finished)
                {
                    resultsDelay += dt;
                    if (resultsDelay > 1.5f)
                    {
                        SaveBestLap();
                        state = GameState.Results;
                    }
                }
                break;

            case GameState.Paused:
                if (Raylib.IsKeyPressed(KeyboardKey.Escape) || Raylib.IsKeyPressed(KeyboardKey.P))
                    state = GameState.Racing;
                else
                    foreach (Button b in pauseButtons) b.Update();
                break;

            case GameState.Results:
                race!.Update(dt); // the others keep racing behind the results screen
                race.UpdateAudio();
                if (Raylib.IsKeyPressed(KeyboardKey.Enter)) StartRace();
                else if (Raylib.IsKeyPressed(KeyboardKey.Escape)) state = GameState.Menu;
                else foreach (Button b in resultsButtons) b.Update();
                break;
        }
    }

    void SaveBestLap()
    {
        float best = race!.Player.BestLap;
        var key = (race.Player.Car, race.Def);
        if (best > 0 && (!bestLaps.TryGetValue(key, out float old) || best < old))
            bestLaps[key] = best;
    }

    void UpdateMenu()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Left) || Raylib.IsKeyPressed(KeyboardKey.A))
            SelectCar(-1);
        if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressed(KeyboardKey.D))
            SelectCar(1);
        if (Raylib.IsKeyPressed(KeyboardKey.Tab))
            SelectTrack(Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift) ? -1 : 1);
        if (Raylib.IsKeyPressed(KeyboardKey.Enter))
        {
            StartRace();
            return;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            QuitRequested = true;
            return;
        }

        if (Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            for (int i = 0; i < cars.Count; i++)
                if (i != selectedCar && Raylib.CheckCollisionPointRec(Ui.Mouse, TabRect(i)))
                {
                    showcaseDir = i > selectedCar ? 1 : -1;
                    Audio.Whoosh();
                    selectedCar = i;
                    showcaseT = 0;
                }
        }

        foreach (Button b in menuButtons) b.Update();
    }

    Rectangle TabRect(int i)
    {
        float w = (StripW - StripGap * (cars.Count - 1)) / cars.Count;
        return new Rectangle(StripX + i * (w + StripGap), StripY, w, StripH);
    }

    // Menu showroom area in UI units; the 3D car image is drawn into it
    static readonly Rectangle ShowcaseArea = new(80, 196, 720, 400);

    // 3D renders that go into their own images. Runs before the frame starts drawing.
    public void PreDraw()
    {
        if (state != GameState.Menu) return;
        Car car = cars[selectedCar];
        float e = 1 - MathF.Pow(1 - showcaseT, 3);
        // Twice the screen resolution, then shrunk: smooth edges
        float pixels = Ui.Scale * Raylib.GetRenderWidth() / (float)Raylib.GetScreenWidth() * 2;
        float spin = -2.4f + (float)Raylib.GetTime() * 0.35f;
        Showcase.Render(car.Type, car.Color, spin, (0.85f + 0.15f * e) * (car.Type == CarType.BurgerCar ? 0.72f : 1), (int)(ShowcaseArea.Width * pixels), (int)(ShowcaseArea.Height * pixels));
    }

    public void Draw()
    {
        if (state != lastState) { overlayT = 0; lastState = state; }
        overlayT = Ui.Approach(overlayT, 1, 9);

        switch (state)
        {
            case GameState.Menu:
                DrawMenu();
                break;
            case GameState.Racing:
                race!.Draw();
                break;
            case GameState.Paused:
                race!.Draw();
                Ui.Overlay((byte)(170 * overlayT));
                Ui.BeginMenu();
                DrawPause();
                Ui.End();
                break;
            case GameState.Results:
                race!.Draw();
                Ui.Overlay((byte)(175 * overlayT));
                Ui.BeginMenu();
                DrawResults();
                Ui.End();
                break;
        }
    }

    static string ClassName(CarType t) => t switch
    {
        CarType.SUV => "SUV",
        CarType.Sedan => "Sport sedan",
        CarType.SportsCar => "Sports car",
        CarType.HeadCar => "Head car",
        CarType.BurgerCar => "Burger",
        _ => "Hypercar",
    };

    void DrawMenu()
    {
        Car car = cars[selectedCar];
        float time = (float)Raylib.GetTime();
        showcaseT = Ui.Approach(showcaseT, 1, 7);

        // Backdrop: deep graphite gradient with a glow in the car's colour
        int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
        Raylib.DrawRectangleGradientV(0, 0, sw, sh, Ui.Bg1, Ui.Bg0);

        Ui.BeginMenu();
        // dot grid
        for (int x = 16; x < Ui.BaseWidth; x += 32)
            for (int y = 16; y < Ui.BaseHeight; y += 32)
                Raylib.DrawCircleSector(new Vector2(x, y), 1, 0, 360, 8, new Color(255, 255, 255, 9)); // tiny dots don't need 36 sides
        Ui.Glow(Turntable, 470, Ui.Alpha(car.Color, 0.20f));
        Ui.Glow(Turntable, 240, Ui.Alpha(car.Color, 0.12f));

        Ui.End();
        Ui.Vignette(0.9f);
        Ui.BeginMenu();

        // Top bar: wordmark + quit
        for (int i = 0; i < 3; i++)
        {
            float bx = 80 + i * 9;
            Raylib.DrawTriangle(new Vector2(bx + 6, 34), new Vector2(bx, 58), new Vector2(bx + 5, 58), Ui.Alpha(Ui.Accent, 1 - i * 0.28f));
            Raylib.DrawTriangle(new Vector2(bx + 6, 34), new Vector2(bx + 5, 58), new Vector2(bx + 11, 34), Ui.Alpha(Ui.Accent, 1 - i * 0.28f));
        }
        Ui.DrawText("RACING", 116, 35, 28, Ui.TextColor, Typeface.Display, 0.12f);
        Ui.DrawText("GAME", 116 + Ui.MeasureText("RACING ", 28, Typeface.Display, 0.12f), 35, 28, Ui.Muted, Typeface.Display, 0.12f);
        Raylib.DrawLineEx(new Vector2(80, 78), new Vector2(1200, 78), 1, Ui.Stroke);

        // Showcase: name + class
        float e = 1 - MathF.Pow(1 - showcaseT, 3); // ease-out
        float slide = (1 - e) * 40 * showcaseDir;
        Color fade = Ui.Alpha(Ui.TextColor, e);
        Ui.Label(car.Brand, 82 + slide * 0.5f, 106, Ui.Alpha(Ui.Accent, e), 13);
        Ui.DrawText(car.Model.ToUpperInvariant(), 78 + slide * 0.5f, 124, 88, fade, Typeface.Display, 0.02f);
        Ui.Pill(ClassName(car.Type), 82 + slide * 0.5f, 212, new Color(255, 255, 255, (int)(20 * e)), Ui.Alpha(Ui.Muted, e));

        // Turntable with the 3D car (rendered beforehand in PreDraw)
        Texture2D tex = Showcase.Texture;
        Rectangle area = ShowcaseArea;
        area.X += slide * 3;
        Raylib.DrawTexturePro(tex, new Rectangle(0, 0, tex.Width, -tex.Height), area, Vector2.Zero, 0, Color.White);

        // Car picker strip
        for (int i = 0; i < cars.Count; i++) DrawCarTab(i);

        DrawSidePanel(car);

        foreach (Button b in menuButtons) b.Draw();

        Ui.KeyHints(640, 684, new[] { ("A  D", "Choose car"), ("TAB", "Track"), ("ENTER", "Start race"), ("M", Audio.MusicMuted ? "Music off" : "Music on"), ("T", RayTracing.Enabled ? "Ray tracing on" : "Ray tracing off"), ("F", "Full screen"), ("ESC", "Quit") });
        Ui.End();
    }

    void DrawCarTab(int i)
    {
        Car car = cars[i];
        Rectangle r = TabRect(i);
        bool selected = i == selectedCar;
        bool hovered = Raylib.CheckCollisionPointRec(Ui.Mouse, r);

        Ui.RoundRect(r, 12, selected ? new Color(255, 255, 255, 22) : hovered ? new Color(255, 255, 255, 14) : new Color(255, 255, 255, 6));
        Ui.RoundOutline(r, 12, 1, selected ? Ui.Alpha(Ui.Accent, 0.75f) : Ui.Stroke);
        if (selected) Raylib.DrawRectangleRounded(new Rectangle(r.X + 16, r.Y - 1, 28, 3), 1, 4, Ui.Accent);

        Color text = selected ? Ui.TextColor : Ui.Muted;
        Ui.DrawText($"0{i + 1}", r.X + 16, r.Y + 13, 12, selected ? Ui.Accent : Ui.Dim, Typeface.Bold);
        // Name shrinks to fit next to the icon (six tabs are narrow)
        string name = car.Model.ToUpperInvariant();
        float room = r.Width - 16 - 58, size = 22;
        float nameW = Ui.MeasureText(name, size, Typeface.Display, 0.03f);
        if (nameW > room) size = MathF.Max(10, size * room / nameW);
        Ui.DrawText(name, r.X + 16, r.Y + 29 + (22 - size) * 0.7f, size, text, Typeface.Display, 0.03f);
        if (car.Type is CarType.HeadCar or CarType.BurgerCar)
        {
            // The picture itself, small, as its icon
            bool head = car.Type == CarType.HeadCar;
            Texture2D icon = head ? HeadCar.Icon : BurgerCar.Icon;
            Rectangle src = head ? HeadCar.PhotoArea : new Rectangle(0, 0, icon.Width, icon.Height);
            float h = head ? 23 : 27, w = h * src.Width / src.Height;
            Rectangle dst = new(r.X + r.Width - 34 - w / 2, r.Y + r.Height / 2 - h / 2, w, h);
            Raylib.DrawTexturePro(icon, src, dst, Vector2.Zero, 0, selected ? Color.White : new Color(150, 150, 150, 255));
        }
        else
            Racer.DrawCar(new Vector2(r.X + r.Width - 34, r.Y + r.Height / 2), -MathF.PI / 2, selected ? car.Color : Ui.Alpha(Darken(car.Color), 1f), 1.05f, 0, car.Type);
    }

    static Color Darken(Color c) => new((int)(c.R * 0.55f), (int)(c.G * 0.55f), (int)(c.B * 0.55f), 255);

    void DrawSidePanel(Car car)
    {
        Rectangle p = SidePanel;
        Ui.GlassPanel(p, 18, new Color(14, 16, 22, 225));
        float x0 = p.X + 24, x1 = p.X + p.Width - 24, w = x1 - x0;

        Ui.Label("Performance", x0, p.Y + 22, Ui.Muted);
        DrawStat("Top speed", $"{car.MaxSpeed:F0}", "km/h", car.MaxSpeed / 360f, x0, x1, p.Y + 42);
        DrawStat("Acceleration", $"{car.Acceleration / 9f:0.0}", "/ 10", car.Acceleration / 90f, x0, x1, p.Y + 88);
        DrawStat("Handling", $"{car.Handling * 10:0.0}", "/ 10", car.Handling, x0, x1, p.Y + 134);

        DrawTrackCard(car, x0, x1, TrackCardY);

        Raylib.DrawLineEx(new Vector2(x0, SettingsY - 24), new Vector2(x1, SettingsY - 24), 1, Ui.Stroke);
        Ui.Label("Race settings", x0, SettingsY - 12, Ui.Muted);

        DrawSetting("Laps", laps.ToString(), 0);
        DrawSetting("Opponents", opponents.ToString(), 1);
        DrawSetting("Difficulty", DifficultyNames[difficulty].ToUpperInvariant(), 2);
        DrawSetting("Gearbox", manualGears ? "MANUAL" : "AUTO", 3);

        void DrawSetting(string label, string value, int row)
        {
            float y = SettingsY + row * SettingsRowH;
            Ui.DrawText(label, x0, y + 6, 16, Ui.TextColor, Typeface.Body);
            Ui.TextCentered(value, x1 - 75, y + 4, 22, Ui.TextColor, Typeface.Display, 0.04f);
            if (row < 3) Raylib.DrawLineEx(new Vector2(x0, y + SettingsRowH - 4), new Vector2(x1, y + SettingsRowH - 4), 1, new Color(255, 255, 255, 10));
        }
    }

    // The chosen circuit: name, character, length and climb, best lap, and a small map coloured by height
    void DrawTrackCard(Car car, float x0, float x1, float y)
    {
        TrackDef def = TrackDef.All[selectedTrack];
        Track t = Track.Get(def);
        Rectangle card = new(x0, y, x1 - x0, TrackCardH);
        Ui.RoundRect(card, 12, new Color(255, 255, 255, 8));
        Ui.RoundOutline(card, 12, 1, Ui.Stroke);

        Rectangle map = new(x1 - 36 - 88, y + 12, 84, TrackCardH - 24);
        float tx = x0 + 40, room = map.X - 10 - tx;
        Ui.Label($"Track {selectedTrack + 1} / {TrackDef.All.Length}", tx, y + 12, Ui.Accent);
        if (bestLaps.TryGetValue((car, def), out float best))
            Ui.TextRight($"PB {Ui.FormatTime(best)}", map.X - 10, y + 10, 13, Ui.Purple, Typeface.Bold);

        string name = def.Name.ToUpperInvariant();
        float size = 26, nameW = Ui.MeasureText(name, size, Typeface.Display, 0.03f);
        if (nameW > room) size = MathF.Max(12, size * room / nameW);
        Ui.DrawText(name, tx, y + 28 + (26 - size) * 0.7f, size, Ui.TextColor, Typeface.Display, 0.03f);
        Ui.DrawText(def.Tagline, tx, y + 58, 13, Ui.Muted, Typeface.Body);
        Ui.DrawText($"{t.LengthKm:0.0} km  ·  {t.ClimbMetres:0} m climb", tx, y + 77, 12, Ui.Dim, Typeface.Body);

        // Mini map, low parts cool and high parts warm
        Vector2 sz = t.BoundsMax - t.BoundsMin;
        float scale = MathF.Min(map.Width / sz.X, map.Height / sz.Y);
        Vector2 off = new Vector2(map.X, map.Y) + (new Vector2(map.Width, map.Height) - sz * scale) / 2;
        Vector2 M(Vector2 w) => off + (w - t.BoundsMin) * scale;
        float lo = t.Heights.Min(), hi = MathF.Max(t.Heights.Max(), lo + 1);
        for (int i = 0; i < t.Count; i += 3)
        {
            float k = (t.Heights[i] - lo) / (hi - lo);
            Raylib.DrawLineEx(M(t.PointAt(i)), M(t.PointAt(i + 3)), 2.6f, Ui.Lerp(new Color(110, 170, 230, 255), Ui.Accent, k));
        }
        Vector2 s0 = M(t.PointAt(0)), sn = t.NormalAt(0) * 5;
        Raylib.DrawLineEx(s0 - sn, s0 + sn, 2.5f, Color.White);
    }

    static void DrawStat(string label, string value, string unit, float ratio, float x0, float x1, float y)
    {
        Ui.DrawText(label, x0, y + 8, 14, Ui.Muted, Typeface.Body);
        float uw = Ui.MeasureText(unit, 13, Typeface.Body);
        Ui.TextRight(unit, x1, y + 9, 13, Ui.Dim, Typeface.Body);
        Ui.TextRight(value, x1 - uw - 6, y, 26, Ui.TextColor, Typeface.Display);
        Ui.SegmentBar(x0, y + 32, x1 - x0, ratio, Ui.Accent, 20, 5);
    }

    void DrawPause()
    {
        float e = 1 - MathF.Pow(1 - overlayT, 3);
        Rectangle p = new(490, 196 + (1 - e) * 16, 300, 330);
        Ui.GlassPanel(p, 20, new Color(14, 16, 22, (int)(235 * e)));
        Ui.LabelCentered("Race paused", 640, p.Y + 30, Ui.Alpha(Ui.Accent, e));
        Ui.TextCentered("PAUSED", 640, p.Y + 50, 58, Ui.Alpha(Ui.TextColor, e), Typeface.Display, 0.08f);
        Ui.TextCentered($"Race time  {Ui.FormatTime(race!.RaceTime)}", 640, p.Y + 110, 13, Ui.Alpha(Ui.Muted, e), Typeface.Body);
        foreach (Button b in pauseButtons) b.Draw();
        Ui.KeyHints(640, 560, new[] { ("ESC", "Resume") }, e);
    }

    void DrawResults()
    {
        Race r = race!;
        float e = 1 - MathF.Pow(1 - overlayT, 3);
        Rectangle panel = new(240, 60 + (1 - e) * 20, 800, 540);
        Ui.GlassPanel(panel, 22, new Color(14, 16, 22, (int)(238 * e)));
        float x0 = panel.X + 36, x1 = panel.X + panel.Width - 36;
        float py = panel.Y;

        int place = r.FinishOrder.IndexOf(r.Player) + 1;
        string word = place == 1 ? "VICTORY" : place <= 3 ? "PODIUM FINISH" : "RACE COMPLETE";
        Color hero = place == 1 ? Ui.Accent : Ui.TextColor;

        Ui.Label($"Race result  ·  {r.Def.Name}  ·  {r.Laps} {(r.Laps == 1 ? "lap" : "laps")}", x0, py + 30, Ui.Muted);
        string pText = $"P{place}";
        Ui.DrawText(pText, x0 - 3, py + 50, 104, Ui.Alpha(hero, e), Typeface.Display);
        float pw = Ui.MeasureText(pText, 104, Typeface.Display);
        Ui.DrawText(word, x0 + pw + 22, py + 72, 42, Ui.Alpha(Ui.TextColor, e), Typeface.Display, 0.06f);
        Ui.DrawText($"Total {Ui.FormatTime(r.Player.FinishTime)}    ·    Best lap {Ui.FormatTime(r.Player.BestLap)}",
            x0 + pw + 24, py + 120, 14, Ui.Alpha(Ui.Muted, e), Typeface.Body);

        float hy = py + 176;
        Raylib.DrawLineEx(new Vector2(x0, hy - 14), new Vector2(x1, hy - 14), 1, Ui.Stroke);
        const float cTime = 640, cGap = 800;
        Ui.Label("Pos", x0, hy, Ui.Dim);
        Ui.Label("Driver", x0 + 62, hy, Ui.Dim);
        Ui.LabelRight("Time", cTime, hy, Ui.Dim);
        Ui.LabelRight("Gap", cGap, hy, Ui.Dim);
        Ui.LabelRight("Best lap", x1, hy, Ui.Dim);

        List<Racer> standings = r.Standings();
        float fastest = standings.Where(s => s.BestLap > 0).Select(s => s.BestLap).DefaultIfEmpty(0).Min();
        float winner = standings[0].Finished ? standings[0].FinishTime : 0;
        Color[] podium = { new(232, 186, 72, 255), new(196, 202, 214, 255), new(204, 132, 78, 255) };

        for (int i = 0; i < standings.Count; i++)
        {
            Racer racer = standings[i];
            float rowE = Math.Clamp(overlayT * 1.6f - i * 0.08f, 0, 1);
            float ry = hy + 26 + i * 50 + (1 - rowE) * 12;
            Rectangle row = new(x0 - 12, ry, x1 - x0 + 24, 44);
            if (racer.IsPlayer)
            {
                Ui.RoundRect(row, 10, Ui.Alpha(Ui.Accent, 0.13f * rowE));
                Ui.RoundOutline(row, 10, 1, Ui.Alpha(Ui.Accent, 0.45f * rowE));
            }
            else if (i % 2 == 0) Ui.RoundRect(row, 10, new Color(255, 255, 255, (int)(6 * rowE)));

            Rectangle chip = new(x0, ry + 8, 30, 28);
            Ui.RoundRect(chip, 7, i < 3 ? Ui.Alpha(podium[i], rowE) : new Color(255, 255, 255, (int)(18 * rowE)));
            Ui.TextCentered($"{i + 1}", chip.X + 15, chip.Y + 6, 17, i < 3 ? Ui.Alpha(new Color(20, 16, 8, 255), rowE) : Ui.Alpha(Ui.TextColor, rowE), Typeface.Bold);

            Raylib.DrawRectangleRounded(new Rectangle(x0 + 62, ry + 11, 3, 22), 1, 4, Ui.Alpha(racer.Color, rowE));
            Ui.DrawText(racer.Name.ToUpperInvariant(), x0 + 76, ry + 11, 24, Ui.Alpha(Ui.TextColor, rowE), Typeface.Display, 0.05f);

            string time = racer.Finished ? Ui.FormatTime(racer.FinishTime) : $"LAP {racer.CurrentLap(r.Laps)}";
            Ui.TextRight(time, cTime, ry + 13, 18, Ui.Alpha(racer.Finished ? Ui.TextColor : Ui.Muted, rowE), Typeface.Bold);
            string gap = i == 0 ? "—" : racer.Finished && winner > 0 ? $"+{racer.FinishTime - winner:0.000}" : "—";
            Ui.TextRight(gap, cGap, ry + 15, 15, Ui.Alpha(Ui.Muted, rowE), Typeface.Bold);
            bool isFastest = racer.BestLap > 0 && racer.BestLap == fastest;
            Ui.TextRight(Ui.FormatTime(racer.BestLap), x1, ry + 15, 15, Ui.Alpha(isFastest ? Ui.Purple : Ui.Muted, rowE), Typeface.Bold);
        }

        foreach (Button b in resultsButtons) b.Draw();
        Ui.KeyHints(640, 690, new[] { ("ENTER", "Race again"), ("ESC", "Main menu") }, e);
    }
}
