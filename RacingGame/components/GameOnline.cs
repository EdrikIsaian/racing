using System.Numerics;
using Raylib_cs;

// Multiplayer screens: pick a name, host a room or join one, then the lobby before the race.
partial class Game
{
    TextField nameField = null!, ipField = null!;
    readonly List<Button> mpButtons = new();
    readonly List<Button> lobbyButtons = new();      // everyone in the room
    readonly List<Button> lobbyHostButtons = new();  // only the host changes the race
    readonly List<Button> onlinePauseButtons = new();
    readonly List<Button> onlineResultsButtons = new();
    int autoStartPlayers; // RACING_NET_START: the host starts by itself once this many have joined

    // Layout (1280x720 design space)
    static readonly Rectangle MpLeft = new(80, 110, 500, 530), MpRight = new(620, 110, 580, 530);
    static readonly Rectangle LobbyLeft = new(80, 110, 560, 530), LobbyRight = new(680, 110, 520, 530);
    const float RoomRowY = 52, RoomRowH = 62, RoomRowGap = 8;
    const float LobbyCarY = 38, LobbyTrackY = 110, LobbySettingsY = 248;

    // Two players in the same car get different paint
    static readonly Color[] PlayerColors =
    {
        new(245, 245, 250, 255), new(60, 200, 90, 255), new(40, 170, 255, 255), new(255, 80, 60, 255),
        new(190, 110, 255, 255), new(255, 120, 190, 255), new(255, 210, 0, 255), new(120, 220, 220, 255),
    };

    static bool NameChar(char c) => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.';

    void InitOnline()
    {
        string user = Environment.UserName;
        if (user.Length > 0) user = char.ToUpperInvariant(user[0]) + user[1..];
        float mx0 = MpLeft.X + 24, mw = MpLeft.Width - 48;
        nameField = new TextField(new Rectangle(mx0, MpLeft.Y + 44, mw, 48), Net.CleanName(user), "Your name", 14, NameChar);
        ipField = new TextField(new Rectangle(mx0, MpLeft.Y + 356, mw - 132, 48), "", "e.g. 192.168.1.23", 40,
            c => char.IsLetterOrDigit(c) || c is '.' or '-') { OnSubmit = JoinTyped };

        mpButtons.Add(new Button(mx0, MpLeft.Y + 206, mw, 54, "HOST A ROOM", HostRoom, 24, ButtonStyle.Primary));
        mpButtons.Add(new Button(mx0 + mw - 120, MpLeft.Y + 356, 120, 48, "JOIN", JoinTyped, 20));
        mpButtons.Add(new Button(1146, 28, 84, 34, "BACK", CloseMultiplayer, 17, ButtonStyle.Ghost));

        float lx0 = LobbyRight.X + 24, lx1 = LobbyRight.X + LobbyRight.Width - 24;
        float y0 = LobbyRight.Y;
        lobbyButtons.Add(new Button(1146, 28, 84, 34, "LEAVE", LeaveRoom, 17, ButtonStyle.Ghost));
        lobbyButtons.Add(Button.Arrow(lx0 + 16, y0 + LobbyCarY + 28, 28, -1, () => SelectCar(-1)));
        lobbyButtons.Add(Button.Arrow(lx1 - 16, y0 + LobbyCarY + 28, 28, 1, () => SelectCar(1)));
        AddLobbySelector(lobbyButtons, lx1, 3, () => manualGears = !manualGears, () => manualGears = !manualGears);

        lobbyHostButtons.Add(Button.Arrow(lx0 + 16, y0 + LobbyTrackY + TrackCardH / 2, 28, -1, () => SelectTrack(-1)));
        lobbyHostButtons.Add(Button.Arrow(lx1 - 16, y0 + LobbyTrackY + TrackCardH / 2, 28, 1, () => SelectTrack(1)));
        AddLobbySelector(lobbyHostButtons, lx1, 0, () => laps = Math.Max(1, laps - 1), () => laps = Math.Min(10, laps + 1));
        AddLobbySelector(lobbyHostButtons, lx1, 1, () => opponents = Math.Max(0, opponents - 1), () => opponents = Math.Min(Net.MaxOpponents, opponents + 1));
        AddLobbySelector(lobbyHostButtons, lx1, 2, () => difficulty = Math.Max(0, difficulty - 1), () => difficulty = Math.Min(2, difficulty + 1));
        lobbyHostButtons.Add(new Button(lx0, LobbyRight.Y + LobbyRight.Height - 70, lx1 - lx0, 52, "START RACE", HostStart, 26, ButtonStyle.Primary));

        onlinePauseButtons.Add(new Button(514, 336, 252, 48, "RESUME", () => state = GameState.Racing, 20, ButtonStyle.Primary));
        onlinePauseButtons.Add(new Button(514, 394, 252, 48, "LEAVE RACE", LeaveOnlineRace, 20));

        onlineResultsButtons.Add(new Button(520, 622, 240, 54, "BACK TO LOBBY", LeaveOnlineRace, 22, ButtonStyle.Primary));
    }

    static void AddLobbySelector(List<Button> list, float rightX, int row, Action minus, Action plus)
    {
        float rowY = LobbyRight.Y + LobbySettingsY + row * SettingsRowH;
        list.Add(Button.Arrow(rightX - 136, rowY + 14, 26, -1, minus));
        list.Add(Button.Arrow(rightX - 15, rowY + 14, 26, 1, plus));
    }

    // For test runs: RACING_NET=host or RACING_NET=join:192.168.1.23, RACING_NET_START=2
    public void AutoNet(string mode, int startWhen)
    {
        autoStartPlayers = startWhen;
        if (mode == "host") HostRoom();
        else if (mode.StartsWith("join:"))
        {
            state = GameState.Multiplayer;
            Net.Join(mode[5..], nameField.Text, selectedCar);
        }
    }

    // ---- Moving between screens

    void OpenMultiplayer()
    {
        state = GameState.Multiplayer;
        Net.Stop();
        Net.StartBrowsing();
    }

    void CloseMultiplayer()
    {
        TextField.Blur();
        Net.Stop();
        Net.StopBrowsing();
        state = GameState.Menu;
    }

    string PlayerName()
    {
        nameField.Text = Net.CleanName(nameField.Text);
        return nameField.Text;
    }

    void HostRoom()
    {
        TextField.Blur();
        if (!Net.Host(PlayerName(), selectedCar)) return;
        Net.StopBrowsing();
        opponents = Math.Min(opponents, Net.MaxOpponents);
        SyncLobbySettings();
        state = GameState.Lobby;
    }

    void JoinTyped()
    {
        TextField.Blur();
        if (ipField.Text.Trim().Length == 0)
        {
            Net.Status = "Type the address shown in the host's lobby first";
            Net.StatusIsError = true;
            return;
        }
        Net.Join(ipField.Text, PlayerName(), selectedCar);
    }

    void JoinRoom(Room room)
    {
        TextField.Blur();
        ipField.Text = room.Address;
        Net.Join(room.Address, PlayerName(), selectedCar);
    }

    void LeaveRoom()
    {
        race = null;
        Net.Stop();
        Net.StartBrowsing();
        state = GameState.Multiplayer;
    }

    void HostStart()
    {
        if (!Net.IsHost) return;
        SyncLobbySettings();
        Net.StartRace(selectedCar);
    }

    // Back from an online race to the room. Your car (and, for the host, the AI) leaves the track.
    void LeaveOnlineRace()
    {
        race?.Retire();
        race = null;
        state = Net.InRoom ? GameState.Lobby : GameState.Multiplayer;
    }

    // ---- Every frame

    void UpdateNetwork(float dt)
    {
        Net.Update(dt);

        if (Net.TakeLostHost())
        {
            race = null;
            state = GameState.Multiplayer;
            Net.StartBrowsing();
            return;
        }
        if (!Net.InRoom) return;

        if (Net.TakeStart() is StartInfo start)
        {
            StartOnlineRace(start);
            return;
        }
        bool inOnlineRace = race != null && race.Online && state is GameState.Racing or GameState.Paused or GameState.Results;
        if (!inOnlineRace) Net.DiscardRaceMessages();
    }

    void StartOnlineRace(StartInfo s)
    {
        var grid = new List<RaceEntry>();
        Car aiCar = cars[Math.Clamp(s.AiCar, 0, cars.Count - 1)];
        for (int i = 0; i < s.AiCount; i++)
            grid.Add(new RaceEntry(Race.AiNames[i], aiCar, Race.AiColors[i], Net.IsHost ? RacerKind.Ai : RacerKind.Remote, 100 + i));

        var carsTaken = new HashSet<int>();
        int paint = 0;
        foreach (LobbyPlayer p in s.Players)
        {
            int ci = Math.Clamp(p.Car, 0, cars.Count - 1);
            Car car = cars[ci];
            Color color = carsTaken.Add(ci) ? car.Color : PlayerColors[paint++ % PlayerColors.Length];
            grid.Add(new RaceEntry(p.Name, car, color, p.Id == Net.MyId ? RacerKind.Player : RacerKind.Remote, p.Id));
        }

        race = new Race(TrackDef.All[Math.Clamp(s.Track, 0, TrackDef.All.Length - 1)], grid, s.Laps,
            DifficultySkill[Math.Clamp(s.Difficulty, 0, 2)], manualGears, online: true);
        resultsDelay = 0;
        state = GameState.Racing;
    }

    void SyncLobbySettings()
    {
        LobbySettings l = Net.Lobby;
        if (l.Track != selectedTrack || l.Laps != laps || l.Opponents != opponents || l.Difficulty != difficulty)
        {
            l.Track = selectedTrack;
            l.Laps = laps;
            l.Opponents = opponents;
            l.Difficulty = difficulty;
            Net.SendLobby();
        }
    }

    void UpdateMultiplayer()
    {
        if (Net.InRoom)
        {
            Net.StopBrowsing();
            state = GameState.Lobby;
            return;
        }

        bool typing = TextField.AnyFocused;
        nameField.Update();
        ipField.Update();
        if (typing) return; // keys belong to the text box (Esc just finishes typing)

        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            CloseMultiplayer();
            return;
        }

        if (Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            List<Room> rooms = Net.Rooms();
            for (int i = 0; i < rooms.Count && i < 6; i++)
                if (Raylib.CheckCollisionPointRec(Ui.Mouse, RoomRow(i)))
                {
                    Audio.Click();
                    JoinRoom(rooms[i]);
                    return;
                }
        }
        foreach (Button b in mpButtons) b.Update();
    }

    void UpdateLobby()
    {
        if (!Net.InRoom)
        {
            state = GameState.Multiplayer;
            Net.StartBrowsing();
            return;
        }

        if (Net.IsHost)
        {
            opponents = Math.Min(opponents, Net.MaxOpponents);
            SyncLobbySettings();
            if (autoStartPlayers > 0 && Net.Lobby.Players.Count >= autoStartPlayers)
            {
                autoStartPlayers = 0;
                HostStart();
                return;
            }
        }
        else selectedTrack = Math.Clamp(Net.Lobby.Track, 0, TrackDef.All.Length - 1);
        Net.SetCar(selectedCar);

        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            LeaveRoom();
            return;
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Left) || Raylib.IsKeyPressed(KeyboardKey.A)) SelectCar(-1);
        if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressed(KeyboardKey.D)) SelectCar(1);
        if (Net.IsHost)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Tab))
                SelectTrack(Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift) ? -1 : 1);
            if (Raylib.IsKeyPressed(KeyboardKey.Enter))
            {
                HostStart();
                return;
            }
        }

        foreach (Button b in lobbyButtons) b.Update();
        if (Net.IsHost) foreach (Button b in lobbyHostButtons) b.Update();
    }

    // ---- Drawing

    static Rectangle RoomRow(int i) =>
        new(MpRight.X + 20, MpRight.Y + RoomRowY + i * (RoomRowH + RoomRowGap), MpRight.Width - 40, RoomRowH);

    void DrawMultiplayer()
    {
        DrawBackdrop(new Vector2(640, 380), Ui.Accent);
        float time = (float)Raylib.GetTime();

        // Left: name, host, join by address
        Rectangle p = MpLeft;
        Ui.GlassPanel(p, 18, new Color(14, 16, 22, 225));
        float x0 = p.X + 24, x1 = p.X + p.Width - 24, y0 = p.Y;
        Ui.Label("Your name", x0, y0 + 24, Ui.Muted);
        nameField.Draw();

        Raylib.DrawLineEx(new Vector2(x0, y0 + 118), new Vector2(x1, y0 + 118), 1, Ui.Stroke);
        Ui.Label("Host", x0, y0 + 136, Ui.Accent);
        Ui.DrawText("Open a room on this laptop.", x0, y0 + 156, 15, Ui.TextColor, Typeface.Body);
        Ui.DrawText("Friends on the same Wi-Fi can join it.", x0, y0 + 176, 15, Ui.Muted, Typeface.Body);

        Raylib.DrawLineEx(new Vector2(x0, y0 + 290), new Vector2(x1, y0 + 290), 1, Ui.Stroke);
        Ui.Label("Join by address", x0, y0 + 308, Ui.Accent);
        Ui.DrawText("Type the address shown in the host's lobby.", x0, y0 + 328, 15, Ui.Muted, Typeface.Body);
        ipField.Draw();

        if (Net.Status.Length > 0)
        {
            Color c = Net.StatusIsError ? Ui.Bad : Ui.TextColor;
            if (!Net.StatusIsError) Ui.Circle(new Vector2(x0 + 5, y0 + 441), 3 + MathF.Sin(time * 6), Ui.Accent);
            Ui.DrawText(Net.Status, x0 + (Net.StatusIsError ? 0 : 16), y0 + 432, 15, c, Typeface.Body);
        }
        Ui.DrawText("Hosting? If macOS asks about incoming connections, click Allow.", x0, y0 + p.Height - 36, 12, Ui.Dim, Typeface.Body);

        // Right: rooms found on the network
        Rectangle q = MpRight;
        Ui.GlassPanel(q, 18, new Color(14, 16, 22, 225));
        float rx0 = q.X + 24, rx1 = q.X + q.Width - 24;
        Ui.Label("Rooms on your network", rx0, q.Y + 24, Ui.Muted);
        string dots = new('.', 1 + (int)(time * 2.5f) % 3);
        Ui.TextRight("Searching" + dots.PadRight(3), rx1, q.Y + 20, 13, Ui.Dim, Typeface.Body);

        List<Room> rooms = Net.Rooms();
        if (rooms.Count == 0)
        {
            float cy = q.Y + q.Height / 2 - 40;
            Ui.TextCentered("NO ROOMS YET", q.X + q.Width / 2, cy, 34, Ui.Muted, Typeface.Display, 0.06f);
            Ui.TextCentered("Rooms show up here when someone on your Wi-Fi hosts one.", q.X + q.Width / 2, cy + 46, 14, Ui.Dim, Typeface.Body);
            Ui.TextCentered("Not showing? Join by address instead.", q.X + q.Width / 2, cy + 68, 14, Ui.Dim, Typeface.Body);
        }
        for (int i = 0; i < rooms.Count && i < 6; i++)
        {
            Room room = rooms[i];
            Rectangle r = RoomRow(i);
            bool hovered = Raylib.CheckCollisionPointRec(Ui.Mouse, r);
            Ui.RoundRect(r, 12, hovered ? new Color(255, 255, 255, 22) : new Color(255, 255, 255, 8));
            Ui.RoundOutline(r, 12, 1, hovered ? Ui.Alpha(Ui.Accent, 0.7f) : Ui.Stroke);
            Raylib.DrawRectangleRounded(new Rectangle(r.X + 14, r.Y + 14, 3, r.Height - 28), 1, 4, Ui.Accent);
            Ui.DrawText($"{room.Name.ToUpperInvariant()}'S ROOM", r.X + 30, r.Y + 10, 26, Ui.TextColor, Typeface.Display, 0.04f);
            Ui.DrawText($"{room.Players} {(room.Players == 1 ? "player" : "players")}  ·  {room.Address}", r.X + 30, r.Y + 38, 13, Ui.Muted, Typeface.Body);
            float pw = Ui.MeasureText("JOIN", 11, Typeface.Body, 0.16f) + 24;
            Ui.Pill("JOIN", r.X + r.Width - 16 - pw, r.Y + r.Height / 2 - 11, hovered ? Ui.Accent : new Color(255, 255, 255, 20), hovered ? new Color(24, 18, 6, 255) : Ui.TextColor);
        }

        foreach (Button b in mpButtons) b.Draw();
        Ui.KeyHints(640, 684, new[] { ("CLICK", "Join a room"), ("ESC", "Back") });
        Ui.End();
    }

    void DrawLobby()
    {
        Car myCar = cars[selectedCar];
        DrawBackdrop(new Vector2(360, 380), myCar.Color);
        LobbySettings l = Net.Lobby;
        float time = (float)Raylib.GetTime();

        // Left: who's in the room
        Rectangle p = LobbyLeft;
        Ui.GlassPanel(p, 18, new Color(14, 16, 22, 225));
        float x0 = p.X + 24, x1 = p.X + p.Width - 24, y0 = p.Y;
        Ui.Label("Players", x0, y0 + 22, Ui.Muted);
        Ui.LabelRight($"{l.Players.Count} / {Net.MaxCars}", x1, y0 + 22, Ui.Dim);

        LobbyPlayer? hostPlayer = l.Players.Find(pl => pl.Id == 0);
        for (int i = 0; i < l.Players.Count; i++)
        {
            LobbyPlayer pl = l.Players[i];
            Car car = cars[Math.Clamp(pl.Car, 0, cars.Count - 1)];
            bool me = pl.Id == Net.MyId;
            Rectangle row = new(x0 - 8, y0 + 46 + i * 46, x1 - x0 + 16, 40);
            Ui.RoundRect(row, 10, me ? Ui.Alpha(Ui.Accent, 0.12f) : new Color(255, 255, 255, 7));
            if (me) Ui.RoundOutline(row, 10, 1, Ui.Alpha(Ui.Accent, 0.4f));
            Raylib.DrawRectangleRounded(new Rectangle(row.X + 12, row.Y + 10, 3, 20), 1, 4, car.Color);
            Ui.DrawText(pl.Name.ToUpperInvariant(), row.X + 26, row.Y + 9, 24, Ui.TextColor, Typeface.Display, 0.04f);
            float tx = row.X + 34 + Ui.MeasureText(pl.Name.ToUpperInvariant(), 24, Typeface.Display, 0.04f);
            if (pl.Id == 0) tx += Ui.Pill("HOST", tx, row.Y + 9, Ui.Alpha(Ui.Accent, 0.9f), new Color(24, 18, 6, 255)) + 6;
            if (me) tx += Ui.Pill("YOU", tx, row.Y + 9, new Color(255, 255, 255, 24), Ui.TextColor) + 6;
            if (pl.Racing)
                Ui.Pill("ON TRACK", tx, row.Y + 9, Ui.Alpha(Ui.Warn, 0.25f), Ui.Warn);
            Ui.TextRight(car.Model, row.X + row.Width - 14, row.Y + 12, 15, Ui.Muted, Typeface.Body);
        }

        Raylib.DrawLineEx(new Vector2(x0, y0 + 432), new Vector2(x1, y0 + 432), 1, Ui.Stroke);
        if (Net.IsHost)
        {
            Ui.Label("Room address", x0, y0 + 448, Ui.Accent);
            Ui.DrawText(Net.LocalAddress(), x0, y0 + 466, 36, Ui.TextColor, Typeface.Display, 0.04f);
            Ui.DrawText("Friends open MULTIPLAYER and pick your room, or type this address.", x0, y0 + 504, 13, Ui.Muted, Typeface.Body);
        }
        else
        {
            Ui.Label("Connected to", x0, y0 + 448, Ui.Accent);
            Ui.DrawText($"{(hostPlayer?.Name ?? "HOST").ToUpperInvariant()}'S ROOM", x0, y0 + 466, 36, Ui.TextColor, Typeface.Display, 0.04f);
            Ui.DrawText(Net.HostAddress, x0, y0 + 504, 13, Ui.Muted, Typeface.Body);
        }

        // Right: your car, and the race the host has set up
        Rectangle q = LobbyRight;
        Ui.GlassPanel(q, 18, new Color(14, 16, 22, 225));
        float rx0 = q.X + 24, rx1 = q.X + q.Width - 24, ry0 = q.Y;
        float cx = (rx0 + rx1) / 2;
        Ui.Label("Your car", rx0, ry0 + 18, Ui.Muted);
        Ui.LabelCentered(myCar.Brand, cx, ry0 + LobbyCarY + 6, Ui.Alpha(Ui.Accent, 1));
        Ui.TextCentered(myCar.Model.ToUpperInvariant(), cx, ry0 + LobbyCarY + 22, 30, Ui.TextColor, Typeface.Display, 0.03f);

        DrawTrackCard(myCar, rx0, rx1, ry0 + LobbyTrackY);

        float sy = ry0 + LobbySettingsY;
        Raylib.DrawLineEx(new Vector2(rx0, sy - 22), new Vector2(rx1, sy - 22), 1, Ui.Stroke);
        Ui.Label(Net.IsHost ? "Race settings" : "Race settings  ·  set by the host", rx0, sy - 12, Ui.Muted);
        int lapsShown = Net.IsHost ? laps : l.Laps;
        int aiShown = Math.Min(Net.IsHost ? opponents : l.Opponents, Net.MaxOpponents);
        int diffShown = Math.Clamp(Net.IsHost ? difficulty : l.Difficulty, 0, 2);
        LobbySetting("Laps", lapsShown.ToString(), 0);
        LobbySetting("AI cars", aiShown.ToString(), 1);
        LobbySetting("Difficulty", DifficultyNames[diffShown].ToUpperInvariant(), 2);
        LobbySetting("Gearbox (yours)", manualGears ? "MANUAL" : "AUTO", 3);

        void LobbySetting(string label, string value, int row)
        {
            float y = sy + row * SettingsRowH;
            Ui.DrawText(label, rx0, y + 6, 16, Ui.TextColor, Typeface.Body);
            Ui.TextCentered(value, rx1 - 75, y + 4, 22, Ui.TextColor, Typeface.Display, 0.04f);
            if (row < 3) Raylib.DrawLineEx(new Vector2(rx0, y + SettingsRowH - 4), new Vector2(rx1, y + SettingsRowH - 4), 1, new Color(255, 255, 255, 10));
        }

        float by = q.Y + q.Height - 70;
        if (Net.IsHost)
        {
            int onTrack = l.Players.Count(pl => pl.Racing && pl.Id != 0);
            if (onTrack > 0)
                Ui.TextCentered($"{onTrack} still on track. Starting now pulls them into the new race.", cx, by - 24, 13, Ui.Warn, Typeface.Body);
            else if (l.Players.Count == 1)
                Ui.TextCentered("Waiting for friends to join. You can also start on your own.", cx, by - 24, 13, Ui.Muted, Typeface.Body);
        }
        else
        {
            LobbyPlayer? mine = l.Players.Find(pl => pl.Id == Net.MyId);
            bool hostRacing = hostPlayer?.Racing == true && mine?.Racing != true;
            string dots = new('.', 1 + (int)(time * 2.5f) % 3);
            string msg = hostRacing ? "A race is on. You'll join the next one" : $"Waiting for {hostPlayer?.Name ?? "the host"} to start";
            Rectangle wait = new(rx0, by, rx1 - rx0, 52);
            Ui.RoundRect(wait, 10, new Color(255, 255, 255, 8));
            Ui.RoundOutline(wait, 10, 1, Ui.Stroke);
            Ui.TextCentered((msg + dots.PadRight(3)).ToUpperInvariant(), cx, by + 15, 22, Ui.Muted, Typeface.Display, 0.06f);
        }

        foreach (Button b in lobbyButtons) b.Draw();
        if (Net.IsHost) foreach (Button b in lobbyHostButtons) b.Draw();

        Ui.KeyHints(640, 684, Net.IsHost
            ? new[] { ("A  D", "Choose car"), ("TAB", "Track"), ("ENTER", "Start race"), ("ESC", "Close room") }
            : new[] { ("A  D", "Choose car"), ("ESC", "Leave room") });
        Ui.End();
    }
}
