using System.Collections.Concurrent;
using System.IO;
using System.Media;
using System.Text;

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

    public static void Play(string kind)
    {
        if (kind == "Off") return;
        _ = Task.Run(() =>
        {
            try
            {
                byte[] data = Cache.GetOrAdd(kind, Generate);
                using var stream = new MemoryStream(data, writable: false);
                using var player = new SoundPlayer(stream);
                player.PlaySync();
            }
            catch { /* Audio is optional; never block a mail notification. */ }
        });
    }

    private static byte[] Generate(string kind)
    {
        double duration = kind switch
        {
            "PaperFlick" => 0.48,
            "ClearBell" => 1.10,
            _ => 0.92
        };
        int count = (int)(duration * SampleRate);
        var wave = new short[count];
        uint noiseSeed = 0x1D4C9BAFu;

        for (int i = 0; i < count; i++)
        {
            double t = (double)i / SampleRate;
            double signal;
            if (kind == "PaperFlick")
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
}
