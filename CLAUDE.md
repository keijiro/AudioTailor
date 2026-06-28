# AudioTailor

## Manual Waveform Display Verification

Waveform rendering cannot be verified by the Test Runner, so it must be checked manually using the following steps.

### Steps

1. Find any audio clip in the project and open the AudioTailor window with it.

```bash
unity command eval --project-path $(pwd) --code "
var guids = UnityEditor.AssetDatabase.FindAssets(\"t:AudioClip\");
if (guids.Length == 0) return \"No audio clip found\";
var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
var clip = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.AudioClip>(path);
var asm = System.Reflection.Assembly.Load(\"AudioTailor.Editor\");
var t = asm.GetType(\"AudioTailor.Editor.AudioTailorWindow\");
var open = t.GetMethod(\"Open\", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
open.Invoke(null, new object[] { clip });
return \"Opened AudioTailor with: \" + path;
"
```

2. Use the `unity-window-capture` skill to capture the "Audio Tailor" window and visually inspect the result.

### Pass Criteria

- Green waveform bars are rendered across the entire waveform area
- The waveform area is not blank
