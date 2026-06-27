using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace AudioTailor.Editor
{

[BurstCompile]
struct WaveformMinMaxJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float> Samples;
    public int Channels;
    public int FrameCount;
    public int PixelCount;
    [WriteOnly] public NativeArray<float2> MinMax;

    public void Execute(int x)
    {
        var f0 = (int)((float)x / PixelCount * FrameCount);
        var f1 = math.min(math.max(f0 + 1, (int)((float)(x + 1) / PixelCount * FrameCount)), FrameCount);

        var lo = float.MaxValue;
        var hi = float.MinValue;
        for (var f = f0; f < f1; f++)
        {
            var v = Samples[f * Channels];
            lo = math.min(lo, v);
            hi = math.max(hi, v);
        }
        MinMax[x] = new float2(lo, hi);
    }
}

static class WaveformBuilder
{
    public static NativeArray<float2> ComputeMinMax(
        NativeArray<float> samples, int channels, int pixelCount,
        Allocator allocator = Allocator.TempJob)
    {
        var frameCount = samples.Length / channels;
        var minMax = new NativeArray<float2>(pixelCount, allocator);

        new WaveformMinMaxJob
        {
            Samples = samples,
            Channels = channels,
            FrameCount = frameCount,
            PixelCount = pixelCount,
            MinMax = minMax,
        }.Schedule(pixelCount, 64).Complete();

        return minMax;
    }
}

} // namespace AudioTailor.Editor
