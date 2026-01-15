using System;
using UnityEngine;
using Bhaptics.SDK2;

/// <summary>
/// Index-list / pair 기반 "스텝 이동 + 펄스" 플레이어.
/// - stepIntervalSec 마다 centerIndex가 1칸씩 이동(정수)
/// - 각 스텝 구간에서 pulse(ON/OFF)로 "움직인다" 느낌을 강화
/// - gaussian 꼬리는 cutoff + topK로 정리
/// </summary>
public class IndexPatternHapticsPlayer : MonoBehaviour
{
    public const int VestMotorCount = 32;

    [Header("Timing")]
    [SerializeField] private bool useUnscaledTime = true;
    [SerializeField] private int durationMillis = 25;          // PlayMotors duration
    [SerializeField] private float stepIntervalSec = 0.25f;    // ✅ 스텝 간격 (0.15~0.35 추천)
    [SerializeField] private float pulseOnSec = 0.14f;         // ✅ 한 스텝에서 켜져있는 시간
    [SerializeField] private float pulseFadeSec = 0.03f;       // ✅ on->off 부드럽게(짧게)

    [Header("Intensity")]
    [Range(0f, 1f)] [SerializeField] private float maxIntensity01 = 1.0f;
    [SerializeField] private float outputGamma = 1.0f;

    [Header("Selection (to make motion salient)")]
    [SerializeField] private float cutoff01 = 0.06f;  // ✅ 꼬리 제거(기존 0.02보다 올림 추천)
    [SerializeField] private int topK = 3;            // ✅ 최대 몇 개 모터만 울릴지 (2~4 추천)

    // ===== runtime =====
    private bool _running;
    private float _sigma = 0.9f;

    private Mode _mode = Mode.None;

    private int[] _indicesA;
    private int[] _indicesB;
    private MotorPair[] _pairs;

    private int _centerIndex;      // 정수 center
    private float _stepTimer;      // stepInterval 누적
    private float _pulseTimer;     // pulse envelope
    private int _lenCached;

    private float[] _raw01 = new float[VestMotorCount];
    private int[] _motorValues = new int[VestMotorCount];

    public enum Mode { None, SingleList, DualListSync, PairList }

    [Serializable]
    public struct MotorPair
    {
        public int a;
        public int b;
        public MotorPair(int a, int b) { this.a = a; this.b = b; }
    }

    private void Update()
    {
        if (!_running) return;

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (dt <= 0f) return;

        _stepTimer += dt;
        _pulseTimer += dt;

        // ✅ 스텝 이동: 일정 간격마다 centerIndex 증가
        while (_stepTimer >= stepIntervalSec && _lenCached > 0)
        {
            _stepTimer -= stepIntervalSec;
            _centerIndex = (_centerIndex + 1) % _lenCached;
            _pulseTimer = 0f; // 새 스텝 시작 -> 펄스 리셋
        }

        float pulse = ComputePulse(_pulseTimer);

        Array.Clear(_raw01, 0, _raw01.Length);

        switch (_mode)
        {
            case Mode.SingleList:
                ApplyGaussianToList(_indicesA, _centerIndex, _sigma, pulse);
                break;

            case Mode.DualListSync:
                ApplyGaussianToList(_indicesA, _centerIndex, _sigma, pulse);
                ApplyGaussianToList(_indicesB, _centerIndex, _sigma, pulse);
                break;

            case Mode.PairList:
                ApplyGaussianToPairs(_pairs, _centerIndex, _sigma, pulse);
                break;

            default:
                return;
        }

        // ✅ topK 강제(움직임 단서 강화)
        ApplyTopKMask(_raw01, topK);

        for (int i = 0; i < VestMotorCount; i++)
        {
            float v = Mathf.Clamp01(_raw01[i]);
            if (v > 0f) v = Mathf.Pow(v, Mathf.Max(0.01f, outputGamma));
            _motorValues[i] = Mathf.RoundToInt(v * 100f);
        }

        BhapticsLibrary.PlayMotors((int)PositionType.Vest, _motorValues, durationMillis);
    }

    private float ComputePulse(float t)
    {
        // ON 구간: 0~pulseOnSec
        if (t <= pulseOnSec) return 1f;

        // Fade 구간: pulseOnSec ~ pulseOnSec + pulseFadeSec
        if (t <= pulseOnSec + pulseFadeSec)
        {
            float u = (t - pulseOnSec) / Mathf.Max(1e-5f, pulseFadeSec);
            return Mathf.Lerp(1f, 0f, u);
        }

        // OFF
        return 0f;
    }

    // ===== Public controls =====

    public void Stop()
    {
        _running = false;
        Array.Clear(_motorValues, 0, _motorValues.Length);
        BhapticsLibrary.PlayMotors((int)PositionType.Vest, _motorValues, 1);
    }

    public void StartSingleList(int[] indices, float speedDegPerSec_ignored, float sigma, float startCenter = 0f)
    {
        _mode = Mode.SingleList;
        _indicesA = indices;
        _indicesB = null;
        _pairs = null;

        _sigma = Mathf.Max(0.0001f, sigma);

        _lenCached = (indices == null) ? 0 : indices.Length;
        _centerIndex = (int)Mathf.Repeat(startCenter, Mathf.Max(1, _lenCached));
        _stepTimer = 0f;
        _pulseTimer = 0f;
        _running = true;
    }

    public void StartDualListSync(int[] indicesA, int[] indicesB, float speedDegPerSec_ignored, float sigma, float startCenter = 0f)
    {
        _mode = Mode.DualListSync;
        _indicesA = indicesA;
        _indicesB = indicesB;
        _pairs = null;

        _sigma = Mathf.Max(0.0001f, sigma);

        _lenCached = Mathf.Min(indicesA?.Length ?? 0, indicesB?.Length ?? 0);
        _centerIndex = (int)Mathf.Repeat(startCenter, Mathf.Max(1, _lenCached));
        _stepTimer = 0f;
        _pulseTimer = 0f;
        _running = true;
    }

    public void StartPairList(MotorPair[] pairs, float speedDegPerSec_ignored, float sigma, float startCenter = 0f)
    {
        _mode = Mode.PairList;
        _pairs = pairs;
        _indicesA = null;
        _indicesB = null;

        _sigma = Mathf.Max(0.0001f, sigma);

        _lenCached = (pairs == null) ? 0 : pairs.Length;
        _centerIndex = (int)Mathf.Repeat(startCenter, Mathf.Max(1, _lenCached));
        _stepTimer = 0f;
        _pulseTimer = 0f;
        _running = true;
    }

    public void SetSigma(float sigma) => _sigma = Mathf.Max(0.0001f, sigma);

    /// <summary>
    /// ✅ 이제 "속도"는 stepIntervalSec로 표현하는 게 체감이 좋아서,
    /// deg/s를 직접 쓰지 않고, 30/60/90을 stepInterval로 매핑한다.
    /// (실험 매니저에서 호출해도 되고, 여기서도 제공)
    /// </summary>
    public void SetSpeedDegPerSec(float degPerSec)
    {
        // 30/60/90에 대해 체감이 분리되도록 간단 매핑(Inspector에서 직접 수정 가능하게 하고 싶으면 public 변수로 빼도 됨)
        if (degPerSec <= 35f) stepIntervalSec = 0.30f;
        else if (degPerSec <= 75f) stepIntervalSec = 0.22f;
        else stepIntervalSec = 0.16f;
    }

    // ===== Gaussian application =====

    private void ApplyGaussianToList(int[] list, int centerIndex, float sigma, float pulse)
    {
        if (list == null || list.Length == 0) return;

        for (int i = 0; i < list.Length; i++)
        {
            float d = CircularDistance(i, centerIndex, list.Length);
            float w = Gaussian(d, sigma) * pulse;

            if (w < cutoff01) continue;

            int motor = list[i];
            if (motor < 0 || motor >= VestMotorCount) continue;

            _raw01[motor] = Mathf.Max(_raw01[motor], w * maxIntensity01);
        }
    }

    private void ApplyGaussianToPairs(MotorPair[] pairs, int centerIndex, float sigma, float pulse)
    {
        if (pairs == null || pairs.Length == 0) return;

        for (int i = 0; i < pairs.Length; i++)
        {
            float d = CircularDistance(i, centerIndex, pairs.Length);
            float w = Gaussian(d, sigma) * pulse;

            if (w < cutoff01) continue;

            int a = pairs[i].a;
            int b = pairs[i].b;
            if (a >= 0 && a < VestMotorCount) _raw01[a] = Mathf.Max(_raw01[a], w * maxIntensity01);
            if (b >= 0 && b < VestMotorCount) _raw01[b] = Mathf.Max(_raw01[b], w * maxIntensity01);
        }
    }

    private static float Gaussian(float d, float sigma)
    {
        float s2 = sigma * sigma;
        return Mathf.Exp(-(d * d) / (2f * Mathf.Max(1e-6f, s2)));
    }

    private static float CircularDistance(float i, float center, int len)
    {
        float diff = Mathf.Abs(i - center);
        return Mathf.Min(diff, len - diff);
    }

    private static void ApplyTopKMask(float[] arr, int k)
    {
        if (k <= 0) { Array.Clear(arr, 0, arr.Length); return; }
        // k가 배열보다 크면 그대로
        if (k >= arr.Length) return;

        // 간단 topK: k번째 큰 값(threshold) 찾기
        // (O(n*k)지만 n=32라 충분)
        float[] copy = new float[arr.Length];
        Array.Copy(arr, copy, arr.Length);

        for (int t = 0; t < k; t++)
        {
            int best = -1;
            float bestVal = -1f;
            for (int i = 0; i < copy.Length; i++)
            {
                if (copy[i] > bestVal)
                {
                    bestVal = copy[i];
                    best = i;
                }
            }
            if (best >= 0) copy[best] = -999f; // mark
        }

        // threshold는 (k번째 선택 이후) 남아있는 최대값보다 큰 값들이 topK였음.
        // 다시 한 번 topK 인덱스를 찾아 마스크 적용
        bool[] keep = new bool[arr.Length];
        for (int t = 0; t < k; t++)
        {
            int best = -1;
            float bestVal = -1f;
            for (int i = 0; i < arr.Length; i++)
            {
                if (!keep[i] && arr[i] > bestVal)
                {
                    bestVal = arr[i];
                    best = i;
                }
            }
            if (best >= 0 && bestVal > 0f) keep[best] = true;
        }

        for (int i = 0; i < arr.Length; i++)
        {
            if (!keep[i]) arr[i] = 0f;
        }
    }
}
