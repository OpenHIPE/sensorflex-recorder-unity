using System;
using System.Collections.Generic;
using System.IO;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SensorFlex.Recorder
{
    // Subscribes to ARFoundation callbacks, builds per-frame SfzFrameRecords in memory,
    // and hands color/depth images to a platform CaptureBackend:
    //
    //   iOS     — IosCaptureBackend     (ARKit  → SFVideoEncoder.mm)
    //   Android — AndroidCaptureBackend (ARCore → SFVideoEncoderAndroid.java / C# LZ4)
    //   Other   — NullCaptureBackend    (pose + metadata only, warnings emitted)
    //
    // FrameRecords are kept in memory; ArchiveFinalizer consumes them to build frames.jsonl.
    internal sealed class CaptureCoordinator : IDisposable
    {
        // ── Public state ───────────────────────────────────────────────────

        public string                TempDir         { get; }
        public string                SessionId       { get; }
        public SfzSessionMetadata    SessionMetadata { get; private set; }
        public List<SfzFrameRecord>  FrameRecords    { get; } = new List<SfzFrameRecord>(4096);

        // Set to true when the max recording duration is reached.
        public bool LimitReached { get; private set; }

        // true after BeginStandby() has detected image dimensions from ARKit.
        public bool HasDims { get; private set; }

        // true after StartEngines() has been called.
        public bool EnginesStarted { get; private set; }

        // true when the async native encoder setup is complete and Begin() can be called with zero drops.
        public bool IsEncoderReady => EnginesStarted && _backend.IsEncoderReady;

        // ── Private fields ─────────────────────────────────────────────────

        readonly ARSensorFlexRecorder _config;
        readonly CaptureBackend       _backend;
        ARCameraManager   _cameraManager;
        AROcclusionManager _occlusionManager;
        Camera            _mainCamera;

        int   _frameIndex;
        int   _rgbFrameIndex;  // next video frame index in rgb.mp4
        bool  _isCapturing;
        bool  _captureDepthEnabled;

        bool  _hasValidIntrinsics;
        float _cachedFx, _cachedFy, _cachedCx, _cachedCy;

        long _firstTimestampNs;
        long _lastTimestampNs;

        long _maxDurationNs;

        bool _standbySubscribed;   // prevents double-subscription in BeginStandby()
        bool _warnedDropped;
        bool _warnedMissingIntrinsics;

        const EnvironmentDepthMode TargetDepthMode = EnvironmentDepthMode.Medium;

        // ── Constructor ────────────────────────────────────────────────────

        public CaptureCoordinator(ARSensorFlexRecorder config, string tempDir, string sessionId)
        {
            _config   = config;
            TempDir   = tempDir;
            SessionId = sessionId;
            _backend  = CaptureBackend.Create(config, tempDir);
        }

        // ── Lifecycle ──────────────────────────────────────────────────────

        // Phase 1 of pre-warm: set up managers + subscribe to one ARKit frame to
        // detect image dimensions. Returns false if the camera subsystem is not
        // yet running (caller should retry next Update frame).
        public bool BeginStandby()
        {
            if (HasDims || EnginesStarted || _isCapturing) return true;

            var origin = _config.GetComponent<XROrigin>();
            _mainCamera = origin != null ? origin.Camera : Camera.main;
            if (_mainCamera == null) return false;

            _cameraManager    = _mainCamera.GetComponent<ARCameraManager>();
            _occlusionManager = _mainCamera.GetComponent<AROcclusionManager>();

            if (_cameraManager == null ||
                _cameraManager.subsystem == null ||
                !_cameraManager.subsystem.running)
                return false;

            _captureDepthEnabled = _config.CaptureDepth;
            if (_captureDepthEnabled &&
                (_occlusionManager == null || _occlusionManager.subsystem == null))
                _captureDepthEnabled = false;
            _backend.Configure(_config.CaptureColor, _captureDepthEnabled);

            // Switch depth to the recording mode now, not at Begin(): changing it restarts
            // the provider's depth pipeline (ARCore: ~2.5 s with no depth).
            if (_captureDepthEnabled && !_standbySubscribed)
                ConfigureDepthMode();

            Directory.CreateDirectory(TempDir);
            if (!_standbySubscribed)
            {
                _standbySubscribed = true;
                _cameraManager.frameReceived += OnStandbyFrameReceived;
            }
            return true;
        }

        // Phase 2: start native encoder sessions with the pre-detected dimensions.
        // Must only be called after the previous session's WaitForBothFinished has
        // completed (static NativeVideoEncoder state must be free).
        public void StartEngines()
        {
            if (EnginesStarted || !HasDims) return;
            EnginesStarted = true;
            _backend.StartEngines();
        }

        // Phase 3: called when the user presses Start. Resets per-session counters
        // and enables frame recording. Encoders must be started (via StartCapture or
        // StartEngines) before this.
        public void Begin()
        {
            if (_isCapturing) return;

            _frameIndex              = 0;
            _rgbFrameIndex           = 0;
            _hasValidIntrinsics      = false;
            _warnedDropped           = false;
            _warnedMissingIntrinsics = false;
            _firstTimestampNs        = 0;
            _lastTimestampNs         = 0;
            LimitReached             = false;
            _maxDurationNs           = _config.MaxRecordingSeconds > 0
                                         ? (long)(_config.MaxRecordingSeconds * 1_000_000_000.0)
                                         : 0L;
            FrameRecords.Clear();
            _backend.ResetForCapture(keepDims: true);

            if (_captureDepthEnabled && _occlusionManager != null)
                ConfigureDepthMode();

            _cameraManager.frameReceived += OnCameraFrameReceived;
            _isCapturing = true;

            Debug.Log($"[SF-Recorder] Recording begun (pre-warmed={EnginesStarted}). TempDir='{TempDir}'");
        }

        // Standby dim-detection callback. Retries next frame until the backend has its dimensions
        // (e.g. ARKit/ARCore depth not yet available, or a new ARCore camera config settling).
        void OnStandbyFrameReceived(ARCameraFrameEventArgs _)
        {
            _cameraManager.frameReceived -= OnStandbyFrameReceived;

            if (_backend.TryDetectDims(_cameraManager, _occlusionManager))
                HasDims = true;
            else
                _cameraManager.frameReceived += OnStandbyFrameReceived;
        }

        public bool StartCapture()
        {
            if (_isCapturing) return true;

            var origin = _config.GetComponent<XROrigin>();
            _mainCamera = origin != null ? origin.Camera : Camera.main;

            if (_mainCamera == null)
            {
                Debug.LogError("[SF-Recorder] Cannot find AR camera. Attach ARSensorFlexRecorder to the XROrigin that owns the AR camera.");
                return false;
            }

            _cameraManager    = _mainCamera.GetComponent<ARCameraManager>();
            _occlusionManager = _mainCamera.GetComponent<AROcclusionManager>();

            if (_cameraManager == null)
            {
                Debug.LogError("[SF-Recorder] ARCameraManager not found on the AR camera.");
                return false;
            }

            if (_cameraManager.subsystem == null)
            {
                Debug.LogError("[SF-Recorder] XRCameraSubsystem is not active.");
                return false;
            }

            _captureDepthEnabled = _config.CaptureDepth;
            if (_captureDepthEnabled)
            {
                if (_occlusionManager == null || _occlusionManager.subsystem == null)
                {
                    Debug.LogWarning("[SF-Recorder] Depth capture requested but XROcclusionSubsystem is unavailable. Depth will be skipped.");
                    _captureDepthEnabled = false;
                }
                else
                {
                    ConfigureDepthMode();
                }
            }
            _backend.Configure(_config.CaptureColor, _captureDepthEnabled);
            _backend.ResetForCapture(keepDims: false);

            _frameIndex              = 0;
            _rgbFrameIndex           = 0;
            _hasValidIntrinsics      = false;
            _warnedDropped           = false;
            _warnedMissingIntrinsics = false;
            _firstTimestampNs        = 0;
            _lastTimestampNs         = 0;
            LimitReached             = false;
            _maxDurationNs           = _config.MaxRecordingSeconds > 0
                                         ? (long)(_config.MaxRecordingSeconds * 1_000_000_000.0)
                                         : 0L;
            FrameRecords.Clear();

            Directory.CreateDirectory(TempDir);
            _cameraManager.frameReceived += OnCameraFrameReceived;
            _isCapturing = true;

            Debug.Log($"[SF-Recorder] Capture started. TempDir='{TempDir}' Depth={_captureDepthEnabled} MaxSeconds={_config.MaxRecordingSeconds}");
            return true;
        }

        public void StopCapture()
        {
            if (!_isCapturing) return;
            _isCapturing = false;

            if (_cameraManager != null)
                _cameraManager.frameReceived -= OnCameraFrameReceived;

            _backend.Finish();

            SessionMetadata = new SfzSessionMetadata
            {
                SessionId      = SessionId,
                StartTimeUtc   = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                DeviceModel    = SystemInfo.deviceModel,
                DeviceOs       = SystemInfo.operatingSystem,
                ArFramework    = ResolveCaptureFramework(),
                Fps            = ComputeActualFps(),
                FrameCount     = FrameRecords.Count,
                HasRgb         = _backend.HasColorDims,
                RgbWidth       = _backend.ColorWidth,
                RgbHeight      = _backend.ColorHeight,
                HasDepth       = _backend.HasDepthDims,
                DepthWidth     = _backend.DepthWidth,
                DepthHeight    = _backend.DepthHeight,
                DepthSensor    = _backend.DepthSensor,
                DepthEncoding  = _backend.DepthEncoding,
                DepthUnits     = _backend.DepthUnits,
                RgbCodec       = _config.RgbCodec,
                DepthCodec     = _config.DepthCodec,
                DepthMaxMeters = _config.DepthMaxMeters,
            };

            Debug.Log($"[SF-Recorder] Capture stopped. Frames={FrameRecords.Count} ActualFPS={SessionMetadata.Fps}");
        }

        public void Dispose()
        {
            // Unsubscribe standby callback if we're disposed mid-scan.
            if (_standbySubscribed && _cameraManager != null)
            {
                _cameraManager.frameReceived -= OnStandbyFrameReceived;
                _standbySubscribed = false;
            }
            StopCapture();
        }

        // ── Frame callback ─────────────────────────────────────────────────

        void OnCameraFrameReceived(ARCameraFrameEventArgs args)
        {
            if (!_isCapturing) return;

            long timestampNs = args.timestampNs.HasValue
                ? args.timestampNs.Value
                : (long)(Time.realtimeSinceStartup * 1_000_000_000L);

            if (_firstTimestampNs == 0) _firstTimestampNs = timestampNs;
            _lastTimestampNs = timestampNs;

            if (_maxDurationNs > 0 && (timestampNs - _firstTimestampNs) >= _maxDurationNs)
            {
                StopCapture();
                LimitReached = true;
                return;
            }

            bool hasColor = false;
            bool hasDepth = false;

            long relativeNs = timestampNs - _firstTimestampNs;
            if (_config.CaptureColor)
                hasColor = _backend.EncodeColor(_cameraManager, relativeNs);
            if (_captureDepthEnabled)
                hasDepth = _backend.EncodeDepth(_occlusionManager, relativeNs);

            // ── Pose ──────────────────────────────────────────────────────
            Vector3    position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            if (_config.CapturePose && _mainCamera != null)
            {
                position = _mainCamera.transform.position;
                rotation = _mainCamera.transform.rotation;
            }

            // ── Intrinsics ────────────────────────────────────────────────
            bool  hasIntrinsics = false;
            float fx = 0, fy = 0, cx = 0, cy = 0;
            if (_config.CaptureIntrinsics)
            {
                if (_hasValidIntrinsics)
                {
                    fx = _cachedFx; fy = _cachedFy; cx = _cachedCx; cy = _cachedCy;
                    hasIntrinsics = true;
                }
                else
                {
                    int colorW = _backend.ColorWidth, colorH = _backend.ColorHeight;
                    if (TryGetIntrinsics(colorW, colorH, out float oFx, out float oFy, out float oCx, out float oCy))
                    {
                        fx = oFx; fy = oFy; cx = oCx; cy = oCy;
                        _cachedFx = fx; _cachedFy = fy; _cachedCx = cx; _cachedCy = cy;
                        _hasValidIntrinsics = true;
                        hasIntrinsics       = true;
                    }
                    else if (!_warnedMissingIntrinsics)
                    {
                        Debug.LogWarning("[SF-Recorder] No camera intrinsics available yet.");
                        _warnedMissingIntrinsics = true;
                    }
                }
            }

            FrameRecords.Add(new SfzFrameRecord
            {
                FrameIndex    = _frameIndex,
                TimestampNs   = timestampNs,
                Position      = position,
                Rotation      = rotation,
                HasIntrinsics = hasIntrinsics,
                Fx = fx, Fy = fy, Cx = cx, Cy = cy,
                HasColor = hasColor,
                RgbFrame = hasColor ? _rgbFrameIndex++ : -1,
                HasDepth = hasDepth,
            });

            _frameIndex++;
        }

        // ── Intrinsics ─────────────────────────────────────────────────────

        bool TryGetIntrinsics(int colorW, int colorH,
            out float fx, out float fy, out float cx, out float cy)
        {
            fx = fy = cx = cy = 0f;

            if (_cameraManager != null && _cameraManager.subsystem != null &&
                _cameraManager.subsystem.TryGetIntrinsics(out var native))
            {
                if (IsValidIntrinsics(native.focalLength.x, native.focalLength.y,
                                      native.principalPoint.x, native.principalPoint.y,
                                      native.resolution.x, native.resolution.y))
                {
                    fx = native.focalLength.x;
                    fy = native.focalLength.y;
                    cx = native.principalPoint.x;
                    cy = native.principalPoint.y;
                    return true;
                }
            }

            int w = colorW > 0 ? colorW : (_mainCamera != null ? _mainCamera.pixelWidth  : Screen.width);
            int h = colorH > 0 ? colorH : (_mainCamera != null ? _mainCamera.pixelHeight : Screen.height);

            if (_mainCamera != null && w > 0 && h > 0 && _mainCamera.fieldOfView > 0f)
            {
                float vFovRad     = _mainCamera.fieldOfView * Mathf.Deg2Rad;
                float estimatedFy = 0.5f * h / Mathf.Tan(vFovRad * 0.5f);
                fx = estimatedFy * ((float)w / h); fy = estimatedFy;
                cx = w * 0.5f; cy = h * 0.5f;
                return IsValidIntrinsics(fx, fy, cx, cy, w, h);
            }

            return false;
        }

        static bool IsValidIntrinsics(float fx, float fy, float cx, float cy, int w, int h)
            => fx > 0 && fy > 0 && cx >= 0 && cx <= w && cy >= 0 && cy <= h;

        // ── Depth mode ─────────────────────────────────────────────────────

        void ConfigureDepthMode()
        {
            if (_occlusionManager == null) return;

            if (_occlusionManager.requestedEnvironmentDepthMode == EnvironmentDepthMode.Disabled ||
                _occlusionManager.requestedEnvironmentDepthMode == EnvironmentDepthMode.Fastest)
            {
                Debug.LogWarning($"[SF-Recorder] Promoting depth mode to {TargetDepthMode}.");
                _occlusionManager.requestedEnvironmentDepthMode = TargetDepthMode;
            }

            _occlusionManager.environmentDepthTemporalSmoothingRequested = true;

            Debug.Log($"[SF-Recorder] Depth configured: mode={_occlusionManager.requestedEnvironmentDepthMode} sensor={_backend.DepthSensor}");
        }

        // ── Helpers ────────────────────────────────────────────────────────

        int ComputeActualFps()
        {
            int count = FrameRecords.Count;
            if (count < 2) return 0;
            double durationSec = (_lastTimestampNs - _firstTimestampNs) / 1_000_000_000.0;
            return durationSec > 0 ? Mathf.RoundToInt((float)((count - 1) / durationSec)) : 0;
        }

        static string ResolveCaptureFramework() => Application.platform switch
        {
            RuntimePlatform.IPhonePlayer  => "ARKit",
            RuntimePlatform.Android       => "ARCore",
            RuntimePlatform.OSXEditor
            or RuntimePlatform.WindowsEditor
            or RuntimePlatform.LinuxEditor => "ARFoundation Simulation",
            _                              => "ARFoundation"
        };
    }
}
