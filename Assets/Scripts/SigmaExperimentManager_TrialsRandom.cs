using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class SigmaExperimentManager_TrialsRandom : MonoBehaviour
{
    [Header("Controllers")]
    public YawGaussianPathStageController yawCtrl;
    public RollGuassianPathState rollCtrl; // ✅ 네 기존 RollGaussianController 그대로 연결
    public PitchGaussianPathStageController pitchCtrl;

    [Header("UI (Do NOT show direction/speed)")]
    public TMP_Text stageLabelText;
    public Slider sigmaSlider;
    public TMP_Text sigmaValueText;
    public Button nextButton;

    [Header("Speeds (editable in Inspector)")]
    public float[] speedDegPerSec = new float[] { 30f, 60f, 90f };

    [Header("Sigma slider")]
    public float sigmaMin = 0.30f;
    public float sigmaMax = 2.00f;
    public float sigmaStep = 0.01f;
    public float sigmaDefault = 0.90f;

    [Header("CSV")]
    public string csvHeader = "file_index,timestamp_iso,direction,speed_deg_per_sec,part,sigma,trial_index,stage_in_trial\n";
    public bool autosavePartialEachStage = true;

    // ===== trial flow =====
    private List<Trial> _trials;
    private int _trialIdx = 0;
    private int _stageInTrial = 0;

    // ===== output paths =====
    private string _dir;
    private int _fileIndex;
    private string _finalCsvPath;
    private string _partialCsvPath;

    // ===== fixed-slot storage =====
    private Dictionary<string, SavedRow> _slot = new Dictionary<string, SavedRow>();

    private struct SavedRow
    {
        public string timestampIso;
        public string direction;
        public float speed;
        public string part;
        public float sigma;
        public int trialIndex;
        public int stageInTrial;
    }

    private void Awake()
    {
        if (yawCtrl == null || rollCtrl == null || pitchCtrl == null)
        {
            Debug.LogError("[Experiment] Assign yawCtrl, rollCtrl, pitchCtrl in Inspector.");
            enabled = false; return;
        }

        if (stageLabelText == null || sigmaSlider == null || nextButton == null)
        {
            Debug.LogError("[Experiment] UI refs missing.");
            enabled = false; return;
        }

        sigmaSlider.minValue = sigmaMin;
        sigmaSlider.maxValue = sigmaMax;
        sigmaSlider.value = sigmaDefault;
        sigmaSlider.onValueChanged.AddListener(OnSigmaChanged);
        nextButton.onClick.AddListener(OnNextClicked);

        PrepareCsvPaths_AssetsData();
        BuildTrialsAndShuffle();
        EnterCurrentStage();
    }

    private void OnDestroy()
    {
        yawCtrl.StopAll();
        pitchCtrl.StopAll();
        // roll도 stop 처리(네 roll 코드에 Stop 메서드가 없다면, rollCtrl.enabled=false 같은 방식으로 맞춰줘야 함)
        // 여기서는 rollCtrl이 자체 Stop을 가진다고 가정하지 않음.
    }

    // =========================
    // Paths: Assets/data
    // =========================
    private void PrepareCsvPaths_AssetsData()
    {
        _dir = Path.Combine(Application.dataPath, "data");
        Directory.CreateDirectory(_dir);

        int max = 0;
        foreach (var f in Directory.GetFiles(_dir, "*.csv"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (int.TryParse(name, out int n)) max = Mathf.Max(max, n);
        }

        _fileIndex = max + 1;
        _finalCsvPath = Path.Combine(_dir, $"{_fileIndex}.csv");
        _partialCsvPath = Path.Combine(_dir, $"{_fileIndex}.partial.csv");

        File.WriteAllText(_partialCsvPath, csvHeader);

        Debug.Log($"[Experiment] CSV Final   : {_finalCsvPath}");
        Debug.Log($"[Experiment] CSV Partial : {_partialCsvPath}");
    }

    // =========================
    // Option B: trial 랜덤
    // =========================
    private void BuildTrialsAndShuffle()
    {
        _trials = new List<Trial>();
        foreach (var spd in speedDegPerSec)
        {
            _trials.Add(Trial.MakeYaw(spd));
            _trials.Add(Trial.MakeRoll(spd));
            _trials.Add(Trial.MakePitch(spd));
        }

        var rng = new System.Random(Guid.NewGuid().GetHashCode());
        for (int i = _trials.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_trials[i], _trials[j]) = (_trials[j], _trials[i]);
        }

        _trialIdx = 0;
        _stageInTrial = 0;
    }

    private void EnterCurrentStage()
    {
        StopAllControllers();

        if (_trialIdx >= _trials.Count)
        {
            stageLabelText.text = "Done";
            nextButton.interactable = false;
            WriteFinalCsvSorted();
            return;
        }

        int trialNumber = _trialIdx + 1;
        int stages = _trials[_trialIdx].StageCount;

        stageLabelText.text = (stages == 2)
            ? $"Stage {trialNumber}-{_stageInTrial + 1}"
            : $"Stage {trialNumber}";

        // stage 시작 시 해당 stage의 sigma를 slider에 로드
        float s = LoadStageSigmaOrDefault(_trials[_trialIdx], _stageInTrial);
        s = Snap(s);
        sigmaSlider.SetValueWithoutNotify(s);
        UpdateSigmaValueText(s);

        // 재생 시작
        StartPlaybackForStage(_trials[_trialIdx], _stageInTrial, s);
    }

    private void StopAllControllers()
    {
        yawCtrl.StopAll();
        pitchCtrl.StopAll();
        rollCtrl.StopHaptics();
        // roll은 네 코드 구조에 따라 "Stop"이 없을 수도 있어서 비활성/강도0 처리 방식이 필요할 수 있음
        // 일단 rollCtrl은 stage 들어갈 때만 돌게끔 네 RollGaussianController 내부에 Start/Stop이 있으면 연결해줘.
    }

    private float LoadStageSigmaOrDefault(Trial t, int stageIdx0)
    {
        if (t.kind == TrialKind.Yaw)
        {
            var st = (stageIdx0 == 0) ? YawGaussianPathStageController.YawStage.Front : YawGaussianPathStageController.YawStage.Side;
            return yawCtrl.GetSigmaForStage(st);
        }
        if (t.kind == TrialKind.Pitch)
        {
            var st = (stageIdx0 == 0) ? PitchGaussianPathStageController.PitchStage.Front : PitchGaussianPathStageController.PitchStage.Top;
            return pitchCtrl.GetSigmaForStage(st);
        }

        // roll은 1단계
        return sigmaDefault;
    }

    private void StartPlaybackForStage(Trial t, int stageIdx0, float sigma)
    {
        float spd = t.speedDegPerSec;

        if (t.kind == TrialKind.Yaw)
        {
            var st = (stageIdx0 == 0) ? YawGaussianPathStageController.YawStage.Front : YawGaussianPathStageController.YawStage.Side;
            yawCtrl.SetSigmaForStage(st, sigma);
            yawCtrl.StartStage(st, spd);
        }
        else if (t.kind == TrialKind.Roll)
        {
            // ✅ roll은 네 기존 컨트롤러를 그대로 쓰되,
            // RollGaussianController 안에 speed/sigma setter가 있으면 여기서 호출하면 됨.
            // (네 roll 코드 구조가 정확히 어떤지에 따라 함수명이 달라질 수 있어서,
            //  일단은 "Inspector에서 speed를 미리 세팅하고, sigma만 slider로 조절"을 추천)
            //
            // 예: rollCtrl.SetSpeedDegPerSec(spd); rollCtrl.SetSigma(sigma); rollCtrl.Start();
            //
            // 여기서는 roll이 이미 괜찮다고 했으니, rollCtrl은 별도 stage 작업 없이 동작한다고 가정.
            rollCtrl.StartStage(spd);
        }
        else // Pitch
        {
            var st = (stageIdx0 == 0) ? PitchGaussianPathStageController.PitchStage.Front : PitchGaussianPathStageController.PitchStage.Top;
            pitchCtrl.SetSigmaForStage(st, sigma);
            pitchCtrl.StartStage(st, spd);
        }
    }

    private void OnSigmaChanged(float v)
    {
        v = Snap(v);
        sigmaSlider.SetValueWithoutNotify(v);
        UpdateSigmaValueText(v);

        // 현재 stage의 sigma를 실시간 반영
        if (_trialIdx >= _trials.Count) return;

        var t = _trials[_trialIdx];

        if (t.kind == TrialKind.Yaw)
        {
            var st = (_stageInTrial == 0) ? YawGaussianPathStageController.YawStage.Front : YawGaussianPathStageController.YawStage.Side;
            yawCtrl.SetSigmaForStage(st, v);
        }
        else if (t.kind == TrialKind.Pitch)
        {
            var st = (_stageInTrial == 0) ? PitchGaussianPathStageController.PitchStage.Front : PitchGaussianPathStageController.PitchStage.Top;
            pitchCtrl.SetSigmaForStage(st, v);
        }
        else
        {
            // roll은 필요하면 여기서도 반영
        }
    }

    private void OnNextClicked()
    {
        if (_trialIdx < _trials.Count)
        {
            var t = _trials[_trialIdx];
            SaveToSlot(t, _trialIdx + 1, _stageInTrial + 1, sigmaSlider.value);

            if (autosavePartialEachStage)
                WritePartialCsvSorted();
        }

        var cur = _trials[_trialIdx];
        _stageInTrial++;

        if (_stageInTrial >= cur.StageCount)
        {
            _trialIdx++;
            _stageInTrial = 0;
        }

        EnterCurrentStage();
    }

    // =========================
    // Slot 저장 (고정 순서 출력용)
    // =========================
    private void SaveToSlot(Trial t, int trialIndex1Based, int stageInTrial1Based, float sigma)
    {
        string ts = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);

        string dir = t.kind.ToString().ToLowerInvariant();
        string part =
            (t.kind == TrialKind.Yaw)   ? (stageInTrial1Based == 1 ? "front" : "side") :
            (t.kind == TrialKind.Pitch) ? (stageInTrial1Based == 1 ? "front" : "top")  :
                                          "main";

        var row = new SavedRow
        {
            timestampIso = ts,
            direction = dir,
            speed = t.speedDegPerSec,
            part = part,
            sigma = sigma,
            trialIndex = trialIndex1Based,
            stageInTrial = stageInTrial1Based
        };

        string key = MakeSlotKey(dir, t.speedDegPerSec, part);
        _slot[key] = row;
    }

    private string MakeSlotKey(string direction, float speed, string part)
        => $"{direction}|{speed.ToString(CultureInfo.InvariantCulture)}|{part}";

    private List<(string direction, float speed, string part)> BuildFixedOutputOrder()
    {
        var list = new List<(string, float, string)>();

        foreach (var spd in speedDegPerSec)
        {
            list.Add(("yaw", spd, "front"));
            list.Add(("yaw", spd, "side"));
        }

        foreach (var spd in speedDegPerSec)
        {
            list.Add(("roll", spd, "main"));
        }

        foreach (var spd in speedDegPerSec)
        {
            list.Add(("pitch", spd, "front"));
            list.Add(("pitch", spd, "top"));
        }

        return list;
    }

    private void WritePartialCsvSorted() => WriteSortedToPath(_partialCsvPath);
    private void WriteFinalCsvSorted()   => WriteSortedToPath(_finalCsvPath);

    private void WriteSortedToPath(string path)
    {
        var order = BuildFixedOutputOrder();

        using (var sw = new StreamWriter(path, false))
        {
            sw.Write(csvHeader);

            foreach (var (direction, speed, part) in order)
            {
                string key = MakeSlotKey(direction, speed, part);

                if (_slot.TryGetValue(key, out var r))
                {
                    sw.WriteLine(string.Join(",",
                        _fileIndex.ToString(CultureInfo.InvariantCulture),
                        r.timestampIso,
                        Quote(r.direction),
                        r.speed.ToString(CultureInfo.InvariantCulture),
                        Quote(r.part),
                        r.sigma.ToString(CultureInfo.InvariantCulture),
                        r.trialIndex.ToString(CultureInfo.InvariantCulture),
                        r.stageInTrial.ToString(CultureInfo.InvariantCulture)
                    ));
                }
                else
                {
                    sw.WriteLine(string.Join(",",
                        _fileIndex.ToString(CultureInfo.InvariantCulture),
                        "",
                        Quote(direction),
                        speed.ToString(CultureInfo.InvariantCulture),
                        Quote(part),
                        "",
                        "",
                        ""
                    ));
                }
            }
        }
    }

    private static string Quote(string s) => $"\"{s}\"";

    // =========================
    // Slider helpers
    // =========================
    private float Snap(float v)
    {
        v = Mathf.Clamp(v, sigmaMin, sigmaMax);
        if (sigmaStep <= 0f) return v;
        return Mathf.Round(v / sigmaStep) * sigmaStep;
    }

    private void UpdateSigmaValueText(float v)
    {
        if (sigmaValueText) sigmaValueText.text = v.ToString("0.00");
    }

    // =========================
    // Trial defs
    // =========================
    private enum TrialKind { Yaw, Roll, Pitch }

    private struct Trial
    {
        public TrialKind kind;
        public float speedDegPerSec;
        public int StageCount => (kind == TrialKind.Roll) ? 1 : 2;

        public static Trial MakeYaw(float spd)   => new Trial { kind = TrialKind.Yaw, speedDegPerSec = spd };
        public static Trial MakeRoll(float spd)  => new Trial { kind = TrialKind.Roll, speedDegPerSec = spd };
        public static Trial MakePitch(float spd) => new Trial { kind = TrialKind.Pitch, speedDegPerSec = spd };
    }
}
