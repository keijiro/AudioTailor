using UnityEngine;
using UnityEditor;

namespace AudioTailor.Editor
{

static class AudioTailorHeaderButton
{
    [InitializeOnLoadMethod]
    static void Register() => UnityEditor.Editor.finishedDefaultHeaderGUI += OnHeaderGUI;

    static void OnHeaderGUI(UnityEditor.Editor editor)
    {
        if (!EditorUtility.IsPersistent(editor.target)) return;
        if (editor.target is not AudioImporter importer) return;

        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(importer.assetPath);
        if (clip == null) return;

        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Audio Tailor"))
            AudioTailorWindow.Open(clip);
        EditorGUILayout.EndHorizontal();
    }
}

} // namespace AudioTailor.Editor
