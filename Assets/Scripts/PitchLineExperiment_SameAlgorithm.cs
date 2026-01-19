using System;
using UnityEngine;
using Bhaptics.SDK2;

public class PitchLineExperiment_SameAlgorithm : MonoBehaviour
{
    private const int VestMotorCount = 32;

    [Serializable]
    public struct Pair
    {
        public int a;
        public int b;
        public Pair(int a, int b) { this.a = a; this.b = b; }
    }

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
    [SerializeField] private int neighborPairs = 2;

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

    [Header("Pitch Pair Loops (your spec)")]
    [SerializeField] private Pair[] edgeLoop =
    {
        new Pair(12,15), new Pair(8,11), new Pair(4,7), new Pair(0,3),
        new Pair(16,19), new Pair(20,23), new Pair(24,27), new Pair(28,31),
    };

    [SerializeField] private Pair[] centerLoop =
    {
        new Pair(13,14), new Pair(9,10), new Pair(5,6), new Pair(1,2),
        new Pair(17,18), new Pair(21,22), new Pair(25,26), new Pair(29,30),
    };

    private bool _running;
    private float _s;
    private Pair[] _activePairs;

    private readonly float[] _raw01 = new float[VestMotorCount];
    private readonly float[] _smoothed01 = new float[VestMotorCount];
    private readonly int[] _motorValues = new int[VestMotorCount];

    private void Awake()
    {
        _activePairs = edgeLoop;
        ResetState();
    }

    private void OnDisable() => StopAll();

    private void Update()
    {
        if (!_running) return;

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (dt <= 0f) return;

        Array.Clear(_raw01, 0, _raw01.Length);

        if (_activePairs == null || _activePairs.Length == 0)
        {
            SmoothAndSend(dt);
            return;
        }

        int N = _activePairs.Length;

        float cps = angularSpeedDegPerSec / 360f;
        float speedIdxPerSec = cps * N;
        _s = Wrap(_s + speedIdxPerSec * dt, N);

        ApplyGaussianCircular_SeamAware_Pairs(_activePairs, _s, maxIntensity01);

        SmoothAndSend(dt);
    }

    // ===== PUBLIC API =====
    public void StartEdge()
    {
        _activePairs = edgeLoop;
        ResetState();
        _running = true;
    }

    public void StartCenter()
    {
        _activePairs = centerLoop;
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

    private void ApplyGaussianCircular_SeamAware_Pairs(Pair[] pairs, float center, float peak)
    {
        int N = pairs.Length;
        float c = Wrap(center, N);
        int ci = Mathf.FloorToInt(c);

        // 8 pairs: seam between 3 and 4, and between last and first
        const float seamA = 3.5f;
        float seamB = N - 0.5f;

        float sigmaForCenter = IsNearSeam(c, seamA, seamB, N, seamWidthIdx) ? sigmaSeam : sigmaMain;
        sigmaForCenter = Mathf.Max(1e-4f, sigmaForCenter);
        float inv2sig2 = 1f / (2f * sigmaForCenter * sigmaForCenter);

        for (int k = -neighborPairs; k <= neighborPairs; k++)
        {
            int idx = Mod(ci + k, N);
            Pair p = pairs[idx];

            float d = CircularDelta(c, idx, N);
            float g = Mathf.Exp(-(d * d) * inv2sig2);
            if (g < cutoff01) continue;

            float norm = SoftThresholdKneeImproved(g, perceptualThreshold01);
            float outVal = norm * peak;

            WritePairMax(p, outVal);
        }
    }

    private void WritePairMax(Pair p, float v01)
    {
        if (p.a >= 0 && p.a < VestMotorCount) _raw01[p.a] = Mathf.Max(_raw01[p.a], v01);
        if (p.b >= 0 && p.b < VestMotorCount) _raw01[p.b] = Mathf.Max(_raw01[p.b], v01);
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
