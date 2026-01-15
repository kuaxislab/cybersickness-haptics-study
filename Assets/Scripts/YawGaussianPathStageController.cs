using System;
using UnityEngine;
using Bhaptics.SDK2;

public class YawGaussianPathStageController : MonoBehaviour
{
    public enum SpeedMode { DegreesPerSecond, RadiansPerSecond, CyclesPerSecond }
    public enum Stage { FrontOnly, FrontThenSide } // ✅ next stage = front+side sweep

    // ===============================
    // Compatibility API for SigmaExperimentManager_TrialsRandom
    // (Old API: YawStage + StartStage/SetSigmaForStage/GetSigmaForStage)
    // ===============================
    public enum YawStage { Front, Side }

    public float GetSigmaForStage(YawStage st)
    {
    return (st == YawStage.Front) ? GetSigmaFrontLocked() : GetSigmaSide();
    }

    public void SetSigmaForStage(YawStage st, float sigma)
    {
    if (st == YawStage.Front) SetSigmaFrontLocked(sigma);
    else SetSigmaSide(sigma);
    }

    public void StartStage(YawStage st, float speedDegPerSec)
    {
    // ✅ manager가 Side stage를 요청하면, 우리가 원하는 "FrontThenSide"로 실행
    if (st == YawStage.Front) StartStageFrontOnly(speedDegPerSec);
    else StartStageFrontThenSide(speedDegPerSec);
    }


    private const int VestMotorCount = 32;

    [Header("Paths")]
    [SerializeField] private int[] frontPath = { 4, 5, 6, 7 };
    [SerializeField] private int[] sidePath  = { 6, 7, 23, 22 };

    [Header("Stage")]
    [SerializeField] private Stage stage = Stage.FrontOnly;

    [Header("Speed Input (set by manager)")]
    [SerializeField] private SpeedMode speedMode = SpeedMode.DegreesPerSecond;
    [SerializeField] private float angularSpeedDegPerSec = 60f;
    [SerializeField] private float angularSpeedRadPerSec = 1.0f;
    [SerializeField] private float cyclesPerSecond = 0.25f;

    [Header("Cycle Rest")]
    [Tooltip("After completing ONE full sweep, pause outputs for this duration.")]
    [SerializeField] private float restAfterCycleSec = 0.6f;
    [Tooltip("If true, freeze motion during rest.")]
    [SerializeField] private bool freezeMotionDuringRest = true;

    [Header("Intensity")]
    [Range(0f, 1f)] [SerializeField] private float maxIntensity01 = 0.10f;

    [Header("Sigma (Front fixed, Side adjustable)")]
    [Tooltip("Front sigma is 'locked' once user decides it in the FrontOnly stage.")]
    [SerializeField] private float sigmaFront = 0.35f;

    [Tooltip("Side sigma is what user adjusts in FrontThenSide stage.")]
    [SerializeField] private float sigmaSide = 0.60f;

    [Header("Sigma Transition (log-domain blend)")]
    [Tooltip("Blend width around the front->side boundary in 'index units'. 0.8~1.6 recommended.")]
    [SerializeField] private float sigmaBlendWindow = 1.2f;

    [Header("Neighborhood (Rings-like: 3 motors only)")]
    [SerializeField] private bool limitToNeighbors = true;
    [SerializeField] private int neighborCount = 1; // ✅ 1 => exactly 3 motors

    [Header("Cutoff / Threshold (normalized 0..1)")]
    [Range(0f, 0.4f)] [SerializeField] private float cutoff01 = 0.12f;
    [Range(0f, 0.5f)] [SerializeField] private float perceptualThreshold01 = 0.08f;

    [Header("Output shaping")]
    [Range(0f, 0.2f)] [SerializeField] private float minOn01 = 0.02f;
    [SerializeField] private float outputGamma = 1.0f;

    [Header("Smoothing")]
    [SerializeField] private float smoothingTau = 0.10f;
    [SerializeField] private bool useUnscaledTime = true;

    [Header("bHaptics")]
    [SerializeField] private int durationMillis = 80;

    [Header("Wrap behavior")]
    [Tooltip("If true: when wrap happens, skip output this frame and enter rest (prevents 'first motor repeats').")]
    [SerializeField] private bool skipOutputOnWrap = true;

    // ===== runtime =====
    private bool _running;
    private float _restTimer;

    private float _s;                     // path position (0..totalLen)
    private int[] _combinedPathCache;      // front+side
    private int _frontLen, _sideLen, _combinedLen;

    private readonly float[] _raw01 = new float[VestMotorCount];
    private readonly float[] _smoothed01 = new float[VestMotorCount];
    private readonly int[] _motorValues = new int[VestMotorCount];

    private void Awake()
    {
        RebuildCombinedPath();
        ResetState();
    }

    private void OnValidate()
    {
        if (!Application.isPlaying)
        {
            RebuildCombinedPath();
            ResetState();
        }
    }

    private void Update()
    {
        if (!_running) return;

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (dt <= 0f) return;

        // Rest phase
        if (_restTimer > 0f)
        {
            _restTimer -= dt;

            Array.Clear(_raw01, 0, _raw01.Length);
            SmoothAndSend(dt);

            if (!freezeMotionDuringRest && _restTimer > 0f)
                AdvanceSOnly(dt);

            return;
        }

        Array.Clear(_raw01, 0, _raw01.Length);

        float totalLen = GetTotalLenForStage();
        bool wrapped;
        (_s, wrapped) = AdvanceS_WithWrap(_s, totalLen, dt);

        if (wrapped && restAfterCycleSec > 0.0001f)
        {
            _restTimer = restAfterCycleSec;

            // ✅ Remove "first motor repeats" at cycle boundary
            if (skipOutputOnWrap)
            {
                Array.Clear(_raw01, 0, _raw01.Length);
                SmoothAndSend(dt);
                return;
            }
        }

        ApplyStageAtS(_s);

        SmoothAndSend(dt);
    }

    // ===== Public API =====
    public void StartStageFrontOnly(float speedDegPerSec)
    {
        stage = Stage.FrontOnly;
        SetSpeedDegPerSec(speedDegPerSec);
        ResetState();
        _running = true;
    }

    public void StartStageFrontThenSide(float speedDegPerSec)
    {
        stage = Stage.FrontThenSide;
        SetSpeedDegPerSec(speedDegPerSec);
        ResetState();
        _running = true;
    }

    public void StopAll()
    {
        _running = false;
        _restTimer = 0f;

        Array.Clear(_raw01, 0, _raw01.Length);
        Array.Clear(_smoothed01, 0, _smoothed01.Length);
        Array.Clear(_motorValues, 0, _motorValues.Length);

        BhapticsLibrary.PlayMotors((int)PositionType.Vest, _motorValues, 80);
    }

    // front sigma는 "사용자가 FrontOnly에서 결정한 값"으로 고정해두고
    // side sigma만 next stage에서 slider로 조절
    public void SetSigmaFrontLocked(float sigma) => sigmaFront = Mathf.Max(1e-4f, sigma);
    public void SetSigmaSide(float sigma) => sigmaSide = Mathf.Max(1e-4f, sigma);

    public float GetSigmaFrontLocked() => sigmaFront;
    public float GetSigmaSide() => sigmaSide;

    public void SetSpeedDegPerSec(float degPerSec)
    {
        speedMode = SpeedMode.DegreesPerSecond;
        angularSpeedDegPerSec = degPerSec;
    }

    // ===== Core =====
    private void ResetState()
    {
        RebuildCombinedPath();

        _s = 0f;
        _restTimer = 0f;

        Array.Clear(_raw01, 0, _raw01.Length);
        Array.Clear(_smoothed01, 0, _smoothed01.Length);
    }

    private void RebuildCombinedPath()
    {
        _frontLen = (frontPath == null) ? 0 : frontPath.Length;
        _sideLen  = (sidePath == null) ? 0 : sidePath.Length;

        // combined = front + side (no special de-dupe; neighbor=1 will still be local)
        _combinedLen = Mathf.Max(0, _frontLen + _sideLen);
        _combinedPathCache = new int[_combinedLen];

        int w = 0;
        for (int i = 0; i < _frontLen; i++) _combinedPathCache[w++] = frontPath[i];
        for (int i = 0; i < _sideLen;  i++) _combinedPathCache[w++] = sidePath[i];
    }

    private float GetTotalLenForStage()
    {
        if (stage == Stage.FrontOnly) return Mathf.Max(1f, _frontLen);
        return Mathf.Max(1f, _combinedLen);
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

    private float AdvanceS(float s, float totalLen, float dt)
    {
        float cps = GetCyclesPerSecond();
        float speed = cps * totalLen;     // steps per sec
        return Wrap(s + speed * dt, totalLen);
    }

    private void AdvanceSOnly(float dt)
    {
        float totalLen = GetTotalLenForStage();
        _s = AdvanceS(_s, totalLen, dt);
    }

    private (float s, bool wrapped) AdvanceS_WithWrap(float s, float totalLen, float dt)
    {
        float cps = GetCyclesPerSecond();
        float speed = cps * totalLen;
        float next = s + speed * dt;

        bool wrapped = false;
        if (next >= totalLen) wrapped = true;

        next = Wrap(next, totalLen);
        return (next, wrapped);
    }

    private void ApplyStageAtS(float s)
    {
        if (stage == Stage.FrontOnly)
        {
            // ✅ Front만, sigmaFront 고정
            ApplyPathNeighborLimited(frontPath, s, sigmaFront, maxIntensity01, perceptualThreshold01);
            return;
        }

        // ✅ FrontThenSide: front 구간은 sigmaFront (locked),
        // side 구간은 sigmaSide (slider),
        // boundary에서는 log-domain(pow)로 자연스럽게 blend
        ApplyPathNeighborLimited_CombinedSigma(_combinedPathCache, s, maxIntensity01);
    }

    private void ApplyPathNeighborLimited_CombinedSigma(int[] path, float s, float peak)
    {
        if (path == null || path.Length == 0) return;

        int N = path.Length;
        float center = Mathf.Clamp(s, 0f, N - 1e-4f);
        int c = Mathf.FloorToInt(center);

        int K = limitToNeighbors ? Mathf.Clamp(neighborCount, 0, N / 2) : (N / 2);

        for (int k = -K; k <= K; k++)
        {
            int idx = c + k;                 // ✅ no Mod
            if (idx < 0 || idx >= N) continue;

            int motorId = path[idx];
            if (motorId < 0 || motorId >= VestMotorCount) continue;

            float d = center - idx;          // ✅ no Wrap distance

            float blend = GetSideBlendByIndex(center, _frontLen, sigmaBlendWindow);
            float localSigma = sigmaFront * Mathf.Pow(sigmaSide / Mathf.Max(1e-6f, sigmaFront), blend);

            float inv2sig2 = 1f / (2f * Mathf.Max(1e-4f, localSigma) * Mathf.Max(1e-4f, localSigma));
            float gauss = Mathf.Exp(-(d * d) * inv2sig2);

            if (gauss < cutoff01) continue;

            float norm = SoftThresholdKnee(gauss, perceptualThreshold01);
            float outVal = norm * peak;

            if (outVal > _raw01[motorId]) _raw01[motorId] = outVal;
        }

    }

    // Simple helper if you want non-combined use (front only etc.)
    private void ApplyPathNeighborLimited(int[] path, float s, float sigma, float peak, float thr01)
    {
        if (path == null || path.Length == 0) return;

        int N = path.Length;
        float center = Mathf.Clamp(s, 0f, N - 1e-4f);
        int c = Mathf.FloorToInt(center);

        int K = limitToNeighbors ? Mathf.Clamp(neighborCount, 0, N / 2) : (N / 2);

        float inv2sig2 = 1f / (2f * Mathf.Max(1e-4f, sigma) * Mathf.Max(1e-4f, sigma));

        for (int k = -K; k <= K; k++)
        {
            int idx = c + k;                 // ✅ linear index (no wrap)
            if (idx < 0 || idx >= N) continue;

            int motorId = path[idx];
            if (motorId < 0 || motorId >= VestMotorCount) continue;

            float d = center - idx;          // ✅ linear distance (no wrap)
            float gauss = Mathf.Exp(-(d * d) * inv2sig2);
            if (gauss < cutoff01) continue;

            float norm = SoftThresholdKnee(gauss, thr01);
            float outVal = norm * peak;

            if (outVal > _raw01[motorId]) _raw01[motorId] = outVal;
        }

    }

    // blend=0 in front region, blend=1 in side region, smooth around boundary
    private static float GetSideBlendByIndex(float centerIdx, int frontLen, float window)
    {
        if (frontLen <= 0) return 1f;
        float boundary = frontLen; // side starts at this index

        float w = Mathf.Max(1e-3f, window);
        float x = (centerIdx - boundary) / w; // 0 at boundary

        // Map x to smoothstep 0..1 across [-1, +1] range
        float t = Mathf.Clamp01((x + 1f) * 0.5f);
        return SmoothStep01(t);
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
                norm = SoftThresholdKnee(norm, perceptualThreshold01);

                if (norm > 0f && norm < minOn01) norm = minOn01;
                norm = Mathf.Pow(norm, Mathf.Max(0.01f, outputGamma));
                v = norm * peak;
            }

            _motorValues[i] = Mathf.RoundToInt(Mathf.Clamp01(v) * 100f);
        }

        BhapticsLibrary.PlayMotors((int)PositionType.Vest, _motorValues, durationMillis);
    }

    // ===== helpers =====
    private static float Wrap(float x, float m)
    {
        if (m <= 0f) return 0f;
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

    private static float SmoothStep01(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    // soft-knee threshold on normalized signal
    private static float SoftThresholdKnee(float x01, float thr01)
    {
        x01 = Mathf.Clamp01(x01);
        thr01 = Mathf.Clamp01(thr01);
        if (thr01 <= 0f) return x01;

        if (x01 < thr01)
        {
            float t = Mathf.Clamp01(x01 / Mathf.Max(1e-6f, thr01));
            float s = t * t * (3f - 2f * t);
            return x01 * s;
        }
        else
        {
            float y = (x01 - thr01) / Mathf.Max(1e-6f, (1f - thr01));
            y = Mathf.Clamp01(y);
            return y * y * (3f - 2f * y);
        }
    }
}
