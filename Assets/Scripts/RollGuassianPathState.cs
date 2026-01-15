using System;
using UnityEngine;
using Bhaptics.SDK2;

public class RollGuassianPathState : MonoBehaviour
{
    public enum SpeedMode { DegreesPerSecond, RadiansPerSecond, CyclesPerSecond }

    [Header("Speed")]
    [SerializeField] private SpeedMode speedMode = SpeedMode.DegreesPerSecond;
    [SerializeField] private float angularSpeedDegPerSec = 60f;
    [SerializeField] private float angularSpeedRadPerSec = 1.0f;
    [SerializeField] private float cyclesPerSecond = 0.25f;

    [Header("Intensity")]
    [Range(0f, 1f)] [SerializeField] private float maxIntensity01 = 0.85f;

    [Header("Gaussian Shape")]
    [SerializeField] private float sigmaStepsMain = 0.9f;
    [SerializeField] private float sigmaStepsSub = 0.75f;

    [SerializeField] private int neighborCountMain = 2; // ±2 => 5 motors
    [SerializeField] private int neighborCountSub = 1;  // ±1 => 3 motors
    [SerializeField] private float cutoff01 = 0.02f;

    [Header("Weights per group")]
    [Range(0f, 1f)] [SerializeField] private float mainScale = 1.0f;
    [Range(0f, 1f)] [SerializeField] private float subScale  = 0.9f;

    [Header("Smoothing")]
    [SerializeField] private float smoothingTau = 0.08f;
    [SerializeField] private bool useUnscaledTime = true;

    [Header("bHaptics Call")]
    [SerializeField] private int durationMillis = 50;

    private const int VestMotorCount = 32; // ✅ 32로 통일(vest)

    [Header("Roll Paths (Front/Back run together)")]
    [SerializeField] private int[] mainFront = { 12, 13, 14, 15, 11, 7, 3, 2, 1, 0, 4, 8 };
    [SerializeField] private int[] mainBack  = { 28, 29, 30, 31, 27, 23, 19, 18, 17, 16, 20, 24 };

    [SerializeField] private int[] subFront  = { 9, 10, 6, 5 };
    [SerializeField] private int[] subBack   = { 25, 26, 22, 21 };

    private float[] _raw01 = new float[VestMotorCount];
    private float[] _smoothed01 = new float[VestMotorCount];

    private bool _running;

    private int _step;        // 0..11
    private float _phase;     // 0..1 within current step
    private int MainSteps => mainFront.Length; // 12
    private int SubSteps  => subFront.Length;  // 4

    private void OnDisable() => StopHaptics();

    private void Update()
    {
        if (!_running) return;

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (dt <= 0f) return;

        float cps = GetCyclesPerSecond();
        float stepsPerSec = cps * MainSteps;

        _phase += stepsPerSec * dt;
        while (_phase >= 1f)
        {
            _phase -= 1f;
            _step = (_step + 1) % MainSteps;
        }

        Array.Clear(_raw01, 0, _raw01.Length);

        float centerMain = _step + _phase;
        ApplyGaussianOnPath(mainFront, centerMain, sigmaStepsMain, neighborCountMain, mainScale);
        ApplyGaussianOnPath(mainBack,  centerMain, sigmaStepsMain, neighborCountMain, mainScale);

        float centerSub = GetSubCenterFromMain(_step, _phase);
        ApplyGaussianOnPath(subFront, centerSub, sigmaStepsSub, neighborCountSub, subScale);
        ApplyGaussianOnPath(subBack,  centerSub, sigmaStepsSub, neighborCountSub, subScale);

        float a = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, smoothingTau));
        for (int i = 0; i < VestMotorCount; i++)
            _smoothed01[i] = Mathf.Lerp(_smoothed01[i], _raw01[i], a);

        int[] motorValues = new int[VestMotorCount];
        for (int i = 0; i < VestMotorCount; i++)
            motorValues[i] = Mathf.RoundToInt(Mathf.Clamp01(_smoothed01[i]) * 100f);

        BhapticsLibrary.PlayMotors((int)PositionType.Vest, motorValues, durationMillis);
    }

    // ===== ✅ Manager hooks =====
    public void StartStage(float speedDegPerSec)
    {
        SetSpeedDegPerSec(speedDegPerSec);
        StartHaptics();
    }

    public void SetSpeedDegPerSec(float degPerSec)
    {
        speedMode = SpeedMode.DegreesPerSecond;
        angularSpeedDegPerSec = degPerSec;
    }

    public void StartHaptics()
    {
        _step = 0;
        _phase = 0f;

        Array.Clear(_raw01, 0, _raw01.Length);
        Array.Clear(_smoothed01, 0, _smoothed01.Length);

        _running = true;
    }

    public void StopHaptics()
    {
        _running = false;

        Array.Clear(_raw01, 0, _raw01.Length);
        Array.Clear(_smoothed01, 0, _smoothed01.Length);

        int[] zeros = new int[VestMotorCount];
        BhapticsLibrary.PlayMotors((int)PositionType.Vest, zeros, 100);
    }

    private void ApplyGaussianOnPath(int[] path, float center, float sigmaSteps, int neighborCount, float scale)
    {
        int n = path.Length;
        if (n < 2) return;

        center = Repeat(center, n);

        float inv2sig2 = 1f / (2f * sigmaSteps * sigmaSteps);
        int centerIdx = Mathf.FloorToInt(center);

        for (int k = -neighborCount; k <= neighborCount; k++)
        {
            int idx = Mod(centerIdx + k, n);
            int motorId = path[idx];
            if (motorId < 0 || motorId >= VestMotorCount) continue;

            float d = ShortestCyclicDistance(center, idx, n);
            float val = maxIntensity01 * Mathf.Exp(-(d * d) * inv2sig2) * scale;
            if (val < cutoff01) continue;

            if (val > _raw01[motorId]) _raw01[motorId] = val;
        }
    }

    private float GetSubCenterFromMain(int mainStep, float mainPhase)
    {
        int groupSize = MainSteps / SubSteps; // 3
        float mainPos = mainStep + mainPhase;
        return mainPos / groupSize; // [0,4)
    }

    private float GetCyclesPerSecond()
    {
        switch (speedMode)
        {
            case SpeedMode.DegreesPerSecond: return angularSpeedDegPerSec / 360f;
            case SpeedMode.RadiansPerSecond: return angularSpeedRadPerSec / (2f * Mathf.PI);
            case SpeedMode.CyclesPerSecond:
            default: return cyclesPerSecond;
        }
    }

    private static int Mod(int x, int m)
    {
        int r = x % m;
        return r < 0 ? r + m : r;
    }

    private static float Repeat(float x, float len)
    {
        x %= len;
        if (x < 0f) x += len;
        return x;
    }

    private static float ShortestCyclicDistance(float a, float b, float len)
    {
        float d = a - b;
        d = (d + len * 0.5f) % len;
        if (d < 0f) d += len;
        d -= len * 0.5f;
        return d;
    }
}
