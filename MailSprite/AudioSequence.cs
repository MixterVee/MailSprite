using System.IO;
using System.Media;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MailSprite;

/// <summary>
/// Plays a built-in notification and an external WAV announcement through one
/// uninterrupted Windows audio stream. This avoids switching audio sessions
/// between the notification and voice on sound drivers that clip beginnings
/// or ends of separately played sounds.
/// </summary>
internal static class AudioSequence
{
    private const int OutputRate = 44100;
    private const int OutputChannels = 2;
    private const int GapMilliseconds = 160;
    private const int TailMilliseconds = 700;
    private static readonly WaveFormat OutputFormat = new(OutputRate, 16, OutputChannels);

    public static void Play(string notification, string voicePath)
    {
        bool hasNotification = notification != "Off";
        bool hasVoice = !string.IsNullOrWhiteSpace(voicePath) && File.Exists(voicePath);
        if (!hasNotification && !hasVoice) return;

        try
        {
            using var audio = new MemoryStream();
            using (var writer = new WaveFileWriter(audio, OutputFormat))
            {
                if (hasNotification)
                {
                    using var builtinStream = new MemoryStream(BuiltInSounds.GetWav(notification), writable: false);
                    using var builtinReader = new WaveFileReader(builtinStream);
                    AppendSamples(writer, builtinReader.ToSampleProvider());
                }

                if (hasVoice)
                {
                    if (hasNotification) AppendSilence(writer, GapMilliseconds);
                    using var voiceReader = new AudioFileReader(voicePath);
                    AppendSamples(writer, voiceReader);
                }

                // Keep the output device active after the actual WAV ends.
                // Sound drivers with aggressive enhancement/power gating can
                // otherwise discard the last audible fraction of a clip.
                AppendSilence(writer, TailMilliseconds);
            }

            using var stream = new MemoryStream(audio.ToArray(), writable: false);
            using var player = new SoundPlayer(stream);
            player.Load();
            player.PlaySync();
        }
        catch
        {
            // If an unusual voice WAV format cannot be decoded, preserve the
            // existing audible alert rather than failing silently.
            if (hasNotification) BuiltInSounds.PlaySync(notification);
            if (hasVoice)
            {
                if (hasNotification) Thread.Sleep(GapMilliseconds);
                try
                {
                    using var player = new SoundPlayer(voicePath);
                    player.Load();
                    player.PlaySync();
                }
                catch { /* Unsupported WAV: keep the notification intact. */ }
            }
        }
    }

    private static void AppendSamples(WaveFileWriter destination, ISampleProvider source)
    {
        if (source.WaveFormat.SampleRate != OutputRate)
            source = new WdlResamplingSampleProvider(source, OutputRate);

        int channels = source.WaveFormat.Channels;
        if (channels < 1) throw new InvalidDataException("Audio source has no channels.");

        const int FramesPerRead = 4096;
        var samples = new float[FramesPerRead * channels];
        var pcm = new byte[FramesPerRead * OutputChannels * sizeof(short)];

        int samplesRead;
        while ((samplesRead = source.Read(samples, 0, samples.Length)) > 0)
        {
            int frameCount = samplesRead / channels;
            for (int frame = 0; frame < frameCount; frame++)
            {
                int offset = frame * channels;
                float left;
                float right;
                if (channels == 1)
                    left = right = samples[offset];
                else if (channels == 2)
                {
                    left = samples[offset];
                    right = samples[offset + 1];
                }
                else
                {
                    float sum = 0;
                    for (int ch = 0; ch < channels; ch++) sum += samples[offset + ch];
                    left = right = sum / channels;
                }

                short sampleL = ToPcm16(left);
                short sampleR = ToPcm16(right);
                int b = frame * 4;
                pcm[b] = (byte)sampleL;
                pcm[b + 1] = (byte)(sampleL >> 8);
                pcm[b + 2] = (byte)sampleR;
                pcm[b + 3] = (byte)(sampleR >> 8);
            }
            destination.Write(pcm, 0, frameCount * 4);
        }
    }

    private static short ToPcm16(float value) =>
        (short)Math.Round(Math.Clamp(value, -1.0f, 1.0f) * 32767.0f);

    private static void AppendSilence(WaveFileWriter destination, int milliseconds)
    {
        int bytes = OutputRate * OutputChannels * sizeof(short) * milliseconds / 1000;
        destination.Write(new byte[bytes], 0, bytes);
    }
}
