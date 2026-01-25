using System;
using UnityEngine;
using Bhaptics.SDK2;

public class VRRotationHapticsManualController : MonoBehaviour
{
    public enum AxisType { Yaw, Roll, Pitch }
    public enum HapticCondition { GuideDirection, OppositeDirection, RandomMotors, None }

    [Header("References")]
    [Tooltip("XR Origin 또는 OVRCameraRig '루트 Transform' (Main Camera X)")]
    public Transform rigRoot;

    public YawGaussianPathStageController yawController;
    public RollGuassianPathState rollController; // 네 프로젝트 롤 타입
    public PitchGaussianPathStageController pitchController;

    [Header("Current Setting (Inspector에서 바꾸고 Play/Stop)")]
    public AxisType axis = AxisType.Yaw;
    public HapticCondition condition = HapticCondition.GuideDirection;

    [Tooltip("deg/sec. 카메라 회전 속도 = 햅틱 진행 속도(Guide/Opposite일 때)")]
    public float angularSpeedDegPerSec = 40f;

    [Tooltip("Time.timeScale 영향 안 받게")]
    public bool useUnscaledTime = true;

    [Header("Haptics - Common")]
    [Range(0f, 1f)] public float maxIntensity01 = 0.30f;

    public bool useNormalization = false;
    public YawGaussianPathStageController.NormalizationMode yawNormalizationMode = YawGaussianPathStageController.NormalizationMode.None;
    public PitchGaussianPathStageController.NormalizationMode pitchNormalizationMode = PitchGaussianPathStageController.NormalizationMode.None;
    public float energyTargetMotors = 2.5f;

    [Header("Haptics - Yaw Sigmas")]
    public float yawSigmaFrontBack = 0.70f;
    public float yawSigmaSeam = 0.90f;

    [Header("Haptics - Pitch Sigmas")]
    public float pitchSigmaMain = 0.70f;
    public float pitchSigmaSeam = 0.90f;

    [Header("Random Motors Condition")]
    public float randomUpdateInterval = 0.10f;
    [Range(1, 10)] public int randomMotorCount = 3;
    [Range(0f, 1f)] public float randomIntensity01 = 0.25f;
    public int randomDurationMs = 60;


    // ===== runtime =====
    [SerializeField, Tooltip("현재 실행 중 여부(읽기용)")]
    private bool isRunning = false;

    private float _randomElapsed = 0f;
    private readonly int[] _randomMotors = new int[32];

    private void OnDisable()
    {
        StopNow();
    }

    private void Update()
    {
        if (!isRunning) return;

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (dt <= 0f) return;

        RotateRig(dt);

        if (condition == HapticCondition.RandomMotors)
        {
            _randomElapsed += dt;
            if (_randomElapsed >= Mathf.Max(0.01f, randomUpdateInterval))
            {
                _randomElapsed = 0f;
                SendRandomMotorsOnce();
            }
        }
    }

    // ======================
    // Inspector 버튼으로 누를 API
    // ======================
    public void PlayNow()
    {
        if (rigRoot == null)
        {
            Debug.LogError("[Manual] rigRoot가 비어있음. XR Origin/OVRCameraRig 루트를 넣어.");
            return;
        }

        StopAllHapticsHard();
        ApplyCommonHapticParams();

        isRunning = true;
        _randomElapsed = 0f;

        float hapticSpeed =
            (condition == HapticCondition.OppositeDirection) ? -angularSpeedDegPerSec :
            (condition == HapticCondition.GuideDirection) ?  angularSpeedDegPerSec :
            0f;

        if (condition == HapticCondition.GuideDirection || condition == HapticCondition.OppositeDirection)
        {
            StartAxisHaptics(hapticSpeed);
        }
        else if (condition == HapticCondition.RandomMotors)
        {
            Array.Clear(_randomMotors, 0, _randomMotors.Length);
        }
        // None이면 햅틱 없음(카메라만 회전)
    }

    public void StopNow()
    {
        isRunning = false;
        _randomElapsed = 0f;
        StopAllHapticsHard();
    }

    // ======================
    // 내부 동작
    // ======================
    private void RotateRig(float dt)
    {
        Vector3 axisVec = axis switch
        {
            AxisType.Yaw => Vector3.up,
            AxisType.Roll => Vector3.forward,
            AxisType.Pitch => Vector3.right,
            _ => Vector3.up
        };

        float angle = angularSpeedDegPerSec * dt;
        rigRoot.Rotate(axisVec, angle, Space.Self);
    }

    private void ApplyCommonHapticParams()
    {
        if (yawController != null) yawController.SetMaxIntensity01(maxIntensity01);
        if (pitchController != null) pitchController.SetMaxIntensity01(maxIntensity01);
        if (rollController != null) rollController.SetMaxIntensity01(maxIntensity01);

        if (yawController != null)
        {
            yawController.SetSigmaForStage(YawGaussianPathStageController.YawStage.Front, yawSigmaFrontBack);
            yawController.SetSigmaForStage(YawGaussianPathStageController.YawStage.Side, yawSigmaSeam);

            if (useNormalization)
                yawController.SetNormalizationMode(yawNormalizationMode, energyTargetMotors);
            else
                yawController.SetNormalizationMode(YawGaussianPathStageController.NormalizationMode.None, energyTargetMotors);
        }

        if (pitchController != null)
        {
            pitchController.SetSigmaForStage(PitchGaussianPathStageController.PitchStage.Front, pitchSigmaMain);
            pitchController.SetSigmaForStage(PitchGaussianPathStageController.PitchStage.Top, pitchSigmaSeam);

            if (useNormalization)
                pitchController.SetNormalizationMode(pitchNormalizationMode, energyTargetMotors);
            else
                pitchController.SetNormalizationMode(PitchGaussianPathStageController.NormalizationMode.None, energyTargetMotors);
        }
    }

    private void StartAxisHaptics(float hapticSpeedDegPerSec)
    {
        switch (axis)
        {
            case AxisType.Yaw:
                if (yawController == null) { Debug.LogError("[Manual] yawController null"); return; }
                yawController.StopAll();
                yawController.StartStage(YawGaussianPathStageController.YawStage.Front, hapticSpeedDegPerSec);
                break;

            case AxisType.Roll:
                if (rollController == null) { Debug.LogError("[Manual] rollController null"); return; }
                rollController.StopHaptics();
                rollController.StartStage(Mathf.Abs(hapticSpeedDegPerSec)); // roll은 현재 부호 의미 약함
                break;

            case AxisType.Pitch:
                if (pitchController == null) { Debug.LogError("[Manual] pitchController null"); return; }
                pitchController.StopAll();
                pitchController.StartStage(PitchGaussianPathStageController.PitchStage.Front, hapticSpeedDegPerSec);
                break;
        }
    }

    private void SendRandomMotorsOnce()
    {
        Array.Clear(_randomMotors, 0, _randomMotors.Length);

        int k = Mathf.Clamp(randomMotorCount, 1, 10);
        int intensity = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(randomIntensity01) * 100f), 0, 100);

        for (int i = 0; i < k; i++)
        {
            int tries = 0;
            while (tries++ < 50)
            {
                int m = UnityEngine.Random.Range(0, 32);
                if (_randomMotors[m] == 0)
                {
                    _randomMotors[m] = intensity;
                    break;
                }
            }
        }

        BhapticsLibrary.PlayMotors((int)PositionType.Vest, _randomMotors, Mathf.Max(10, randomDurationMs));
    }

    private void StopAllHapticsHard()
    {
        if (yawController != null) yawController.StopAll();
        if (rollController != null) rollController.StopHaptics();
        if (pitchController != null) pitchController.StopAll();

        Array.Clear(_randomMotors, 0, _randomMotors.Length);
        BhapticsLibrary.PlayMotors((int)PositionType.Vest, _randomMotors, 60);
    }
}
