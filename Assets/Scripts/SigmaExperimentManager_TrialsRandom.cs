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
    public RollGuassianPathState rollCtrl;
    public PitchGaussianPathStageController pitchCtrl;

    [Header("UI")]
    public TMP_Text stageLabelText;
    public Slider sigmaSlider;
    public TMP_Text sigmaValueText;
    public Button nextButton;
    public Button playButton;
    public TMP_Text instructionText;

    [Header("UI - Guide Images")]
    public Image bgImage;
    public Image axisGuideImage;
    public Sprite yawSprite;
    public Sprite rollSprite;
    public Sprite pitchSprite;
    public bool showAxisImage = true;

    [Header("Canvas BG Color")]
    public bool setBgGreyOnStart = true;
    public Color bgGrey = new Color(0.25f, 0.25f, 0.25f, 1f);

    [Header("Experiment Sessions")]
    [Tooltip("Total sessions to run. Session 0 = intensity calibration. Sessions 1.. are randomized trials.")]
    public int sessionCount = 3;

    [Header("Speeds (editable in Inspector)")]
    public float[] yawRollSpeedsDegPerSec = new float[] { 20f, 40f, 60f };
    public float[] pitchSpeedsDegPerSec = new float[] { 15f, 30f, 45f };

    [Header("Session 0 (Calibration) Settings")]
    public float calibrationSpeedDegPerSec = 60f;
    public float calibrationSigmaDefault = 0.70f;

    [Tooltip("Intensity slider range (Session 0 only)")]
    public float intensityMin = 0.00f;
    public float intensityMax = 0.40f;
    public float intensityStep = 0.01f;
    public float intensityDefault = 0.10f;

    [Header("Sigma slider (Session 1.. only)")]
    public float sigmaMin = 0.30f;
    public float sigmaMax = 2.00f;
    public float sigmaStep = 0.01f;
    public float sigmaDefault = 0.90f;

    [Header("Random slider start position per stage (Session 1.. only)")]
    [Tooltip("If true, slider starts at random position each stage. For Yaw/Pitch seam stage, slider starts from stage0 sigma.")]
    public bool randomizeSliderStart = true;

    [Header("Flow")]
    [Tooltip("If true, each stage waits for Play button.")]
    public bool requirePlayButtonPerStage = true;

    [Header("CSV")]
    public string csvHeader =
        "file_index,session_index,timestamp_iso,mode,direction,speed_deg_per_sec,part,sigma,intensity01,trial_index,stage_in_trial\n";
    public bool autosaveEachStage = true;

    // ===== runtime state =====
    private int _sessionIdx = 0;

    // Session 0: calibration steps
    private List<CalStep> _calSteps;
    private int _calStepIdx = 0;

    // Session 1..: trials
    private List<Trial> _trials;
    private int _trialIdx = 0;
    private int _stageInTrial = 0;

    // carry stage0 sigma -> stage1 slider start (Yaw/Pitch)
    private float _lastYawStage0Sigma = -1f;
    private float _lastPitchStage0Sigma = -1f;

    // calibration intensities
    private float _calYawIntensity = -1f;
    private float _calRollIntensity = -1f;
    private float _calPitchIntensity = -1f;

    // file
    private int _fileIndex = 1;
    private string _finalCsvPath;

    // play gating
    private bool _isPlaying = false;

    private System.Random _rng = new System.Random();

    private void Start()
    {
        if (setBgGreyOnStart && bgImage) bgImage.color = bgGrey;

        if (sigmaSlider)
            sigmaSlider.onValueChanged.AddListener(OnSliderChanged);

        if (nextButton)
            nextButton.onClick.AddListener(OnNext);

        if (playButton)
            playButton.onClick.AddListener(OnPlay);

        PrepareCsv();
        BuildCalibrationSteps();
        EnterCalibrationStep();
    }

    private void OnDestroy()
    {
        if (sigmaSlider)
            sigmaSlider.onValueChanged.RemoveListener(OnSliderChanged);
    }

    // =========================
    // Session 0: Calibration
    // =========================
    private void BuildCalibrationSteps()
    {
        _calSteps = new List<CalStep>
        {
            new CalStep{ kind = TrialKind.Yaw },
            new CalStep{ kind = TrialKind.Roll },
            new CalStep{ kind = TrialKind.Pitch },
        };
        _calStepIdx = 0;
    }

    private void EnterCalibrationStep()
    {
        SafeStopAllControllers();
        _isPlaying = false;

        if (_calStepIdx >= _calSteps.Count)
        {
            _sessionIdx = 1;
            BuildTrialsForSession(_sessionIdx);
            EnterTrialStage();
            return;
        }

        var step = _calSteps[_calStepIdx];
        UpdateAxisGuideSprite(step.kind);

        if (stageLabelText) stageLabelText.text = $"Session 0 (Calibration) - {step.kind}";
        if (instructionText) instructionText.text = "Adjust intensity, then press Next.";

        float startI = GetExistingCalIntensityOrDefault(step.kind);

        sigmaSlider.minValue = intensityMin;
        sigmaSlider.maxValue = intensityMax;

        float snapped = Snap(startI, intensityMin, intensityMax, intensityStep);
        sigmaSlider.SetValueWithoutNotify(snapped);
        UpdateSigmaValueText(snapped);

        StartCalibrationPlayback(step.kind, calibrationSpeedDegPerSec, calibrationSigmaDefault, snapped);
    }

    private float GetExistingCalIntensityOrDefault(TrialKind kind)
    {
        switch (kind)
        {
            case TrialKind.Yaw:   return (_calYawIntensity  >= 0f) ? _calYawIntensity  : intensityDefault;
            case TrialKind.Roll:  return (_calRollIntensity >= 0f) ? _calRollIntensity : intensityDefault;
            case TrialKind.Pitch: return (_calPitchIntensity>= 0f) ? _calPitchIntensity: intensityDefault;
        }
        return intensityDefault;
    }

    private void StartCalibrationPlayback(TrialKind kind, float speedDegPerSec, float sigmaDefaultLocal, float intensity01)
    {
        ApplyIntensityForKind(kind, intensity01);

        if (kind == TrialKind.Yaw)
        {
            // calibration: set both to same so user feels continuity
            yawCtrl.SetSigmaForStage(YawGaussianPathStageController.YawStage.Front, sigmaDefaultLocal);
            yawCtrl.SetSigmaForStage(YawGaussianPathStageController.YawStage.Side,  sigmaDefaultLocal);
            yawCtrl.StartStage(YawGaussianPathStageController.YawStage.Front, speedDegPerSec);
        }
        else if (kind == TrialKind.Roll)
        {
            rollCtrl.StartStage(speedDegPerSec);
        }
        else // Pitch
        {
            pitchCtrl.SetSigmaForStage(PitchGaussianPathStageController.PitchStage.Front, sigmaDefaultLocal);
            pitchCtrl.SetSigmaForStage(PitchGaussianPathStageController.PitchStage.Top,   sigmaDefaultLocal);
            pitchCtrl.StartStage(PitchGaussianPathStageController.PitchStage.Front, speedDegPerSec);
        }
    }

    private void SaveCalibrationIntensity(TrialKind kind, float intensity01)
    {
        if (kind == TrialKind.Yaw) _calYawIntensity = intensity01;
        else if (kind == TrialKind.Roll) _calRollIntensity = intensity01;
        else _calPitchIntensity = intensity01;
    }

    private void ApplyCalibratedIntensityToAllControllers()
    {
        if (_calYawIntensity < 0f) _calYawIntensity = intensityDefault;
        if (_calRollIntensity < 0f) _calRollIntensity = intensityDefault;
        if (_calPitchIntensity < 0f) _calPitchIntensity = intensityDefault;

        yawCtrl.SetMaxIntensity01(_calYawIntensity);
        rollCtrl.SetMaxIntensity01(_calRollIntensity);
        pitchCtrl.SetMaxIntensity01(_calPitchIntensity);
    }

    private void ApplyIntensityForKind(TrialKind kind, float intensity01)
    {
        intensity01 = Mathf.Clamp01(intensity01);

        if (kind == TrialKind.Yaw) yawCtrl.SetMaxIntensity01(intensity01);
        else if (kind == TrialKind.Roll) rollCtrl.SetMaxIntensity01(intensity01);
        else pitchCtrl.SetMaxIntensity01(intensity01);
    }

    // =========================
    // Session 1..: Trials
    // =========================
    private void BuildTrialsForSession(int sessionIdx)
    {
        _trials = new List<Trial>();

        // trial-level random (Yaw, Roll at yawRoll speeds; Pitch at pitch speeds)
        foreach (var spd in yawRollSpeedsDegPerSec)
        {
            _trials.Add(Trial.MakeYaw(spd));
            _trials.Add(Trial.MakeRoll(spd));
        }
        foreach (var spd in pitchSpeedsDegPerSec)
        {
            _trials.Add(Trial.MakePitch(spd));
        }
        Shuffle(_trials);

        _trialIdx = 0;
        _stageInTrial = 0;

        _lastYawStage0Sigma = -1f;
        _lastPitchStage0Sigma = -1f;

        ApplyCalibratedIntensityToAllControllers();
    }

    private void Shuffle(List<Trial> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private void EnterTrialStage()
    {
        SafeStopAllControllers();
        _isPlaying = false;

        // session end?
        if (_trialIdx >= _trials.Count)
        {
            _sessionIdx++;
            if (_sessionIdx >= sessionCount)
            {
                if (stageLabelText) stageLabelText.text = "DONE";
                if (instructionText) instructionText.text = "";
                return;
            }

            BuildTrialsForSession(_sessionIdx);
            _trialIdx = 0;
            _stageInTrial = 0;
        }

        var t = _trials[_trialIdx];

        // ALWAYS keep guide image synced with current trial kind
        UpdateAxisGuideSprite(t.kind);

        string part = GetPartLabel(t, _stageInTrial);

        if (stageLabelText)
            stageLabelText.text = $"Session {_sessionIdx} - Trial {_trialIdx + 1}/{_trials.Count} - {t.kind} - {part}";

        if (instructionText)
        {
            if ((t.kind == TrialKind.Yaw || t.kind == TrialKind.Pitch) && _stageInTrial == 0)
                instructionText.text = "Main stage: slider tunes OVERALL flow (main + seam together).";
            else if ((t.kind == TrialKind.Yaw || t.kind == TrialKind.Pitch) && _stageInTrial == 1)
                instructionText.text = "Seam stage: slider tunes seam only (fine adjustment).";
            else
                instructionText.text = "Adjust sigma, then press Play (or Next if auto).";
        }

        // slider config
        sigmaSlider.minValue = sigmaMin;
        sigmaSlider.maxValue = sigmaMax;

        float startSigma = DecideInitialSigmaForStage(t, _stageInTrial);
        startSigma = Snap(startSigma, sigmaMin, sigmaMax, sigmaStep);
        sigmaSlider.SetValueWithoutNotify(startSigma);
        UpdateSigmaValueText(startSigma);

        if (!requirePlayButtonPerStage)
        {
            StartPlaybackForStage(t, _stageInTrial, startSigma);
            _isPlaying = true;
        }
    }

    private float DecideInitialSigmaForStage(Trial t, int stageIdx0)
    {
        // For Yaw/Pitch seam stage, start from stage0 sigma (because stage0 sets both together)
        if (t.kind == TrialKind.Yaw && stageIdx0 == 1 && _lastYawStage0Sigma > 0f) return _lastYawStage0Sigma;
        if (t.kind == TrialKind.Pitch && stageIdx0 == 1 && _lastPitchStage0Sigma > 0f) return _lastPitchStage0Sigma;

        if (!randomizeSliderStart) return sigmaDefault;

        return UnityEngine.Random.Range(sigmaMin, sigmaMax);
    }

    private void StartPlaybackForStage(Trial t, int stageIdx0, float sigma)
    {
        float spd = t.speedDegPerSec;
        ApplyCalibratedIntensityToAllControllers();

        if (t.kind == TrialKind.Yaw)
        {
            if (stageIdx0 == 0)
            {
                // ✅ Main stage = overall flow: link main + seam
                yawCtrl.SetSigmaForStage(YawGaussianPathStageController.YawStage.Front, sigma);
                yawCtrl.SetSigmaForStage(YawGaussianPathStageController.YawStage.Side,  sigma);
                yawCtrl.StartStage(YawGaussianPathStageController.YawStage.Front, spd);
            }
            else
            {
                // ✅ Seam stage = seam only
                yawCtrl.SetSigmaForStage(YawGaussianPathStageController.YawStage.Side, sigma);
                yawCtrl.StartStage(YawGaussianPathStageController.YawStage.Side, spd);
            }
        }
        else if (t.kind == TrialKind.Roll)
        {
            rollCtrl.StartStage(spd);
        }
        else // Pitch
        {
            if (stageIdx0 == 0)
            {
                // ✅ Main stage = overall flow: link main + seam
                pitchCtrl.SetSigmaForStage(PitchGaussianPathStageController.PitchStage.Front, sigma);
                pitchCtrl.SetSigmaForStage(PitchGaussianPathStageController.PitchStage.Top,   sigma);
                pitchCtrl.StartStage(PitchGaussianPathStageController.PitchStage.Front, spd);
            }
            else
            {
                // ✅ Seam stage = seam only
                pitchCtrl.SetSigmaForStage(PitchGaussianPathStageController.PitchStage.Top, sigma);
                pitchCtrl.StartStage(PitchGaussianPathStageController.PitchStage.Top, spd);
            }
        }
    }

    private void SafeStopAllControllers()
    {
        if (yawCtrl) yawCtrl.StopAll();
        if (rollCtrl) rollCtrl.StopHaptics();
        if (pitchCtrl) pitchCtrl.StopAll();
    }

    private void UpdateAxisGuideSprite(TrialKind kind)
    {
        if (!axisGuideImage) return;

        axisGuideImage.enabled = showAxisImage;
        if (!showAxisImage) return;

        switch (kind)
        {
            case TrialKind.Yaw:   axisGuideImage.sprite = yawSprite; break;
            case TrialKind.Roll:  axisGuideImage.sprite = rollSprite; break;
            case TrialKind.Pitch: axisGuideImage.sprite = pitchSprite; break;
        }
    }

    private string GetPartLabel(Trial t, int stageIdx0)
    {
        if (t.kind == TrialKind.Roll) return "full";
        if (t.kind == TrialKind.Yaw) return (stageIdx0 == 0) ? "main(front+back)" : "seam";
        return (stageIdx0 == 0) ? "main(front+back)" : "seam";
    }

    private void OnPlay()
    {
        if (_sessionIdx == 0) return; // calibration is always playing

        if (_trialIdx >= _trials.Count) return;

        var t = _trials[_trialIdx];
        float sigma = sigmaSlider.value;

        StartPlaybackForStage(t, _stageInTrial, sigma);
        _isPlaying = true;
    }

    private void OnNext()
    {
        if (_sessionIdx == 0)
        {
            var step = _calSteps[Mathf.Clamp(_calStepIdx, 0, _calSteps.Count - 1)];
            SaveCalibrationIntensity(step.kind, sigmaSlider.value);

            _calStepIdx++;
            EnterCalibrationStep();
            return;
        }

        if (_trialIdx >= _trials.Count) return;

        var t = _trials[_trialIdx];

        if (requirePlayButtonPerStage && !_isPlaying)
        {
            if (instructionText) instructionText.text = "Press Play first.";
            return;
        }

        float sigma = sigmaSlider.value;

        // carry stage0 sigma -> seam stage start
        if (t.kind == TrialKind.Yaw && _stageInTrial == 0) _lastYawStage0Sigma = sigma;
        if (t.kind == TrialKind.Pitch && _stageInTrial == 0) _lastPitchStage0Sigma = sigma;

        // CSV row
        string part = GetPartLabel(t, _stageInTrial);
        float intensityNow = GetIntensityForKind(t.kind);

        AppendCsvRow(
            sessionIndex: _sessionIdx,
            mode: "trial",
            direction: t.kind,
            speedDegPerSec: t.speedDegPerSec,
            part: part,
            sigma: sigma,
            intensity01: intensityNow,
            trialIndex1Based: _trialIdx + 1,
            stageInTrial1Based: _stageInTrial + 1
        );

        // advance
        _stageInTrial++;
        if (_stageInTrial >= t.StageCount)
        {
            _trialIdx++;
            _stageInTrial = 0;
        }

        EnterTrialStage();
    }

    private float GetIntensityForKind(TrialKind kind)
    {
        if (kind == TrialKind.Yaw) return (_calYawIntensity >= 0f) ? _calYawIntensity : yawCtrl.GetMaxIntensity01();
        if (kind == TrialKind.Roll) return (_calRollIntensity >= 0f) ? _calRollIntensity : rollCtrl.GetMaxIntensity01();
        return (_calPitchIntensity >= 0f) ? _calPitchIntensity : pitchCtrl.GetMaxIntensity01();
    }

    // =========================
    // Slider handler (UPDATED)
    // =========================
    private void OnSliderChanged(float v)
    {
        // Session 0: intensity live update
        if (_sessionIdx == 0)
        {
            var step = _calSteps[Mathf.Clamp(_calStepIdx, 0, _calSteps.Count - 1)];

            v = Snap(v, intensityMin, intensityMax, intensityStep);
            sigmaSlider.SetValueWithoutNotify(v);
            UpdateSigmaValueText(v);

            ApplyIntensityForKind(step.kind, v);
            return;
        }

        // Session 1..: sigma live update
        v = Snap(v, sigmaMin, sigmaMax, sigmaStep);
        sigmaSlider.SetValueWithoutNotify(v);
        UpdateSigmaValueText(v);

        if (_trialIdx >= _trials.Count) return;

        var t = _trials[_trialIdx];

        if (t.kind == TrialKind.Yaw)
        {
            if (_stageInTrial == 0)
            {
                // ✅ Main stage = overall flow: link main + seam
                yawCtrl.SetSigmaForStage(YawGaussianPathStageController.YawStage.Front, v);
                yawCtrl.SetSigmaForStage(YawGaussianPathStageController.YawStage.Side,  v);
            }
            else
            {
                // ✅ Seam stage = seam only
                yawCtrl.SetSigmaForStage(YawGaussianPathStageController.YawStage.Side, v);
            }
        }
        else if (t.kind == TrialKind.Pitch)
        {
            if (_stageInTrial == 0)
            {
                // ✅ Main stage = overall flow: link main + seam
                pitchCtrl.SetSigmaForStage(PitchGaussianPathStageController.PitchStage.Front, v);
                pitchCtrl.SetSigmaForStage(PitchGaussianPathStageController.PitchStage.Top,   v);
            }
            else
            {
                // ✅ Seam stage = seam only
                pitchCtrl.SetSigmaForStage(PitchGaussianPathStageController.PitchStage.Top, v);
            }
        }
        else
        {
            // Roll: sigma not used (unless you add it later)
        }
    }

    // =========================
    // CSV
    // =========================
    private void PrepareCsv()
    {
        string dir = Path.Combine(Application.dataPath, "data");
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        int idx = 1;
        while (File.Exists(Path.Combine(dir, $"{idx}.csv")))
            idx++;

        _fileIndex = idx;
        _finalCsvPath = Path.Combine(dir, $"{_fileIndex}.csv");

        // ✅ Inspector에 "\n"이 문자로 저장돼 있어도 정상화
        string header = csvHeader ?? "";
        header = header.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\r", "\n");

        // ✅ 헤더 끝에 개행 1개 보장
        if (!header.EndsWith("\n"))
            header += "\n";

        File.WriteAllText(_finalCsvPath, header);
    }


    private void AppendCsvRow(
        int sessionIndex,
        string mode,
        TrialKind direction,
        float speedDegPerSec,
        string part,
        float sigma,
        float intensity01,
        int trialIndex1Based,
        int stageInTrial1Based)
    {
        string ts = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        string dir = direction.ToString().ToLowerInvariant();

        string line = string.Join(",",
            _fileIndex.ToString(CultureInfo.InvariantCulture),
            sessionIndex.ToString(CultureInfo.InvariantCulture),
            Quote(ts),
            Quote(mode),
            Quote(dir),
            speedDegPerSec.ToString(CultureInfo.InvariantCulture),
            Quote(part),                    // 🔥 중요
            sigma.ToString(CultureInfo.InvariantCulture),
            intensity01.ToString(CultureInfo.InvariantCulture),
            trialIndex1Based.ToString(CultureInfo.InvariantCulture),
            stageInTrial1Based.ToString(CultureInfo.InvariantCulture)
        );

        File.AppendAllText(_finalCsvPath, line + "\n");

        if (autosaveEachStage)
            Debug.Log($"[Experiment] Saved: S{sessionIndex} {mode} {dir} {part} spd={speedDegPerSec} sigma={sigma:0.00} I={intensity01:0.00}");
    }

    private static string Quote(string s) => $"\"{s}\"";

    // =========================
    // Helpers
    // =========================
    private float Snap(float v, float mn, float mx, float step)
    {
        v = Mathf.Clamp(v, mn, mx);
        if (step <= 0f) return v;
        return Mathf.Round(v / step) * step;
    }

    private void UpdateSigmaValueText(float v)
    {
        if (sigmaValueText) sigmaValueText.text = v.ToString("0.00");
    }

    // =========================
    // Types
    // =========================
    private enum TrialKind { Yaw, Roll, Pitch }

    private struct CalStep
    {
        public TrialKind kind;
    }

    private struct Trial
    {
        public TrialKind kind;
        public float speedDegPerSec;

        public int StageCount => (kind == TrialKind.Roll) ? 1 : 2;

        public static Trial MakeYaw(float spd) => new Trial { kind = TrialKind.Yaw, speedDegPerSec = spd };
        public static Trial MakeRoll(float spd) => new Trial { kind = TrialKind.Roll, speedDegPerSec = spd };
        public static Trial MakePitch(float spd) => new Trial { kind = TrialKind.Pitch, speedDegPerSec = spd };
    }
}
