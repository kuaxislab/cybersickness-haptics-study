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

    [Tooltip("CSV file name format: 1_line.csv, 2_line.csv, ...")]
    public string csvPrefix = "line";

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

    // counts: Yaw(4) / Roll(3) / Pitch(2) reuse
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

        // Force requested speeds (even if Inspector differs)
        yawParams.speedDegPerSec = 40f;
        rollParams.speedDegPerSec = 40f;
        pitchParams.speedDegPerSec = 30f;

        PrepareCsv();

        if (submitButton) submitButton.onClick.AddListener(Submit);

        InitRankDropdowns();

        // start: Yaw
        step = AxisStep.Yaw;
        EnterStepUI(step);
        SetHint("Test freely, rank them, then Next.");
    }

    // ===== Next stage (ranking-driven) =====
    public void Submit()
    {
        if (step != AxisStep.Yaw && step != AxisStep.Roll && step != AxisStep.Pitch) return;

        if (!TryGetRanking(step, out int bestIndex1Based, out string[] orderedChoices, out int[] itemRanks))
            return;

        StopAll();
        AppendCsvRow(bestIndex1Based, orderedChoices, itemRanks);

        if (step == AxisStep.Yaw) step = AxisStep.Roll;
        else if (step == AxisStep.Roll) step = AxisStep.Pitch;
        else step = AxisStep.Done;

        EnterStepUI(step);
        SetHint(step == AxisStep.Done ? "Done. Thank you!" : $"Next: {step}. Test, rank, Next.");
    }

    // internal stop
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

        SetHint($"[Yaw] Testing T{idx1to4} (count: {_testCounts[idx1to4 - 1]})");
    }

    // Roll: Front / Back / Both
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
            if (rollCountBackText) rollCountBackText.text = _testCounts[1].ToString();
            if (rollCountBothText) rollCountBothText.text = _testCounts[2].ToString();
        }
        else if (step == AxisStep.Pitch)
        {
            if (pitchCountEdgeText) pitchCountEdgeText.text = _testCounts[0].ToString();
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

    /// <summary>
    /// Returns:
    /// - bestIndex1Based: which item is ranked #1 (1-based index in that step's item list)
    /// - orderedChoices: choice1..choice4 (1st..4th place labels). Unused entries are "".
    /// - itemRanks: rank per item in fixed order (e.g., yaw: [T1,T2,T3,T4], roll: [Front,Back,Both], pitch: [Edge,Center])
    /// </summary>
    private bool TryGetRanking(AxisStep s, out int bestIndex1Based, out string[] orderedChoices, out int[] itemRanks)
    {
        bestIndex1Based = -1;
        orderedChoices = new string[4] { "", "", "", "" };
        itemRanks = Array.Empty<int>();

        if (s == AxisStep.Yaw)
        {
            TMP_Dropdown[] dds = { yawRankTest1, yawRankTest2, yawRankTest3, yawRankTest4 };
            int[] ranks = new int[4];
            if (!ReadRanks(dds, 4, ranks)) return false;

            itemRanks = ranks;

            // best = item whose rank is 1
            for (int i = 0; i < 4; i++)
                if (ranks[i] == 1) bestIndex1Based = i + 1;

            // ordered choices by rank: 1st..4th
            for (int r = 1; r <= 4; r++)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (ranks[i] == r)
                    {
                        orderedChoices[r - 1] = $"T{i + 1}";
                        break;
                    }
                }
            }

            return true;
        }

        if (s == AxisStep.Roll)
        {
            TMP_Dropdown[] dds = { rollRankFront, rollRankBack, rollRankBoth };
            int[] ranks = new int[3];
            if (!ReadRanks(dds, 3, ranks)) return false;

            itemRanks = ranks;

            for (int i = 0; i < 3; i++)
                if (ranks[i] == 1) bestIndex1Based = i + 1;

            string[] labels = { "Front", "Back", "Both" };
            for (int r = 1; r <= 3; r++)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (ranks[i] == r)
                    {
                        orderedChoices[r - 1] = labels[i];
                        break;
                    }
                }
            }

            return true;
        }

        if (s == AxisStep.Pitch)
        {
            TMP_Dropdown[] dds = { pitchRankEdge, pitchRankCenter };
            int[] ranks = new int[2];
            if (!ReadRanks(dds, 2, ranks)) return false;

            itemRanks = ranks;

            bestIndex1Based = (ranks[0] == 1) ? 1 : 2;

            string[] labels = { "Edge", "Center" };
            for (int r = 1; r <= 2; r++)
            {
                for (int i = 0; i < 2; i++)
                {
                    if (ranks[i] == r)
                    {
                        orderedChoices[r - 1] = labels[i];
                        break;
                    }
                }
            }

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
            ApplyToYaw(yawParams);
        else if (step == AxisStep.Roll)
            ApplyToRoll(rollParams);
        else if (step == AxisStep.Pitch)
            ApplyToPitch(pitchParams);
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

        int index = 1;
        while (true)
        {
            string fileName = $"{index}_{csvPrefix}.csv"; // 1_line.csv, 2_line.csv, ...
            string fullPath = Path.Combine(dir, fileName);
            if (!File.Exists(fullPath))
            {
                _csvPath = fullPath;
                break;
            }
            index++;
        }

        // Unified header: ranks + ordered choices saved separately.
        // rank1..rank4: item ranks (step-dependent)
        // choice1..choice4: 1st..4th place items (step-dependent)
        File.WriteAllText(_csvPath,
            "timestamp_iso,participant,step," +
            "bestIndex1Based," +
            "rank1,rank2,rank3,rank4," +
            "choice1,choice2,choice3,choice4," +
            "testCount1,testCount2,testCount3,testCount4," +
            "intensity01,sigmaMain,sigmaSeam,seamWidthIdx,thr,cutoff,smoothingTau,gamma,minOn,speedDegPerSec,durationMillis\n",
            Encoding.UTF8);

        Debug.Log($"[FlowManager] CSV created: {_csvPath}");
    }

    private void AppendCsvRow(int bestIndex1Based, string[] orderedChoices, int[] itemRanks)
    {
        string ts = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);

        AxisParams p = (step == AxisStep.Yaw) ? yawParams
                    : (step == AxisStep.Roll) ? rollParams
                    : pitchParams;

        // Map ranks into rank1..rank4 columns (step-dependent)
        // Yaw: ranks=[T1,T2,T3,T4]
        // Roll: ranks=[Front,Back,Both] => rank4 blank
        // Pitch: ranks=[Edge,Center] => rank3,rank4 blank
        string r1 = (itemRanks.Length >= 1) ? itemRanks[0].ToString(CultureInfo.InvariantCulture) : "";
        string r2 = (itemRanks.Length >= 2) ? itemRanks[1].ToString(CultureInfo.InvariantCulture) : "";
        string r3 = (itemRanks.Length >= 3) ? itemRanks[2].ToString(CultureInfo.InvariantCulture) : "";
        string r4 = (itemRanks.Length >= 4) ? itemRanks[3].ToString(CultureInfo.InvariantCulture) : "";

        string c1 = (orderedChoices.Length >= 1) ? orderedChoices[0] : "";
        string c2 = (orderedChoices.Length >= 2) ? orderedChoices[1] : "";
        string c3 = (orderedChoices.Length >= 3) ? orderedChoices[2] : "";
        string c4 = (orderedChoices.Length >= 4) ? orderedChoices[3] : "";

        string line = string.Join(",",
            Q(ts), Q(participantId), Q(step.ToString()),
            bestIndex1Based.ToString(CultureInfo.InvariantCulture),

            Q(r1), Q(r2), Q(r3), Q(r4),
            Q(c1), Q(c2), Q(c3), Q(c4),

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
        Debug.Log($"[FlowManager] Saved row: {step} best={bestIndex1Based} choices={c1},{c2},{c3},{c4}");
    }

    private static string Q(string s) => $"\"{s}\"";
}
