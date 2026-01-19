using System;
using UnityEngine;
using Bhaptics.SDK2;

/// <summary>
/// Pitch full-loop Gaussian brush (Yaw Seam-aware Algorithm applied to Pitch)
/// - Always full loop over (FrontPairs + BackPairs)
/// - Constant speed (NO dwell control)
/// - sigmaMain: body brush (Front + Back)
/// - sigmaSeam: only near 2 seam boundaries
///   Seam A (Top): between Front end and Back start
///   Seam B (Bottom): between Back end and Front start
///
/// Manager stage meaning:
/// - PitchStage.Front : sigmaMain tuning (body)
/// - PitchStage.Top   : sigmaSeam tuning (seams)
/// </summary>
public class PitchGaussianPathStageController : MonoBehaviour
{
    private const int VestMotorCount = 32;

    [Serializable]
    public struct Pair
    {
        public int a;
        public int b;
        public Pair(int a, int b) { this.a = a; this.b = b; }
    }

    // =========================
    // Manager compatibility API
    // =========================
    public enum PitchStage { Front, Top } // "Top" is used as seam-tuning stage

    public void StartStage(PitchStage st, float speedDegPerSec)
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

    public float GetSigmaForStage(PitchStage st) => (st == PitchStage.Front) ? sigmaMain : sigmaSeam;

    public void SetSigmaForStage(PitchStage st, float sigma)
    {
        sigma = Mathf.Max(1e-4f, sigma);
        if (st == PitchStage.Front) sigmaMain = sigma;   // body brush
        else sigmaSeam = sigma;                          // seam brush
    }

    public void SetMaxIntensity01(float v) => maxIntensity01 = Mathf.Clamp01(v);
    public float GetMaxIntensity01() => maxIntensity01;

    // =========================
    // Paths (Front -> Back loop)
    // =========================
    [Header("Pitch Loop Pairs")]
    [Tooltip("Front (Upward): 12,15 -> 8,11 -> 4,7 -> 0,3")]
    [SerializeField] private Pair[] frontPairs =
    {
        new Pair(12,15),
        new Pair(8,11),
        new Pair(4,7),
        new Pair(0,3),
    };

    [Tooltip("Back (Downward): 16,19 -> 20,23 -> 24,27 -> 28,31")]
    [SerializeField] private Pair[] backPairs =
    {
        new Pair(16,19),
        new Pair(20,23),
        new Pair(24,27),
        new Pair(28,31),
    };

    // =========================
    // Config (Yaw와 동일 결)
    // =========================
    [Header("Speed (deg/sec)")]
    [SerializeField] private float angularSpeedDegPerSec = 60f;

    [Header("Intensity")]
    [Range(0f, 1f)]
    [SerializeField] private float maxIntensity01 = 0.30f;

    [Header("Sigma (Main vs Seam)")]
    [Tooltip("Sigma for main body (Front+Back).")]
    [SerializeField] private float sigmaMain = 0.70f;

    [Tooltip("Sigma for seams only (Top & Bottom connections). Usually larger.")]
    [SerializeField] private float sigmaSeam = 0.90f;

    [Tooltip("Width of seam region (index units). 0.7~1.3 recommended.")]
    [SerializeField] private float seamWidthIdx = 1.0f;

    [Header("Neighborhood")]
    [Tooltip("2 is recommended for smoothness at low speed.")]
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

    [Header("bHaptics")]
    [SerializeField] private int durationMillis = 30;

    // =========================
    // Runtime
    // =========================
    private bool _running;
    private float _s;          // center position [0..N)
    private Pair[] _loopPairs; // front + back
    private int _N;
    private int _frontLen;

    private readonly float[] _raw01 = new float[VestMotorCount];
    private readonly float[] _smoothed01 = new float[VestMotorCount];
    private readonly int[] _motorValues = new int[VestMotorCount];

    private void Awake()
    {
        RebuildLoop();
        ResetState();
    }

    private void OnValidate()
    {
        neighborPairs = Mathf.Max(1, neighborPairs);
        sigmaMain = Mathf.Max(1e-4f, sigmaMain);
        sigmaSeam = Mathf.Max(1e-4f, sigmaSeam);
        seamWidthIdx = Mathf.Max(1e-3f, seamWidthIdx);
        smoothingTau = Mathf.Max(1e-4f, smoothingTau);
        durationMillis = Mathf.Max(10, durationMillis);

        RebuildLoop();
    }

    private void Update()
    {
        if (!_running) return;

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (dt <= 0f) return;

        Array.Clear(_raw01, 0, _raw01.Length);

        if (_loopPairs == null || _N <= 0)
        {
            SmoothAndSend(dt);
            return;
        }

        // Yaw와 동일: constant speed in index-space
        float cps = angularSpeedDegPerSec / 360f;
        float speedIdxPerSec = cps * _N;

        _s = Wrap(_s + speedIdxPerSec * dt, _N);

        ApplyGaussianCircular_SeamAware(_s, maxIntensity01);
        SmoothAndSend(dt);
    }

    private void RebuildLoop()
    {
        int fl = (frontPairs == null) ? 0 : frontPairs.Length;
        int bl = (backPairs == null) ? 0 : backPairs.Length;

        _frontLen = fl;
        _N = Mathf.Max(0, fl + bl);
        _loopPairs = new Pair[_N];

        int w = 0;
        for (int i = 0; i < fl; i++) _loopPairs[w++] = frontPairs[i];
        for (int i = 0; i < bl; i++) _loopPairs[w++] = backPairs[i];
    }

    private void ResetState()
    {
        RebuildLoop();
        _s = 0f;
        Array.Clear(_raw01, 0, _raw01.Length);
        Array.Clear(_smoothed01, 0, _smoothed01.Length);
        Array.Clear(_motorValues, 0, _motorValues.Length);
    }

    // =========================
    // Seam-aware Gaussian (Yaw랑 동일 아이디어)
    // =========================
    private void ApplyGaussianCircular_SeamAware(float center, float peak)
    {
        float c = Wrap(center, _N);
        int ci = Mathf.FloorToInt(c);

        // Seam A: between Front end and Back start => frontLen - 0.5
        // Seam B: between Back end and Front start => N - 0.5
        float seamA = (_frontLen <= 0) ? 0.5f : (_frontLen - 0.5f);
        float seamB = _N - 0.5f;

        float sigmaForCenter = IsNearSeam(c, seamA, seamB, _N, seamWidthIdx) ? sigmaSeam : sigmaMain;
        sigmaForCenter = Mathf.Max(1e-4f, sigmaForCenter);
        float inv2sig2 = 1f / (2f * sigmaForCenter * sigmaForCenter);

        for (int k = -neighborPairs; k <= neighborPairs; k++)
        {
            int idx = Mod(ci + k, _N);
            Pair p = _loopPairs[idx];

            float d = CircularDelta(c, idx, _N);
            float g = Mathf.Exp(-(d * d) * inv2sig2);
            if (g < cutoff01) continue;

            float norm = SoftThresholdKneeImproved(g, perceptualThreshold01);
            float outVal = norm * peak;

            WritePairMax(p, outVal);
        }
    }

    private static bool IsNearSeam(float c, float seamA, float seamB, int N, float width)
    {
        float da = CircularDelta(c, seamA, N);
        float db = CircularDelta(c, seamB, N);
        return (Mathf.Abs(da) <= width) || (Mathf.Abs(db) <= width);
    }

    private void WritePairMax(Pair p, float v01)
    {
        if (p.a >= 0 && p.a < VestMotorCount) _raw01[p.a] = Mathf.Max(_raw01[p.a], v01);
        if (p.b >= 0 && p.b < VestMotorCount) _raw01[p.b] = Mathf.Max(_raw01[p.b], v01);
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

    private static float CircularDelta(float a, float b, int N)
    {
        float d = a - b;
        float half = N * 0.5f;
        if (d > half) d -= N;
        if (d < -half) d += N;
        return d;
    }
}
