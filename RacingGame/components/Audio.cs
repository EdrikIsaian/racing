using Raylib_cs;

// What one car sounds like this frame
struct EngineVoice
{
    public float Rpm, Redline, Throttle;
    public int Cylinders;
    public float Growl;     // 0..1, how much low "burble" the engine has
    public float Volume;    // after distance falloff
    public float Pan;       // -1 left .. 1 right
    public float Skid;      // 0..1 tyre squeal
    public float Wind;      // 0..1 wind/road noise (player only)
    public float Gravel;    // 0..1 driving on grass
}

// All sound: generated effects, procedural music and a live engine mixer.
// Nothing is loaded from disk; every sound is synthesized when the game starts.
static unsafe class Audio
{
    public const int SampleRate = 44100;

    public static bool MusicMuted;
    public static bool Enabled { get; private set; }

    const float MusicVolume = 1.0f, SfxVolume = 0.4f, EngineVolume = 0.45f;

    // ---- music
    enum Track { None, Menu, Race }
    static Track wanted = Track.Menu;
    static Task<(short[] Menu, short[] Race)>? rendering;
    static readonly MusicSource?[] music = new MusicSource?[3];
    static readonly string[] MusicExtensions = { ".mp3", ".ogg", ".wav", ".flac" };

    // ---- effects
    static Sound beep, go, shift, crash, lap, finalLap, finish, click, whoosh;

    // ---- engines
    static AudioStream engineStream;
    const int EngineFrames = 1024;
    static readonly short[] engineBuffer = new short[EngineFrames * 2];
    static readonly EngineVoice[] voices = new EngineVoice[8];
    static readonly VoiceState[] states = new VoiceState[8];
    static int voiceCount;
    static float enginesGain; // fades engines in/out (e.g. when pausing)
    static bool enginesWanted;
    static uint noiseSeed = 0x9E3779B9;
    static readonly float[] mixL = new float[EngineFrames], mixR = new float[EngineFrames];

    struct VoiceState
    {
        public float Phase, HalfPhase, Lp, Rpm, Gain, Throttle, SkidGain, SkidPhase, SkidLfo, SkidLp, WindLp, WindGain, GravelLp, GravelGain;
    }

    // ------------------------------------------------------------- setup

    public static void Init()
    {
        Raylib.InitAudioDevice();
        Enabled = Raylib.IsAudioDeviceReady();
        if (!Enabled) return;

        Raylib.SetAudioStreamBufferSizeDefault(EngineFrames);
        engineStream = Raylib.LoadAudioStream(SampleRate, 16, 2);
        Raylib.PlayAudioStream(engineStream);

        beep = MakeSound(Tone(880, 0.16f, 0.5f, decay: 6));
        go = MakeSound(Tone(1320, 0.55f, 0.5f, decay: 3.5f));
        shift = MakeSound(ShiftPop());
        crash = MakeSound(CrashWave());
        lap = MakeSound(Chime(new[] { 76, 83 }, 0.12f));
        finalLap = MakeSound(Chime(new[] { 72, 76, 79, 84 }, 0.10f));
        finish = MakeSound(Chime(new[] { 72, 76, 79, 84, 88, 91 }, 0.09f, 1.1f));
        click = MakeSound(Tick());
        whoosh = MakeSound(WhooshWave());

        // Your own music files win over the generated tracks (see assets/music/README.txt):
        // background.* plays everywhere, menu.* / race.* only on that screen.
        MusicSource? background = LoadMusicFile("background");
        music[(int)Track.Menu] = LoadMusicFile("menu") ?? background;
        music[(int)Track.Race] = LoadMusicFile("race") ?? background; // shared, so it keeps playing across screens

        // Generated music takes a moment to synthesize, so do it in the background
        if (music[(int)Track.Menu] == null || music[(int)Track.Race] == null)
            rendering = Task.Run(() => (MusicGen.MenuLoop(), MusicGen.RaceLoop()));
    }

    public static void Shutdown()
    {
        if (!Enabled) return;
        foreach (var s in new[] { beep, go, shift, crash, lap, finalLap, finish, click, whoosh }) Raylib.UnloadSound(s);
        foreach (var m in music.Distinct()) m?.Unload();
        Raylib.UnloadAudioStream(engineStream);
        Raylib.CloseAudioDevice();
    }

    // ------------------------------------------------------------- per frame

    public static void Update(float dt)
    {
        if (!Enabled) return;

        if (rendering is { IsCompleted: true })
        {
            var (menu, race) = rendering.Result;
            rendering = null;
            Raylib.SetAudioStreamBufferSizeDefault(4096);
            music[(int)Track.Menu] ??= new MusicStream(menu);
            music[(int)Track.Race] ??= new MusicStream(race);
        }

        // Crossfade between the menu and race music
        for (int i = 1; i < music.Length; i++)
        {
            MusicSource? m = music[i];
            if (m == null || Array.IndexOf(music, m) != i) continue; // same source in two slots: update once
            bool active = music[(int)wanted] == m;
            float target = !MusicMuted && active ? MusicVolume : 0;
            m.Volume += (target - m.Volume) * (1 - MathF.Exp(-2.5f * dt));
            m.Update();
        }

        // Engines
        float gainTarget = enginesWanted ? 1 : 0;
        enginesGain += (gainTarget - enginesGain) * (1 - MathF.Exp(-8f * dt));
        enginesWanted = false;
        while (Raylib.IsAudioStreamProcessed(engineStream))
        {
            MixEngines();
            fixed (short* p = engineBuffer) Raylib.UpdateAudioStream(engineStream, p, EngineFrames);
        }
    }

    public static void PlayMenuMusic() => wanted = Track.Menu;
    public static void PlayRaceMusic() => wanted = Track.Race;

    // Call every frame while cars should be heard; if not called, the engines fade out
    public static void SetEngines(ReadOnlySpan<EngineVoice> list)
    {
        voiceCount = Math.Min(list.Length, voices.Length);
        list[..voiceCount].CopyTo(voices);
        enginesWanted = true;
    }

    // ------------------------------------------------------------- effects

    public static void Beep() => Play(beep, 0.6f);
    public static void Go() => Play(go, 0.7f);
    public static void Shift() => Play(shift, 0.55f, 0.9f + Random.Shared.NextSingle() * 0.2f);
    public static void Crash(float strength) => Play(crash, Math.Clamp(strength, 0.15f, 1f), 0.85f + Random.Shared.NextSingle() * 0.3f);
    public static void Lap() => Play(lap, 0.55f);
    public static void FinalLap() => Play(finalLap, 0.55f);
    public static void Finish() => Play(finish, 0.6f);
    public static void Click() => Play(click, 0.5f);
    public static void Whoosh() => Play(whoosh, 0.4f);

    static void Play(Sound s, float volume, float pitch = 1)
    {
        if (!Enabled) return;
        Raylib.SetSoundVolume(s, volume * SfxVolume);
        Raylib.SetSoundPitch(s, pitch);
        Raylib.PlaySound(s);
    }

    static Sound MakeSound(float[] samples)
    {
        var pcm = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++) pcm[i] = (short)(Math.Clamp(samples[i], -1f, 1f) * 32000);
        fixed (short* p = pcm)
        {
            var wave = new Wave { FrameCount = (uint)pcm.Length, SampleRate = SampleRate, SampleSize = 16, Channels = 1, Data = p };
            return Raylib.LoadSoundFromWave(wave); // copies the samples
        }
    }

    static float[] Tone(float freq, float length, float vol, float decay)
    {
        var s = new float[(int)(length * SampleRate)];
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)SampleRate;
            float env = MathF.Min(1, t * 200) * MathF.Exp(-t * decay) * MathF.Min(1, (length - t) * 50);
            float p = t * freq * MathF.Tau;
            s[i] = (MathF.Sin(p) + 0.25f * MathF.Sin(2 * p)) * env * vol;
        }
        return s;
    }

    // Exhaust "pop" + clunk of the gear lever
    static float[] ShiftPop()
    {
        var s = new float[(int)(0.18f * SampleRate)];
        float lp = 0;
        uint seed = 99;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)SampleRate;
            lp += 0.18f * (NoiseF(ref seed) - lp);
            float pop = lp * MathF.Exp(-t * 35) * 2.2f;
            float thunk = MathF.Sin(t * MathF.Tau * (120 - 300 * t)) * MathF.Exp(-t * 45) * 0.6f;
            s[i] = pop + thunk;
        }
        return s;
    }

    static float[] CrashWave()
    {
        var s = new float[(int)(0.45f * SampleRate)];
        float lp = 0, lp2 = 0;
        uint seed = 4242;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)SampleRate;
            float n = NoiseF(ref seed);
            lp += 0.25f * (n - lp);
            lp2 += 0.04f * (n - lp2);
            float body = MathF.Sin(t * MathF.Tau * (85 - 60 * t)) * MathF.Exp(-t * 12);
            float crunch = lp * MathF.Exp(-t * 16) * 0.8f + lp2 * MathF.Exp(-t * 6) * 1.5f;
            float rattle = (n > 0.97f ? 0.5f : 0) * MathF.Exp(-t * 9);
            s[i] = (body + crunch + rattle) * 0.8f;
        }
        return s;
    }

    static float[] Chime(int[] notes, float gap, float tail = 0.6f)
    {
        float length = gap * notes.Length + tail;
        var s = new float[(int)(length * SampleRate)];
        for (int k = 0; k < notes.Length; k++)
        {
            float f = 440 * MathF.Pow(2, (notes[k] - 69) / 12f);
            int start = (int)(k * gap * SampleRate);
            for (int i = start; i < s.Length; i++)
            {
                float t = (i - start) / (float)SampleRate;
                float p = t * f * MathF.Tau;
                s[i] += (MathF.Sin(p) + 0.3f * MathF.Sin(2 * p) + 0.1f * MathF.Sin(3 * p)) * MathF.Min(1, t * 300) * MathF.Exp(-t * 4.5f) * 0.28f;
            }
        }
        return s;
    }

    static float[] Tick()
    {
        var s = new float[(int)(0.05f * SampleRate)];
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)SampleRate;
            s[i] = MathF.Sin(t * MathF.Tau * 2200) * MathF.Exp(-t * 120) * 0.6f + MathF.Sin(t * MathF.Tau * 700) * MathF.Exp(-t * 80) * 0.4f;
        }
        return s;
    }

    static float[] WhooshWave()
    {
        var s = new float[(int)(0.35f * SampleRate)];
        float lp = 0;
        uint seed = 5;
        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)SampleRate;
            float x = t / 0.35f;
            float a = 0.02f + 0.25f * MathF.Sin(x * MathF.PI); // filter opens and closes
            lp += a * (NoiseF(ref seed) - lp);
            s[i] = lp * MathF.Sin(x * MathF.PI) * 1.2f;
        }
        return s;
    }

    static float NoiseF(ref uint s)
    {
        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
        return (s / (float)uint.MaxValue) * 2 - 1;
    }

    // ------------------------------------------------------------- engine mixer

    // Synthesizes one buffer of all engines, tyres, wind and gravel.
    // An engine is a set of harmonics of its firing frequency (RPM / 60 * cylinders / 2),
    // plus combustion noise pulses, through a filter that opens up with throttle.
    static void MixEngines()
    {
        const float sr = SampleRate;
        float smooth = 1 - MathF.Exp(-1f / (0.02f * sr)); // ~20 ms parameter smoothing
        Array.Clear(mixL);
        Array.Clear(mixR);

        for (int v = 0; v < voiceCount; v++)
        {
            EngineVoice voice = voices[v];
            ref VoiceState st = ref states[v];
            if (st.Rpm == 0) st.Rpm = voice.Rpm;

            float angle = (voice.Pan + 1) * MathF.PI / 4; // equal-power pan
            float gl = MathF.Cos(angle), gr = MathF.Sin(angle);
            float targetGain = voice.Volume * (0.35f + 0.65f * voice.Throttle) * (0.55f + 0.45f * voice.Rpm / voice.Redline);
            float cyl = voice.Cylinders / 2f;

            for (int i = 0; i < EngineFrames; i++)
            {
                st.Rpm += (voice.Rpm - st.Rpm) * smooth;
                st.Gain += (targetGain - st.Gain) * smooth;
                st.Throttle += (voice.Throttle - st.Throttle) * smooth;
                st.SkidGain += (voice.Skid * voice.Volume - st.SkidGain) * smooth;
                st.WindGain += (voice.Wind - st.WindGain) * smooth;
                st.GravelGain += (voice.Gravel - st.GravelGain) * smooth;

                float fire = st.Rpm / 60f * cyl;
                st.Phase += fire / sr;
                if (st.Phase >= 1) st.Phase -= 1;
                st.HalfPhase += fire * 0.5f / sr;
                if (st.HalfPhase >= 1) st.HalfPhase -= 1;
                float p = st.Phase * MathF.Tau;

                float tone = MathF.Sin(p) + 0.55f * MathF.Sin(2 * p + 0.4f) + 0.30f * MathF.Sin(3 * p + 1.1f) + 0.16f * MathF.Sin(4 * p + 0.2f)
                           + voice.Growl * 0.7f * MathF.Sin(st.HalfPhase * MathF.Tau);
                float pulse = MathF.Max(0, MathF.Sin(p));
                float combustion = NoiseF(ref noiseSeed) * pulse * pulse * (0.25f + 0.9f * st.Throttle);
                float x = tone * 0.45f + combustion;

                float cutoff = 350 + 2600 * st.Throttle + st.Rpm * 0.18f;
                st.Lp += (1 - MathF.Exp(-MathF.Tau * cutoff / sr)) * (x - st.Lp);
                float sample = st.Lp * st.Gain;

                // Tyre squeal: wobbling tone + hiss
                if (st.SkidGain > 0.001f)
                {
                    st.SkidLfo += 6.5f / sr;
                    st.SkidPhase += (1050 + 90 * MathF.Sin(st.SkidLfo * MathF.Tau)) / sr;
                    if (st.SkidPhase >= 1) st.SkidPhase -= 1;
                    st.SkidLp += 0.3f * (NoiseF(ref noiseSeed) - st.SkidLp);
                    sample += (MathF.Sin(st.SkidPhase * MathF.Tau) * 0.22f + st.SkidLp * 0.18f) * st.SkidGain;
                }
                // Wind / road roar
                if (st.WindGain > 0.001f)
                {
                    st.WindLp += 0.03f * (NoiseF(ref noiseSeed) - st.WindLp);
                    sample += st.WindLp * st.WindGain * 0.9f;
                }
                // Grass and gravel rumble
                if (st.GravelGain > 0.001f)
                {
                    float n = NoiseF(ref noiseSeed);
                    st.GravelLp += 0.12f * (n - st.GravelLp);
                    sample += (st.GravelLp * 0.6f + (n > 0.985f ? 0.4f : 0)) * st.GravelGain;
                }

                mixL[i] += sample * gl;
                mixR[i] += sample * gr;
            }
        }

        float g = EngineVolume * enginesGain;
        for (int i = 0; i < EngineFrames; i++)
        {
            float l = mixL[i] * g, r = mixR[i] * g;
            engineBuffer[i * 2] = (short)(l / (1 + MathF.Abs(l)) * 30000);     // soft clip
            engineBuffer[i * 2 + 1] = (short)(r / (1 + MathF.Abs(r)) * 30000);
        }
    }

    static MusicSource? LoadMusicFile(string name)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "assets", "music");
        foreach (string ext in MusicExtensions)
        {
            string path = Path.Combine(dir, name + ext);
            if (!File.Exists(path)) continue;
            Music m = Raylib.LoadMusicStream(path);
            if (Raylib.IsMusicValid(m)) return new FileMusic(m);
        }
        return null;
    }

    abstract class MusicSource
    {
        public float Volume;
        public abstract void Update();
        public abstract void Unload();
    }

    // A music file (mp3/ogg/wav/flac) streamed from disk, looping
    sealed class FileMusic : MusicSource
    {
        readonly Music music;

        public FileMusic(Music m)
        {
            music = m;
            music.Looping = true;
            Raylib.SetMusicVolume(music, 0);
            Raylib.PlayMusicStream(music);
        }

        public override void Update()
        {
            Raylib.SetMusicVolume(music, Volume);
            Raylib.UpdateMusicStream(music);
        }

        public override void Unload() => Raylib.UnloadMusicStream(music);
    }

    // A looping music track streamed from a pre-rendered buffer
    sealed class MusicStream : MusicSource
    {
        const int Chunk = 4096;
        readonly short[] pcm;      // interleaved stereo
        readonly short[] chunk = new short[Chunk * 2];
        readonly AudioStream stream;
        int position;              // in frames
        public MusicStream(short[] pcm)
        {
            this.pcm = pcm;
            stream = Raylib.LoadAudioStream(SampleRate, 16, 2);
            Raylib.SetAudioStreamVolume(stream, 0);
            Raylib.PlayAudioStream(stream);
        }

        public override void Update()
        {
            Raylib.SetAudioStreamVolume(stream, Volume);
            int frames = pcm.Length / 2;
            while (Raylib.IsAudioStreamProcessed(stream))
            {
                for (int i = 0; i < Chunk; i++)
                {
                    chunk[i * 2] = pcm[position * 2];
                    chunk[i * 2 + 1] = pcm[position * 2 + 1];
                    position = (position + 1) % frames;
                }
                fixed (short* p = chunk) Raylib.UpdateAudioStream(stream, p, Chunk);
            }
        }

        public override void Unload() => Raylib.UnloadAudioStream(stream);
    }
}
