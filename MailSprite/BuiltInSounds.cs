using System.Collections.Concurrent;
using System.IO;
using System.Media;
using System.Text;
using NAudio.Wave;

namespace MailSprite;

/// <summary>
/// All three sounds are synthesized from code using mathematical waveforms,
/// without downloaded assets, third-party accounts, or ongoing fees.
/// These PCM WAV buffers are original and bundled by compilation.
/// </summary>
internal static class BuiltInSounds
{
    private const int SampleRate = 44100;
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

    // Expose the exact generated/bundled PCM WAV for single-stream playback.
    // The notification chime and WAV announcement are then played by one
    // SoundPlayer instead of separate playback sessions on different devices.
    public static byte[] GetWav(string kind) => Cache.GetOrAdd(kind, Generate);

    // Standalone tray-menu samples use the SAME device warm-up as an
    // incoming-mail alert. Otherwise previews could clip on some laptops.
    public static void Play(string kind)
    {
        if (kind == "Off") return;
        _ = Task.Run(() => AudioSequence.Play(kind, ""));
    }

    // Used by the ordered notification pipeline: do NOT fire-and-forget.
    public static void PlaySync(string kind)
    {
        if (kind == "Off") return;
        try
        {
            byte[] data = GetWav(kind);
            using var stream = new MemoryStream(data, writable: false);
            using var player = new SoundPlayer(stream);
            player.PlaySync();
        }
        catch { /* Audio is optional; never block a mail notification. */ }
    }

    private static byte[] Generate(string kind)
    {
        // The supplied chime recording is compiled into the EXE. It is
        // decoded to PCM WAV once, then played synchronously with SoundPlayer
        // so the selected WAV announcement begins only after it finishes.
        if (kind == "UserChime") return DecodeIncludedChime();

        double duration = kind switch
        {
            "PaperFlick" => 0.48,
            "ClearBell" => 1.10,
            "SlowClang" => 2.70,
            _ => 0.92
        };
        int count = (int)(duration * SampleRate);
        var wave = new short[count];
        uint noiseSeed = 0x1D4C9BAFu;

        for (int i = 0; i < count; i++)
        {
            double t = (double)i / SampleRate;
            double signal;
            if (kind == "SlowClang")
            {
                // Three mellow metallic strikes: synthesized from the same
                // fundamental and inharmonic partials as the earlier slow
                // bell effect. Keep the ringing tails between strikes.
                signal = SlowBellStrike(t, 0.20, 0.48)
                    + SlowBellStrike(t, 1.10, 0.52)
                    + SlowBellStrike(t, 1.88, 0.66);
            }
            else if (kind == "PaperFlick")
            {
                // Soft paper rustle with a quiet low-pitched tap.
                noiseSeed ^= noiseSeed << 13;
                noiseSeed ^= noiseSeed >> 17;
                noiseSeed ^= noiseSeed << 5;
                double noise = ((noiseSeed & 0xFFFF) / 32767.5) - 1.0;
                double rustle = noise * Math.Exp(-18 * t) * 0.19;
                double second = Math.Max(0, t - 0.13);
                rustle += noise * Math.Exp(-30 * second) * 0.06
                    * (t >= 0.13 ? 1.0 : 0.0);
                double thud = Math.Sin(2 * Math.PI * 184 * t)
                    * Math.Exp(-25 * t) * 0.12;
                signal = rustle + thud;
            }
            else if (kind == "ClearBell")
            {
                // Bell partials with slightly inharmonic frequencies.
                double bell = Math.Sin(2 * Math.PI * 783.99 * t) * 0.31
                    + Math.Sin(2 * Math.PI * 1567.98 * t) * 0.12
                    + Math.Sin(2 * Math.PI * 2365.7 * t) * 0.06;
                signal = bell * (1 - Math.Exp(-100 * t)) * Math.Exp(-3.4 * t);
                if (t >= 0.23)
                {
                    double tt = t - 0.23;
                    signal += (Math.Sin(2 * Math.PI * 1046.50 * tt) * 0.23 +
                               Math.Sin(2 * Math.PI * 2093.00 * tt) * 0.06)
                        * (1 - Math.Exp(-110 * tt)) * Math.Exp(-4.2 * tt);
                }
            }
            else
            {
                // Two warm, quiet notes, no synthetic speech.
                signal = WarmNote(t, 523.25, 0.0, 0.29)
                    + WarmNote(t, 659.25, 0.20, 0.34)
                    + WarmNote(t, 783.99, 0.38, 0.22);
            }
            // Remove hard start/end discontinuities and leave enough headroom.
            double fadeOut = Math.Min(1, (duration - t) / 0.05);
            signal *= Math.Max(0, fadeOut);
            wave[i] = (short)(Math.Clamp(signal, -0.95, 0.95) * 32767);
        }

        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true))
        {
            int dataSize = count * sizeof(short);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1); // Linear PCM
            writer.Write((short)1); // Mono
            writer.Write(SampleRate);
            writer.Write(SampleRate * sizeof(short));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            foreach (short sample in wave) writer.Write(sample);
        }
        return output.ToArray();
    }

    private static double WarmNote(double time, double frequency,
                                   double onset, double strength)
    {
        if (time < onset) return 0;
        double t = time - onset;
        double attack = 1 - Math.Exp(-62 * t);
        double falloff = Math.Exp(-5.1 * t);
        double fundamental = Math.Sin(2 * Math.PI * frequency * t);
        double overtone = 0.24 * Math.Sin(2 * Math.PI * frequency * 2 * t);
        return (fundamental + overtone) * strength * attack * falloff;
    }
    private static byte[] DecodeIncludedChime()
    {
        using Stream compressed =
            typeof(BuiltInSounds).Assembly.GetManifestResourceStream("MailSprite.Assets.Chime.mp3")
            ?? throw new FileNotFoundException("The bundled chime is missing.");
        using var reader = new Mp3FileReader(compressed);
        using var wav = new MemoryStream();
        WaveFileWriter.WriteWavFileToStream(wav, reader);
        return wav.ToArray();
    }

    private static double SlowBellStrike(double time, double start, double volume)
    {
        if (time < start) return 0;
        double t = time - start;
        double attack = 1 - Math.Exp(-90 * t);
        double decay = Math.Exp(-2.8 * t);
        double shimmer = Math.Exp(-6.5 * t);
        // Deliberately inharmonic frequencies produce bell-like resonance
        // instead of the pure sine of a digital beep.
        double resonance =
            Math.Sin(2 * Math.PI * 280.0 * t) * 0.26
            + Math.Sin(2 * Math.PI * 558.7 * t) * 0.38
            + Math.Sin(2 * Math.PI * 843.1 * t) * 0.19
            + Math.Sin(2 * Math.PI * 1126.5 * t) * 0.11
            + Math.Sin(2 * Math.PI * 669.5 * t) * 0.13
            + Math.Sin(2 * Math.PI * 1343.7 * t) * 0.055
            + Math.Sin(2 * Math.PI * 1693.0 * t) * 0.025;
        double transient = Math.Sin(2 * Math.PI * 2312.5 * t)
            * shimmer * 0.04;
        return volume * attack * decay * (resonance + transient);
    }

}
