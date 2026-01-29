using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class XRSubsystemProbe : MonoBehaviour
{
    void Start()
    {
        var inputs = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(inputs);

        Debug.Log($"XRInputSubsystem count = {inputs.Count}");
        foreach (var s in inputs)
        {
            Debug.Log($"XRInputSubsystem running={s.running}");
        }
    }
}
