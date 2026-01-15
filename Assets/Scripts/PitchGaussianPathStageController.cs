using System;
using UnityEngine;
using Bhaptics.SDK2;

public class PitchGaussianPathStageController : MonoBehaviour
{
    public enum SpeedMode { DegreesPerSecond, RadiansPerSecond, CyclesPerSecond }
    public enum Stage { FrontOnly, FrontThenTop }

    private const int VestMotorCount = 32;

    // 각 step은 "왼/오른(또는 좌/우)" 2개 모터를 한 쌍으로 울림
    [Serializable]
    public struct Pair
    {
        public int a;
        public int b;
        public Pair(int a, int b) { this.a = a; this.b = b; }
    }

    // ===============================
    // Compatibility API for SigmaExperimentManager_TrialsRandom
    // (Old API: PitchStage + StartStage/SetSigmaForStage/GetSigmaForStage)
    // ===============================
    public enum PitchStage { Front, Top }

    public float GetSigmaForStage(PitchStage st)
    {
        return (st == PitchStage.Front) ? GetSigmaFrontLocked() : GetSigmaTop();
    }

    public void SetSigmaForStage(PitchStage st, float sigma)
    {
        if (st == PitchStage.Front) SetSigmaFrontLocked(sigma);
        else SetSigmaTop(sigma);
    }

    public void StartStage(PitchStage st, float speedDegPerSec)
    {
        // ✅ manager가 Top stage를 요청하면, 우리가 원하는 "FrontThenTop"로 실행
        if (st == PitchStage.Front) StartStageFrontOnly(speedDegPerSec);
        else StartStageFrontThenTop(speedDegPerSec);
    }


    [SerializeField] private Pair[] frontPairs =
    {
        new Pair(13,14),
        new Pair(9,10),
        new Pair(5,6),
        new Pair(1,2),
    };

    [SerializeField] private Pair[] topPairs =
    {
        new Pair(12,15),
        new Pair(8,11),
        new Pair(4,7),
        new Pair(0,3),
    };

    [Header("Stage")]
    [SerializeField] private Stage stage = Stage.FrontOnly;

    [Header("Speed Input (set by manager)")]
    [SerializeField] private SpeedMode speedMode = SpeedMode.DegreesPerSecond;
    [SerializeField] private float angularSpeedDegPerSec = 60f;
    [SerializeField] private float angularSpeedRadPerSec = 1.0f;
    [SerializeField] private float cyclesPerSecond = 0.25f;

    [Header("Cycle Rest")]
    [SerializeField] private float restAfterCycleSec = 0.6f;
    [SerializeField] private bool freezeMotionDuringRest = true;

    [Header("Intensity")]
    [Range(0f, 1f)] [SerializeField] private float maxIntensity01 = 0.10f;

    [Header("Sigma (Front fixed, Top adjustable)")]
    [SerializeField] private float sigmaFront = 0.35f;
    [SerializeField] private float sigmaTop   = 0.60f;

    [Header("Sigma Transition (log-domain blend)")]
    [SerializeField] private float sigmaBlendWindow = 1.2f;

    [Header("Neighborhood (3 pairs only)")]
    [SerializeField] private bool limitToNeighborPairs = true;
    [SerializeField] private int neighborPairs = 1; // ✅ 1 => exactly 3 pairs

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
    [SerializeField] private bool skipOutputOnWrap = true;

    // ===== runtime =====
    private bool _running;
    private float _restTimer;
    private float _s;

    private Pair[] _combinedPairs;
    private int _frontLen, _topLen, _combinedLen;

    private readonly float[] _raw01 = new float[VestMotorCount];
    private readonly float[] _smoothed01 = new float[VestMotorCount];
    private readonly int[] _motorValues = new int[VestMotorCount];

    private void Awake()
    {
        RebuildCombined();
        ResetState();
    }

    private void OnValidate()
    {
        if (!Application.isPlaying)
        {
            RebuildCombined();
            ResetState();
        }
    }

    private void Update()
    {
        if (!_running) return;

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (dt <= 0f) return;

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

    // ===== public API =====
    public void StartStageFrontOnly(float speedDegPerSec)
    {
        stage = Stage.FrontOnly;
        SetSpeedDegPerSec(speedDegPerSec);
        ResetState();
        _running = true;
    }

    public void StartStageFrontThenTop(float speedDegPerSec)
    {
        stage = Stage.FrontThenTop;
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

    public void SetSigmaFrontLocked(float sigma) => sigmaFront = Mathf.Max(1e-4f, sigma);
    public void SetSigmaTop(float sigma) => sigmaTop = Mathf.Max(1e-4f, sigma);

    public float GetSigmaFrontLocked() => sigmaFront;
    public float GetSigmaTop() => sigmaTop;

    public void SetSpeedDegPerSec(float degPerSec)
    {
        speedMode = SpeedMode.DegreesPerSecond;
        angularSpeedDegPerSec = degPerSec;
    }

    // ===== internals =====
    private void ResetState()
    {
        RebuildCombined();
        _s = 0f;
        _restTimer = 0f;
        Array.Clear(_raw01, 0, _raw01.Length);
        Array.Clear(_smoothed01, 0, _smoothed01.Length);
    }

    private void RebuildCombined()
    {
        _frontLen = (frontPairs == null) ? 0 : frontPairs.Length;
        _topLen   = (topPairs == null) ? 0 : topPairs.Length;

        _combinedLen = Mathf.Max(0, _frontLen + _topLen);
        _combinedPairs = new Pair[_combinedLen];

        int w = 0;
        for (int i = 0; i < _frontLen; i++) _combinedPairs[w++] = frontPairs[i];
        for (int i = 0; i < _topLen;   i++) _combinedPairs[w++] = topPairs[i];
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

    private void AdvanceSOnly(float dt)
    {
        float totalLen = GetTotalLenForStage();
        _s = AdvanceS(_s, totalLen, dt);
    }

    private float AdvanceS(float s, float totalLen, float dt)
    {
        float cps = GetCyclesPerSecond();
        float speed = cps * totalLen;
        return Wrap(s + speed * dt, totalLen);
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
            ApplyPairsNeighborLimited(frontPairs, s, sigmaFront, maxIntensity01, perceptualThreshold01);
            return;
        }

        ApplyPairsNeighborLimited_CombinedSigma(_combinedPairs, s, maxIntensity01);
    }

    private void ApplyPairsNeighborLimited_CombinedSigma(Pair[] pairs, float s, float peak)
    {
        if (pairs == null || pairs.Length == 0) return;

        int N = pairs.Length;

        // ✅ combined 구간에서는 "선형"으로만: 마지막<->처음 이웃 처리(원형 wrap) 금지
        float center = Mathf.Clamp(s, 0f, N - 1e-4f);
        int c = Mathf.FloorToInt(center);

        int K = limitToNeighborPairs ? Mathf.Clamp(neighborPairs, 0, N / 2) : (N / 2);

        for (int k = -K; k <= K; k++)
        {
            int idx = c + k;
            if (idx < 0 || idx >= N) continue;

            Pair p = pairs[idx];

            // ✅ 선형 거리
            float d = center - idx;

            // frontLen 경계 기준으로 sigma log-blend
            float blend = GetTopBlendByIndex(center, _frontLen, sigmaBlendWindow);
            float localSigma = sigmaFront * Mathf.Pow(sigmaTop / Mathf.Max(1e-6f, sigmaFront), blend);

            float sig = Mathf.Max(1e-4f, localSigma);
            float inv2sig2 = 1f / (2f * sig * sig);
            float gauss = Mathf.Exp(-(d * d) * inv2sig2);

            if (gauss < cutoff01) continue;

            float norm = SoftThresholdKnee(gauss, perceptualThreshold01);
            float outVal = norm * peak;

            WritePairMax(p, outVal);
        }
    }

    private void ApplyPairsNeighborLimited(Pair[] pairs, float s, float sigma, float peak, float thr01)
    {
        if (pairs == null || pairs.Length == 0) return;

        int N = pairs.Length;

        // ✅ 여기서도 s를 "선형 인덱스 좌표"로 취급 (0..N)
        float center = Mathf.Clamp(s, 0f, N - 1e-4f);
        int c = Mathf.FloorToInt(center);

        int K = limitToNeighborPairs ? Mathf.Clamp(neighborPairs, 0, N / 2) : (N / 2);

        float sig = Mathf.Max(1e-4f, sigma);
        float inv2sig2 = 1f / (2f * sig * sig);

        for (int k = -K; k <= K; k++)
        {
            int idx = c + k;
            if (idx < 0 || idx >= N) continue;

            float d = center - idx; // 선형 거리
            float gauss = Mathf.Exp(-(d * d) * inv2sig2);

            if (gauss < cutoff01) continue;

            float norm = SoftThresholdKnee(gauss, thr01);
            float outVal = norm * peak;

            WritePairMax(pairs[idx], outVal);
        }
    }



    private void WritePairMax(Pair p, float v01)
    {
        if (p.a >= 0 && p.a < VestMotorCount) _raw01[p.a] = Mathf.Max(_raw01[p.a], v01);
        if (p.b >= 0 && p.b < VestMotorCount) _raw01[p.b] = Mathf.Max(_raw01[p.b], v01);
    }

    private static float GetTopBlendByIndex(float centerIdx, int frontLen, float window)
    {
        if (frontLen <= 0) return 1f;
        float boundary = frontLen;

        float w = Mathf.Max(1e-3f, window);
        float x = (centerIdx - boundary) / w;

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
