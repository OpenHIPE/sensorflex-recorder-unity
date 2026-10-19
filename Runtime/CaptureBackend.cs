using System.IO;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SensorFlex.Recorder
{
    // Platform-specific half of the capture pipeline. CaptureCoordinator owns the session
    // lifecycle, pose, intrinsics and frame records; a backend owns everything that touches
    // XRCpuImage layouts and the native encoders.
    //
    //   IosCaptureBackend     — ARKit:  YCbCr bi-planar color, float32 m depth → SFVideoEncoder.mm
    //   AndroidCaptureBackend — ARCore: YUV_420_888 color, uint16 mm depth → MediaCodec / C# LZ4
    //   NullCaptureBackend    — Editor / other: nothing captured (pose + metadata only)
    //
    // Call order: Configure → [TryDetectDims… → StartEngines] → ResetForCapture →
    //             Encode* per frame → Finish
    internal abstract class CaptureBackend
    {
        public const string ColorFileName = "color.mp4";

        protected readonly ARSensorFlexRecorder Config;
        protected readonly string RgbMp4Path;
        protected readonly string DepthBinPath;
        protected readonly string DepthSizesPath;
        protected readonly string DepthMp4Path;

        protected CaptureBackend(ARSensorFlexRecorder config, string tempDir)
        {
            Config         = config;
            RgbMp4Path     = Path.Combine(tempDir, ColorFileName);
            DepthBinPath   = Path.Combine(tempDir, "depth.bin");
            DepthSizesPath = Path.Combine(tempDir, "depth_sizes.bin");
            DepthMp4Path   = Path.Combine(tempDir, "depth.mp4");
        }

        public static CaptureBackend Create(ARSensorFlexRecorder config, string tempDir)
        {
#if UNITY_IOS && !UNITY_EDITOR
            return new IosCaptureBackend(config, tempDir);
#elif UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidCaptureBackend(config, tempDir);
#else
            return new NullCaptureBackend(config, tempDir);
#endif
        }

        // ── Session-level results (read by CaptureCoordinator at StopCapture) ──

        public bool HasColorDims { get; protected set; }
        public int  ColorWidth   { get; protected set; }
        public int  ColorHeight  { get; protected set; }
        public bool HasDepthDims { get; protected set; }
        public int  DepthWidth   { get; protected set; }
        public int  DepthHeight  { get; protected set; }

        public abstract string DepthSensor { get; }

        // session.json depth.encoding — how to decode the bytes.
        public virtual string DepthEncoding => Config.DepthCodec switch
        {
            SfzDepthCodec.HEVC => "hevc_float16",
            SfzDepthCodec.H264 => "h264_uint8_norm",
            SfzDepthCodec.Zstd => "zstd_float32",
            _                  => "lz4_float32",
        };

        // session.json depth.units — what a decoded value means.
        public virtual string DepthUnits => "meters";

        // ── Configuration ──────────────────────────────────────────────────

        protected bool CaptureColor { get; private set; }
        protected bool CaptureDepth { get; private set; }

        protected bool IsVideoDepth =>
            Config.DepthCodec == SfzDepthCodec.HEVC || Config.DepthCodec == SfzDepthCodec.H264;

        public void Configure(bool captureColor, bool captureDepth)
        {
            CaptureColor = captureColor;
            CaptureDepth = captureDepth;
        }

        // ── Lifecycle hooks ────────────────────────────────────────────────

        // true when Begin() can start without dropping frames to encoder warm-up.
        public virtual bool IsEncoderReady => true;

        // Standby: called once per camera frame until it returns true. Detects
        // image dimensions so StartEngines can open encoders before recording.
        public virtual bool TryDetectDims(ARCameraManager cameraManager, AROcclusionManager occlusionManager)
            => true;

        // Standby: open encoders using the dimensions from TryDetectDims.
        public virtual void StartEngines() { }

        // keepDims=true after standby (encoders already open); false for a cold StartCapture.
        public virtual void ResetForCapture(bool keepDims)
        {
            _warnedMissingDepth = false;
            if (keepDims) return;
            HasColorDims = false;
            HasDepthDims = false;
        }

        // Per frame. Return true only if the frame was handed to an encoder.
        // relativeNs is the timestamp relative to the first recorded frame.
        public abstract bool EncodeColor(ARCameraManager cameraManager, long relativeNs);
        public abstract bool EncodeDepth(AROcclusionManager occlusionManager, long relativeNs);

        // Flush/close encoders. NativeVideoEncoder.WaitForBothFinished blocks on the result.
        public virtual void Finish() { }

        // ── Shared helpers ─────────────────────────────────────────────────

        bool _warnedMissingDepth;

        protected bool TryAcquireDepth(AROcclusionManager occlusionManager, out XRCpuImage image)
        {
            if (occlusionManager.TryAcquireEnvironmentDepthCpuImage(out image))
            {
                _warnedMissingDepth = false;
                return true;
            }

            if (!_warnedMissingDepth)
            {
                Debug.LogWarning(
                    $"[SF-Recorder] Depth unavailable. " +
                    $"requested={occlusionManager.requestedEnvironmentDepthMode} " +
                    $"current={occlusionManager.currentEnvironmentDepthMode}");
                _warnedMissingDepth = true;
            }
            return false;
        }

        protected static void WarnOnce(ref bool warned, string message)
        {
            if (warned) return;
            Debug.LogWarning(message);
            warned = true;
        }
    }

    // Editor / unsupported platforms: records pose + metadata only.
    internal sealed class NullCaptureBackend : CaptureBackend
    {
        bool _warnedColor, _warnedDepth;

        public NullCaptureBackend(ARSensorFlexRecorder config, string tempDir) : base(config, tempDir) { }

        public override string DepthSensor => "arfoundation_environment_depth";

        public override bool EncodeColor(ARCameraManager cameraManager, long relativeNs)
        {
            WarnOnce(ref _warnedColor,
                "[SF-Recorder] RGB recording is only supported on iOS and Android. No frames will be captured on this platform.");
            return false;
        }

        public override bool EncodeDepth(AROcclusionManager occlusionManager, long relativeNs)
        {
            WarnOnce(ref _warnedDepth,
                "[SF-Recorder] Depth recording is only supported on iOS and Android. No depth will be captured on this platform.");
            return false;
        }
    }
}
