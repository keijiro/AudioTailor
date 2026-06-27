using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace AudioTailor.Editor.Tests
{

[TestFixture]
class WaveformBuilderTests
{
    [Test]
    public void ComputeMinMax_ConstantSignal_ReturnsCorrectMinMax()
    {
        const int frameCount = 1000;
        const int pixelCount = 100;
        const float value = 0.5f;

        var samples = new NativeArray<float>(frameCount, Allocator.TempJob);
        for (var i = 0; i < frameCount; i++) samples[i] = value;

        var result = WaveformBuilder.ComputeMinMax(samples, 1, pixelCount);
        samples.Dispose();

        Assert.AreEqual(pixelCount, result.Length);
        for (var x = 0; x < pixelCount; x++)
        {
            Assert.AreEqual(value, result[x].x, 0.001f, $"min at column {x}");
            Assert.AreEqual(value, result[x].y, 0.001f, $"max at column {x}");
        }
        result.Dispose();
    }

    [Test]
    public void ComputeMinMax_SineWave_MinNegativeMaxPositive()
    {
        const int frameCount = 44100;
        const int pixelCount = 200;

        var samples = new NativeArray<float>(frameCount, Allocator.TempJob);
        for (var i = 0; i < frameCount; i++) samples[i] = Mathf.Sin(i * 0.1f);

        var result = WaveformBuilder.ComputeMinMax(samples, 1, pixelCount);
        samples.Dispose();

        Assert.AreEqual(pixelCount, result.Length);
        var hasNegativeMin = false;
        var hasPositiveMax = false;
        for (var x = 0; x < pixelCount; x++)
        {
            if (result[x].x < 0) hasNegativeMin = true;
            if (result[x].y > 0) hasPositiveMax = true;
        }
        result.Dispose();

        Assert.IsTrue(hasNegativeMin, "Sine wave should have negative min values");
        Assert.IsTrue(hasPositiveMax, "Sine wave should have positive max values");
    }

    [Test]
    public void ComputeMinMax_PixelCountMatchesRequest()
    {
        const int pixelCount = 137; // intentionally odd number
        var samples = new NativeArray<float>(1000, Allocator.TempJob);

        var result = WaveformBuilder.ComputeMinMax(samples, 1, pixelCount);
        samples.Dispose();

        Assert.AreEqual(pixelCount, result.Length);
        result.Dispose();
    }

    [Test]
    public void ComputeMinMax_SingleFrame_DoesNotThrow()
    {
        var samples = new NativeArray<float>(1, Allocator.TempJob);
        samples[0] = 0.3f;

        NativeArray<float2> result = default;
        Assert.DoesNotThrow(() => result = WaveformBuilder.ComputeMinMax(samples, 1, 10));
        samples.Dispose();

        if (result.IsCreated) result.Dispose();
    }
}

} // namespace AudioTailor.Editor.Tests
