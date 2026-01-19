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

    [Header("UI - Guide Images (just show on left/right)")]
    [Tooltip("Assign Canvas BG Image if you want code to set grey.")]
    public Image bgImage;

    [Tooltip("Assign an Image UI object placed on right/left (e.g., Canvas/AxisImage)")]
    public Image axisGuideImage;

    public Sprite yawSprite;
    public Sprite rollSprite;
    public Sprite pitchSprite;

    [Tooltip("Optional: force show/hide axis image")]
    public bool showAxisImage = true;

    [Header("Canvas BG Color (optional)")]
    public bool setBgGreyOnStart = true;
    public Color bgGrey = new Color(0.25f, 0.25f, 0.25f, 1f);

    [Header("Experiment Sessions")]
    [Tooltip("Total sessions to run. Session 0 = intensity calibration. Sessions 1.. are randomized trials.")]
    public int sessionCount = 3;

    [Header("Speeds (editable in Inspector)")]
    public float[] speedDegPerSec = new float[] { 30f, 60f, 90f };

    [Header("Session 0 (Calibration) Settings")]
    [Tooltip("Calibration speed (deg/sec). Session 0 uses ONLY this speed.")]
    public float calibrationSpeedDegPerSec = 60f;

    [Tooltip("Default sigma used during calibration playback (Yaw/Pitch will use current controller internal defaults too).")]
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
    [Tooltip("If true, slider starts at random position each stage. " +
             "BUT for 2-stage directions (Yaw/Pitch) stage 2 starts from stage 1 value (natural carry).")]
    public bool randomizeSliderStart = true;

    [Header("CSV")]
    public string csvHeader =
        "file_index,session_index,timestamp_iso,mode,direction,speed_deg_per_sec,part,sigma,intensity01,trial_index,stage_in_trial\n";
    public bool autosaveEachStage = true;

    // ===== runtime state =====
    private int _sessionIdx = 0;

    // Session 0: calibration steps (Yaw, Roll, Pitch fixed)
    private List<CalStep> _calSteps;
    private int _calStepIdx = 0;

    // Session 1..: trial flow
    private List<Trial> _trials;
    private int _trialIdx = 0;
    private int _stageInTrial = 0;

    // carry between stage1 -> stage2 in yaw/pitch (sigma only, session1..)
    private float _lastYawStage1Sigma = -1f;
    private float _lastPitchStage1Sigma = -1f;

    // intensity calibration results
    private float _calYawIntensity = -1f;
    private float _calRollIntensity = -1f;
    private float _calPitchIntensity = -1f;

    // ===== output paths =====
    private string _dir;
    private int _fileIndex;
    private string _finalCsvPath;

    private System.Random _rng;

    private void Awake()
    {
        _rng = new System.Random(Guid.NewGuid().GetHashCode());

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

        if (setBgGreyOnStart && bgImage != null)
            bgImage.color = bgGrey;

        if (axisGuideImage != null)
            axisGuideImage.enabled = showAxisImage;

        sigmaSlider.onValueChanged.AddListener(OnSliderChanged);
        nextButton.onClick.AddListener(OnNextClicked);

        PrepareCsvPath_AssetsData();

        // Start at Session 0 (Calibration)
        _sessionIdx = 0;
        BuildCalibrationSteps();
        EnterCalibrationStep();
    }

    private void OnDestroy()
    {
        SafeStopAllControllers();
    }

    // =========================
    // CSV: Assets/data/1.csv
    // =========================
    private void PrepareCsvPath_AssetsData()
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
        File.WriteAllText(_finalCsvPath, csvHeader);

        Debug.Log($"[Experiment] CSV Final: {_finalCsvPath}");
    }

    // =========================
    // Session 0: Calibration
    // =========================
    private void BuildCalibrationSteps()
    {
        _calSteps = new List<CalStep>
        {
            new CalStep { kind = TrialKind.Yaw },
            new CalStep { kind = TrialKind.Roll },
            new CalStep { kind = TrialKind.Pitch },
        };
        _calStepIdx = 0;
    }

    private void EnterCalibrationStep()
    {
        SafeStopAllControllers();

        // if finished calibration, move to session 1 trials
        if (_calStepIdx >= _calSteps.Count)
        {
            // apply intensities to controllers (final)
            ApplyCalibratedIntensityToAllControllers();

            // next session
            _sessionIdx = 1;
            BuildTrialsForSession(_sessionIdx);
            EnterTrialStage();
            return;
        }

        var step = _calSteps[_calStepIdx];

        // UI label
        stageLabelText.text = $"Session 0 | Calibration ({_calStepIdx + 1}/3) : {step.kind} intensity";

        // image
        UpdateAxisGuideSprite(step.kind);

        // slider = INTENSITY in session 0
        sigmaSlider.minValue = intensityMin;
        sigmaSlider.maxValue = intensityMax;

        float startI = GetExistingCalIntensityOrDefault(step.kind);
        startI = Snap(startI, intensityMin, intensityMax, intensityStep);

        sigmaSlider.SetValueWithoutNotify(startI);
        UpdateSigmaValueText(startI);

        // run playback using calibration speed, and a safe sigma default
        StartCalibrationPlayback(step.kind, calibrationSpeedDegPerSec, calibrationSigmaDefault, startI);
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
        // intensity apply live
        ApplyIntensityForKind(kind, intensity01);

        // sigma: during calibration we just run “main-ish” so user can feel strength.
        // (You can still tweak default sigmas in each controller’s inspector if needed.)
        if (kind == TrialKind.Yaw)
        {
            yawCtrl.SetSigmaForStage(YawGaussianPathStageController.YawStage.Front, sigmaDefaultLocal);
            yawCtrl.SetSigmaForStage(YawGaussianPathStageController.YawStage.Side,  sigmaDefaultLocal);
            yawCtrl.StartStage(YawGaussianPathStageController.YawStage.Front, speedDegPerSec);
        }
        else if (kind == TrialKind.Roll)
        {
            // roll uses its own internal sigma/logic; we only control intensity here
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

        // Session 1..: yaw/roll/pitch randomized per speed
        foreach (var spd in speedDegPerSec)
        {
            _trials.Add(Trial.MakeYaw(spd));
            _trials.Add(Trial.MakeRoll(spd));
            _trials.Add(Trial.MakePitch(spd));
        }
        Shuffle(_trials);

        _trialIdx = 0;
        _stageInTrial = 0;

        _lastYawStage1Sigma = -1f;
        _lastPitchStage1Sigma = -1f;

        // IMPORTANT: intensity is fixed from calibration
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

        // session end?
        if (_trialIdx >= _trials.Count)
        {
            _sessionIdx++;

            if (_sessionIdx >= Mathf.Max(1, sessionCount))
            {
                stageLabelText.text = "Done";
                nextButton.interactable = false;
                SafeStopAllControllers();
                return;
            }

            BuildTrialsForSession(_sessionIdx);
        }

        var t = _trials[_trialIdx];

        // UI label
        int trialNumber = _trialIdx + 1;
        int stages = t.StageCount;
        stageLabelText.text = (stages == 2)
            ? $"Session {_sessionIdx} | Trial {trialNumber}-{_stageInTrial + 1}"
            : $"Session {_sessionIdx} | Trial {trialNumber}";

        UpdateAxisGuideSprite(t.kind);

        // slider = SIGMA in session 1..
        sigmaSlider.minValue = sigmaMin;
        sigmaSlider.maxValue = sigmaMax;

        float startSigma = DecideInitialSigmaForStage(t, _stageInTrial);
        startSigma = Snap(startSigma, sigmaMin, sigmaMax, sigmaStep);

        sigmaSlider.SetValueWithoutNotify(startSigma);
        UpdateSigmaValueText(startSigma);

        // playback start
        StartPlaybackForStage(t, _stageInTrial, startSigma);
    }

    private void UpdateAxisGuideSprite(TrialKind kind)
    {
        if (axisGuideImage == null) return;

        axisGuideImage.enabled = showAxisImage;
        if (!showAxisImage) return;

        switch (kind)
        {
            case TrialKind.Yaw: axisGuideImage.sprite = yawSprite; break;
            case TrialKind.Roll: axisGuideImage.sprite = rollSprite; break;
            case TrialKind.Pitch: axisGuideImage.sprite = pitchSprite; break;
        }

        axisGuideImage.preserveAspect = true;
    }

    private float DecideInitialSigmaForStage(Trial t, int stageIdx0)
    {
        // yaw/pitch 2-stage: stage2 carries stage1
        if (t.kind == TrialKind.Yaw)
        {
            if (stageIdx0 == 0)
            {
                if (randomizeSliderStart) return RandomValue(sigmaMin, sigmaMax);
                return sigmaDefault;
            }
            if (_lastYawStage1Sigma > 0f) return _lastYawStage1Sigma;
            return sigmaDefault;
        }

        if (t.kind == TrialKind.Pitch)
        {
            if (stageIdx0 == 0)
            {
                if (randomizeSliderStart) return RandomValue(sigmaMin, sigmaMax);
                return sigmaDefault;
            }
            if (_lastPitchStage1Sigma > 0f) return _lastPitchStage1Sigma;
            return sigmaDefault;
        }

        // roll: 1 stage
        if (randomizeSliderStart) return RandomValue(sigmaMin, sigmaMax);
        return sigmaDefault;
    }

    private float RandomValue(float mn, float mx)
    {
        double u = _rng.NextDouble();
        return (float)(mn + (mx - mn) * u);
    }

    private void StartPlaybackForStage(Trial t, int stageIdx0, float sigma)
    {
        float spd = t.speedDegPerSec;

        // intensity is fixed already (Session 0 result)
        ApplyCalibratedIntensityToAllControllers();

        if (t.kind == TrialKind.Yaw)
        {
            var st = (stageIdx0 == 0)
                ? YawGaussianPathStageController.YawStage.Front   // main (front+back)
                : YawGaussianPathStageController.YawStage.Side;   // seam

            yawCtrl.SetSigmaForStage(st, sigma);
            yawCtrl.StartStage(st, spd);
        }
        else if (t.kind == TrialKind.Roll)
        {
            rollCtrl.StartStage(spd);
        }
        else // Pitch
        {
            var st = (stageIdx0 == 0)
                ? PitchGaussianPathStageController.PitchStage.Front // main
                : PitchGaussianPathStageController.PitchStage.Top;  // seam

            pitchCtrl.SetSigmaForStage(st, sigma);
            pitchCtrl.StartStage(st, spd);
        }
    }

    private void SafeStopAllControllers()
    {
        if (yawCtrl != null) yawCtrl.StopAll();
        if (pitchCtrl != null) pitchCtrl.StopAll();
        if (rollCtrl != null) rollCtrl.StopHaptics();
    }

    // =========================
    // Slider events (Session0=Intensity, Session1..=Sigma)
    // =========================
    private void OnSliderChanged(float v)
    {
        // session 0: intensity live update
        if (_sessionIdx == 0)
        {
            var step = _calSteps[Mathf.Clamp(_calStepIdx, 0, _calSteps.Count - 1)];

            v = Snap(v, intensityMin, intensityMax, intensityStep);
            sigmaSlider.SetValueWithoutNotify(v);
            UpdateSigmaValueText(v);

            ApplyIntensityForKind(step.kind, v);
            return;
        }

        // session 1..: sigma live update
        v = Snap(v, sigmaMin, sigmaMax, sigmaStep);
        sigmaSlider.SetValueWithoutNotify(v);
        UpdateSigmaValueText(v);

        if (_trialIdx >= _trials.Count) return;
        var t = _trials[_trialIdx];

        if (t.kind == TrialKind.Yaw)
        {
            var st = (_stageInTrial == 0)
                ? YawGaussianPathStageController.YawStage.Front
                : YawGaussianPathStageController.YawStage.Side;

            yawCtrl.SetSigmaForStage(st, v);
        }
        else if (t.kind == TrialKind.Pitch)
        {
            var st = (_stageInTrial == 0)
                ? PitchGaussianPathStageController.PitchStage.Front
                : PitchGaussianPathStageController.PitchStage.Top;

            pitchCtrl.SetSigmaForStage(st, v);
        }
        else
        {
            // roll: sigma 필요하면 rollCtrl 내부 setter 연결
        }
    }

    private void OnNextClicked()
    {
        // ===== Session 0: save intensity calibration
        if (_sessionIdx == 0)
        {
            var step = _calSteps[_calStepIdx];
            float intensity01 = sigmaSlider.value;
            intensity01 = Snap(intensity01, intensityMin, intensityMax, intensityStep);

            SaveCalibrationIntensity(step.kind, intensity01);

            // CSV (calibration)
            AppendCsvRow(
                sessionIndex: 0,
                mode: "calibration",
                direction: step.kind,
                speedDegPerSec: calibrationSpeedDegPerSec,
                part: "intensity",
                sigma: 0f,
                intensity01: intensity01,
                trialIndex1Based: 0,
                stageInTrial1Based: 0
            );

            _calStepIdx++;
            EnterCalibrationStep();
            return;
        }

        // ===== Session 1..: normal trial stage
        if (_trialIdx < _trials.Count)
        {
            var t = _trials[_trialIdx];
            float sigma = sigmaSlider.value;

            if (t.kind == TrialKind.Yaw && _stageInTrial == 0) _lastYawStage1Sigma = sigma;
            if (t.kind == TrialKind.Pitch && _stageInTrial == 0) _lastPitchStage1Sigma = sigma;

            string part =
                (t.kind == TrialKind.Yaw) ? (_stageInTrial == 0 ? "main" : "seam") :
                (t.kind == TrialKind.Pitch) ? (_stageInTrial == 0 ? "main" : "seam") :
                "main";

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
        }

        var cur = _trials[_trialIdx];
        _stageInTrial++;

        if (_stageInTrial >= cur.StageCount)
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
    // CSV append
    // =========================
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
            ts,
            Quote(mode),
            Quote(dir),
            speedDegPerSec.ToString(CultureInfo.InvariantCulture),
            Quote(part),
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
