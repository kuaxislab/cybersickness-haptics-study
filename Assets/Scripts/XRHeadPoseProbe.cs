using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class XRHeadPoseProbe : MonoBehaviour
{
    void Update()
    {
        var headPos = InputTracking.GetLocalPosition(XRNode.Head);
        var headRot = InputTracking.GetLocalRotation(XRNode.Head);
        Debug.Log($"[XRNode.Head] pos={headPos} rot={headRot.eulerAngles}");

        // 디바이스도 같이 체크
        var dev = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (dev.isValid)
        {
            if (dev.TryGetFeatureValue(CommonUsages.devicePosition, out var p) &&
                dev.TryGetFeatureValue(CommonUsages.deviceRotation, out var r))
            {
                Debug.Log($"[InputDevice] pos={p} rot={r.eulerAngles}");
            }
            else
            {
                Debug.Log("[InputDevice] Head device valid but no pose feature values.");
            }
        }
        else
        {
            Debug.Log("[InputDevice] Head device NOT valid.");
        }
    }
}
