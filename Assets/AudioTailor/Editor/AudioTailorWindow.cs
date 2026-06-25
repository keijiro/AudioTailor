using UnityEngine;
using UnityEditor;

namespace AudioTailor.Editor
{

sealed class AudioTailorWindow : EditorWindow
{
    // Processing options

    [SerializeField] AudioClip _sourceClip;

    [SerializeField] bool _trimSilence;
    [SerializeField] float _silenceThresholdDb = -60;
    [SerializeField] float _releaseThresholdDb = -40;

    [SerializeField] bool _normalize;
    [SerializeField] float _targetLevelDb = -0.1f;

    [SerializeField] bool _makeLoop;
    [SerializeField] float _crossfadeDuration = 0.1f;

    [SerializeField] bool _convertMono;

    // Preview state

    AudioClip _previewClip;
    float[] _processedSamples;
    int _processedChannels;
    int _processedSampleRate;

    // Playback

    GameObject _previewObject;

    // Waveform cache

    Texture2D _waveformTexture;
    object _waveformKey;

    const int WaveformHeight = 80;

    // Entry points

    [MenuItem("Window/Audio Tailor")]
    static void OpenEmpty() => GetWindow<AudioTailorWindow>("Audio Tailor");

    public static void Open(AudioClip clip)
    {
        var w = GetWindow<AudioTailorWindow>("Audio Tailor");
        if (w._sourceClip == clip) return;
        w._sourceClip = clip;
        w.ResetPreview();
    }

    // EditorWindow implementation

    void OnDestroy()
    {
        StopPreview();
        if (_previewClip != null) DestroyImmediate(_previewClip);
        if (_waveformTexture != null) DestroyImmediate(_waveformTexture);
    }

    void OnGUI()
    {
        EditorGUILayout.Space();

        EditorGUI.BeginChangeCheck();
        _sourceClip = (AudioClip)EditorGUILayout.ObjectField(
            "Source Clip", _sourceClip, typeof(AudioClip), false);
        if (EditorGUI.EndChangeCheck())
            ResetPreview();

        DrawWaveformArea();
        DrawPlaybackControls(_previewClip != null ? _previewClip : _sourceClip);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Processing", EditorStyles.boldLabel);
        DrawOptions();

        EditorGUILayout.Space();
        DrawActionButtons();
    }

    // Private UI sections

    void DrawWaveformArea()
    {
        var rect = GUILayoutUtility.GetRect(0, WaveformHeight, GUILayout.ExpandWidth(true));
        if (Event.current.type != EventType.Repaint) return;

        if (rect.width > 1)
        {
            if (_processedSamples != null)
            {
                if (_waveformTexture == null || _waveformKey != (object)_processedSamples ||
                    Mathf.Abs(_waveformTexture.width - (int)rect.width) > 1)
                {
                    if (_waveformTexture != null) DestroyImmediate(_waveformTexture);
                    _waveformTexture = RenderWaveformFromSamples(
                        _processedSamples, _processedChannels, (int)rect.width, WaveformHeight);
                    _waveformKey = _processedSamples;
                }
            }
            else if (_sourceClip != null)
            {
                if (_waveformTexture == null || _waveformKey != (object)_sourceClip ||
                    Mathf.Abs(_waveformTexture.width - (int)rect.width) > 1)
                {
                    if (_waveformTexture != null) DestroyImmediate(_waveformTexture);
                    _waveformTexture = RenderWaveform(_sourceClip, (int)rect.width, WaveformHeight);
                    _waveformKey = (object)_sourceClip;
                }
            }
        }

        if (_waveformTexture != null)
            GUI.DrawTexture(rect, _waveformTexture, ScaleMode.StretchToFill);
        else
            EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));
    }

    void DrawPlaybackControls(AudioClip clip)
    {
        using (new EditorGUI.DisabledScope(clip == null))
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("▶ Play")) PlayPreview(clip);
            if (GUILayout.Button("■ Stop")) StopPreview();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }
    }

    void DrawOptions()
    {
        _trimSilence = EditorGUILayout.ToggleLeft("Trim Silence", _trimSilence);
        if (_trimSilence)
        {
            EditorGUI.indentLevel++;
            _silenceThresholdDb = EditorGUILayout.FloatField("Silence Threshold (dB)", _silenceThresholdDb);
            _releaseThresholdDb = EditorGUILayout.FloatField("Release Threshold (dB)", _releaseThresholdDb);
            EditorGUI.indentLevel--;
        }

        _normalize = EditorGUILayout.ToggleLeft("Normalize", _normalize);
        if (_normalize)
        {
            EditorGUI.indentLevel++;
            _targetLevelDb = EditorGUILayout.FloatField("Target Level (dB)", _targetLevelDb);
            EditorGUI.indentLevel--;
        }

        _makeLoop = EditorGUILayout.ToggleLeft("Make Loop", _makeLoop);
        if (_makeLoop)
        {
            EditorGUI.indentLevel++;
            _crossfadeDuration = EditorGUILayout.FloatField("Crossfade Duration (sec)", _crossfadeDuration);
            EditorGUI.indentLevel--;
        }

        _convertMono = EditorGUILayout.ToggleLeft("Convert to Mono", _convertMono);
    }

    void DrawActionButtons()
    {
        using (new EditorGUI.DisabledScope(_sourceClip == null))
        {
            if (GUILayout.Button("Process"))
                RunProcess();
        }

        if (_previewClip != null)
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Save"))  RunSave();
            if (GUILayout.Button("Reset")) ResetPreview();
            EditorGUILayout.EndHorizontal();
        }
    }

    // Actions

    void RunProcess()
    {
        var opts = new ProcessingOptions
        {
            trimSilence        = _trimSilence,
            silenceThresholdDb = _silenceThresholdDb,
            releaseThresholdDb = _releaseThresholdDb,
            normalize          = _normalize,
            targetLevelDb      = _targetLevelDb,
            makeLoop           = _makeLoop,
            crossfadeDuration  = _crossfadeDuration,
            convertMono        = _convertMono,
        };

        var (samples, channels, sampleRate) = AudioProcessor.ProcessToSamples(_sourceClip, opts);
        if (samples == null) return;

        _processedSamples    = samples;
        _processedChannels   = channels;
        _processedSampleRate = sampleRate;

        if (_previewClip != null) DestroyImmediate(_previewClip);
        _previewClip = AudioClip.Create("Preview",
            _processedSamples.Length / _processedChannels,
            _processedChannels, _processedSampleRate, false);
        _previewClip.SetData(_processedSamples, 0);

        if (_waveformTexture != null) DestroyImmediate(_waveformTexture);
        _waveformTexture = null;
        _waveformKey     = null;
    }

    void RunSave()
    {
        if (_processedSamples == null) return;

        var path = AssetDatabase.GetAssetPath(_sourceClip);
        if (string.IsNullOrEmpty(path))
        {
            EditorUtility.DisplayDialog("Audio Tailor", "Source clip is not a project asset.", "OK");
            return;
        }

        StopPreview();
        AudioProcessor.SaveToFile(path, _processedSamples, _processedChannels, _processedSampleRate);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        ResetPreview();
    }

    void ResetPreview()
    {
        StopPreview();
        if (_previewClip != null) { DestroyImmediate(_previewClip); _previewClip = null; }
        _processedSamples    = null;
        _processedChannels   = 0;
        _processedSampleRate = 0;
        if (_waveformTexture != null) { DestroyImmediate(_waveformTexture); _waveformTexture = null; }
        _waveformKey = null;
    }

    // Waveform rendering

    static Texture2D RenderWaveformFromSamples(float[] samples, int channels, int width, int height)
    {
        var bg     = new Color(0.15f, 0.15f, 0.15f);
        var wave   = new Color(0.4f,  0.8f,  0.4f);
        var frames = samples.Length / channels;
        var pixels = new Color[width * height];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = bg;

        for (var x = 0; x < width; x++)
        {
            var f0 = (int)((float)x       / width * frames);
            var f1 = Mathf.Max(f0 + 1, (int)((float)(x + 1) / width * frames));

            var lo = 0f; var hi = 0f;
            for (var f = f0; f < f1; f++)
            {
                var v = samples[f * channels];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }

            var yLo = Mathf.Clamp((int)((lo * 0.5f + 0.5f) * height), 0, height - 1);
            var yHi = Mathf.Clamp((int)((hi * 0.5f + 0.5f) * height), 0, height - 1);
            for (var y = yLo; y <= yHi; y++)
                pixels[y * width + x] = wave;
        }

        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    static Texture2D RenderWaveform(AudioClip clip, int width, int height)
    {
        var samples = new float[clip.samples * clip.channels];
        if (!clip.GetData(samples, 0)) return null;
        return RenderWaveformFromSamples(samples, clip.channels, width, height);
    }

    // Playback via hidden AudioSource (works with runtime AudioClip.Create clips)

    void PlayPreview(AudioClip clip)
    {
        StopPreview();
        if (clip == null) return;

        _previewObject = new GameObject("AudioTailorPreview")
            { hideFlags = HideFlags.HideAndDontSave };
        var source = _previewObject.AddComponent<AudioSource>();
        source.spatialBlend = 0;
        source.clip = clip;
        source.Play();

        EditorApplication.update += CheckPlaybackFinished;
    }

    void StopPreview()
    {
        EditorApplication.update -= CheckPlaybackFinished;
        if (_previewObject == null) return;
        DestroyImmediate(_previewObject);
        _previewObject = null;
    }

    void CheckPlaybackFinished()
    {
        if (_previewObject == null)
        {
            EditorApplication.update -= CheckPlaybackFinished;
            return;
        }
        if (!_previewObject.GetComponent<AudioSource>().isPlaying)
            StopPreview();
    }
}

} // namespace AudioTailor.Editor
