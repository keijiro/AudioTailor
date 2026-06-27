using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;

namespace AudioTailor.Editor
{

struct ProcessingOptions
{
    public bool trimSilence;
    public float silenceThresholdDb;
    public float releaseThresholdDb;

    public bool normalize;
    public float targetLevelDb;

    public bool makeLoop;
    public int preTrimMs;
    public float crossfadeDuration;

    public bool convertMono;
}

static class AudioProcessor
{
    // Public interface

    public static (float[] samples, int channels, int sampleRate)
        ProcessToSamples(AudioClip clip, ProcessingOptions opts)
    {
        var channels   = clip.channels;
        var sampleRate = clip.frequency;
        var frameCount = clip.samples;

        var raw = new float[frameCount * channels];
        if (!clip.GetData(raw, 0))
        {
            EditorUtility.DisplayDialog("Audio Tailor",
                "Failed to read audio data. The clip may be compressed or streaming.",
                "OK");
            return (null, 0, 0);
        }

        float[] samples;
        int outChannels;

        if (opts.convertMono || channels == 1)
        {
            samples     = ToMono(raw, channels, frameCount);
            outChannels = 1;
        }
        else
        {
            samples     = raw;
            outChannels = channels;
        }

        if (opts.trimSilence)
            samples = TrimSilence(samples, outChannels, sampleRate,
                opts.silenceThresholdDb, opts.releaseThresholdDb);

        if (opts.normalize)
            samples = Normalize(samples, opts.targetLevelDb);

        if (opts.makeLoop)
            samples = MakeLoop(samples, outChannels, sampleRate, opts.preTrimMs, opts.crossfadeDuration);

        return (samples, outChannels, sampleRate);
    }

    public static void Process(AudioClip clip, ProcessingOptions opts, string outputPath)
    {
        var (samples, channels, sampleRate) = ProcessToSamples(clip, opts);
        if (samples != null)
            WriteWav(outputPath, samples, channels, sampleRate);
    }

    internal static void SaveToFile(string assetPath, float[] samples, int channels, int sampleRate)
        => WriteWav(assetPath, samples, channels, sampleRate);

    // Private processing steps

    static float[] ToMono(float[] interleaved, int channels, int frameCount)
    {
        if (channels == 1) return interleaved;
        var mono = new float[frameCount];
        for (var f = 0; f < frameCount; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += interleaved[f * channels + c];
            mono[f] = sum / channels;
        }
        return mono;
    }

    // 10 ms — short enough to be inaudible, long enough to avoid clicks
    const float FadeDurationSec = 0.01f;

    static float[] TrimSilence(float[] samples, int channels, int sampleRate,
        float silenceThresholdDb, float releaseThresholdDb)
    {
        var silenceAmp = DbToAmplitude(silenceThresholdDb);
        var releaseAmp = DbToAmplitude(releaseThresholdDb);
        var frameCount = samples.Length / channels;

        // Leading silence: first frame above silence threshold
        var startFrame = frameCount;
        for (var f = 0; f < frameCount; f++)
        {
            if (FramePeak(samples, f, channels) > silenceAmp)
            {
                startFrame = f;
                break;
            }
        }
        if (startFrame >= frameCount) return Array.Empty<float>();

        // Fade start: last frame above release threshold
        var fadeStart = startFrame;
        for (var f = frameCount - 1; f >= startFrame; f--)
        {
            if (FramePeak(samples, f, channels) > releaseAmp)
            {
                fadeStart = f + 1;
                break;
            }
        }

        // Apply a short linear fade-out to avoid a click at the cut point
        var fadeLen = Math.Min((int)(FadeDurationSec * sampleRate), frameCount - fadeStart);
        var absEnd  = fadeStart + fadeLen;
        for (var f = fadeStart; f < absEnd; f++)
        {
            var t = 1f - (float)(f - fadeStart) / fadeLen;
            for (var c = 0; c < channels; c++)
                samples[f * channels + c] *= t;
        }

        var outFrames = absEnd - startFrame;
        var result = new float[outFrames * channels];
        Array.Copy(samples, startFrame * channels, result, 0, outFrames * channels);
        return result;
    }

    static float[] Normalize(float[] samples, float targetLevelDb)
    {
        var peak = 0f;
        foreach (var s in samples)
            peak = MathF.Max(peak, MathF.Abs(s));

        if (peak < 1e-6f) return samples;

        var scale = DbToAmplitude(targetLevelDb) / peak;
        for (var i = 0; i < samples.Length; i++)
            samples[i] *= scale;
        return samples;
    }

    static float[] MakeLoop(float[] samples, int channels, int sampleRate, int preTrimMs, float crossfadeDuration)
    {
        var frameCount = samples.Length / channels;

        // Trim start and end before crossfading
        var preTrimFrames = Math.Min((int)(preTrimMs / 1000f * sampleRate), frameCount / 4);
        if (preTrimFrames > 0)
        {
            var trimmedFrames = frameCount - 2 * preTrimFrames;
            var trimmed = new float[trimmedFrames * channels];
            Array.Copy(samples, preTrimFrames * channels, trimmed, 0, trimmedFrames * channels);
            samples    = trimmed;
            frameCount = trimmedFrames;
        }

        var crossFrames = Math.Min((int)(crossfadeDuration * sampleRate), frameCount / 2);
        if (crossFrames <= 0) return samples;

        // Blend tail into head over the crossfade region (equal power)
        for (var f = 0; f < crossFrames; f++)
        {
            var t         = (float)f / crossFrames * (MathF.PI / 2f);
            var gainHead  = MathF.Sin(t);
            var gainTail  = MathF.Cos(t);
            var tailFrame = frameCount - crossFrames + f;
            for (var c = 0; c < channels; c++)
            {
                var head = samples[f         * channels + c];
                var tail = samples[tailFrame * channels + c];
                samples[f * channels + c] = head * gainHead + tail * gainTail;
            }
        }

        // Drop the tail that was folded in
        var outFrames = frameCount - crossFrames;
        var result    = new float[outFrames * channels];
        Array.Copy(samples, 0, result, 0, outFrames * channels);
        return result;
    }

    // WAV output

    static void WriteWav(string assetPath, float[] samples, int channels, int sampleRate)
    {
        var fullPath = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", assetPath));

        const int bitsPerSample = 16;
        var byteRate   = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        var dataSize   = samples.Length * 2;

        using var writer = new BinaryWriter(File.Open(fullPath, FileMode.Create));

        // RIFF header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);          // PCM
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        // data chunk
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        foreach (var s in samples)
            writer.Write((short)(Mathf.Clamp(s, -1f, 1f) * short.MaxValue));
    }

    // Utilities

    static float DbToAmplitude(float db) => Mathf.Pow(10f, db / 20f);

    static float FramePeak(float[] samples, int frame, int channels)
    {
        var peak = 0f;
        for (var c = 0; c < channels; c++)
            peak = MathF.Max(peak, MathF.Abs(samples[frame * channels + c]));
        return peak;
    }
}

} // namespace AudioTailor.Editor
