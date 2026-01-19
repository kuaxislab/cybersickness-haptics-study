using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class ExperimentFlowManager : MonoBehaviour
{
    public enum AxisStep { Yaw, Roll, Pitch, Done }

    [Header("Controllers")]
    public YawLineExperiment_SameAlgorithm yawPlayer;
    public RollLineExperiment_SameAlgorithm rollPlayer;
    public PitchLineExperiment_SameAlgorithm pitchPlayer;

    [Header("UI - Top")]
    public TMP_Text stepTitleText;
    public TMP_Text hintText;

    [Header("UI - Test Groups")]
    public GameObject yawTestGroup;
    public GameObject rollTestGroup;
    public GameObject pitchTestGroup;

    [Header("UI - Ranking (Dropdowns)")]
    // Yaw: 4 tests => rank 1~4 (no duplicates)
    public GameObject yawRankingGroup;
    public TMP_Dropdown yawRankTest1;
    public TMP_Dropdown yawRankTest2;
    public TMP_Dropdown yawRankTest3;
    public TMP_Dropdown yawRankTest4;

    // Roll: Front/Back/Both => rank 1~3 (no duplicates)
    public GameObject rollRankingGroup;
    public TMP_Dropdown rollRankFront;
    public TMP_Dropdown rollRankBack;
    public TMP_Dropdown rollRankBoth;

    // Pitch: Edge/Center => rank 1~2 (no duplicates)
    public GameObject pitchRankingGroup;
    public TMP_Dropdown pitchRankEdge;
    public TMP_Dropdown pitchRankCenter;

    [Header("UI - Submit (Next Stage)")]
    public Button submitButton;
    public TMP_Text submitButtonText; // optional: "Next"

    [Header("UI - Optional Count Labels (leave empty if not used)")]
    public TMP_Text yawCount1Text;
    public TMP_Text yawCount2Text;
    public TMP_Text yawCount3Text;
    public TMP_Text yawCount4Text;

    public TMP_Text rollCountFrontText;
    public TMP_Text rollCountBackText;
    public TMP_Text rollCountBothText;

    public TMP_Text pitchCountEdgeText;
    public TMP_Text pitchCountCenterText;

    [Header("Participant / Logging")]
    public string participantId = "P01";
    public string saveFolderRelativeToAssets = "Data";
    public string csvFileName = "axis_line_flow.csv";

    // =========================
    // Per-axis Params
    // =========================
    [Serializable]
    public class AxisParams
    {
        [Range(0f, 1f)] public float intensity01 = 0.30f;

        [Header("Sigma (Main vs Seam)")]
        public float sigmaMain = 0.70f;
        public float sigmaSeam = 0.90f;
        public float seamWidthIdx = 1.0f;

        [Header("Threshold & Cutoff")]
        [Range(0f, 0.5f)] public float perceptualThreshold01 = 0.05f;
        [Range(0f, 0.4f)] public float cutoff01 = 0.05f;

        [Header("Time smoothing")]
        public float smoothingTau = 0.08f;

        [Header("Output shaping")]
        public float outputGamma = 1.20f;
        [Range(0f, 0.2f)] public float minOn01 = 0.00f;

        [Header("Playback")]
        public float speedDegPerSec = 60f;
        public int durationMillis = 30;
    }

    [Header("Yaw Params (applied to Yaw only)")]
    public AxisParams yawParams = new AxisParams();

    [Header("Roll Params (applied to Roll only)")]
    public AxisParams rollParams = new AxisParams();

    [Header("Pitch Params (applied to Pitch only)")]
    public AxisParams pitchParams = new AxisParams();

    [Header("State")]
    public AxisStep step = AxisStep.Yaw;

    // counts: Yaw(4) / Roll(3) / Pitch(2)에서 재사용
    private int[] _testCounts = new int[4];
    private string _csvPath;

    private void Awake()
    {
        if (!yawPlayer || !rollPlayer || !pitchPlayer)
        {
            Debug.LogError("[FlowManager] Assign yawPlayer/rollPlayer/pitchPlayer.");
            enabled = false;
            return;
        }

        PrepareCsv();

        if (submitButton) submitButton.onClick.AddListener(Submit);

        InitRankDropdowns();

        // 시작부터 Yaw
        step = AxisStep.Yaw;
        EnterStepUI(step);
        SetHint("Test freely, rank them, then Next.");
    }

    // ===== Next stage (ranking-driven) =====
    public void Submit()
    {
        if (step != AxisStep.Yaw && step != AxisStep.Roll && step != AxisStep.Pitch) return;

        // 막힘 방지: 테스트 여부로 막지 말고, 랭킹만 유효하면 진행
        if (!TryGetRanking(step, out int bestIndex1Based, out string rankingText))
            return;

        StopAll();
        AppendCsvRow(bestIndex1Based, rankingText);

        if (step == AxisStep.Yaw) step = AxisStep.Roll;
        else if (step == AxisStep.Roll) step = AxisStep.Pitch;
        else step = AxisStep.Done;

        EnterStepUI(step);
        SetHint(step == AxisStep.Done ? "Done. Thank you!" : $"Next: {step}. Test, rank, Next.");
    }

    // 내부 정리용
    public void StopAll()
    {
        yawPlayer.StopAll();
        rollPlayer.StopAll();
        pitchPlayer.StopAll();
    }

    // ===== Test buttons =====
    public void TestYaw(int idx1to4)
    {
        if (step != AxisStep.Yaw) return;

        idx1to4 = Mathf.Clamp(idx1to4, 1, 4);
        _testCounts[idx1to4 - 1]++;
        RefreshCountLabels();

        ApplyParamsForCurrentStep();
        yawPlayer.StartRoute(idx1to4);

        SetHint($"[Yaw] Testing #{idx1to4} (count: {_testCounts[idx1to4 - 1]})");
    }

    // Roll: Front / Back / Both(동시)
    public void TestRollFront()
    {
        if (step != AxisStep.Roll) return;
        _testCounts[0]++;
        RefreshCountLabels();

        ApplyParamsForCurrentStep();
        rollPlayer.StartFront();

        SetHint($"[Roll] Testing Front (count: {_testCounts[0]})");
    }

    public void TestRollBack()
    {
        if (step != AxisStep.Roll) return;
        _testCounts[1]++;
        RefreshCountLabels();

        ApplyParamsForCurrentStep();
        rollPlayer.StartBack();

        SetHint($"[Roll] Testing Back (count: {_testCounts[1]})");
    }

    public void TestRollBoth()
    {
        if (step != AxisStep.Roll) return;
        _testCounts[2]++;
        RefreshCountLabels();

        ApplyParamsForCurrentStep();
        rollPlayer.StartBoth();

        SetHint($"[Roll] Testing Both (count: {_testCounts[2]})");
    }

    public void TestPitchEdge()
    {
        if (step != AxisStep.Pitch) return;
        _testCounts[0]++;
        RefreshCountLabels();

        ApplyParamsForCurrentStep();
        pitchPlayer.StartEdge();

        SetHint($"[Pitch] Testing Edge (count: {_testCounts[0]})");
    }

    public void TestPitchCenter()
    {
        if (step != AxisStep.Pitch) return;
        _testCounts[1]++;
        RefreshCountLabels();

        ApplyParamsForCurrentStep();
        pitchPlayer.StartCenter();

        SetHint($"[Pitch] Testing Center (count: {_testCounts[1]})");
    }

    // ===== UI state =====
    private void EnterStepUI(AxisStep s)
    {
        StopAll();
        Array.Clear(_testCounts, 0, _testCounts.Length);
        RefreshCountLabels();

        if (stepTitleText)
        {
            stepTitleText.text = s switch
            {
                AxisStep.Yaw => "Step 1 / 3 : Yaw",
                AxisStep.Roll => "Step 2 / 3 : Roll",
                AxisStep.Pitch => "Step 3 / 3 : Pitch",
                AxisStep.Done => "Done",
                _ => "Ready"
            };
        }

        bool activeStep = (s == AxisStep.Yaw || s == AxisStep.Roll || s == AxisStep.Pitch);

        SetActive(yawTestGroup, s == AxisStep.Yaw);
        SetActive(rollTestGroup, s == AxisStep.Roll);
        SetActive(pitchTestGroup, s == AxisStep.Pitch);

        SetActive(yawRankingGroup, s == AxisStep.Yaw);
        SetActive(rollRankingGroup, s == AxisStep.Roll);
        SetActive(pitchRankingGroup, s == AxisStep.Pitch);

        ResetRankUI(s);

        // ✅ 함정 제거: Submit은 activeStep이면 항상 보이고, Done이면 숨김
        if (submitButton)
        {
            submitButton.gameObject.SetActive(activeStep);
            submitButton.interactable = activeStep;
        }

        if (submitButtonText)
            submitButtonText.text = (s == AxisStep.Done) ? "Submit" : "Next";

        if (s == AxisStep.Done)
        {
            SetActive(yawTestGroup, false);
            SetActive(rollTestGroup, false);
            SetActive(pitchTestGroup, false);
            SetActive(yawRankingGroup, false);
            SetActive(rollRankingGroup, false);
            SetActive(pitchRankingGroup, false);

            if (submitButton)
            {
                submitButton.gameObject.SetActive(false);
                submitButton.interactable = false;
            }
        }
    }

    private void SetActive(GameObject go, bool on)
    {
        if (go) go.SetActive(on);
    }

    private void SetHint(string s)
    {
        if (hintText) hintText.text = s;
        Debug.Log("[FlowManager] " + s);
    }

    // ===== Count labels =====
    private void RefreshCountLabels()
    {
        if (step == AxisStep.Yaw)
        {
            if (yawCount1Text) yawCount1Text.text = _testCounts[0].ToString();
            if (yawCount2Text) yawCount2Text.text = _testCounts[1].ToString();
            if (yawCount3Text) yawCount3Text.text = _testCounts[2].ToString();
            if (yawCount4Text) yawCount4Text.text = _testCounts[3].ToString();
        }
        else if (step == AxisStep.Roll)
        {
            if (rollCountFrontText) rollCountFrontText.text = _testCounts[0].ToString();
            if (rollCountBackText)  rollCountBackText.text  = _testCounts[1].ToString();
            if (rollCountBothText)  rollCountBothText.text  = _testCounts[2].ToString();
        }
        else if (step == AxisStep.Pitch)
        {
            if (pitchCountEdgeText)   pitchCountEdgeText.text   = _testCounts[0].ToString();
            if (pitchCountCenterText) pitchCountCenterText.text = _testCounts[1].ToString();
        }
    }

    // ===== Ranking dropdowns =====
    private void InitRankDropdowns()
    {
        SetupDropdown(yawRankTest1, 4);
        SetupDropdown(yawRankTest2, 4);
        SetupDropdown(yawRankTest3, 4);
        SetupDropdown(yawRankTest4, 4);

        SetupDropdown(rollRankFront, 3);
        SetupDropdown(rollRankBack, 3);
        SetupDropdown(rollRankBoth, 3);

        SetupDropdown(pitchRankEdge, 2);
        SetupDropdown(pitchRankCenter, 2);
    }

    private void SetupDropdown(TMP_Dropdown dd, int maxRank)
    {
        if (!dd) return;
        dd.ClearOptions();

        var opts = new System.Collections.Generic.List<TMP_Dropdown.OptionData>();
        opts.Add(new TMP_Dropdown.OptionData("-"));

        for (int r = 1; r <= maxRank; r++)
            opts.Add(new TMP_Dropdown.OptionData(r.ToString()));

        dd.AddOptions(opts);
        dd.value = 0;
        dd.RefreshShownValue();
    }

    private void ResetRankUI(AxisStep s)
    {
        if (s == AxisStep.Yaw)
        {
            SetDropdownValue(yawRankTest1, 0);
            SetDropdownValue(yawRankTest2, 0);
            SetDropdownValue(yawRankTest3, 0);
            SetDropdownValue(yawRankTest4, 0);
        }
        else if (s == AxisStep.Roll)
        {
            SetDropdownValue(rollRankFront, 0);
            SetDropdownValue(rollRankBack, 0);
            SetDropdownValue(rollRankBoth, 0);
        }
        else if (s == AxisStep.Pitch)
        {
            SetDropdownValue(pitchRankEdge, 0);
            SetDropdownValue(pitchRankCenter, 0);
        }
    }

    private void SetDropdownValue(TMP_Dropdown dd, int v)
    {
        if (!dd) return;
        dd.value = v;
        dd.RefreshShownValue();
    }

    private bool TryGetRanking(AxisStep s, out int bestIndex1Based, out string rankingText)
    {
        bestIndex1Based = -1;
        rankingText = "";

        if (s == AxisStep.Yaw)
        {
            int[] ranks = new int[4];
            TMP_Dropdown[] dds = { yawRankTest1, yawRankTest2, yawRankTest3, yawRankTest4 };

            if (!ReadRanks(dds, 4, ranks)) return false;

            for (int i = 0; i < 4; i++)
                if (ranks[i] == 1) bestIndex1Based = i + 1;

            rankingText = $"T1={ranks[0]};T2={ranks[1]};T3={ranks[2]};T4={ranks[3]}";
            return true;
        }

        if (s == AxisStep.Roll)
        {
            int[] ranks = new int[3];
            TMP_Dropdown[] dds = { rollRankFront, rollRankBack, rollRankBoth };

            if (!ReadRanks(dds, 3, ranks)) return false;

            for (int i = 0; i < 3; i++)
                if (ranks[i] == 1) bestIndex1Based = i + 1;

            rankingText = $"Front={ranks[0]};Back={ranks[1]};Both={ranks[2]}";
            return true;
        }

        if (s == AxisStep.Pitch)
        {
            int[] ranks = new int[2];
            TMP_Dropdown[] dds = { pitchRankEdge, pitchRankCenter };

            if (!ReadRanks(dds, 2, ranks)) return false;

            bestIndex1Based = (ranks[0] == 1) ? 1 : 2;
            rankingText = $"Edge={ranks[0]};Center={ranks[1]}";
            return true;
        }

        SetHint("Invalid state.");
        return false;
    }

    private bool ReadRanks(TMP_Dropdown[] dds, int maxRank, int[] outRanks)
    {
        for (int i = 0; i < dds.Length; i++)
        {
            if (!dds[i])
            {
                SetHint("Ranking dropdown is not assigned in Inspector.");
                return false;
            }

            int v = dds[i].value; // 0..maxRank
            if (v == 0)
            {
                SetHint("Please rank all items (no '-' left).");
                return false;
            }

            outRanks[i] = v;
        }

        for (int r = 1; r <= maxRank; r++)
        {
            int count = 0;
            for (int i = 0; i < outRanks.Length; i++)
                if (outRanks[i] == r) count++;

            if (count != 1)
            {
                SetHint("Ranks must be unique (no duplicates).");
                return false;
            }
        }

        return true;
    }

    // =========================
    // Apply Params per current step
    // =========================
    private void ApplyParamsForCurrentStep()
    {
        if (step == AxisStep.Yaw)
        {
            ApplyToYaw(yawParams);
        }
        else if (step == AxisStep.Roll)
        {
            ApplyToRoll(rollParams);
        }
        else if (step == AxisStep.Pitch)
        {
            ApplyToPitch(pitchParams);
        }
    }

    private void ApplyToYaw(AxisParams p)
    {
        yawPlayer.SetMaxIntensity01(p.intensity01);
        yawPlayer.SetSigmas(p.sigmaMain, p.sigmaSeam);
        yawPlayer.SetShaping(p.perceptualThreshold01, p.cutoff01, p.smoothingTau, p.outputGamma, p.minOn01, p.seamWidthIdx);
        yawPlayer.SetSpeedAndDuration(p.speedDegPerSec, p.durationMillis);
    }

    private void ApplyToRoll(AxisParams p)
    {
        rollPlayer.SetMaxIntensity01(p.intensity01);
        rollPlayer.SetSigmas(p.sigmaMain, p.sigmaSeam);
        rollPlayer.SetShaping(p.perceptualThreshold01, p.cutoff01, p.smoothingTau, p.outputGamma, p.minOn01, p.seamWidthIdx);
        rollPlayer.SetSpeedAndDuration(p.speedDegPerSec, p.durationMillis);
    }

    private void ApplyToPitch(AxisParams p)
    {
        pitchPlayer.SetMaxIntensity01(p.intensity01);
        pitchPlayer.SetSigmas(p.sigmaMain, p.sigmaSeam);
        pitchPlayer.SetShaping(p.perceptualThreshold01, p.cutoff01, p.smoothingTau, p.outputGamma, p.minOn01, p.seamWidthIdx);
        pitchPlayer.SetSpeedAndDuration(p.speedDegPerSec, p.durationMillis);
    }

    // ===== CSV =====
    private void PrepareCsv()
    {
        string dir = Path.Combine(Application.dataPath, saveFolderRelativeToAssets);
        Directory.CreateDirectory(dir);
        _csvPath = Path.Combine(dir, csvFileName);

        if (!File.Exists(_csvPath))
        {
            File.WriteAllText(_csvPath,
                "timestamp_iso,participant,step,best,rankingText," +
                "testCount1,testCount2,testCount3,testCount4," +
                "intensity01,sigmaMain,sigmaSeam,seamWidthIdx,thr,cutoff,smoothingTau,gamma,minOn,speedDegPerSec,durationMillis\n",
                Encoding.UTF8);
        }
    }

    private void AppendCsvRow(int best, string rankingText)
    {
        string ts = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);

        AxisParams p = (step == AxisStep.Yaw) ? yawParams
                    : (step == AxisStep.Roll) ? rollParams
                    : pitchParams;

        string line = string.Join(",",
            Q(ts), Q(participantId), Q(step.ToString()),
            best.ToString(CultureInfo.InvariantCulture),
            Q(rankingText),
            _testCounts[0].ToString(CultureInfo.InvariantCulture),
            _testCounts[1].ToString(CultureInfo.InvariantCulture),
            _testCounts[2].ToString(CultureInfo.InvariantCulture),
            _testCounts[3].ToString(CultureInfo.InvariantCulture),
            p.intensity01.ToString(CultureInfo.InvariantCulture),
            p.sigmaMain.ToString(CultureInfo.InvariantCulture),
            p.sigmaSeam.ToString(CultureInfo.InvariantCulture),
            p.seamWidthIdx.ToString(CultureInfo.InvariantCulture),
            p.perceptualThreshold01.ToString(CultureInfo.InvariantCulture),
            p.cutoff01.ToString(CultureInfo.InvariantCulture),
            p.smoothingTau.ToString(CultureInfo.InvariantCulture),
            p.outputGamma.ToString(CultureInfo.InvariantCulture),
            p.minOn01.ToString(CultureInfo.InvariantCulture),
            p.speedDegPerSec.ToString(CultureInfo.InvariantCulture),
            p.durationMillis.ToString(CultureInfo.InvariantCulture)
        );

        File.AppendAllText(_csvPath, line + "\n", Encoding.UTF8);
        Debug.Log($"[FlowManager] Saved row: {step} best={best} ranking={rankingText}");
    }

    private static string Q(string s) => $"\"{s}\"";
}
