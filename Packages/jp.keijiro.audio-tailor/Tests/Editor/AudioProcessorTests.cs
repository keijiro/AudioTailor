using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace AudioTailor.Editor.Tests
{

[TestFixture]
class AudioProcessorTests
{
    static AudioClip MakeClip(float[] samples, int channels = 1, int sampleRate = 44100)
    {
        var frameCount = samples.Length / channels;
        var clip = AudioClip.Create("test", frameCount, channels, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    static void Destroy(AudioClip clip) => Object.DestroyImmediate(clip);

    // --- TrimSilence ---

    [Test]
    public void TrimSilence_RemovesLeadingAndTrailingSilence()
    {
        // 100 silent + 100 loud + 100 silent
        var samples = new float[300];
        for (var i = 100; i < 200; i++) samples[i] = 0.5f;
        var clip = MakeClip(samples);

        var opts = new ProcessingOptions
        {
            trimSilence = true,
            silenceThresholdDb = -60,
            releaseThresholdDb = -30,
        };
        var (result, _, _) = AudioProcessor.ProcessToSamples(clip, opts);
        Destroy(clip);

        Assert.IsTrue(result.IsCreated);
        Assert.Less(result.Length, 300, "Trimmed result should be shorter than the original");
        Assert.Greater(result.Length, 0, "Trimmed result should not be empty");
        result.Dispose();
    }

    [Test]
    public void TrimSilence_AllSilent_ReturnsEmpty()
    {
        var samples = new float[200]; // all zeros
        var clip = MakeClip(samples);

        var opts = new ProcessingOptions
        {
            trimSilence = true,
            silenceThresholdDb = -60,
            releaseThresholdDb = -30,
        };
        var (result, _, _) = AudioProcessor.ProcessToSamples(clip, opts);
        Destroy(clip);

        Assert.IsTrue(result.IsCreated);
        Assert.AreEqual(0, result.Length, "All-silent clip should trim to empty");
        result.Dispose();
    }

    // --- Normalize ---

    [Test]
    public void Normalize_AdjustsPeakToTargetLevel()
    {
        var samples = new float[1000];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.5f * Mathf.Sin(i * 0.1f);
        var clip = MakeClip(samples);

        const float targetDb = -0.1f;
        var opts = new ProcessingOptions { normalize = true, targetLevelDb = targetDb };
        var (result, _, _) = AudioProcessor.ProcessToSamples(clip, opts);
        Destroy(clip);

        Assert.IsTrue(result.IsCreated);
        var peak = 0f;
        foreach (var s in result) if (Mathf.Abs(s) > peak) peak = Mathf.Abs(s);

        var expectedPeak = Mathf.Pow(10f, targetDb / 20f);
        Assert.AreEqual(expectedPeak, peak, 0.001f, "Peak should match target level");
        result.Dispose();
    }

    [Test]
    public void Normalize_AllZero_DoesNotCrash()
    {
        var samples = new float[100];
        var clip = MakeClip(samples);

        var opts = new ProcessingOptions { normalize = true, targetLevelDb = -0.1f };
        var (result, _, _) = AudioProcessor.ProcessToSamples(clip, opts);
        Destroy(clip);

        Assert.IsTrue(result.IsCreated);
        result.Dispose();
    }

    // --- MakeLoop ---

    [Test]
    public void MakeLoop_ReducesSampleCountByCrossfade()
    {
        var samples = new float[4000];
        for (var i = 0; i < samples.Length; i++) samples[i] = Mathf.Sin(i * 0.01f);
        var clip = MakeClip(samples, 1, 44100);

        const int crossfadePct = 10;
        var opts = new ProcessingOptions
        {
            makeLoop = true,
            crossfadeDuration = crossfadePct / 100f * clip.length,
        };
        var (result, _, _) = AudioProcessor.ProcessToSamples(clip, opts);
        Destroy(clip);

        Assert.IsTrue(result.IsCreated);
        Assert.Less(result.Length, 4000, "Loop crossfade should reduce sample count");
        result.Dispose();
    }

    // --- ConvertMono ---

    [Test]
    public void ConvertMono_StereoInput_ReturnsMonoOutput()
    {
        // Stereo: interleaved L/R
        var samples = new float[2000];
        for (var i = 0; i < samples.Length; i++) samples[i] = (i % 2 == 0) ? 0.8f : -0.8f;
        var clip = MakeClip(samples, channels: 2);

        var opts = new ProcessingOptions { convertMono = true };
        var (result, channels, _) = AudioProcessor.ProcessToSamples(clip, opts);
        Destroy(clip);

        Assert.IsTrue(result.IsCreated);
        Assert.AreEqual(1, channels, "Output should be mono");
        Assert.AreEqual(1000, result.Length, "Mono frame count should be half of stereo samples");
        result.Dispose();
    }
}

} // namespace AudioTailor.Editor.Tests
