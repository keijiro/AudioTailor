using Unity.Collections;
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

    [SerializeField] bool _trimSilence = true;
    [SerializeField] float _silenceThresholdDb = -60;
    [SerializeField] float _releaseThresholdDb = -30;

    [SerializeField] bool _normalize = true;
    [SerializeField] float _targetLevelDb = -0.1f;

    [SerializeField] bool _makeLoop;
    [SerializeField] int _preTrimMs;
    [SerializeField] int _crossfadePct = 10;

    [SerializeField] bool _convertMono;

    // Runtime state

    AudioClip _previewClip;
    NativeArray<float> _processedSamples;
    object _processedToken;  // stable key for waveform cache comparison
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
    Button _saveBtn;

    // Waveform cache

    RenderTexture _waveformRT;
    GraphicsBuffer _waveformBuffer;
    Material _waveformMaterial;
    object _waveformAudioKey;
    int _waveformFrameCount;
    int _waveformChannels;
    bool _blitScheduled;


    // Entry points

    [MenuItem("Window/Audio/Audio Tailor")]
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
        if (_processedSamples.IsCreated) _processedSamples.Dispose();
        ReleaseWaveformResources();
        if (_waveformMaterial != null) DestroyImmediate(_waveformMaterial);
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

        WireSlider(root, "silence-threshold-field",  _silenceThresholdDb, v => _silenceThresholdDb = v);
        WireSlider(root, "release-threshold-field",  _releaseThresholdDb, v => _releaseThresholdDb = v);
        WireSlider(root, "target-level-field",       _targetLevelDb,      v => _targetLevelDb      = v);
        WireSliderInt(root, "pre-trim-ms-field",   _preTrimMs,    v => _preTrimMs    = v);
        WireSliderInt(root, "crossfade-pct-field", _crossfadePct, v => _crossfadePct = v);

        var monoToggle = root.Q<Toggle>("mono-toggle");
        monoToggle.SetValueWithoutNotify(_convertMono);
        monoToggle.RegisterValueChangedCallback(e => _convertMono = e.newValue);

        // Action buttons
        _processBtn = root.Q<Button>("process-btn");
        _processBtn.clicked += RunProcess;
        _processBtn.SetEnabled(_sourceClip != null);
        _clipField.RegisterValueChangedCallback(e => _processBtn.SetEnabled(e.newValue != null));

        _saveBtn = root.Q<Button>("save-btn");
        _saveBtn.clicked += RunSave;
        _saveBtn.SetEnabled(false);
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

    static void WireSlider(VisualElement root, string name, float initialValue,
        System.Action<float> onChange)
    {
        var field = root.Q<Slider>(name);
        field.SetValueWithoutNotify(initialValue);
        field.RegisterValueChangedCallback(e => onChange(e.newValue));
    }

    static void WireSliderInt(VisualElement root, string name, int initialValue,
        System.Action<int> onChange)
    {
        var field = root.Q<SliderInt>(name);
        field.SetValueWithoutNotify(initialValue);
        field.RegisterValueChangedCallback(e => onChange(e.newValue));
    }

    // Waveform

    void UpdateWaveform(float width, int height)
    {
        var w = (int)width;
        if (w <= 0 || height <= 0) return;

        var newAudioKey  = _processedSamples.IsCreated ? _processedToken : (object)_sourceClip;
        var audioChanged = _waveformAudioKey != newAudioKey;
        var sizeChanged  = _waveformRT == null
                        || Mathf.Abs(_waveformRT.width  - w)      > 1
                        || Mathf.Abs(_waveformRT.height - height) > 1;

        if (!audioChanged && !sizeChanged) return;

        if (audioChanged)
        {
            if (_waveformBuffer != null) { _waveformBuffer.Dispose(); _waveformBuffer = null; }
            _waveformAudioKey = newAudioKey;

            if (_processedSamples.IsCreated)
            {
                _waveformBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    _processedSamples.Length, sizeof(float));
                _waveformBuffer.SetData(_processedSamples);
                _waveformFrameCount = _processedSamples.Length / _processedChannels;
                _waveformChannels   = _processedChannels;
            }
            else if (_sourceClip != null)
            {
                var count = _sourceClip.samples * _sourceClip.channels;
                using var tmp = new NativeArray<float>(count, Allocator.Temp);
                if (_sourceClip.GetData(tmp.AsSpan(), 0))
                {
                    _waveformBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                        count, sizeof(float));
                    _waveformBuffer.SetData(tmp);
                    _waveformFrameCount = _sourceClip.samples;
                    _waveformChannels   = _sourceClip.channels;
                }
            }
        }

        if (sizeChanged)
        {
            if (_waveformRT != null) { _waveformRT.Release(); _waveformRT = null; }
            if (_waveformBuffer != null)
            {
                _waveformRT = new RenderTexture(w, height, 0, RenderTextureFormat.ARGB32)
                    { hideFlags = HideFlags.HideAndDontSave };
                _waveformRT.Create();
            }
        }

        _waveformImage.image = _waveformRT;

        if (_waveformBuffer != null && _waveformRT != null)
            ScheduleBlit();
    }

    void InvalidateWaveform()
    {
        _waveformAudioKey = null;
        if (_waveformImage != null) _waveformImage.image = null;
        if (_waveformContainer != null)
        {
            var r = _waveformContainer.contentRect;
            UpdateWaveform(r.width, (int)r.height);
        }
    }

    void ReleaseWaveformResources()
    {
        if (_waveformRT     != null) { _waveformRT.Release(); _waveformRT = null; }
        if (_waveformBuffer != null) { _waveformBuffer.Dispose(); _waveformBuffer = null; }
    }

    void ScheduleBlit()
    {
        if (_blitScheduled) return;
        _blitScheduled = true;
        EditorApplication.delayCall += BlitWaveform;
    }

    void BlitWaveform()
    {
        _blitScheduled = false;
        if (_waveformBuffer == null || _waveformRT == null) return;

        if (_waveformMaterial == null)
        {
            var shader = Shader.Find("Hidden/AudioTailor/Waveform");
            _waveformMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        _waveformMaterial.SetBuffer("_AudioBuffer",      _waveformBuffer);
        _waveformMaterial.SetInt   ("_FrameCount",       _waveformFrameCount);
        _waveformMaterial.SetInt   ("_Channels",         _waveformChannels);
        _waveformMaterial.SetInt   ("_PixelWidth",       _waveformRT.width);
        _waveformMaterial.SetInt   ("_PixelHeight",      _waveformRT.height);
        _waveformMaterial.SetColor ("_WaveColor",        new Color(0.4f, 0.8f, 0.4f).linear);
        _waveformMaterial.SetColor ("_BackgroundColor",  new Color(0.15f, 0.15f, 0.15f).linear);

        // Defer to outside of UIElements' rendering pass (avoids Metal command buffer conflict)
        Graphics.Blit(Texture2D.blackTexture, _waveformRT, _waveformMaterial);
        Repaint();
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
            preTrimMs          = _preTrimMs,
            crossfadeDuration  = _crossfadePct / 100f * _sourceClip.length,
            convertMono        = _convertMono,
        };

        if (_processedSamples.IsCreated) { _processedSamples.Dispose(); _processedSamples = default; }
        _processedToken = null;

        var (samples, channels, sampleRate) = AudioProcessor.ProcessToSamples(_sourceClip, opts);
        if (!samples.IsCreated) return;

        _processedSamples    = samples;
        _processedToken      = new object();
        _processedChannels   = channels;
        _processedSampleRate = sampleRate;

        if (_previewClip != null) DestroyImmediate(_previewClip);
        var frameCount = _processedSamples.Length / _processedChannels;
        _previewClip = AudioClip.Create("Preview", frameCount, _processedChannels, _processedSampleRate, false);
        _previewClip.SetData(_processedSamples.AsReadOnlySpan(), 0);

        if (_saveBtn != null) _saveBtn.SetEnabled(true);
        InvalidateWaveform();
    }

    void RunSave()
    {
        if (!_processedSamples.IsCreated) return;

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
        if (_processedSamples.IsCreated) { _processedSamples.Dispose(); _processedSamples = default; }
        _processedToken      = null;
        _processedChannels   = 0;
        _processedSampleRate = 0;

        if (_saveBtn != null) _saveBtn.SetEnabled(false);
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
        source.loop = _makeLoop;
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

}

} // namespace AudioTailor.Editor
