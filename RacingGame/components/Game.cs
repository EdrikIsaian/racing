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
    };
    int selectedCar;
    int laps = 3;
    int opponents = 3;
    int difficulty = 1;
    static readonly string[] DifficultyNames = { "Easy", "Normal", "Hard" };
    static readonly float[] DifficultySkill = { 0.72f, 0.88f, 0.98f };

    // Best lap per car for this session
    readonly Dictionary<Car, float> bestLaps = new();

    readonly List<Button> menuButtons = new();
    readonly List<Button> pauseButtons = new();
    readonly List<Button> resultsButtons = new();

    const int CardWidth = 270, CardHeight = 290, CardGap = 20, CardY = 140;

    public Game()
    {
        // Option selectors: three groups centred at x = 260, 640, 1020
        AddSelector(260, () => laps = Math.Max(1, laps - 1), () => laps = Math.Min(10, laps + 1));
        AddSelector(640, () => opponents = Math.Max(0, opponents - 1), () => opponents = Math.Min(5, opponents + 1));
        AddSelector(1020, () => difficulty = Math.Max(0, difficulty - 1), () => difficulty = Math.Min(2, difficulty + 1));
        menuButtons.Add(new Button(490, 590, 300, 58, "START RACE", StartRace, 26, primary: true));
        menuButtons.Add(new Button(1150, 20, 110, 38, "Quit", () => QuitRequested = true, 18));

        pauseButtons.Add(new Button(540, 300, 200, 50, "Resume", () => state = GameState.Racing));
        pauseButtons.Add(new Button(540, 365, 200, 50, "Restart", StartRace));
        pauseButtons.Add(new Button(540, 430, 200, 50, "Main menu", () => state = GameState.Menu));

        resultsButtons.Add(new Button(430, 580, 200, 52, "Race again", StartRace, 22, primary: true));
        resultsButtons.Add(new Button(650, 580, 200, 52, "Main menu", () => state = GameState.Menu, 22));
    }

    void AddSelector(int centerX, Action minus, Action plus)
    {
        menuButtons.Add(new Button(centerX - 110, 500, 44, 44, "<", minus, 24));
        menuButtons.Add(new Button(centerX + 66, 500, 44, 44, ">", plus, 24));
    }

    void StartRace()
    {
        race = new Race(cars[selectedCar], opponents, laps, DifficultySkill[difficulty]);
        resultsDelay = 0;
        state = GameState.Racing;
    }

    public void Update(float dt)
    {
        switch (state)
        {
            case GameState.Menu:
                UpdateMenu();
                break;

            case GameState.Racing:
                race!.Update(dt);
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
                    foreach (Button b in pauseButtons.ToList()) b.Update();
                break;

            case GameState.Results:
                race!.Update(dt); // the others keep racing behind the results screen
                if (Raylib.IsKeyPressed(KeyboardKey.Enter)) StartRace();
                else if (Raylib.IsKeyPressed(KeyboardKey.Escape)) state = GameState.Menu;
                else foreach (Button b in resultsButtons.ToList()) b.Update();
                break;
        }
    }

    void SaveBestLap()
    {
        float best = race!.Player.BestLap;
        Car car = race.Player.Car;
        if (best > 0 && (!bestLaps.TryGetValue(car, out float old) || best < old))
            bestLaps[car] = best;
    }

    void UpdateMenu()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Left) || Raylib.IsKeyPressed(KeyboardKey.A))
            selectedCar = (selectedCar + cars.Count - 1) % cars.Count;
        if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressed(KeyboardKey.D))
            selectedCar = (selectedCar + 1) % cars.Count;
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
                if (Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), CardRect(i)))
                    selectedCar = i;
        }

        foreach (Button b in menuButtons.ToList()) b.Update();
    }

    Rectangle CardRect(int i)
    {
        int total = cars.Count * CardWidth + (cars.Count - 1) * CardGap;
        int x = (Raylib.GetScreenWidth() - total) / 2 + i * (CardWidth + CardGap);
        return new Rectangle(x, CardY, CardWidth, CardHeight);
    }

    public void Draw()
    {
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
                Ui.Overlay();
                Ui.TextCentered("PAUSED", Raylib.GetScreenWidth() / 2, 200, 60, Color.White);
                foreach (Button b in pauseButtons) b.Draw();
                break;
            case GameState.Results:
                race!.Draw();
                DrawResults();
                break;
        }
    }

    void DrawMenu()
    {
        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        Raylib.DrawRectangleGradientV(0, 0, w, h, new Color(18, 22, 36, 255), new Color(40, 22, 30, 255));
        Ui.TextCentered("RACING GAME", w / 2, 36, 60, Ui.Accent);
        Ui.TextCentered("Choose your car", w / 2, 102, 20, Ui.Muted);

        for (int i = 0; i < cars.Count; i++) DrawCarCard(i);

        DrawSelectorValue(260, "LAPS", laps.ToString());
        DrawSelectorValue(640, "OPPONENTS", opponents.ToString());
        DrawSelectorValue(1020, "DIFFICULTY", DifficultyNames[difficulty]);

        foreach (Button b in menuButtons) b.Draw();

        Ui.TextCentered("Arrows / A-D to pick a car   Enter to start   Esc to quit", w / 2, 670, 16, Ui.Muted);
        Ui.TextCentered("Opponents drive the same car as you", w / 2, 692, 14, new Color(120, 126, 140, 255));
    }

    void DrawCarCard(int i)
    {
        Car car = cars[i];
        Rectangle r = CardRect(i);
        bool selected = i == selectedCar;
        bool hovered = Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), r);

        Raylib.DrawRectangleRounded(r, 0.08f, 8, hovered && !selected ? Ui.PanelLight : Ui.Panel);
        Raylib.DrawRectangleRoundedLinesEx(r, 0.08f, 8, selected ? 4 : 2, selected ? Ui.Accent : new Color(60, 68, 90, 255));

        int cx = (int)(r.X + r.Width / 2);
        float bob = selected ? MathF.Sin((float)Raylib.GetTime() * 3) * 3 : 0;
        Racer.DrawCar(new Vector2(cx, r.Y + 62 + bob), 0, car.Color, 2.6f, 0);

        Ui.TextCentered(car.Name, cx, (int)r.Y + 118, 22, Color.White);
        Ui.TextCentered(car.Type.ToString().ToUpper(), cx, (int)r.Y + 146, 14, Ui.Muted);

        int barX = (int)r.X + 20, barY = (int)r.Y + 178;
        DrawStat("Top speed", $"{car.MaxSpeed:F0} km/h", car.MaxSpeed / 350f, barX, barY);
        DrawStat("Acceleration", "", car.Acceleration / 90f, barX, barY + 30);
        DrawStat("Handling", "", car.Handling, barX, barY + 60);

        string best = bestLaps.TryGetValue(car, out float t) ? $"Best lap {Ui.FormatTime(t)}" : "No lap time yet";
        Ui.TextCentered(best, cx, (int)r.Y + 266, 14, bestLaps.ContainsKey(car) ? Ui.Good : new Color(110, 116, 130, 255));
    }

    static void DrawStat(string label, string value, float ratio, int x, int y)
    {
        Raylib.DrawText(label, x, y, 14, Ui.Muted);
        if (value != "") Raylib.DrawText(value, x + 230 - Raylib.MeasureText(value, 14), y, 14, Color.White);
        Raylib.DrawRectangleRounded(new Rectangle(x, y + 17, 230, 7), 1f, 6, new Color(50, 56, 74, 255));
        Raylib.DrawRectangleRounded(new Rectangle(x, y + 17, 230 * Math.Clamp(ratio, 0.05f, 1f), 7), 1f, 6, Ui.Accent);
    }

    static void DrawSelectorValue(int centerX, string label, string value)
    {
        Ui.TextCentered(label, centerX, 472, 16, Ui.Muted);
        Raylib.DrawRectangleRounded(new Rectangle(centerX - 60, 500, 120, 44), 0.25f, 8, Ui.Panel);
        Ui.TextCentered(value, centerX, 510, 26, Color.White);
    }

    void DrawResults()
    {
        Race r = race!;
        int w = Raylib.GetScreenWidth();
        Ui.Overlay(130);

        var panel = new Rectangle(w / 2 - 360, 90, 720, 470);
        Raylib.DrawRectangleRounded(panel, 0.06f, 8, Ui.Panel);

        int place = r.FinishOrder.IndexOf(r.Player) + 1;
        string headline = place == 1 ? "YOU WIN!" : $"You finished P{place}";
        Ui.TextCentered(headline, w / 2, 112, 48, place == 1 ? Ui.Accent : Color.White);

        int x = (int)panel.X + 40, y = 190;
        Raylib.DrawText("POS", x, y, 16, Ui.Muted);
        Raylib.DrawText("DRIVER", x + 70, y, 16, Ui.Muted);
        Raylib.DrawText("TIME", x + 340, y, 16, Ui.Muted);
        Raylib.DrawText("BEST LAP", x + 500, y, 16, Ui.Muted);

        List<Racer> standings = r.Standings();
        for (int i = 0; i < standings.Count; i++)
        {
            Racer racer = standings[i];
            int rowY = y + 34 + i * 40;
            if (racer.IsPlayer) Raylib.DrawRectangleRounded(new Rectangle(x - 16, rowY - 8, 672, 36), 0.3f, 6, new Color(255, 196, 0, 40));
            Color c = racer.IsPlayer ? Ui.Accent : Color.White;
            Ui.Text($"{i + 1}", x, rowY, 22, c);
            Raylib.DrawRectangle(x + 70, rowY + 5, 12, 12, racer.Color);
            Ui.Text(racer.Name, x + 92, rowY, 22, c);
            string time = racer.Finished ? Ui.FormatTime(racer.FinishTime) : $"Lap {racer.CurrentLap(r.Laps)}...";
            Ui.Text(time, x + 340, rowY, 22, racer.Finished ? Color.White : Ui.Muted);
            Ui.Text(Ui.FormatTime(racer.BestLap), x + 500, rowY, 22, Ui.Muted);
        }

        foreach (Button b in resultsButtons) b.Draw();
    }
}
