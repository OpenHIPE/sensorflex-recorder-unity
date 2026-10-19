#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SensorFlex.Recorder
{
    // ARCore capture:
    //   color — YUV_420_888 (Y, U, V planes; U/V pixelStride 1 or 2) → NV12 written straight
    //           into a direct-ByteBuffer slot → SFVideoEncoderAndroid (MediaCodec)
    //   depth — DepthUint16 millimetres → C# LZ4 worker (lz4_uint16, native precision)
    //           or uint8-normalised NV12 → SFVideoEncoderAndroid (HEVC/H264)
    internal sealed class AndroidCaptureBackend : CaptureBackend
    {
        // Largest CPU image we ask ARCore for (landscape). Larger configs cost encode time.
        const int MaxCpuImageWidth  = 1920;
        const int MaxCpuImageHeight = 1080;
        const int PreferredFps      = 30;
        // Frames to wait for the new camera config to take effect before accepting any size.
        const int MaxConfigSettleFrames = 30;

        bool _rgbStarted;
        bool _depthLz4Started;
        bool _depthVideoStarted;

        bool _cameraConfigRequested;
        int  _targetColorW, _targetColorH;
        int  _settleFrames;

        bool _warnedColorFormat, _warnedColorSize, _warnedRgbBusy;
        bool _warnedDepthFormat, _warnedDepthSize, _warnedDepthBusy;
        bool _warnedZstd;

        public AndroidCaptureBackend(ARSensorFlexRecorder config, string tempDir) : base(config, tempDir) { }

        public override string DepthSensor => "arcore_environment_depth";

        // Zstd is not available on Android — block depth is always LZ4.
        public override string DepthEncoding => Config.DepthCodec switch
        {
            SfzDepthCodec.HEVC => "hevc_uint8_norm",
            SfzDepthCodec.H264 => "h264_uint8_norm",
            _                  => "lz4_uint16",
        };

        public override string DepthUnits => IsVideoDepth ? "meters" : "millimeters";

        // ── Standby ────────────────────────────────────────────────────────

        public override bool TryDetectDims(ARCameraManager cameraManager, AROcclusionManager occlusionManager)
        {
            if (CaptureColor && Config.AndroidPreferHighResCpuImage && !_cameraConfigRequested)
            {
                _cameraConfigRequested = true;
                if (TrySelectCameraConfig(cameraManager))
                    return false; // let the new configuration take effect first
            }

            bool colorDone = !CaptureColor;
            bool depthDone = !CaptureDepth || !IsVideoDepth; // LZ4: dims taken from first recorded frame

            if (CaptureColor && cameraManager.TryAcquireLatestCpuImage(out var colorImg))
            {
                using (colorImg)
                {
                    bool matchesTarget = _targetColorW == 0 ||
                                         (colorImg.width == _targetColorW && colorImg.height == _targetColorH);
                    if (matchesTarget || ++_settleFrames > MaxConfigSettleFrames)
                    {
                        ColorWidth   = colorImg.width;
                        ColorHeight  = colorImg.height;
                        HasColorDims = true;
                        colorDone    = true;
                    }
                }
            }

            if (!depthDone && occlusionManager != null &&
                occlusionManager.TryAcquireEnvironmentDepthCpuImage(out var depthImg))
            {
                using (depthImg)
                {
                    DepthWidth   = depthImg.width;
                    DepthHeight  = depthImg.height;
                    HasDepthDims = true;
                    depthDone    = true;
                }
            }

            return colorDone && depthDone;
        }

        // Picks the largest CPU image ≤ 1920x1080, preferring 30 fps on ties.
        // Returns true if the configuration was changed.
        bool TrySelectCameraConfig(ARCameraManager cameraManager)
        {
            try
            {
                using var configs = cameraManager.GetConfigurations(Allocator.Temp);
                if (!configs.IsCreated || configs.Length == 0) return false;

                int  best   = -1;
                long bestPx = 0;
                for (int i = 0; i < configs.Length; i++)
                {
                    var c = configs[i];
                    if (Math.Max(c.width, c.height) > MaxCpuImageWidth ||
                        Math.Min(c.width, c.height) > MaxCpuImageHeight)
                        continue;

                    long px = (long)c.width * c.height;
                    bool better = px > bestPx ||
                                  (px == bestPx && c.framerate == PreferredFps && configs[best].framerate != PreferredFps);
                    if (better) { best = i; bestPx = px; }
                }
                if (best < 0) return false;

                var chosen = configs[best];
                _targetColorW = chosen.width;
                _targetColorH = chosen.height;

                var current = cameraManager.currentConfiguration;
                if (current.HasValue && current.Value == chosen) return false;

                cameraManager.currentConfiguration = chosen;
                Debug.Log($"[SF-Recorder] ARCore camera config → {chosen.width}x{chosen.height} @ {chosen.framerate} fps");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SF-Recorder] Could not change ARCore camera config: {e.Message}");
                _targetColorW = _targetColorH = 0;
                return false;
            }
        }

        public override void StartEngines()
        {
            if (CaptureColor && ColorWidth > 0)
                StartRgb(ColorWidth, ColorHeight);

            if (CaptureDepth && IsVideoDepth && DepthWidth > 0)
                StartDepthVideo(DepthWidth, DepthHeight);
        }

        public override void ResetForCapture(bool keepDims)
        {
            base.ResetForCapture(keepDims);
            _warnedColorFormat = _warnedColorSize = _warnedRgbBusy = false;
            _warnedDepthFormat = _warnedDepthSize = _warnedDepthBusy = false;
            if (keepDims) return;
            _rgbStarted        = false;
            _depthLz4Started   = false;
            _depthVideoStarted = false;
        }

        public override void Finish()
        {
            // Called even when RGB never started so the done-event is signalled (no 60 s wait).
            NativeVideoEncoder.FinishRgbSession();
            NativeVideoEncoder.FinishDepthSession();
        }

        void StartRgb(int w, int h)
        {
            NativeVideoEncoder.StartRgbSession(RgbMp4Path, w, h, Config.RgbCodec);
            ColorWidth   = w;
            ColorHeight  = h;
            HasColorDims = true;
            _rgbStarted  = true;
        }

        void StartDepthVideo(int w, int h)
        {
            NativeVideoEncoder.StartDepthVideoSession(DepthMp4Path, w, h, Config.DepthCodec, Config.DepthMaxMeters);
            DepthWidth         = w;
            DepthHeight        = h;
            HasDepthDims       = true;
            _depthVideoStarted = true;
        }

        // ── Color ──────────────────────────────────────────────────────────

        public override unsafe bool EncodeColor(ARCameraManager cameraManager, long relativeNs)
        {
            if (!cameraManager.TryAcquireLatestCpuImage(out var image))
                return false;

            try
            {
                if (image.format != XRCpuImage.Format.AndroidYuv420_888 || image.planeCount != 3)
                {
                    WarnOnce(ref _warnedColorFormat,
                        $"[SF-Recorder] Unexpected ARCore CPU image format {image.format} ({image.planeCount} planes) — color skipped.");
                    return false;
                }

                if (!_rgbStarted)
                    StartRgb(image.width, image.height);

                if (image.width != ColorWidth || image.height != ColorHeight)
                {
                    WarnOnce(ref _warnedColorSize,
                        $"[SF-Recorder] Camera image changed size mid-recording ({image.width}x{image.height}, " +
                        $"encoder is {ColorWidth}x{ColorHeight}) — frames dropped.");
                    return false;
                }

                int slot = NativeVideoEncoder.AcquireRgbSlot(out IntPtr dst);
                if (slot < 0)
                {
                    WarnOnce(ref _warnedRgbBusy, "[SF-Recorder] RGB encoder busy — frame dropped.");
                    return false;
                }

                var y = image.GetPlane(0);
                var u = image.GetPlane(1);
                var v = image.GetPlane(2);
                Yuv420ToNv12(
                    (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(y.data), y.rowStride,
                    (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(u.data), u.rowStride, u.pixelStride,
                    (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(v.data), v.rowStride, v.pixelStride,
                    (byte*)dst, image.width, image.height);

                return NativeVideoEncoder.SubmitRgbSlot(slot, relativeNs);
            }
            finally
            {
                image.Dispose();
            }
        }

        // YUV_420_888 → tightly packed NV12 (Y plane, then interleaved UV).
        // Handles planar (pixelStride 1) and semi-planar NV12/NV21 (pixelStride 2) sources.
        static unsafe void Yuv420ToNv12(
            byte* srcY, int yRowStride,
            byte* srcU, int uRowStride, int uPixelStride,
            byte* srcV, int vRowStride, int vPixelStride,
            byte* dst, int width, int height)
        {
            for (int row = 0; row < height; row++)
                UnsafeUtility.MemCpy(dst + row * width, srcY + row * yRowStride, width);

            byte* dstUV   = dst + width * height;
            int   chromaW = width  / 2;
            int   chromaH = height / 2;
            for (int row = 0; row < chromaH; row++)
            {
                byte* uRow = srcU + row * uRowStride;
                byte* vRow = srcV + row * vRowStride;
                byte* d    = dstUV + row * width;
                for (int col = 0; col < chromaW; col++)
                {
                    d[col * 2]     = uRow[col * uPixelStride];
                    d[col * 2 + 1] = vRow[col * vPixelStride];
                }
            }
        }

        // ── Depth ──────────────────────────────────────────────────────────

        public override unsafe bool EncodeDepth(AROcclusionManager occlusionManager, long relativeNs)
        {
            if (!TryAcquireDepth(occlusionManager, out var image))
                return false;

            try
            {
                if (image.format != XRCpuImage.Format.DepthUint16)
                {
                    WarnOnce(ref _warnedDepthFormat,
                        $"[SF-Recorder] Unexpected ARCore depth format {image.format} — depth skipped.");
                    return false;
                }

                if (IsVideoDepth)
                {
                    if (!_depthVideoStarted) StartDepthVideo(image.width, image.height);
                }
                else if (!_depthLz4Started)
                {
                    if (Config.DepthCodec == SfzDepthCodec.Zstd)
                        WarnOnce(ref _warnedZstd, "[SF-Recorder] Zstd depth is not available on Android — recording as LZ4.");
                    NativeVideoEncoder.StartDepthLz4Writer(DepthBinPath, DepthSizesPath,
                                                           image.width, image.height, bytesPerPixel: 2);
                    DepthWidth       = image.width;
                    DepthHeight      = image.height;
                    HasDepthDims     = true;
                    _depthLz4Started = true;
                }

                if (image.width != DepthWidth || image.height != DepthHeight)
                {
                    WarnOnce(ref _warnedDepthSize,
                        $"[SF-Recorder] Depth image changed size mid-recording ({image.width}x{image.height}) — frames dropped.");
                    return false;
                }

                var   plane = image.GetPlane(0);
                byte* src   = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(plane.data);

                if (!IsVideoDepth)
                {
                    bool queued = NativeVideoEncoder.AppendDepthLz4Frame((IntPtr)src, plane.rowStride, image.width, image.height);
                    if (!queued) WarnOnce(ref _warnedDepthBusy, "[SF-Recorder] Depth LZ4 writer busy — frame dropped.");
                    return queued;
                }

                int slot = NativeVideoEncoder.AcquireDepthVideoSlot(out IntPtr dst);
                if (slot < 0)
                {
                    WarnOnce(ref _warnedDepthBusy, "[SF-Recorder] Depth video encoder busy — frame dropped.");
                    return false;
                }

                float maxMm = (Config.DepthMaxMeters > 0f ? Config.DepthMaxMeters : 10f) * 1000f;
                DepthMmToNv12Norm(src, plane.rowStride, (byte*)dst, image.width, image.height, 255f / maxMm);
                return NativeVideoEncoder.SubmitDepthVideoSlot(slot, relativeNs);
            }
            finally
            {
                image.Dispose();
            }
        }

        // uint16 mm → uint8 Y (0..depth_max_meters → 0..255), neutral chroma.
        static unsafe void DepthMmToNv12Norm(byte* src, int rowStride, byte* dst, int width, int height, float scale)
        {
            for (int row = 0; row < height; row++)
            {
                ushort* s = (ushort*)(src + row * rowStride);
                byte*   d = dst + row * width;
                for (int col = 0; col < width; col++)
                {
                    float n = s[col] * scale;
                    d[col] = (byte)(n > 255f ? 255 : n);
                }
            }
            UnsafeUtility.MemSet(dst + width * height, 128, width * (height / 2));
        }
    }
}
#endif
