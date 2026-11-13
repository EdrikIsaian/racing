using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Text;

// LAN multiplayer. One laptop hosts a room (TCP), the others connect to it directly.
// Every machine drives its own car and sends its state ~30x a second; the host passes
// each message on to everyone else and also drives the AI cars.
// Hosts announce their room on the local network (UDP broadcast) so it shows up in the join list.

enum Msg : byte
{
    Hello = 1, // client → host: version, name, car
    Welcome,   // host → client: your id
    Reject,    // host → client: reason
    Lobby,     // host → all: settings and players
    SetCar,    // client → host: car index
    Start,     // host → all: the race to set up
    State,     // anyone: one car's state (host relays)
    Retire,    // anyone: this car left the race (host relays)
}

// One car's state, sent by whoever drives it
struct CarState
{
    public int Id;
    public Vector2 Position, Velocity;
    public float Heading, Steer, Throttle, Rpm;
    public byte Gear;
    public bool Braking, Skidding, OnTrack, Reversing, Finished;
    public int TrackIndex, ProgressSamples, LapsDone;
    public float FinishTime, BestLap;

    public void Write(BinaryWriter w)
    {
        w.Write(Id);
        w.Write(Position.X); w.Write(Position.Y);
        w.Write(Velocity.X); w.Write(Velocity.Y);
        w.Write(Heading); w.Write(Steer); w.Write(Throttle); w.Write(Rpm);
        w.Write(Gear);
        w.Write((byte)((Braking ? 1 : 0) | (Skidding ? 2 : 0) | (OnTrack ? 4 : 0) | (Reversing ? 8 : 0) | (Finished ? 16 : 0)));
        w.Write(TrackIndex); w.Write(ProgressSamples); w.Write(LapsDone);
        w.Write(FinishTime); w.Write(BestLap);
    }

    public static CarState Read(BinaryReader r)
    {
        var s = new CarState
        {
            Id = r.ReadInt32(),
            Position = new Vector2(r.ReadSingle(), r.ReadSingle()),
            Velocity = new Vector2(r.ReadSingle(), r.ReadSingle()),
            Heading = r.ReadSingle(), Steer = r.ReadSingle(), Throttle = r.ReadSingle(), Rpm = r.ReadSingle(),
            Gear = r.ReadByte(),
        };
        byte f = r.ReadByte();
        s.Braking = (f & 1) != 0; s.Skidding = (f & 2) != 0; s.OnTrack = (f & 4) != 0; s.Reversing = (f & 8) != 0; s.Finished = (f & 16) != 0;
        s.TrackIndex = r.ReadInt32(); s.ProgressSamples = r.ReadInt32(); s.LapsDone = r.ReadInt32();
        s.FinishTime = r.ReadSingle(); s.BestLap = r.ReadSingle();
        return s;
    }
}

class LobbyPlayer
{
    public int Id;
    public string Name = "";
    public int Car;
    public bool Racing; // still out on track in the last race
}

class LobbySettings
{
    public int Track, Laps = 3, Opponents = 2, Difficulty = 1;
    public readonly List<LobbyPlayer> Players = new();
}

// Everything a machine needs to build the same grid. AI cars fill the front, then players in this order.
class StartInfo
{
    public int Track, Laps, Difficulty, AiCar, AiCount;
    public readonly List<LobbyPlayer> Players = new();
}

record Room(string Address, string Name, int Players, long SeenAt);

// One TCP connection. Messages are framed as [length: 2 bytes][type: 1 byte][body].
sealed class Peer
{
    readonly TcpClient tcp;
    readonly NetworkStream stream;
    readonly object sendLock = new();
    volatile bool closed;
    public int Id = -1;
    public readonly string Address;

    public Peer(TcpClient tcp, ConcurrentQueue<(Peer, byte[]?)> inbox)
    {
        this.tcp = tcp;
        tcp.NoDelay = true;    // small packets, send right away
        tcp.SendTimeout = 3000;
        stream = tcp.GetStream();
        Address = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString() ?? "?";
        new Thread(() => ReadLoop(inbox)) { IsBackground = true, Name = "net-read" }.Start();
    }

    void ReadLoop(ConcurrentQueue<(Peer, byte[]?)> inbox)
    {
        var header = new byte[2];
        try
        {
            while (!closed)
            {
                stream.ReadExactly(header);
                var data = new byte[header[0] | header[1] << 8];
                stream.ReadExactly(data);
                inbox.Enqueue((this, data));
            }
        }
        catch { }
        inbox.Enqueue((this, null)); // disconnected
    }

    public void Send(byte[] packet)
    {
        if (closed) return;
        try { lock (sendLock) stream.Write(packet); }
        catch { Close(); }
    }

    public void Close()
    {
        closed = true;
        try { tcp.Close(); } catch { }
    }
}

static class Net
{
    public const int Port = 47777, DiscoveryPort = 47778, Version = 1, MaxCars = 8;

    public static bool IsHost { get; private set; }
    public static bool Active => IsHost || host != null || attempt != null;
    public static bool InRoom => IsHost || (host != null && MyId >= 0);
    public static int MyId { get; private set; } = -1;
    public static string HostAddress { get; private set; } = "";
    public static string Status = "";   // what's happening / what went wrong, for the screens
    public static bool StatusIsError;
    public static readonly LobbySettings Lobby = new();

    static TcpListener? listener;
    static readonly List<Peer> peers = new();             // host: joined clients
    static readonly ConcurrentBag<Peer> allPeers = new(); // host: every connection, for cleanup
    static Peer? host;                                    // client: the connection to the host
    static ConcurrentQueue<(Peer, byte[]?)> inbox = new();
    static int nextId = 1;
    static string myName = "";
    static int myCar;

    static StartInfo? pendingStart;
    static readonly List<CarState> states = new();
    static readonly List<int> retired = new();
    static bool lostHost;

    // ---- Hosting

    public static bool Host(string name, int car)
    {
        Stop();
        try
        {
            listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();
        }
        catch (Exception e)
        {
            listener = null;
            SetStatus($"Couldn't open a room on port {Port} ({e.Message})", true);
            return false;
        }
        IsHost = true;
        MyId = 0;
        nextId = 1;
        Lobby.Players.Clear();
        Lobby.Players.Add(new LobbyPlayer { Id = 0, Name = CleanName(name), Car = car });
        SetStatus("", false);

        TcpListener l = listener;
        var q = inbox;
        new Thread(() =>
        {
            try
            {
                while (true)
                {
                    TcpClient c = l.AcceptTcpClient();
                    allPeers.Add(new Peer(c, q));
                }
            }
            catch { } // listener stopped
        }) { IsBackground = true, Name = "net-accept" }.Start();

        StartBeacon();
        return true;
    }

    // Host: settings changed, tell everyone
    public static void SendLobby()
    {
        if (!IsHost) return;
        byte[] packet = Pack(Msg.Lobby, w =>
        {
            w.Write(Lobby.Track); w.Write(Lobby.Laps); w.Write(Lobby.Opponents); w.Write(Lobby.Difficulty);
            w.Write((byte)Lobby.Players.Count);
            foreach (LobbyPlayer p in Lobby.Players)
            {
                w.Write(p.Id); w.Write(p.Name); w.Write(p.Car); w.Write(p.Racing);
            }
        });
        foreach (Peer p in peers) p.Send(packet);
    }

    public static int MaxOpponents => Math.Clamp(MaxCars - Lobby.Players.Count, 0, 5);

    // Host: everyone onto the grid
    public static void StartRace(int aiCar)
    {
        if (!IsHost) return;
        var s = new StartInfo
        {
            Track = Lobby.Track, Laps = Lobby.Laps, Difficulty = Lobby.Difficulty, AiCar = aiCar,
            AiCount = Math.Min(Lobby.Opponents, MaxOpponents),
        };
        foreach (LobbyPlayer p in Lobby.Players)
        {
            p.Racing = true;
            s.Players.Add(new LobbyPlayer { Id = p.Id, Name = p.Name, Car = p.Car });
        }
        byte[] packet = Pack(Msg.Start, w =>
        {
            w.Write(s.Track); w.Write(s.Laps); w.Write(s.Difficulty); w.Write(s.AiCar); w.Write(s.AiCount);
            w.Write((byte)s.Players.Count);
            foreach (LobbyPlayer p in s.Players) { w.Write(p.Id); w.Write(p.Name); w.Write(p.Car); }
        });
        foreach (Peer p in peers) p.Send(packet);
        SendLobby();
        DiscardRaceMessages();
        pendingStart = s;
    }

    // ---- Joining

    sealed class ConnectAttempt { public volatile TcpClient? Tcp; public volatile string? Error; }
    static ConnectAttempt? attempt;

    public static void Join(string address, string name, int car)
    {
        Stop();
        address = address.Trim();
        myName = CleanName(name);
        myCar = car;
        HostAddress = address;
        SetStatus($"Connecting to {address}…", false);
        var a = new ConnectAttempt();
        attempt = a;
        Task.Run(async () =>
        {
            var tcp = new TcpClient();
            try
            {
                using var cts = new CancellationTokenSource(4000);
                await tcp.ConnectAsync(address, Port, cts.Token);
                if (attempt == a) a.Tcp = tcp;
                else tcp.Close();
            }
            catch (OperationCanceledException) { tcp.Close(); a.Error = $"No room found at {address}"; }
            catch (Exception e) { tcp.Close(); a.Error = e is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused ? $"No room open at {address}" : $"Couldn't connect to {address}"; }
        });
    }

    public static void SetCar(int car)
    {
        if (IsHost)
        {
            if (Lobby.Players.Count > 0 && Lobby.Players[0].Car != car) { Lobby.Players[0].Car = car; SendLobby(); }
        }
        else if (host != null && myCar != car)
        {
            myCar = car;
            host.Send(Pack(Msg.SetCar, w => w.Write(car)));
        }
    }

    // ---- In the race

    public static void SendState(in CarState s)
    {
        CarState copy = s;
        byte[] packet = Pack(Msg.State, w => copy.Write(w));
        if (IsHost) foreach (Peer p in peers) p.Send(packet);
        else host?.Send(packet);
    }

    // This car is leaving the race (the driver went back to the lobby)
    public static void SendRetire(int id)
    {
        byte[] packet = Pack(Msg.Retire, w => w.Write(id));
        if (IsHost)
        {
            foreach (Peer p in peers) p.Send(packet);
            LobbyPlayer? me = Lobby.Players.Find(p => p.Id == id);
            if (me != null && me.Racing) { me.Racing = false; SendLobby(); }
        }
        else host?.Send(packet);
    }

    public static StartInfo? TakeStart() { var s = pendingStart; pendingStart = null; return s; }
    public static void TakeStates(List<CarState> into) { into.AddRange(states); states.Clear(); }
    public static void TakeRetired(List<int> into) { into.AddRange(retired); retired.Clear(); }
    public static void DiscardRaceMessages() { states.Clear(); retired.Clear(); }
    public static bool TakeLostHost() { bool b = lostHost; lostHost = false; return b; }

    // ---- Every frame (main thread)

    public static void Update(float dt)
    {
        if (attempt is ConnectAttempt a)
        {
            if (a.Error != null)
            {
                attempt = null;
                SetStatus(a.Error, true);
            }
            else if (a.Tcp != null)
            {
                attempt = null;
                host = new Peer(a.Tcp, inbox);
                host.Send(Pack(Msg.Hello, w => { w.Write(Version); w.Write(myName); w.Write(myCar); }));
                SetStatus("Joining the room…", false);
            }
        }

        if (IsHost) UpdateBeaconText();

        while (inbox.TryDequeue(out var item))
        {
            var (peer, data) = item;
            if (data == null) OnDisconnect(peer);
            else if (data.Length > 0)
            {
                try { Handle(peer, data); }
                catch { } // a malformed message: ignore it
            }
        }
    }

    static void Handle(Peer peer, byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        var type = (Msg)r.ReadByte();

        if (IsHost)
        {
            switch (type)
            {
                case Msg.Hello:
                {
                    int version = r.ReadInt32();
                    string name = CleanName(r.ReadString());
                    int car = r.ReadInt32();
                    string? reason = version != Version ? "The host is running a different version of the game"
                        : Lobby.Players.Count >= MaxCars ? "That room is full" : null;
                    if (reason != null)
                    {
                        peer.Send(Pack(Msg.Reject, w => w.Write(reason)));
                        peer.Close();
                        return;
                    }
                    peer.Id = nextId++;
                    peers.Add(peer);
                    Lobby.Players.Add(new LobbyPlayer { Id = peer.Id, Name = UniqueName(name), Car = car });
                    Lobby.Opponents = Math.Min(Lobby.Opponents, MaxOpponents);
                    peer.Send(Pack(Msg.Welcome, w => w.Write(peer.Id)));
                    SendLobby();
                    break;
                }
                case Msg.SetCar:
                {
                    LobbyPlayer? p = Lobby.Players.Find(x => x.Id == peer.Id);
                    if (p != null) { p.Car = r.ReadInt32(); SendLobby(); }
                    break;
                }
                case Msg.State:
                {
                    CarState s = CarState.Read(r);
                    if (s.Id != peer.Id) return; // clients only drive their own car
                    Relay(peer, data);
                    states.Add(s);
                    break;
                }
                case Msg.Retire:
                {
                    int id = r.ReadInt32();
                    if (id != peer.Id) return;
                    Relay(peer, data);
                    retired.Add(id);
                    LobbyPlayer? p = Lobby.Players.Find(x => x.Id == id);
                    if (p != null) { p.Racing = false; SendLobby(); }
                    break;
                }
            }
            return;
        }

        if (peer != host) return;
        switch (type)
        {
            case Msg.Welcome:
                MyId = r.ReadInt32();
                SetStatus("", false);
                break;
            case Msg.Reject:
                string reason = r.ReadString();
                CloseAll();
                SetStatus(reason, true);
                break;
            case Msg.Lobby:
            {
                Lobby.Track = r.ReadInt32(); Lobby.Laps = r.ReadInt32(); Lobby.Opponents = r.ReadInt32(); Lobby.Difficulty = r.ReadInt32();
                Lobby.Players.Clear();
                int n = r.ReadByte();
                for (int i = 0; i < n; i++)
                    Lobby.Players.Add(new LobbyPlayer { Id = r.ReadInt32(), Name = r.ReadString(), Car = r.ReadInt32(), Racing = r.ReadBoolean() });
                break;
            }
            case Msg.Start:
            {
                var s = new StartInfo { Track = r.ReadInt32(), Laps = r.ReadInt32(), Difficulty = r.ReadInt32(), AiCar = r.ReadInt32(), AiCount = r.ReadInt32() };
                int n = r.ReadByte();
                for (int i = 0; i < n; i++)
                    s.Players.Add(new LobbyPlayer { Id = r.ReadInt32(), Name = r.ReadString(), Car = r.ReadInt32() });
                DiscardRaceMessages();
                pendingStart = s;
                break;
            }
            case Msg.State:
                states.Add(CarState.Read(r));
                break;
            case Msg.Retire:
                retired.Add(r.ReadInt32());
                break;
        }
    }

    // Host: pass a client's message on to every other client
    static void Relay(Peer from, byte[] data)
    {
        byte[] packet = Frame(data);
        foreach (Peer p in peers)
            if (p != from) p.Send(packet);
    }

    static void OnDisconnect(Peer peer)
    {
        if (IsHost)
        {
            if (!peers.Remove(peer)) return;
            Lobby.Players.RemoveAll(p => p.Id == peer.Id);
            retired.Add(peer.Id);
            byte[] packet = Pack(Msg.Retire, w => w.Write(peer.Id));
            foreach (Peer p in peers) p.Send(packet);
            SendLobby();
        }
        else if (peer == host)
        {
            bool wasInRoom = MyId >= 0;
            CloseAll();
            if (!StatusIsError) SetStatus(wasInRoom ? "Lost connection to the host" : "The host closed the connection", true);
            lostHost = wasInRoom;
        }
    }

    // Leave the room (or close it, when hosting)
    public static void Stop()
    {
        CloseAll();
        SetStatus("", false);
    }

    static void CloseAll()
    {
        attempt = null;
        try { listener?.Stop(); } catch { }
        listener = null;
        foreach (Peer p in allPeers) p.Close();
        allPeers.Clear();
        peers.Clear();
        host?.Close();
        host = null;
        beacon?.Dispose();
        beacon = null;
        IsHost = false;
        MyId = -1;
        Lobby.Players.Clear();
        pendingStart = null;
        DiscardRaceMessages();
        inbox = new ConcurrentQueue<(Peer, byte[]?)>(); // anything still arriving from old connections is ignored
    }

    static void SetStatus(string text, bool error)
    {
        Status = text;
        StatusIsError = error;
    }

    // ---- Finding rooms on the local network

    // Looking up network interfaces is slow on macOS, so it and the broadcasting run on their own thread,
    // never in the game loop. The game only reads the results.
    static UdpClient? beacon;
    static volatile string beaconText = "";
    static volatile string localAddress = "…";

    static void StartBeacon()
    {
        UpdateBeaconText();
        localAddress = "…";
        UdpClient b;
        try { b = new UdpClient { EnableBroadcast = true }; }
        catch { return; }
        beacon = b;
        new Thread(() =>
        {
            localAddress = FindLocalAddress();
            List<IPAddress> targets = BroadcastAddresses();
            try
            {
                while (beacon == b)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(beaconText);
                    foreach (IPAddress addr in targets)
                    {
                        try { b.Send(bytes, bytes.Length, new IPEndPoint(addr, DiscoveryPort)); }
                        catch (ObjectDisposedException) { return; }
                        catch { }
                    }
                    Thread.Sleep(1000);
                }
            }
            catch { }
        }) { IsBackground = true, Name = "net-beacon" }.Start();
    }

    static void UpdateBeaconText()
    {
        string name = Lobby.Players.Count > 0 ? Lobby.Players[0].Name : "Room";
        beaconText = $"RCG|{Version}|{name}|{Lobby.Players.Count}";
    }

    static UdpClient? browser;
    static readonly ConcurrentDictionary<string, Room> rooms = new();

    public static void StartBrowsing()
    {
        if (browser != null) return;
        rooms.Clear();
        try
        {
            var u = new UdpClient();
            u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            u.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            browser = u;
            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] b = u.Receive(ref from);
                        string[] parts = Encoding.UTF8.GetString(b).Split('|');
                        if (parts.Length < 4 || parts[0] != "RCG" || parts[1] != Version.ToString()) continue;
                        string ip = from.Address.MapToIPv4().ToString();
                        rooms[ip] = new Room(ip, parts[2], int.TryParse(parts[3], out int n) ? n : 1, Environment.TickCount64);
                    }
                }
                catch { } // stopped
            }) { IsBackground = true, Name = "net-browse" }.Start();
        }
        catch { browser = null; } // port busy: joining by address still works
    }

    public static void StopBrowsing()
    {
        browser?.Dispose();
        browser = null;
    }

    public static List<Room> Rooms()
    {
        long now = Environment.TickCount64;
        return rooms.Values.Where(r => now - r.SeenAt < 3500).OrderBy(r => r.Name).ToList();
    }

    // ---- Addresses

    static IEnumerable<UnicastIPAddressInformation> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)
                        && !a.Address.ToString().StartsWith("169.254."));

    // This laptop's address on the local network, for others to type in ("…" until it's been looked up)
    public static string LocalAddress() => localAddress;

    static string FindLocalAddress()
    {
        try
        {
            var all = LanAddresses().Select(a => a.Address.ToString()).ToList();
            return all.FirstOrDefault(a => a.StartsWith("192.168.")) ?? all.FirstOrDefault(a => a.StartsWith("10.")) ?? all.FirstOrDefault() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    static List<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var a in LanAddresses())
            {
                byte[] ip = a.Address.GetAddressBytes(), mask = a.IPv4Mask.GetAddressBytes();
                if (mask.Length != 4) continue;
                for (int i = 0; i < 4; i++) ip[i] = (byte)(ip[i] | ~mask[i]);
                list.Add(new IPAddress(ip));
            }
        }
        catch { }
        return list;
    }

    // ---- Helpers

    public static string CleanName(string name)
    {
        name = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').ToArray()).Trim();
        if (name.Length > 14) name = name[..14].Trim();
        return name.Length == 0 ? "Player" : name;
    }

    static string UniqueName(string name)
    {
        string n = name;
        for (int i = 2; Lobby.Players.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); i++) n = $"{name} {i}";
        return n;
    }

    static byte[] Pack(Msg type, Action<BinaryWriter>? body = null)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write((ushort)0); // length, filled in below
            w.Write((byte)type);
            body?.Invoke(w);
        }
        byte[] b = ms.ToArray();
        int len = b.Length - 2;
        b[0] = (byte)len;
        b[1] = (byte)(len >> 8);
        return b;
    }

    static byte[] Frame(byte[] payload)
    {
        var b = new byte[payload.Length + 2];
        b[0] = (byte)payload.Length;
        b[1] = (byte)(payload.Length >> 8);
        payload.CopyTo(b, 2);
        return b;
    }
}
