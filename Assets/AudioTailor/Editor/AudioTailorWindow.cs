using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

namespace AudioTailor.Editor
{

sealed class AudioTailorWindow : EditorWindow
{
    // Processing options

    [SerializeField] AudioClip _sourceClip;

    [SerializeField] bool _trimSilence;
    [SerializeField] float _silenceThresholdDb = -60;
    [SerializeField] float _releaseThresholdDb = -30;

    [SerializeField] bool _normalize;
    [SerializeField] float _targetLevelDb = -0.1f;

    [SerializeField] bool _makeLoop;
    [SerializeField] float _crossfadeDuration = 0.1f;

    [SerializeField] bool _convertMono;

    // Runtime state

    AudioClip _previewClip;
    float[] _processedSamples;
    int _processedChannels;
    int _processedSampleRate;

    // Playback

    GameObject _previewObject;
    float _playbackTime;

    // UI references

    ObjectField _clipField;
    Button _processBtn;
    VisualElement _waveformContainer;
    Image _waveformImage;
    VisualElement _playheadElement;
    VisualElement _saveResetRow;

    // Waveform cache

    Texture2D _waveformTexture;
    object _waveformKey;


    // Entry points

    [MenuItem("Window/Audio Tailor")]
    static void OpenEmpty() => GetWindow<AudioTailorWindow>("Audio Tailor");

    public static void Open(AudioClip clip)
    {
        var w = GetWindow<AudioTailorWindow>("Audio Tailor");
        if (w._sourceClip == clip) return;
        w._sourceClip = clip;
        w.ResetPreview();
        w._clipField?.SetValueWithoutNotify(clip);
        w._processBtn?.SetEnabled(clip != null);
        w.InvalidateWaveform();
    }

    // EditorWindow

    void OnDestroy()
    {
        StopPreview();
        if (_previewClip != null) DestroyImmediate(_previewClip);
        if (_waveformTexture != null) DestroyImmediate(_waveformTexture);
    }

    void CreateGUI()
    {
        var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
            "Assets/AudioTailor/Editor/AudioTailorWindow.uxml");
        uxml.CloneTree(rootVisualElement);

        var root = rootVisualElement;

        // Source clip
        _clipField = root.Q<ObjectField>("clip-field");
        _clipField.objectType = typeof(AudioClip);
        _clipField.SetValueWithoutNotify(_sourceClip);
        _clipField.RegisterValueChangedCallback(e =>
        {
            _sourceClip = (AudioClip)e.newValue;
            ResetPreview();
            InvalidateWaveform();
        });

        // Waveform
        _waveformContainer = root.Q("waveform-container");
        _waveformImage = root.Q<Image>("waveform-image");
        _waveformImage.scaleMode = ScaleMode.StretchToFill;
        _waveformContainer.RegisterCallback<GeometryChangedEvent>(
            e => UpdateWaveform(e.newRect.width, (int)e.newRect.height));

        _playheadElement = root.Q("playhead");

        // Playback buttons
        root.Q<Button>("play-btn").clicked += () => PlayPreview(_previewClip ?? _sourceClip);
        root.Q<Button>("stop-btn").clicked += StopPreview;

        // Options
        WireToggleGroup(root, "trim-toggle",      "trim-options",      _trimSilence, v => _trimSilence = v);
        WireToggleGroup(root, "normalize-toggle", "normalize-options", _normalize,   v => _normalize   = v);
        WireToggleGroup(root, "loop-toggle",      "loop-options",      _makeLoop,    v => _makeLoop    = v);

        WireFloatField(root, "silence-threshold-field",  _silenceThresholdDb, v => _silenceThresholdDb = v);
        WireFloatField(root, "release-threshold-field",  _releaseThresholdDb, v => _releaseThresholdDb = v);
        WireFloatField(root, "target-level-field",       _targetLevelDb,      v => _targetLevelDb      = v);
        WireFloatField(root, "crossfade-duration-field", _crossfadeDuration,  v => _crossfadeDuration  = v);

        var monoToggle = root.Q<Toggle>("mono-toggle");
        monoToggle.SetValueWithoutNotify(_convertMono);
        monoToggle.RegisterValueChangedCallback(e => _convertMono = e.newValue);

        // Action buttons
        _processBtn = root.Q<Button>("process-btn");
        _processBtn.clicked += RunProcess;
        _processBtn.SetEnabled(_sourceClip != null);
        _clipField.RegisterValueChangedCallback(e => _processBtn.SetEnabled(e.newValue != null));

        root.Q<Button>("save-btn").clicked  += RunSave;
        root.Q<Button>("reset-btn").clicked += ResetPreview;

        _saveResetRow = root.Q("save-reset-row");
    }

    // UI helpers

    static void WireToggleGroup(VisualElement root, string toggleName, string optionsName,
        bool initialValue, System.Action<bool> onToggle)
    {
        var toggle  = root.Q<Toggle>(toggleName);
        var options = root.Q(optionsName);
        toggle.SetValueWithoutNotify(initialValue);
        options.style.display = initialValue ? DisplayStyle.Flex : DisplayStyle.None;
        toggle.RegisterValueChangedCallback(e =>
        {
            onToggle(e.newValue);
            options.style.display = e.newValue ? DisplayStyle.Flex : DisplayStyle.None;
        });
    }

    static void WireFloatField(VisualElement root, string name, float initialValue,
        System.Action<float> onChange)
    {
        var field = root.Q<FloatField>(name);
        field.SetValueWithoutNotify(initialValue);
        field.RegisterValueChangedCallback(e => onChange(e.newValue));
    }

    // Waveform

    void UpdateWaveform(float width, int height)
    {
        var w      = (int)width;
        var newKey = _processedSamples != null ? (object)_processedSamples : (object)_sourceClip;

        if (w > 0 && height > 0 && _waveformTexture != null && _waveformKey == newKey &&
            Mathf.Abs(_waveformTexture.width  - w)      <= 1 &&
            Mathf.Abs(_waveformTexture.height - height) <= 1)
            return;

        if (_waveformTexture != null) DestroyImmediate(_waveformTexture);
        _waveformTexture = null;
        _waveformKey     = newKey;

        if (w > 0 && height > 0)
        {
            if (_processedSamples != null)
                _waveformTexture = RenderWaveformFromSamples(
                    _processedSamples, _processedChannels, w, height);
            else if (_sourceClip != null)
                _waveformTexture = RenderWaveform(_sourceClip, w, height);
        }

        _waveformImage.image = _waveformTexture;
    }

    void InvalidateWaveform()
    {
        if (_waveformTexture != null) { DestroyImmediate(_waveformTexture); _waveformTexture = null; }
        _waveformKey = null;
        if (_waveformImage != null) _waveformImage.image = null;
        if (_waveformContainer != null)
        {
            var r = _waveformContainer.contentRect;
            UpdateWaveform(r.width, (int)r.height);
        }
    }

    // Playhead

    void UpdatePlayhead()
    {
        if (_playheadElement == null) return;
        var displayClip = _previewClip ?? _sourceClip;
        if (_previewObject != null && displayClip != null && displayClip.length > 0)
        {
            var t = Mathf.Clamp01(_playbackTime / displayClip.length);
            _playheadElement.style.left    = _waveformContainer.contentRect.width * t;
            _playheadElement.style.display = DisplayStyle.Flex;
        }
        else
        {
            _playheadElement.style.display = DisplayStyle.None;
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

        if (_saveResetRow != null) _saveResetRow.style.display = DisplayStyle.Flex;
        InvalidateWaveform();
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

        if (_saveResetRow != null) _saveResetRow.style.display = DisplayStyle.None;
        InvalidateWaveform();
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
        _playbackTime = 0;
        if (_previewObject == null) return;
        DestroyImmediate(_previewObject);
        _previewObject = null;
        UpdatePlayhead();
    }

    void CheckPlaybackFinished()
    {
        if (_previewObject == null)
        {
            EditorApplication.update -= CheckPlaybackFinished;
            return;
        }
        var source = _previewObject.GetComponent<AudioSource>();
        if (!source.isPlaying)
        {
            StopPreview();
            return;
        }
        _playbackTime = source.time;
        UpdatePlayhead();
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
}

} // namespace AudioTailor.Editor
