using System;
using System.IO;
using System.Text;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
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

[BurstCompile]
struct ToMonoJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float> Input;
    public int Channels;
    [WriteOnly] public NativeArray<float> Output;

    public void Execute(int frame)
    {
        var sum = 0f;
        for (var c = 0; c < Channels; c++) sum += Input[frame * Channels + c];
        Output[frame] = sum / Channels;
    }
}

[BurstCompile]
struct NormalizePeakJob : IJob
{
    [ReadOnly] public NativeArray<float> Samples;
    [WriteOnly] public NativeArray<float> Peak;

    public void Execute()
    {
        var peak = 0f;
        for (var i = 0; i < Samples.Length; i++)
            peak = math.max(peak, math.abs(Samples[i]));
        Peak[0] = peak;
    }
}

[BurstCompile]
struct NormalizeScaleJob : IJobParallelFor
{
    public NativeArray<float> Samples;
    public float Scale;

    public void Execute(int i) => Samples[i] *= Scale;
}

static class AudioProcessor
{
    // Public interface

    public static (NativeArray<float> samples, int channels, int sampleRate)
        ProcessToSamples(AudioClip clip, ProcessingOptions opts)
    {
        var channels   = clip.channels;
        var sampleRate = clip.frequency;
        var frameCount = clip.samples;

        var samples = LoadFromClip(clip);
        if (!samples.IsCreated)
        {
            EditorUtility.DisplayDialog("Audio Tailor",
                "Failed to read audio data. The clip may be compressed or streaming.",
                "OK");
            return (default, 0, 0);
        }

        int outChannels;

        if (opts.convertMono && channels > 1)
        {
            var mono = ToMono(samples, channels, frameCount);
            samples.Dispose();
            samples     = mono;
            outChannels = 1;
        }
        else
        {
            outChannels = channels;
        }

        if (opts.trimSilence)
        {
            var trimmed = TrimSilence(samples, outChannels, sampleRate,
                opts.silenceThresholdDb, opts.releaseThresholdDb);
            samples.Dispose();
            samples = trimmed;
        }

        if (opts.normalize)
            Normalize(samples, opts.targetLevelDb);

        if (opts.makeLoop)
        {
            var looped = MakeLoop(samples, outChannels, sampleRate,
                opts.preTrimMs, opts.crossfadeDuration);
            samples.Dispose();
            samples = looped;
        }

        return (samples, outChannels, sampleRate);
    }

    public static void Process(AudioClip clip, ProcessingOptions opts, string outputPath)
    {
        var (samples, channels, sampleRate) = ProcessToSamples(clip, opts);
        if (samples.IsCreated)
        {
            WriteWav(outputPath, samples, channels, sampleRate);
            samples.Dispose();
        }
    }

    internal static void SaveToFile(string assetPath, NativeArray<float> samples, int channels, int sampleRate)
        => WriteWav(assetPath, samples, channels, sampleRate);

    // Private processing steps

    static NativeArray<float> LoadFromClip(AudioClip clip)
    {
        var samples = new NativeArray<float>(clip.samples * clip.channels, Allocator.Persistent);
        if (clip.GetData(samples.AsSpan(), 0)) return samples;
        samples.Dispose();
        return default;
    }

    static NativeArray<float> ToMono(NativeArray<float> interleaved, int channels, int frameCount)
    {
        var output = new NativeArray<float>(frameCount, Allocator.Persistent);
        new ToMonoJob { Input = interleaved, Channels = channels, Output = output }
            .Schedule(frameCount, 64).Complete();
        return output;
    }

    // 10 ms — short enough to be inaudible, long enough to avoid clicks
    const float FadeDurationSec = 0.01f;

    static NativeArray<float> TrimSilence(NativeArray<float> samples, int channels, int sampleRate,
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
        if (startFrame >= frameCount) return new NativeArray<float>(0, Allocator.Persistent);

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
        var result = new NativeArray<float>(outFrames * channels, Allocator.Persistent);
        NativeArray<float>.Copy(samples, startFrame * channels, result, 0, outFrames * channels);
        return result;
    }

    static void Normalize(NativeArray<float> samples, float targetLevelDb)
    {
        using var peakArr = new NativeArray<float>(1, Allocator.TempJob);
        new NormalizePeakJob { Samples = samples, Peak = peakArr }.Schedule().Complete();

        var peak = peakArr[0];
        if (peak < 1e-6f) return;

        var scale = DbToAmplitude(targetLevelDb) / peak;
        new NormalizeScaleJob { Samples = samples, Scale = scale }
            .Schedule(samples.Length, 64).Complete();
    }

    static NativeArray<float> MakeLoop(NativeArray<float> samples, int channels, int sampleRate,
        int preTrimMs, float crossfadeDuration)
    {
        var frameCount    = samples.Length / channels;
        var preTrimFrames = Math.Min((int)(preTrimMs / 1000f * sampleRate), frameCount / 4);

        var workStart  = preTrimFrames;
        var workFrames = frameCount - 2 * preTrimFrames;

        var crossFrames = Math.Min((int)(crossfadeDuration * sampleRate), workFrames / 2);
        var outFrames   = crossFrames > 0 ? workFrames - crossFrames : workFrames;

        // Copy the working region (minus tail) into result
        var result = new NativeArray<float>(outFrames * channels, Allocator.Persistent);
        NativeArray<float>.Copy(samples, workStart * channels, result, 0, outFrames * channels);

        // Overwrite head with crossfade blend (equal power)
        for (var f = 0; f < crossFrames; f++)
        {
            var t         = (float)f / crossFrames * (MathF.PI / 2f);
            var gainHead  = MathF.Sin(t);
            var gainTail  = MathF.Cos(t);
            var tailFrame = workFrames - crossFrames + f;
            for (var c = 0; c < channels; c++)
            {
                var head = samples[(workStart + f)         * channels + c];
                var tail = samples[(workStart + tailFrame) * channels + c];
                result[f * channels + c] = head * gainHead + tail * gainTail;
            }
        }

        return result;
    }

    // WAV output

    static void WriteWav(string assetPath, NativeArray<float> samples, int channels, int sampleRate)
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
        for (var i = 0; i < samples.Length; i++)
            writer.Write((short)(Mathf.Clamp(samples[i], -1f, 1f) * short.MaxValue));
    }

    // Utilities

    static float DbToAmplitude(float db) => Mathf.Pow(10f, db / 20f);

    static float FramePeak(NativeArray<float> samples, int frame, int channels)
    {
        var peak = 0f;
        for (var c = 0; c < channels; c++)
            peak = MathF.Max(peak, MathF.Abs(samples[frame * channels + c]));
        return peak;
    }
}

} // namespace AudioTailor.Editor
