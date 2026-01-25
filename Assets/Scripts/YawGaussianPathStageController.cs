using System;
using UnityEngine;
using Bhaptics.SDK2;

public class YawGaussianPathStageController : MonoBehaviour
{
    public enum NormalizationMode { None, Peak, Energy }

    // =========================
    // Manager compatibility API
    // =========================
    public enum YawStage { Front, Side }

    public void SetNormalizationMode(NormalizationMode mode, float energyTargetMotors = 2.5f)
    {
        normalizationMode = mode;
        energyTarget = Mathf.Max(0.01f, energyTargetMotors);
    }

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
        Array.Clear(_motorValues01, 0, _motorValues01.Length);
        Array.Clear(_motorValuesInt, 0, _motorValuesInt.Length);

        // ✅ SDK는 int[]를 원함
        BhapticsLibrary.PlayMotors((int)PositionType.Vest, _motorValuesInt, 80);
    }

    public float GetSigmaForStage(YawStage st) => (st == YawStage.Front) ? sigmaFrontBack : sigmaSeam;

    public void SetSigmaForStage(YawStage st, float sigma)
    {
        sigma = Mathf.Max(1e-4f, sigma);
        if (st == YawStage.Front) sigmaFrontBack = sigma;
        else sigmaSeam = sigma;
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

    [Header("Perceived Intensity Lock (optional)")]
    [SerializeField] private NormalizationMode normalizationMode = NormalizationMode.None;

    [Tooltip("Energy mode target: interpreted as sum(v/peak), i.e., 'number of motors at peak'.")]
    [SerializeField] private float energyTarget = 2.5f;

    [Header("Normalization Smoothing")]
    [SerializeField] private float normalizationTau = 0.12f; // 0.08~0.20 recommended
    private float _normScaleSmoothed = 1f;

    [Header("Yaw Full Loop Path")]
    [SerializeField] private int[] loopPath = { 4, 5, 6, 7, 23, 22, 21, 20 };

    [Header("Sigma (Front+Back vs Seam)")]
    [SerializeField] private float sigmaFrontBack = 0.70f;
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

    [Header("Smoothing")]
    [SerializeField] private float smoothingTau = 0.08f;
    [SerializeField] private bool useUnscaledTime = true;

    [Header("bHaptics Call")]
    [SerializeField] private int durationMillis = 50;

    // =========================
    // Runtime state
    // =========================
    private bool _running;
    private float _pos; // continuous position along loopPath (0..N)

    private float[] _raw01 = new float[VestMotorCount];
    private float[] _smoothed01 = new float[VestMotorCount];

    // 내부는 0..1 float로 유지
    private float[] _motorValues01 = new float[VestMotorCount];
    // ✅ SDK 전송은 0..100 int
    private int[] _motorValuesInt = new int[VestMotorCount];

    private void OnDisable() => StopAll();

    private void ResetState()
    {
        _pos = 0f;
        Array.Clear(_raw01, 0, _raw01.Length);
        Array.Clear(_smoothed01, 0, _smoothed01.Length);
        Array.Clear(_motorValues01, 0, _motorValues01.Length);
        Array.Clear(_motorValuesInt, 0, _motorValuesInt.Length);
        _normScaleSmoothed = 1f;
    }

    private void OnValidate()
    {
        neighborCount = Mathf.Max(0, neighborCount);
        sigmaFrontBack = Mathf.Max(1e-4f, sigmaFrontBack);
        sigmaSeam = Mathf.Max(1e-4f, sigmaSeam);
        seamWidthIdx = Mathf.Max(1e-4f, seamWidthIdx);
        smoothingTau = Mathf.Max(1e-4f, smoothingTau);
        normalizationTau = Mathf.Max(1e-4f, normalizationTau);
        energyTarget = Mathf.Max(0.01f, energyTarget);
        durationMillis = Mathf.Max(10, durationMillis);
    }

    private void Update()
    {
        if (!_running) return;

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (dt <= 0f) return;

        int n = loopPath.Length;
        float loopsPerSec = angularSpeedDegPerSec / 360f;
        float stepsPerSec = loopsPerSec * n;

        _pos += stepsPerSec * dt;
        _pos %= n;

        Array.Clear(_raw01, 0, _raw01.Length);

        ApplySeamAwareGaussian(_pos);

        NormalizeRawIfNeeded(dt);

        float a = 1f - Mathf.Exp(-dt / Mathf.Max(1e-5f, smoothingTau));
        for (int i = 0; i < VestMotorCount; i++)
            _smoothed01[i] = Mathf.Lerp(_smoothed01[i], _raw01[i], a);

        // output shaping (float 0..1)
        for (int i = 0; i < VestMotorCount; i++)
        {
            float v = _smoothed01[i];
            if (v < perceptualThreshold01) v = 0f;
            if (v > 0f && v < minOn01) v = minOn01;
            _motorValues01[i] = v;
        }

        // ✅ float(0..1) -> int(0..100) 변환
        for (int i = 0; i < VestMotorCount; i++)
        {
            _motorValuesInt[i] = Mathf.Clamp(Mathf.RoundToInt(_motorValues01[i] * 100f), 0, 100);
        }

        BhapticsLibrary.PlayMotors((int)PositionType.Vest, _motorValuesInt, durationMillis);
    }

    private void NormalizeRawIfNeeded(float dt)
    {
        if (normalizationMode == NormalizationMode.None)
        {
            _normScaleSmoothed = 1f;
            return;
        }

        float peak = Mathf.Max(1e-4f, maxIntensity01);
        float targetScale = 1f;

        if (normalizationMode == NormalizationMode.Peak)
        {
            float maxRaw = 0f;
            for (int i = 0; i < VestMotorCount; i++) maxRaw = Mathf.Max(maxRaw, _raw01[i]);
            if (maxRaw > 1e-6f) targetScale = peak / maxRaw;
        }
        else // Energy
        {
            float sumNorm = 0f;
            for (int i = 0; i < VestMotorCount; i++) sumNorm += (_raw01[i] / peak);
            float desired = Mathf.Max(1e-3f, energyTarget);
            if (sumNorm > 1e-6f) targetScale = desired / sumNorm;
        }

        float tau = Mathf.Max(1e-5f, normalizationTau);
        float a = 1f - Mathf.Exp(-dt / tau);
        _normScaleSmoothed = Mathf.Lerp(_normScaleSmoothed, targetScale, a);

        float scale = _normScaleSmoothed;
        if (Mathf.Abs(scale - 1f) < 1e-4f) return;

        for (int i = 0; i < VestMotorCount; i++)
            _raw01[i] = Mathf.Clamp(_raw01[i] * scale, 0f, peak);
    }

    private void ApplySeamAwareGaussian(float centerIdx)
    {
        int n = loopPath.Length;

        // seam positions: between 7<->23 (index 3<->4), and 20<->4 (index 7<->0)
        float seamA = 3.5f;
        float seamB = 7.5f;

        for (int k = -neighborCount; k <= neighborCount; k++)
        {
            float x = centerIdx + k;
            int idx = Mod(Mathf.RoundToInt(x), n);

            float d = WrapDist(centerIdx, idx, n);
            float seamW = SeamWeight(centerIdx, seamA, seamB, n, seamWidthIdx);
            float sigma = Mathf.Lerp(sigmaFrontBack, sigmaSeam, seamW);

            float g = Mathf.Exp(-(d * d) / (2f * sigma * sigma));
            float v = maxIntensity01 * g;

            if (v < cutoff01) continue;

            int motor = loopPath[idx];
            _raw01[motor] = Mathf.Max(_raw01[motor], v);
        }
    }

    private float SeamWeight(float centerIdx, float seamA, float seamB, int n, float width)
    {
        float da = WrapAbs(centerIdx - seamA, n);
        float db = WrapAbs(centerIdx - seamB, n);
        float d = Mathf.Min(da, db);
        return Mathf.Clamp01(1f - (d / Mathf.Max(1e-4f, width)));
    }

    private float WrapAbs(float x, int n)
    {
        x = Mathf.Abs(x);
        return Mathf.Min(x, n - x);
    }

    private float WrapDist(float a, float b, int n)
    {
        float d = Mathf.Abs(a - b);
        return Mathf.Min(d, n - d);
    }

    private int Mod(int x, int m)
    {
        int r = x % m;
        return (r < 0) ? r + m : r;
    }
}
