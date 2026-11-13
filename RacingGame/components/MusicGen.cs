// Procedural music: two original loops (menu + race) synthesized in code at startup.
// Every note is rendered straight into a looping buffer; tails that run past the end wrap
// round to the start, so the loop is seamless.
static class MusicGen
{
    const int Rate = Audio.SampleRate;

    sealed class Mix
    {
        public readonly float[] L, R;
        public int Frames => L.Length;
        public Mix(int frames) { L = new float[frames]; R = new float[frames]; }

        public void Add(int frame, float v, float pan = 0)
        {
            int i = ((frame % Frames) + Frames) % Frames;
            L[i] += v * (1 - pan) * 0.5f * 1.4f;
            R[i] += v * (1 + pan) * 0.5f * 1.4f;
        }
    }

    static float Freq(int midi) => 440f * MathF.Pow(2, (midi - 69) / 12f);

    // ------------------------------------------------------------- instruments

    static void Kick(Mix m, int at, float vol)
    {
        float phase = 0;
        int n = (int)(0.32f * Rate);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float f = 45 + 110 * MathF.Exp(-t * 28);
            phase += f / Rate;
            float env = MathF.Exp(-t * 9);
            float click = i < 90 ? (1 - i / 90f) * 0.5f : 0;
            m.Add(at + i, (MathF.Sin(phase * MathF.Tau) * env + click) * vol);
        }
    }

    static void Snare(Mix m, int at, float vol, ref uint seed)
    {
        int n = (int)(0.22f * Rate);
        float lp = 0, phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = Noise(ref seed);
            lp += 0.35f * (noise - lp);
            float hp = noise - lp;
            phase += 185f / Rate;
            float v = hp * MathF.Exp(-t * 18) * 0.8f + MathF.Sin(phase * MathF.Tau) * MathF.Exp(-t * 30) * 0.5f;
            m.Add(at + i, v * vol);
        }
    }

    static void Hat(Mix m, int at, float vol, float length, float pan, ref uint seed)
    {
        int n = (int)(length * 4 * Rate);
        float lp = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = Noise(ref seed);
            lp += 0.6f * (noise - lp);
            m.Add(at + i, (noise - lp) * MathF.Exp(-t / length) * vol, pan);
        }
    }

    // Plucked/saw bass with a closing filter
    static void Bass(Mix m, int at, int midi, float length, float vol)
    {
        float f = Freq(midi), phase = 0, lp = 0;
        int n = (int)((length + 0.05f) * Rate);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            phase = (phase + f / Rate) % 1f;
            float saw = 2 * phase - 1;
            float cutoff = 180 + 1400 * MathF.Exp(-t * 14);
            lp += (1 - MathF.Exp(-MathF.Tau * cutoff / Rate)) * (saw - lp);
            float env = MathF.Min(1, t * 400) * (t < length ? 1 : MathF.Exp(-(t - length) * 60));
            m.Add(at + i, (lp + MathF.Sin(phase * MathF.Tau) * 0.6f) * env * vol);
        }
    }

    // Soft square-ish pluck for arpeggios and leads
    static void Pluck(Mix m, int at, int midi, float decay, float vol, float pan, float brightness = 1)
    {
        float f = Freq(midi), phase = 0, lp = 0;
        int n = (int)(decay * 5 * Rate);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            phase = (phase + f / Rate) % 1f;
            float p = phase * MathF.Tau;
            float sq = MathF.Sin(p) + MathF.Sin(3 * p) / 3 * brightness + MathF.Sin(5 * p) / 5 * brightness * 0.7f;
            float cutoff = 900 + 4000 * brightness * MathF.Exp(-t * 10);
            lp += (1 - MathF.Exp(-MathF.Tau * cutoff / Rate)) * (sq - lp);
            float env = MathF.Min(1, t * 300) * MathF.Exp(-t / decay);
            m.Add(at + i, lp * env * vol, pan);
        }
    }

    // Warm pad: two detuned saws per note, slow swell, gentle low-pass
    static void Pad(Mix m, int at, int midi, float length, float vol, float cutoff)
    {
        float f = Freq(midi);
        float p1 = 0, p2 = 0.37f, lpL = 0, lpR = 0;
        float a = 1 - MathF.Exp(-MathF.Tau * cutoff / Rate);
        int n = (int)((length + 0.8f) * Rate);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            p1 = (p1 + f * 1.004f / Rate) % 1f;
            p2 = (p2 + f * 0.996f / Rate) % 1f;
            lpL += a * ((2 * p1 - 1) - lpL);
            lpR += a * ((2 * p2 - 1) - lpR);
            float env = MathF.Min(1, t / 0.5f) * (t < length ? 1 : MathF.Exp(-(t - length) * 5));
            int idx = at + i;
            m.Add(idx, lpL * env * vol, -0.6f);
            m.Add(idx, lpR * env * vol, 0.6f);
        }
    }

    static float Noise(ref uint s)
    {
        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
        return (s / (float)uint.MaxValue) * 2 - 1;
    }

    // Circular feedback echo (two passes so the wrapped tail settles)
    static void Echo(Mix m, int delay, float feedback, float wet)
    {
        int n = m.Frames;
        var dl = new float[n];
        var dr = new float[n];
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
            {
                int j = (i - delay + n) % n;
                dl[i] = m.L[i] + dr[j] * feedback; // ping-pong: left echo feeds right
                dr[i] = m.R[i] + dl[j] * feedback;
            }
        for (int i = 0; i < n; i++)
        {
            m.L[i] += (dl[i] - m.L[i]) * wet;
            m.R[i] += (dr[i] - m.R[i]) * wet;
        }
    }

    static void AddInto(Mix dst, Mix src, float vol)
    {
        for (int i = 0; i < dst.Frames; i++) { dst.L[i] += src.L[i] * vol; dst.R[i] += src.R[i] * vol; }
    }

    // Normalise, soft-clip and convert to interleaved 16-bit stereo
    static short[] Finish(Mix m, float level)
    {
        float peak = 0.0001f;
        for (int i = 0; i < m.Frames; i++) peak = MathF.Max(peak, MathF.Max(MathF.Abs(m.L[i]), MathF.Abs(m.R[i])));
        float g = level / peak * 1.6f;
        var pcm = new short[m.Frames * 2];
        for (int i = 0; i < m.Frames; i++)
        {
            pcm[i * 2] = (short)(MathF.Tanh(m.L[i] * g) * 32000);
            pcm[i * 2 + 1] = (short)(MathF.Tanh(m.R[i] * g) * 32000);
        }
        return pcm;
    }

    // ------------------------------------------------------------- songs

    // Driving loop: 128 BPM in A minor, four-on-the-floor, rolling bass, arpeggios, a lead in the second half
    public static short[] RaceLoop()
    {
        const float bpm = 128;
        float beat = 60f / bpm;
        int beatF = (int)(beat * Rate), stepF = beatF / 4; // 16th notes
        const int bars = 16;
        var mix = new Mix(beatF * 4 * bars);
        var arp = new Mix(mix.Frames);
        uint seed = 12345;

        int[] roots = { 45, 41, 48, 43 };                       // A F C G
        int[][] chords = { new[] { 57, 60, 64 }, new[] { 53, 57, 60 }, new[] { 60, 64, 67 }, new[] { 55, 59, 62 } };
        int[] bassPattern = { 0, 0, 12, 0, 0, 12, 0, 7 };
        int[] arpPattern = { 0, 1, 2, 3, 2, 1, 2, 3, 0, 1, 2, 3, 4, 3, 2, 1 };

        // Lead melody for the second half (16th-step, midi, length in steps) per chord
        (int, int, int)[][] lead =
        {
            new[] { (0, 76, 4), (6, 74, 2), (8, 72, 4), (12, 74, 4) },
            new[] { (0, 72, 6), (6, 69, 2), (8, 72, 4), (12, 77, 4) },
            new[] { (0, 76, 4), (4, 79, 4), (8, 76, 4), (12, 72, 4) },
            new[] { (0, 74, 6), (6, 71, 2), (8, 74, 4), (12, 79, 4) },
        };

        for (int bar = 0; bar < bars; bar++)
        {
            int c = bar % 4;
            int barF = bar * 4 * beatF;
            bool second = bar >= 8;

            for (int b = 0; b < 4; b++)
            {
                int at = barF + b * beatF;
                Kick(mix, at, 0.9f);
                if (b % 2 == 1) Snare(mix, at, 0.45f, ref seed);
                Hat(mix, at + beatF / 2, b == 3 ? 0.20f : 0.13f, b == 3 ? 0.09f : 0.03f, 0.3f, ref seed);
                if (second) Hat(mix, at + beatF / 4, 0.06f, 0.02f, -0.3f, ref seed);
            }

            for (int e = 0; e < 8; e++)
                Bass(mix, barF + e * beatF / 2, roots[c] + bassPattern[e] - 12, beat / 2 * 0.8f, 0.30f);

            for (int s = 0; s < 16; s++)
            {
                int k = arpPattern[s];
                int note = chords[c][k % 3] + 12 * (k / 3) + 12;
                Pluck(arp, barF + s * stepF, note, 0.09f, 0.10f, s % 2 == 0 ? -0.35f : 0.35f, second ? 1f : 0.6f);
            }

            if (second)
            {
                foreach (int n in chords[c]) Pad(mix, barF, n, beat * 4, 0.05f, 1400);
                foreach (var (step, note, len) in lead[c])
                    Pluck(mix, barF + step * stepF, note, len * stepF / (float)Rate * 0.9f, 0.16f, 0, 0.8f);
            }
        }

        Echo(arp, stepF * 3, 0.42f, 0.5f);
        AddInto(mix, arp, 1f);
        return Finish(mix, 0.85f);
    }

    // Menu loop: 92 BPM, dreamy seventh chords, slow arpeggio with long echoes, light beat
    public static short[] MenuLoop()
    {
        const float bpm = 92;
        float beat = 60f / bpm;
        int beatF = (int)(beat * Rate), stepF = beatF / 4;
        const int bars = 16;
        var mix = new Mix(beatF * 4 * bars);
        var arp = new Mix(mix.Frames);
        uint seed = 777;

        int[][] chords = { new[] { 53, 57, 60, 64 }, new[] { 52, 55, 59, 62 }, new[] { 50, 53, 57, 60 }, new[] { 48, 52, 55, 59 } };
        int[] arpOrder = { 0, 2, 1, 3, 2, 1, 3, 2 };

        for (int bar = 0; bar < bars; bar++)
        {
            int c = (bar / 2) % 4;
            int barF = bar * 4 * beatF;

            if (bar % 2 == 0)
            {
                foreach (int n in chords[c]) Pad(mix, barF, n, beat * 8 - 0.1f, 0.055f, 900);
                Bass(mix, barF, chords[c][0] - 12, beat * 7.5f, 0.16f);
            }

            Kick(mix, barF, 0.55f);
            Kick(mix, barF + beatF * 2 + beatF / 2, 0.35f);
            Snare(mix, barF + beatF * 1, 0.12f, ref seed);
            Snare(mix, barF + beatF * 3, 0.18f, ref seed);
            for (int e = 0; e < 8; e++)
                Hat(mix, barF + e * beatF / 2 + (e % 2 == 1 ? stepF / 3 : 0), e % 2 == 1 ? 0.07f : 0.045f, 0.025f, 0.25f, ref seed);

            for (int e = 0; e < 8; e++)
            {
                int k = arpOrder[e];
                Pluck(arp, barF + e * 2 * stepF, chords[c][k] + 12, 0.22f, 0.09f, e % 2 == 0 ? -0.4f : 0.4f, 0.5f);
            }
        }

        Echo(arp, stepF * 3, 0.55f, 0.6f);
        AddInto(mix, arp, 1f);
        return Finish(mix, 0.75f);
    }
}
