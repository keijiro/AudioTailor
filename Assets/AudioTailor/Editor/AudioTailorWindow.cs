using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

namespace AudioTailor.Editor
{

[BurstCompile]
struct WaveformMeshJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float> Samples;
    public int FrameCount;
    public int Channels;
    public int PixelCount;
    public float Height;
    public Color32 WaveColor;
    [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<Vertex> Vertices;
    [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<ushort> Indices;

    public void Execute(int x)
    {
        var f0 = (int)((float)x / PixelCount * FrameCount);
        var f1 = math.min(math.max(f0 + 1, (int)((float)(x + 1) / PixelCount * FrameCount)), FrameCount);
        var lo =  1e10f;
        var hi = -1e10f;
        for (var f = f0; f < f1; f++)
        {
            var v = Samples[f * Channels];
            lo = math.min(lo, v);
            hi = math.max(hi, v);
        }

        var yTop = (0.5f - hi * 0.5f) * Height;
        var yBot = math.max((0.5f - lo * 0.5f) * Height, yTop + 1f);

        var vi = x * 4;
        Vertices[vi + 0] = new Vertex { position = new Vector3(x,     yTop, Vertex.nearZ), tint = WaveColor };
        Vertices[vi + 1] = new Vertex { position = new Vector3(x + 1, yTop, Vertex.nearZ), tint = WaveColor };
        Vertices[vi + 2] = new Vertex { position = new Vector3(x + 1, yBot, Vertex.nearZ), tint = WaveColor };
        Vertices[vi + 3] = new Vertex { position = new Vector3(x,     yBot, Vertex.nearZ), tint = WaveColor };

        var ii = x * 6;
        Indices[ii + 0] = (ushort)(vi + 0);
        Indices[ii + 1] = (ushort)(vi + 1);
        Indices[ii + 2] = (ushort)(vi + 2);
        Indices[ii + 3] = (ushort)(vi + 2);
        Indices[ii + 4] = (ushort)(vi + 3);
        Indices[ii + 5] = (ushort)(vi + 0);
    }
}

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
    VisualElement _waveformView;
    VisualElement _playheadElement;
    Button _saveBtn;

    // Waveform cache

    NativeArray<float> _displaySamples;
    object _waveformAudioKey;
    int _displayFrameCount;
    int _displayChannels;

    NativeArray<Vertex> _waveformVertices;
    NativeArray<ushort> _waveformIndices;
    int _waveformPixelWidth;


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
        if (_displaySamples.IsCreated) _displaySamples.Dispose();
        if (_waveformVertices.IsCreated) _waveformVertices.Dispose();
        if (_waveformIndices.IsCreated) _waveformIndices.Dispose();
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
        _waveformView = root.Q("waveform-image");
        _waveformView.generateVisualContent += DrawWaveform;

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

        UpdateDisplaySamples();
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

    void UpdateDisplaySamples()
    {
        var newKey = _processedSamples.IsCreated ? _processedToken : (object)_sourceClip;
        if (_waveformAudioKey == newKey) return;
        _waveformAudioKey = newKey;

        if (_displaySamples.IsCreated) { _displaySamples.Dispose(); _displaySamples = default; }

        if (_processedSamples.IsCreated)
        {
            _displaySamples    = new NativeArray<float>(_processedSamples, Allocator.Persistent);
            _displayFrameCount = _processedSamples.Length / _processedChannels;
            _displayChannels   = _processedChannels;
        }
        else if (_sourceClip != null)
        {
            var count = _sourceClip.samples * _sourceClip.channels;
            _displaySamples = new NativeArray<float>(count, Allocator.Persistent);
            if (!_sourceClip.GetData(_displaySamples.AsSpan(), 0))
            {
                _displaySamples.Dispose();
                _displaySamples = default;
            }
            else
            {
                _displayFrameCount = _sourceClip.samples;
                _displayChannels   = _sourceClip.channels;
            }
        }

        _waveformView?.MarkDirtyRepaint();
    }

    void InvalidateWaveform()
    {
        _waveformAudioKey = null;
        UpdateDisplaySamples();
    }

    void DrawWaveform(MeshGenerationContext ctx)
    {
        if (!_displaySamples.IsCreated) return;

        var rect = _waveformView.contentRect;
        var w = (int)rect.width;
        var h = (int)rect.height;
        if (w < 1 || h < 1) return;

        if (_waveformPixelWidth != w)
        {
            if (_waveformVertices.IsCreated) _waveformVertices.Dispose();
            if (_waveformIndices.IsCreated)  _waveformIndices.Dispose();
            _waveformVertices   = new NativeArray<Vertex>(w * 4, Allocator.Persistent);
            _waveformIndices    = new NativeArray<ushort>(w * 6, Allocator.Persistent);
            _waveformPixelWidth = w;
        }

        new WaveformMeshJob
        {
            Samples    = _displaySamples,
            FrameCount = _displayFrameCount,
            Channels   = _displayChannels,
            PixelCount = w,
            Height     = h,
            WaveColor  = (Color32)new Color(0.4f, 0.8f, 0.4f),
            Vertices   = _waveformVertices,
            Indices    = _waveformIndices,
        }.Schedule(w, 32).Complete();

        var mwd = ctx.Allocate(w * 4, w * 6, Texture2D.whiteTexture);
        mwd.SetAllVertices(_waveformVertices);
        mwd.SetAllIndices(_waveformIndices);
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
