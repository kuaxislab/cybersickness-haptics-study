#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(VRRotationHapticsManualController))]
public class VRRotationHapticsManualControllerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var t = (VRRotationHapticsManualController)target;

        GUILayout.Space(10);
        GUILayout.BeginHorizontal();

        if (GUILayout.Button("PLAY", GUILayout.Height(30)))
            t.PlayNow();

        if (GUILayout.Button("STOP", GUILayout.Height(30)))
            t.StopNow();

        GUILayout.EndHorizontal();
    }
}
#endif
