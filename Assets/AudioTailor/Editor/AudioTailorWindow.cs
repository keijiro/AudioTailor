using UnityEngine;
using UnityEditor;

namespace AudioTailor.Editor
{

sealed class AudioTailorWindow : EditorWindow
{
    // Fields

    [SerializeField] AudioClip _sourceClip;

    [SerializeField] bool _trimSilence;
    [SerializeField] float _silenceThresholdDb = -60;
    [SerializeField] float _releaseThresholdDb = -40;

    [SerializeField] bool _normalize;
    [SerializeField] float _targetLevelDb = -0.1f;

    [SerializeField] bool _makeLoop;
    [SerializeField] float _crossfadeDuration = 0.1f;

    [SerializeField] bool _convertMono;

    // Menu item

    [MenuItem("Window/Audio Tailor")]
    static void Open() => GetWindow<AudioTailorWindow>("Audio Tailor");

    // EditorWindow implementation

    void OnGUI()
    {
        EditorGUILayout.Space();

        _sourceClip = (AudioClip)EditorGUILayout.ObjectField(
            "Source Clip", _sourceClip, typeof(AudioClip), false);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Processing", EditorStyles.boldLabel);

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

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(_sourceClip == null))
        {
            if (GUILayout.Button("Run"))
                RunProcessing();
        }
    }

    // Private members

    void RunProcessing()
    {
        var path = EditorUtility.SaveFilePanelInProject(
            "Save Processed Audio", _sourceClip.name + "_processed", "wav",
            "Choose output location");
        if (string.IsNullOrEmpty(path)) return;

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

        AudioProcessor.Process(_sourceClip, opts, path);
        AssetDatabase.Refresh();
    }
}

} // namespace AudioTailor.Editor
