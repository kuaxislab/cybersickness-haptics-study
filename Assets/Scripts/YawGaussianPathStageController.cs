using System;
using UnityEngine;
using Bhaptics.SDK2;

public class YawGaussianPathStageController : MonoBehaviour
{
    // =========================
    // Manager compatibility API
    // =========================
    public enum YawStage { Front, Side }

    public void StartStage(YawStage st, float speedDegPerSec)
    {
        angularSpeedDegPerSec = speedDegPerSec;
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

    // Stage 1-1: Front/Back 공통(기본 붓) 조정
    // Stage 1-2: Seam(겨드랑이)만 조정
    public float GetSigmaForStage(YawStage st) => (st == YawStage.Front) ? sigmaFrontBack : sigmaSeam;

    public void SetSigmaForStage(YawStage st, float sigma)
    {
        sigma = Mathf.Max(1e-4f, sigma);
        if (st == YawStage.Front)
        {
            sigmaFrontBack = sigma; // front+back
        }
        else
        {
            sigmaSeam = sigma;      // seam only
        }
    }

    public void SetMaxIntensity01(float v) => maxIntensity01 = Mathf.Clamp01(v);
    public float GetMaxIntensity01() => maxIntensity01;

    // =========================
    // Config
    // =========================
    private const int VestMotorCount = 32;

    [Header("Speed (deg/sec)")]
    [SerializeField] private float angularSpeedDegPerSec = 60f;

    [Header("Intensity")]
    [Range(0f, 1f)]
    [SerializeField] private float maxIntensity01 = 0.30f;

    [Header("Yaw Full Loop Path")]
    [SerializeField] private int[] loopPath = { 4, 5, 6, 7, 23, 22, 21, 20 };

    [Header("Sigma (Front+Back vs Seam)")]
    [Tooltip("Front(4-5-6-7) and Back(23-22-21-20) share this sigma.")]
    [SerializeField] private float sigmaFrontBack = 0.70f;

    [Tooltip("Only applied near seams: (7<->23) and (20<->4). Usually larger.")]
    [SerializeField] private float sigmaSeam = 0.90f;

    [Tooltip("How wide the seam region is (in index units). 0.7~1.3 recommended.")]
    [SerializeField] private float seamWidthIdx = 1.0f;

    [Header("Neighborhood (IMPORTANT)")]
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

    [Header("Time Smoothing")]
    [SerializeField] private float smoothingTau = 0.08f;
    [SerializeField] private bool useUnscaledTime = true;

    [Header("bHaptics Play")]
    [SerializeField] private int durationMillis = 30;

    [Header("Optional: Segment Dwell Control (SAFE VERSION)")]
    [SerializeField] private bool useSegmentSpeedMultiplier = false;
    [SerializeField] private float frontSpeedMul = 1.0f;
    [SerializeField] private float sideSpeedMul = 1.2f;
    [SerializeField] private float speedBlendWidthIdx = 1.2f;

    // =========================
    // Runtime
    // =========================
    private bool _running;
    private float _s;

    private readonly float[] _raw01 = new float[VestMotorCount];
    private readonly float[] _smoothed01 = new float[VestMotorCount];
    private readonly int[] _motorValues = new int[VestMotorCount];

    private void Awake()
    {
        ResetState();
    }

    private void OnValidate()
    {
        neighborCount = Mathf.Max(1, neighborCount);
        sigmaFrontBack = Mathf.Max(1e-4f, sigmaFrontBack);
        sigmaSeam = Mathf.Max(1e-4f, sigmaSeam);
        seamWidthIdx = Mathf.Max(1e-3f, seamWidthIdx);

        smoothingTau = Mathf.Max(1e-4f, smoothingTau);
        durationMillis = Mathf.Max(10, durationMillis);

        frontSpeedMul = Mathf.Max(0.01f, frontSpeedMul);
        sideSpeedMul = Mathf.Max(0.01f, sideSpeedMul);
        speedBlendWidthIdx = Mathf.Max(1e-3f, speedBlendWidthIdx);
    }

    private void Update()
    {
        if (!_running) return;

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (dt <= 0f) return;

        Array.Clear(_raw01, 0, _raw01.Length);

        if (loopPath == null || loopPath.Length == 0)
        {
            SmoothAndSend(dt);
            return;
        }

        int N = loopPath.Length;

        // base speed in indices/sec
        float cps = angularSpeedDegPerSec / 360f;
        float baseSpeedIdxPerSec = cps * N;

        // optional speed multiplier (blended)
        float speedMul = 1f;
        if (useSegmentSpeedMultiplier)
        {
            speedMul = GetBlendedSpeedMultiplier(_s, N);
        }

        float speedIdxPerSec = baseSpeedIdxPerSec * speedMul;

        // full loop update
        _s = Wrap(_s + speedIdxPerSec * dt, N);

        // gaussian brush
        ApplyGaussianCircular_SeamAware(loopPath, _s, maxIntensity01);

        // smooth + send
        SmoothAndSend(dt);
    }

    private void ResetState()
    {
        _s = 0f;
        Array.Clear(_raw01, 0, _raw01.Length);
        Array.Clear(_smoothed01, 0, _smoothed01.Length);
        Array.Clear(_motorValues, 0, _motorValues.Length);
    }

    // =========================
    // Seam-aware Gaussian
    // =========================
    private void ApplyGaussianCircular_SeamAware(int[] path, float center, float peak)
    {
        int N = path.Length;
        float c = Wrap(center, N);
        int ci = Mathf.FloorToInt(c);

        // seam midpoints in index space:
        // seam A: between 3 and 4 => 3.5  (7 <-> 23)
        // seam B: between 7 and 0 => 7.5 (20 <-> 4)  (same as -0.5)
        const float seamA = 3.5f;
        float seamB = (N - 0.5f); // 7.5 when N=8

        // pick sigma based on how close the CENTER is to seam midpoints (circular distance)
        float sigmaForCenter = IsNearSeam(c, seamA, seamB, N, seamWidthIdx) ? sigmaSeam : sigmaFrontBack;
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

            if (outVal > _raw01[motorId]) _raw01[motorId] = outVal;
        }
    }

    private static bool IsNearSeam(float c, float seamA, float seamB, int N, float width)
    {
        // Use circular distance between center and seam midpoints
        float da = CircularDelta(c, seamA, N);
        float db = CircularDelta(c, seamB, N);
        return (Mathf.Abs(da) <= width) || (Mathf.Abs(db) <= width);
    }

    // overload: CircularDelta for float idx
    private static float CircularDelta(float a, float b, int N)
    {
        float d = a - b;
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

        // smoothstep
        return t * t * (3f - 2f * t);
    }

    // =========================
    // Output smoothing + send
    // =========================
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

    // =========================
    // Optional speed multiplier (blended)
    // =========================
    private float GetBlendedSpeedMultiplier(float s, int N)
    {
        // boundary is between index 3 and 4 (front->back). We'll blend around 4.0.
        float c = Wrap(s, N);
        float b = 4f; // boundary start of back segment in this path

        float dist = c - b;
        float w = speedBlendWidthIdx;
        float t = Mathf.Clamp01((dist + w) / (2f * w));
        t = t * t * (3f - 2f * t);

        return Mathf.Lerp(frontSpeedMul, sideSpeedMul, t);
    }

    // =========================
    // Math helpers
    // =========================
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

    private static float CircularDelta(float center, int idx, int N)
    {
        float d = center - idx;
        float half = N * 0.5f;
        if (d > half) d -= N;
        if (d < -half) d += N;
        return d;
    }
}
