using System;
using UnityEngine;
using Bhaptics.SDK2;

public class RollLineExperiment_SameAlgorithm : MonoBehaviour
{
    private const int VestMotorCount = 32;

    public enum RollMode { Front, Back, Both }

    [Header("Speed (deg/sec)")]
    [SerializeField] private float angularSpeedDegPerSec = 60f;

    [Header("Intensity")]
    [Range(0f, 1f)]
    [SerializeField] private float maxIntensity01 = 0.30f;

    [Header("Sigma (Main vs Seam)")]
    [SerializeField] private float sigmaMain = 0.70f;
    [SerializeField] private float sigmaSeam = 0.90f;
    [SerializeField] private float seamWidthIdx = 1.0f;

    [Header("Neighborhood")]
    [SerializeField] private int neighborCount = 2;

    [Header("Threshold & Cutoff")]
    [Range(0f, 0.4f)]
    [SerializeField] private float cutoff01 = 0.05f;

    [Range(0f, 0.5f)]
    [SerializeField] private float perceptualThreshold01 = 0.05f;

    [Header("Output shaping")]
    [Range(0f, 0.2f)]
    [SerializeField] private float minOn01 = 0.00f;

    [SerializeField] private float outputGamma = 1.20f;

    [Header("Time smoothing")]
    [SerializeField] private float smoothingTau = 0.08f;
    [SerializeField] private bool useUnscaledTime = true;

    [Header("bHaptics Play")]
    [SerializeField] private int durationMillis = 30;

    [Header("Roll Loops (your spec)")]
    [SerializeField] private int[] frontLoop = { 12, 13, 14, 15, 11, 7, 3, 2, 1, 0, 4, 8 };
    [SerializeField] private int[] backLoop  = { 28, 29, 30, 31, 27, 23, 19, 18, 17, 16, 20, 24 };

    private bool _running;
    private float _s;                 // shared phase for all modes
    private RollMode _mode = RollMode.Front;

    private readonly float[] _raw01 = new float[VestMotorCount];
    private readonly float[] _smoothed01 = new float[VestMotorCount];
    private readonly int[] _motorValues = new int[VestMotorCount];

    private void Awake()
    {
        ResetState();
    }

    private void OnDisable() => StopAll();

    private void Update()
    {
        if (!_running) return;

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (dt <= 0f) return;

        Array.Clear(_raw01, 0, _raw01.Length);

        // choose length N
        int N = GetActiveLength();
        if (N <= 0)
        {
            SmoothAndSend(dt);
            return;
        }

        float cps = angularSpeedDegPerSec / 360f;
        float speedIdxPerSec = cps * N;
        _s = Wrap(_s + speedIdxPerSec * dt, N);

        // Apply according to mode
        if (_mode == RollMode.Front)
        {
            ApplyGaussianCircular_SeamAware(frontLoop, _s, maxIntensity01);
        }
        else if (_mode == RollMode.Back)
        {
            ApplyGaussianCircular_SeamAware(backLoop, _s, maxIntensity01);
        }
        else // Both: front + back 동시에 (같은 phase)
        {
            ApplyGaussianCircular_SeamAware(frontLoop, _s, maxIntensity01);
            ApplyGaussianCircular_SeamAware(backLoop,  _s, maxIntensity01);
        }

        SmoothAndSend(dt);
    }

    private int GetActiveLength()
    {
        if (_mode == RollMode.Front) return (frontLoop != null) ? frontLoop.Length : 0;
        if (_mode == RollMode.Back)  return (backLoop  != null) ? backLoop.Length  : 0;

        // Both: 둘 다 길이가 있어야 함. (길이가 다르면 더 짧은 쪽 기준으로 phase가 꼬일 수 있어서 min 사용)
        int a = (frontLoop != null) ? frontLoop.Length : 0;
        int b = (backLoop  != null) ? backLoop.Length  : 0;
        if (a <= 0 || b <= 0) return 0;
        return Mathf.Min(a, b);
    }

    // ===== PUBLIC API =====
    public void StartFront()
    {
        _mode = RollMode.Front;
        ResetState();
        _running = true;
    }

    public void StartBack()
    {
        _mode = RollMode.Back;
        ResetState();
        _running = true;
    }

    // 앞/뒤를 동시에 울림 (동일 phase, 동일 speed)
    public void StartBoth()
    {
        _mode = RollMode.Both;
        ResetState();
        _running = true;
    }

    public void StopAll()
    {
        _running = false;
        Array.Clear(_raw01, 0, _raw01.Length);
        Array.Clear(_smoothed01, 0, _smoothed01.Length);
        Array.Clear(_motorValues, 0, _motorValues.Length);
        BhapticsLibrary.PlayMotors((int)PositionType.Vest, _motorValues, 80);
    }

    public void SetMaxIntensity01(float v) => maxIntensity01 = Mathf.Clamp01(v);

    public void SetSigmas(float main, float seam)
    {
        sigmaMain = Mathf.Max(1e-4f, main);
        sigmaSeam = Mathf.Max(1e-4f, seam);
    }

    public void SetSpeedAndDuration(float speedDegPerSec, int durationMs)
    {
        angularSpeedDegPerSec = speedDegPerSec;
        durationMillis = Mathf.Max(10, durationMs);
    }

    public void SetShaping(float thr, float cutoff, float tau, float gamma, float minOn, float seamWidth)
    {
        perceptualThreshold01 = Mathf.Clamp01(thr);
        cutoff01 = Mathf.Clamp01(cutoff);
        smoothingTau = Mathf.Max(1e-4f, tau);
        outputGamma = Mathf.Max(0.01f, gamma);
        minOn01 = Mathf.Clamp(minOn, 0f, 0.2f);
        seamWidthIdx = Mathf.Max(1e-3f, seamWidth);
    }

    // ===== internals =====
    private void ResetState()
    {
        _s = 0f;
        Array.Clear(_raw01, 0, _raw01.Length);
        Array.Clear(_smoothed01, 0, _smoothed01.Length);
        Array.Clear(_motorValues, 0, _motorValues.Length);
    }

    private void ApplyGaussianCircular_SeamAware(int[] path, float center, float peak)
    {
        if (path == null || path.Length == 0) return;

        int N = path.Length;
        float c = Wrap(center, N);
        int ci = Mathf.FloorToInt(c);

        // roll loops are length 12 -> seam style: between 3 and 4, and between last and first
        const float seamA = 3.5f;
        float seamB = N - 0.5f;

        float sigmaForCenter = IsNearSeam(c, seamA, seamB, N, seamWidthIdx) ? sigmaSeam : sigmaMain;
        sigmaForCenter = Mathf.Max(1e-4f, sigmaForCenter);
        float inv2sig2 = 1f / (2f * sigmaForCenter * sigmaForCenter);

        for (int k = -neighborCount; k <= neighborCount; k++)
        {
            int idx = Mod(ci + k, N);
            int motorId = path[idx];
            if (motorId < 0 || motorId >= VestMotorCount) continue;

            float d = CircularDelta(c, idx, N);
            float g = Mathf.Exp(-(d * d) * inv2sig2);
            if (g < cutoff01) continue;

            float norm = SoftThresholdKneeImproved(g, perceptualThreshold01);
            float outVal = norm * peak;

            // Both 모드에서는 front/back이 서로 다른 motorId에 들어가서 그냥 max로 합쳐도 됨.
            if (outVal > _raw01[motorId]) _raw01[motorId] = outVal;
        }
    }

    private static bool IsNearSeam(float c, float seamA, float seamB, int N, float width)
    {
        float da = CircularDelta(c, seamA, N);
        float db = CircularDelta(c, seamB, N);
        return (Mathf.Abs(da) <= width) || (Mathf.Abs(db) <= width);
    }

    private static float CircularDelta(float a, float b, int N)
    {
        float d = a - b;
        float half = N * 0.5f;
        if (d > half) d -= N;
        if (d < -half) d += N;
        return d;
    }

    private static float CircularDelta(float center, int idx, int N)
    {
        float d = center - idx;
        float half = N * 0.5f;
        if (d > half) d -= N;
        if (d < -half) d += N;
        return d;
    }

    private static float SoftThresholdKneeImproved(float x01, float thr01)
    {
        if (x01 <= thr01) return 0f;

        float range = 1f - thr01;
        if (range <= 1e-6f) return x01;

        float t = (x01 - thr01) / range;
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    private void SmoothAndSend(float dt)
    {
        float alpha = 1f - Mathf.Exp(-dt / Mathf.Max(1e-5f, smoothingTau));
        float peak = Mathf.Max(1e-4f, maxIntensity01);

        for (int i = 0; i < VestMotorCount; i++)
        {
            _smoothed01[i] = Mathf.Lerp(_smoothed01[i], _raw01[i], alpha);
            float v = _smoothed01[i];

            if (v > 0f)
            {
                float norm = Mathf.Clamp01(v / peak);
                if (minOn01 > 0f && norm > 0f && norm < minOn01) norm = minOn01;
                norm = Mathf.Pow(norm, Mathf.Max(0.01f, outputGamma));
                v = norm * peak;
            }

            _motorValues[i] = Mathf.RoundToInt(Mathf.Clamp01(v) * 100f);
        }

        BhapticsLibrary.PlayMotors((int)PositionType.Vest, _motorValues, durationMillis);
    }

    private static float Wrap(float x, int m)
    {
        if (m <= 0) return 0f;
        x %= m;
        if (x < 0f) x += m;
        return x;
    }

    private static int Mod(int x, int m)
    {
        int r = x % m;
        if (r < 0) r += m;
        return r;
    }
}
