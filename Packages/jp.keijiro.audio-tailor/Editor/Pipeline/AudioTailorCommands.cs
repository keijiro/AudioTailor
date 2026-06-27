using UnityEditor;
using Unity.Pipeline.Commands;

namespace AudioTailor.Editor
{

static class AudioTailorCommands
{
    [CliCommand("audiotailor_process",
        "Process an AudioClip asset with AudioTailor (trim silence, normalize, loop, mono)",
        MainThreadRequired = true)]
    public static string Process(
        [CliArg("asset_path", "Asset-relative path to the AudioClip (e.g. Assets/Engine.wav)", Required = true)]
        string assetPath,

        [CliArg("trim_silence", "Remove leading and trailing silence")]
        bool trimSilence = true,

        [CliArg("silence_threshold_db", "Silence detection threshold in dB")]
        float silenceThresholdDb = -60f,

        [CliArg("release_threshold_db", "Silence release threshold in dB")]
        float releaseThresholdDb = -30f,

        [CliArg("normalize", "Peak-normalize the audio")]
        bool normalize = true,

        [CliArg("target_level_db", "Normalization target level in dB")]
        float targetLevelDb = -0.1f,

        [CliArg("make_loop", "Apply equal-power crossfade to make a seamless loop")]
        bool makeLoop = false,

        [CliArg("pre_trim_ms", "Milliseconds to trim from start and end before looping")]
        int preTrimMs = 0,

        [CliArg("crossfade_pct", "Crossfade length as a percentage of total clip duration (0-100)")]
        int crossfadePct = 10,

        [CliArg("convert_mono", "Mix down to mono")]
        bool convertMono = false)
    {
        var clip = AssetDatabase.LoadAssetAtPath<UnityEngine.AudioClip>(assetPath);
        if (clip == null)
            return $"Error: AudioClip not found at '{assetPath}'";

        var opts = new ProcessingOptions
        {
            trimSilence        = trimSilence,
            silenceThresholdDb = silenceThresholdDb,
            releaseThresholdDb = releaseThresholdDb,
            normalize          = normalize,
            targetLevelDb      = targetLevelDb,
            makeLoop           = makeLoop,
            preTrimMs          = preTrimMs,
            crossfadeDuration  = crossfadePct / 100f * clip.length,
            convertMono        = convertMono,
        };

        AudioProcessor.Process(clip, opts, assetPath);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

        return $"Processed: {assetPath}";
    }
}

} // namespace AudioTailor.Editor
