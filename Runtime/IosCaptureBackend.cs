#if UNITY_IOS && !UNITY_EDITOR
using System;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace SensorFlex.Recorder
{
    // ARKit capture:
    //   color — YCbCr 420 bi-planar (Y + interleaved CbCr) plane pointers → SFRgbEncoder (zero-copy)
    //   depth — DepthFloat32 metres plane pointer → SFDepthLz4 (LZ4/Zstd) or SFDepthVideo (HEVC/H264)
    internal sealed class IosCaptureBackend : CaptureBackend
    {
        bool _rgbStarted;
        bool _depthLz4Started;
        bool _depthVideoStarted;
        bool _warnedRgbNotReady;
        bool _warnedDepthNotReady;

        public IosCaptureBackend(ARSensorFlexRecorder config, string tempDir) : base(config, tempDir) { }

        public override string DepthSensor => "arkit_lidar";

        public override bool IsEncoderReady =>
            NativeVideoEncoder.IsRgbReady &&
            (!(CaptureDepth && IsVideoDepth) || NativeVideoEncoder.IsDepthVideoReady);

        public override bool TryDetectDims(ARCameraManager cameraManager, AROcclusionManager occlusionManager)
        {
            bool colorDone = !CaptureColor;
            bool depthDone = !CaptureDepth;

            if (CaptureColor && cameraManager.TryAcquireLatestCpuImage(out var colorImg))
            {
                ColorWidth   = colorImg.width;
                ColorHeight  = colorImg.height;
                HasColorDims = true;
                colorImg.Dispose();
                colorDone = true;
            }

            if (CaptureDepth)
            {
                if (!IsVideoDepth)
                {
                    depthDone = true; // LZ4/Zstd: dims detected on first recording frame; no pre-warm needed
                }
                else if (occlusionManager != null &&
                         occlusionManager.TryAcquireEnvironmentDepthCpuImage(out var depthImg))
                {
                    DepthWidth   = depthImg.width;
                    DepthHeight  = depthImg.height;
                    HasDepthDims = true;
                    depthImg.Dispose();
                    depthDone = true;
                }
            }

            return colorDone && depthDone;
        }

        public override void StartEngines()
        {
            if (CaptureColor && ColorWidth > 0)
            {
                NativeVideoEncoder.StartRgbSession(RgbMp4Path, ColorWidth, ColorHeight, Config.RgbCodec);
                _rgbStarted = true;
            }

            if (CaptureDepth && IsVideoDepth && DepthWidth > 0)
            {
                NativeVideoEncoder.StartDepthVideoSession(
                    DepthMp4Path, DepthWidth, DepthHeight, Config.DepthCodec, Config.DepthMaxMeters);
                _depthVideoStarted = true;
            }
        }

        public override void ResetForCapture(bool keepDims)
        {
            base.ResetForCapture(keepDims);
            _warnedRgbNotReady   = false;
            _warnedDepthNotReady = false;
            if (keepDims) return;
            _rgbStarted        = false;
            _depthLz4Started   = false;
            _depthVideoStarted = false;
        }

        public override void Finish()
        {
            if (_rgbStarted) NativeVideoEncoder.FinishRgbSession();
            // FinishDepthSession dispatches to LZ4, video, or no-op based on what was started.
            NativeVideoEncoder.FinishDepthSession();
        }

        // ── Color ──────────────────────────────────────────────────────────

        public override unsafe bool EncodeColor(ARCameraManager cameraManager, long relativeNs)
        {
            if (!cameraManager.TryAcquireLatestCpuImage(out var cpuImage))
                return false;

            try
            {
                if (!_rgbStarted)
                {
                    NativeVideoEncoder.StartRgbSession(RgbMp4Path, cpuImage.width, cpuImage.height, Config.RgbCodec);
                    ColorWidth   = cpuImage.width;
                    ColorHeight  = cpuImage.height;
                    HasColorDims = true;
                    _rgbStarted  = true;
                }

                var planeY    = cpuImage.GetPlane(0);
                var planeCbCr = cpuImage.GetPlane(1);
                void* pY    = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(planeY.data);
                void* pCbCr = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(planeCbCr.data);

                bool appended = NativeVideoEncoder.AppendRgbFrame(
                    (IntPtr)pY,    planeY.rowStride,
                    (IntPtr)pCbCr, planeCbCr.rowStride,
                    cpuImage.width, cpuImage.height,
                    relativeNs);

                if (!appended)
                    WarnOnce(ref _warnedRgbNotReady, "[SF-Recorder] RGB encoder not ready — frame dropped.");

                return appended;
            }
            finally
            {
                cpuImage.Dispose();
            }
        }

        // ── Depth ──────────────────────────────────────────────────────────

        public override bool EncodeDepth(AROcclusionManager occlusionManager, long relativeNs)
            => IsVideoDepth ? EncodeDepthVideo(occlusionManager, relativeNs)
                            : EncodeDepthBlock(occlusionManager);

        unsafe bool EncodeDepthVideo(AROcclusionManager occlusionManager, long relativeNs)
        {
            if (!TryAcquireDepth(occlusionManager, out var depthImage))
                return false;

            try
            {
                if (!_depthVideoStarted)
                {
                    NativeVideoEncoder.StartDepthVideoSession(
                        DepthMp4Path, depthImage.width, depthImage.height,
                        Config.DepthCodec, Config.DepthMaxMeters);
                    DepthWidth         = depthImage.width;
                    DepthHeight        = depthImage.height;
                    HasDepthDims       = true;
                    _depthVideoStarted = true;
                }

                var plane  = depthImage.GetPlane(0);
                void* pF32 = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(plane.data);

                bool appended = NativeVideoEncoder.AppendDepthVideoFrame(
                    (IntPtr)pF32, plane.rowStride,
                    depthImage.width, depthImage.height,
                    relativeNs);

                if (!appended)
                    WarnOnce(ref _warnedDepthNotReady, "[SF-Recorder] Depth video encoder not ready — frame dropped.");

                return appended;
            }
            finally
            {
                depthImage.Dispose();
            }
        }

        // LZ4 or Zstd blocks → depth.bin
        unsafe bool EncodeDepthBlock(AROcclusionManager occlusionManager)
        {
            if (!TryAcquireDepth(occlusionManager, out var depthImage))
                return false;

            try
            {
                if (!_depthLz4Started)
                {
                    NativeVideoEncoder.StartDepthLz4Writer(
                        DepthBinPath, DepthSizesPath,
                        depthImage.width, depthImage.height,
                        Config.DepthCodec);
                    DepthWidth       = depthImage.width;
                    DepthHeight      = depthImage.height;
                    HasDepthDims     = true;
                    _depthLz4Started = true;
                }

                var plane  = depthImage.GetPlane(0);
                void* pF32 = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(plane.data);

                bool appended = NativeVideoEncoder.AppendDepthLz4Frame(
                    (IntPtr)pF32, plane.rowStride,
                    depthImage.width, depthImage.height);

                if (!appended)
                    WarnOnce(ref _warnedDepthNotReady, "[SF-Recorder] Depth LZ4 encoder not ready — frame dropped.");

                return appended;
            }
            finally
            {
                depthImage.Dispose();
            }
        }
    }
}
#endif
