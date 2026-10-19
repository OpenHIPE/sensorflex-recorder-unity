using System;
using System.IO;
using System.Threading.Tasks;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace SensorFlex.Recorder
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(XROrigin))]
    [AddComponentMenu("XR/SensorFlex/AR SensorFlex Recorder")]
    public sealed class ARSensorFlexRecorder : MonoBehaviour
    {
        // ── Events ─────────────────────────────────────────────────────────

        // Invoked on the main thread when capture successfully starts.
        // Argument: the temp folder path where binary files are being written.
        public event Action<string> RecordingStartedEvent;

        // Invoked on the main thread when finalization completes.
        // Argument: output .sfz path (single file, always length 1).
        public event Action<string[]> RecordingFinalizedEvent;

        // Invoked on the main thread on any failure (start or finalization).
        // Argument: human-readable error description.
        public event Action<string> RecordingFailedEvent;

        // Invoked on the main thread when the max recording duration is reached and
        // capture is automatically stopped and finalization begins.
        public event Action RecordingLimitReachedEvent;

        // ── Inspector ──────────────────────────────────────────────────────

        [Header("Capture")]
        [Tooltip("Optional session identifier override. Leave empty to auto-generate.")]
        [SerializeField] string m_SessionId = "";

        [Tooltip("Capture RGB frames (iOS and Android).")]
        [SerializeField] bool m_CaptureColor = true;

        [Tooltip("Capture environment depth when supported (iOS and Android).")]
        [SerializeField] bool m_CaptureDepth = true;

        [Tooltip("Capture tracked camera pose.")]
        [SerializeField] bool m_CapturePose = true;

        [Tooltip("Capture per-frame camera intrinsics.")]
        [SerializeField] bool m_CaptureIntrinsics = true;

        [Header("Output")]
        [Tooltip("Relative (to persistentDataPath) or absolute output directory.")]
        [SerializeField] string m_OutputDirectory = "SensorFlexRecordings";

        [Tooltip("Maximum recording duration in seconds. 0 = unlimited.")]
        [Min(0)]
        [SerializeField] int m_MaxRecordingSeconds = 60;

        [Header("Encoding")]
        [SerializeField] SfzRgbCodec m_RgbCodec = SfzRgbCodec.HEVC;
        [SerializeField] SfzDepthCodec m_DepthCodec = SfzDepthCodec.LZ4;
        [Tooltip("Normalisation range for H264 depth (metres). Values beyond this are clamped.")]
        [Min(0.1f)]
        [SerializeField] float m_DepthMaxMeters = 10f;

        [Header("Android")]
        [Tooltip("Switch ARCore to the highest-resolution camera configuration (up to 1920x1080) " +
                 "during standby. ARCore's default CPU image is 640x480.")]
        [SerializeField] bool m_AndroidPreferHighResCpuImage = true;

        [Header("Controls")]
        [SerializeField] bool m_RecordOnStart;

        // ── Public read-only state ─────────────────────────────────────────

        public bool     IsRecording        { get; private set; }
        public bool     IsFinalizing       { get; private set; }
        // True when encoders are fully warmed and StartRecording() will begin with zero frame drops.
        public bool     IsStandby          { get; private set; }
        public string   ActiveSessionId    { get; private set; }
        public string[] LastArchivePaths   { get; private set; }
        public string   LastError          { get; private set; }

        // ── Exposed config (read by CaptureCoordinator) ────────────────────

        internal SfzRgbCodec   RgbCodec       => m_RgbCodec;
        internal SfzDepthCodec DepthCodec     => m_DepthCodec;
        internal float         DepthMaxMeters => m_DepthMaxMeters;
        internal bool          AndroidPreferHighResCpuImage => m_AndroidPreferHighResCpuImage;

        internal string SessionId           => string.IsNullOrEmpty(m_SessionId)
                                                  ? (ActiveSessionId ?? Guid.NewGuid().ToString("N"))
                                                  : m_SessionId;
        internal bool   CaptureColor        => m_CaptureColor;
        internal bool   CaptureDepth        => m_CaptureDepth;
        internal bool   CapturePose         => m_CapturePose;
        internal bool   CaptureIntrinsics   => m_CaptureIntrinsics;
        internal int    MaxRecordingSeconds => m_MaxRecordingSeconds;
        internal string OutputDirectory     => Path.IsPathRooted(m_OutputDirectory)
                                                  ? m_OutputDirectory
                                                  : Path.Combine(Application.persistentDataPath, m_OutputDirectory);

        // ── Private ────────────────────────────────────────────────────────

        CaptureCoordinator _coordinator;
        CaptureCoordinator _standbyCoordinator;
        Task<string[]>     _finalizationTask;
        bool               _recordOnStartPending;
        float              _recordOnStartDeadline;

        // ── Unity lifecycle ────────────────────────────────────────────────

        void Awake()
        {
            NativeVideoEncoder.LogCapabilities(m_RgbCodec, m_DepthCodec);
            NativeVideoEncoder.PrewarmEncoders();
        }

        void Start()
        {
            if (m_RecordOnStart)
            {
                _recordOnStartPending  = true;
                _recordOnStartDeadline = Time.realtimeSinceStartup + 5f;
            }
            PrepareRecording();
        }

        void Update()
        {
            // Drive standby coordinator state machine.
            if (_standbyCoordinator != null && !IsStandby && !IsRecording)
            {
                // Phase 1: try BeginStandby every frame until camera subsystem is ready.
                if (!_standbyCoordinator.HasDims)
                    _standbyCoordinator.BeginStandby();

                // Phase 2: start engines once dims are known and no old finalization is pending
                // (StartRgbSession resets static ManualResetEvents; must not race WaitForBothFinished).
                if (_standbyCoordinator.HasDims &&
                    !_standbyCoordinator.EnginesStarted &&
                    !IsFinalizing)
                {
                    _standbyCoordinator.StartEngines();
                }

                // Phase 3: transition to Standby when native setup is complete.
                if (_standbyCoordinator.IsEncoderReady)
                {
                    IsStandby = true;
                    Debug.Log("[SF-Recorder] Encoders ready — on standby.");
                }
            }

            // RecordOnStart: wait until standby is ready (or deadline passes) before starting.
            if (_recordOnStartPending && !IsRecording)
            {
                if (IsStandby)
                {
                    _recordOnStartPending = false;
                    StartRecording();
                }
                else if (Time.realtimeSinceStartup >= _recordOnStartDeadline)
                {
                    _recordOnStartPending = false;
                    Debug.LogWarning("[SF-Recorder] RecordOnStart timed out waiting for standby — starting cold.");
                    StartRecording();
                }
            }

            // When the coordinator signals that max duration is reached, finalize.
            if (IsRecording && _coordinator != null && _coordinator.LimitReached)
            {
                IsRecording = false;
                Debug.Log("[SF-Recorder] Max recording duration reached. Starting finalization.");
                BeginFinalization();
                PrepareRecording();
                RecordingLimitReachedEvent?.Invoke();
            }

            // Poll async finalization result on the main thread.
            if (_finalizationTask != null && _finalizationTask.IsCompleted)
            {
                var task          = _finalizationTask;
                _finalizationTask = null;
                IsFinalizing      = false;

                if (task.IsFaulted)
                {
                    LastError = task.Exception?.GetBaseException().Message ?? "Unknown finalization error";
                    Debug.LogError($"[SF-Recorder] Finalization failed: {LastError}");
                    RecordingFailedEvent?.Invoke(LastError);
                }
                else
                {
                    LastArchivePaths = task.Result;
                    Debug.Log($"[SF-Recorder] Finalization complete. Output='{LastArchivePaths[0]}'");
                    RecordingFinalizedEvent?.Invoke(LastArchivePaths);
                }
            }
        }

        void OnDestroy()
        {
            if (IsRecording)
                StopRecording();
            _standbyCoordinator?.Dispose();
            _standbyCoordinator = null;
        }

        // ── Public API ─────────────────────────────────────────────────────

        public void StartRecording()
        {
            if (IsRecording || IsFinalizing)
                return;

            LastError        = null;
            LastArchivePaths = null;

            // Fast path: use the pre-warmed standby coordinator.
            if (_standbyCoordinator != null && _standbyCoordinator.IsEncoderReady)
            {
                _coordinator        = _standbyCoordinator;
                _standbyCoordinator = null;
                IsStandby           = false;
                ActiveSessionId     = _coordinator.SessionId;

                _coordinator.Begin();
                IsRecording = true;
                Debug.Log($"[SF-Recorder] Recording started (pre-warmed). Session='{ActiveSessionId}'");
                RecordingStartedEvent?.Invoke(_coordinator.TempDir);
                return;
            }

            // Fallback: cold start (some frame drops expected while encoders warm up).
            _standbyCoordinator?.Dispose();
            _standbyCoordinator = null;
            IsStandby           = false;

            ActiveSessionId = string.IsNullOrEmpty(m_SessionId)
                ? Guid.NewGuid().ToString("N")
                : m_SessionId;

            string tempDir = Path.Combine(
                Application.temporaryCachePath, "SF-Recorder", ActiveSessionId);

            _coordinator = new CaptureCoordinator(this, tempDir, ActiveSessionId);

            if (!_coordinator.StartCapture())
            {
                LastError = "Recorder failed to start. See Console for details.";
                _coordinator.Dispose();
                _coordinator = null;
                RecordingFailedEvent?.Invoke(LastError);
                return;
            }

            IsRecording = true;
            Debug.Log($"[SF-Recorder] Recording started (cold). Session='{ActiveSessionId}'");
            RecordingStartedEvent?.Invoke(tempDir);
        }

        public void StopRecording()
        {
            if (!IsRecording)
                return;

            IsRecording = false;
            BeginFinalization();
            PrepareRecording();
        }

        // ── Private helpers ────────────────────────────────────────────────

        // Prepares the next recording session in the background. Called from Start()
        // and after each StopRecording(). Creates a standby coordinator that dims-scans
        // immediately; StartEngines() is driven from Update() once finalization is done.
        void PrepareRecording()
        {
            if (_standbyCoordinator != null) return;  // already preparing

            string sessionId = string.IsNullOrEmpty(m_SessionId)
                ? Guid.NewGuid().ToString("N")
                : m_SessionId;
            string tempDir = Path.Combine(
                Application.temporaryCachePath, "SF-Recorder", sessionId);

            _standbyCoordinator = new CaptureCoordinator(this, tempDir, sessionId);
            IsStandby = false;
        }

        void BeginFinalization()
        {
            if (_coordinator == null)
                return;

            _coordinator.StopCapture();

            string tempDir  = _coordinator.TempDir;
            var    meta     = _coordinator.SessionMetadata;
            var    records  = _coordinator.FrameRecords;

            _coordinator.Dispose();
            _coordinator = null;

            if (records.Count == 0)
            {
                Debug.LogWarning("[SF-Recorder] Recording stopped with zero frames. No archive will be created.");
                return;
            }

            string outputDir = Path.IsPathRooted(m_OutputDirectory)
                ? m_OutputDirectory
                : Path.Combine(Application.persistentDataPath, m_OutputDirectory);

            IsFinalizing      = true;
            _finalizationTask = ArchiveFinalizer.FinalizeAsync(tempDir, outputDir, meta, records);
            Debug.Log($"[SF-Recorder] Recording stopped ({records.Count} frames). Finalizing archive...");
        }
    }
}
