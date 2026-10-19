#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SensorFlex.Recorder
{
    // Android XR headset rig (e.g. Galaxy XR), frame-driving:
    //
    //   color       — world-facing camera via Camera2 (SFXrCameraRecorder → SFVideoEncoderAndroid),
    //                 the frame source. Pose = head pose at exposure time (OpenXR, via
    //                 OpenXRRigBridge) × camera-from-head offset; nominal intrinsics from the lens.
    //   depth_left  — OpenXR environment depth, left eye view: float32 metres (lz4_float32),
    //   depth_right   each with its own pose, FOV-derived intrinsics and timestamp.
    //
    // Each color frame becomes one SfzFrame; it references the depth sample nearest in time
    // (written once, then referenced by index while it stays the nearest).
    //
    // Limits (measured on Galaxy XR, Phase 0): Camera2 opens one camera at a time, so color is
    // mono; the color camera publishes no calibration, so intrinsics are nominal and the
    // camera-from-head offset is estimated from the left depth view.
    internal sealed class AndroidXrCaptureBackend : CaptureBackend
    {
        const string CameraId       = "0";     // left world-facing camera
        const int    ColorW         = 1920, ColorH = 1440;
        const long   MaxDepthGapNs  = 100_000_000;   // 100 ms
        const long   DepthMinStepNs = 35_000_000;    // read depth back at most ~28 Hz
        const int    ExtrinsicSamples = 30;
        static readonly string[] Eyes = { "depth_left", "depth_right" };

        AndroidJavaObject _camera;
        readonly ManualResetEventSlim _colorDone = new(true);
        DoneProxy _doneProxy;

        Camera             _cam;
        Transform          _tracking;
        AROcclusionManager _occlusion;

        readonly List<SfzFrame> _frames = new(4096);
        readonly List<Pose>     _colorHeads = new(4096);   // head pose per frame; offset applied at Finish
        int    _colorIndex;
        float[] _colorK;                     // nominal fx, fy, cx, cy
        bool   _warnedNoBridge, _warnedNoDepth;
        int    _lastDropped;

        // Depth
        sealed class DepthSample
        {
            public long         MonoNs;
            public Pose[]       Views = new Pose[2];      // tracking space
            public XRFov[]      Fovs  = new XRFov[2];
            public byte[][]     Data  = new byte[2][];    // float32 metres, top row first
            public int          Pending;                  // readbacks outstanding
            public int[]        Index = { -2, -2 };       // -2 = not written yet
        }
        readonly List<DepthSample> _depth = new(6);
        Lz4DepthStreamWriter[] _writers;
        int[] _depthWritten = new int[2];
        int   _depthW, _depthH;
        long  _lastDepthReadNs;

        // Camera-from-head offset (head = OpenXR VIEW), estimated from the left depth view.
        Vector3    _extPos = new(-0.032f, 0f, 0f);
        Quaternion _extRot = Quaternion.identity;
        readonly List<Vector3>    _extPosSamples = new();
        readonly List<Quaternion> _extRotSamples = new();

        public AndroidXrCaptureBackend(ARSensorFlexRecorder config, string tempDir) : base(config, tempDir) { }

        public override bool   DrivesFrames => true;
        public override string ArFramework  => "AndroidXR";
        public override string DepthSensor  => "androidxr_depth";
        public override List<SfzFrame> Frames => _frames;
        public override long LatestFrameTimestampNs => _frames.Count > 0 ? _frames[^1].TimestampNs : 0;

        // ── Lifecycle ──────────────────────────────────────────────────────

        public override bool TryDetectDims(ARCameraManager cameraManager, AROcclusionManager occlusionManager)
        {
            ColorWidth = ColorW; ColorHeight = ColorH; HasColorDims = true;
            return true;
        }

        public override void OnCaptureBegin(Camera camera, Transform trackingSpace, AROcclusionManager occlusionManager)
        {
            _cam       = camera;
            _tracking  = trackingSpace;
            _occlusion = CaptureDepth ? occlusionManager : null;
            _frames.Clear();
            _colorHeads.Clear();
            _colorIndex = 0;

            if (!OpenXRRigBridge.IsAvailable)
                WarnOnce(ref _warnedNoBridge, "[SF-Recorder] OpenXR bridge unavailable (enable 'SensorFlex Recorder Support' " +
                                              "in OpenXR features) — color poses use the current head pose.");

            using (var player   = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var cls      = new AndroidJavaClass("com.sensorflex.recorder.SFXrCameraRecorder"))
            {
                _colorK = cls.CallStatic<float[]>("nominalIntrinsics", activity, CameraId, ColorW, ColorH);
                _camera = new AndroidJavaObject("com.sensorflex.recorder.SFXrCameraRecorder");
                int codec = Config.RgbCodec == SfzRgbCodec.H264 ? 0 : 1;
                _colorDone.Reset();
                if (!_camera.Call<bool>("start", activity, CameraId, RgbMp4Path, ColorW, ColorH, codec))
                {
                    Debug.LogError("[SF-Recorder] Android XR camera failed to start (CAMERA permission granted?).");
                    _colorDone.Set();
                    _camera = null;
                }
            }
            Debug.Log($"[SF-Recorder] Android XR capture: color {ColorW}x{ColorH} camera {CameraId}, " +
                      $"K=[{(_colorK != null ? string.Join(", ", _colorK) : "none")}], depth={_occlusion != null}");

            if (_occlusion != null) _occlusion.frameReceived += OnDepthFrame;
        }

        public override void Tick()
        {
            if (_camera == null) return;
            var stamps = _camera.Call<long[]>("drainTimestamps");
            if (stamps == null) return;
            foreach (long mono in stamps)
                _frames.Add(BuildFrame(mono));

            int dropped = _camera.Call<int>("droppedFrames");
            if (dropped != _lastDropped)
            {
                Debug.LogWarning($"[SF-Recorder] Android XR camera dropped {dropped - _lastDropped} frame(s) (encoder busy).");
                _lastDropped = dropped;
            }
        }

        public override void Finish()
        {
            if (_occlusion != null) _occlusion.frameReceived -= OnDepthFrame;
            if (_camera != null)
            {
                Tick();   // collect the last timestamps before stopping
                _doneProxy = new DoneProxy(_colorDone);
                _camera.Call("stop", _doneProxy);
                _camera = null;
            }
            ApplyFinalOffset();
        }

        // Re-derive every color pose with the final camera-from-head offset, so frames recorded
        // before the offset estimate settled use the same offset as the rest.
        void ApplyFinalOffset()
        {
            for (int i = 0; i < _frames.Count; i++)
            {
                var samples = _frames[i].Samples;
                var color   = samples[0].Value;
                color.Position = _colorHeads[i].position + _colorHeads[i].rotation * _extPos;
                color.Rotation = _colorHeads[i].rotation * _extRot;
                samples[0] = new KeyValuePair<string, SfzSample>(ColorSensor, color);
            }
        }

        public override void WaitForFinish(int timeoutMs)
        {
            _colorDone.Wait(timeoutMs);
            if (_writers != null) foreach (var w in _writers) w.Finish();
        }

        public override bool EncodeColor(ARCameraManager cameraManager, long relativeNs) => false;
        public override bool EncodeDepth(AROcclusionManager occlusionManager, long relativeNs) => false;

        public override List<SfzSensorInfo> DescribeSensors()
        {
            var list = new List<SfzSensorInfo>
            {
                new SfzSensorInfo { Name = ColorSensor, Type = "color", File = ColorFileName,
                                    Encoding = Config.RgbCodec == SfzRgbCodec.H264 ? "h264_mp4" : "hevc_mp4",
                                    Width = ColorW, Height = ColorH },
            };
            if (_writers != null)
                for (int e = 0; e < 2; e++)
                    list.Add(new SfzSensorInfo
                    {
                        Name = Eyes[e], Type = "depth", File = Eyes[e] + ".bin", Encoding = "lz4_float32",
                        Width = _depthW, Height = _depthH, Units = "meters", Source = DepthSensor,
                        SizesFile = Path.Combine(TempDir, Eyes[e] + "_sizes.bin"),
                    });
            return list;
        }

        // ── Frames ─────────────────────────────────────────────────────────

        SfzFrame BuildFrame(long colorMono)
        {
            var frame = new SfzFrame { TimestampNs = colorMono };

            Pose head = HeadAt(colorMono);
            _colorHeads.Add(head);
            var color = new SfzSample
            {
                TimestampNs = colorMono,
                Index       = _colorIndex++,
                Position    = head.position + head.rotation * _extPos,
                Rotation    = head.rotation * _extRot,
            };
            if (_colorK != null && _colorK.Length == 4)
            {
                color.HasIntrinsics = true;
                color.Fx = _colorK[0]; color.Fy = _colorK[1]; color.Cx = _colorK[2]; color.Cy = _colorK[3];
            }
            frame.Add(ColorSensor, color);

            var d = NearestDepth(colorMono);
            if (d != null)
            {
                WriteDepth(d);
                for (int e = 0; e < 2; e++)
                {
                    if (d.Index[e] < 0) continue;
                    var view = ToWorld(d.Views[e]);
                    var s = new SfzSample
                    {
                        TimestampNs = d.MonoNs, Index = d.Index[e],
                        Position = view.position, Rotation = view.rotation, HasIntrinsics = true,
                    };
                    FovToIntrinsics(d.Fovs[e], _depthW, _depthH, out s.Fx, out s.Fy, out s.Cx, out s.Cy);
                    frame.Add(Eyes[e], s);
                }
            }
            return frame;
        }

        // Head pose (world) at a monotonic time; falls back to the current camera pose.
        Pose HeadAt(long mono)
        {
            if (OpenXRRigBridge.IsAvailable)
            {
                var (ok, p) = OpenXRRigBridge.LocateHeadAtMonotonicNs(mono);
                if (ok) return ToWorld(p);
            }
            return _cam != null ? new Pose(_cam.transform.position, _cam.transform.rotation) : Pose.identity;
        }

        Pose ToWorld(Pose trackingPose) => _tracking == null
            ? trackingPose
            : new Pose(_tracking.TransformPoint(trackingPose.position), _tracking.rotation * trackingPose.rotation);

        static void FovToIntrinsics(XRFov f, int w, int h, out float fx, out float fy, out float cx, out float cy)
        {
            float l = Mathf.Tan(f.angleLeft), r = Mathf.Tan(f.angleRight);
            float u = Mathf.Tan(f.angleUp),   d = Mathf.Tan(f.angleDown);
            fx = w / (r - l); cx = -l * fx;
            fy = h / (u - d); cy = u * fy;
        }

        // ── Depth ──────────────────────────────────────────────────────────

        void OnDepthFrame(AROcclusionFrameEventArgs args)
        {
            if (!args.TryGetTimestamp(out long xrTs) || !args.TryGetPoses(out var poses) ||
                !args.TryGetFovs(out var fovs) || poses.Count < 2 || fovs.Count < 2)
                return;

            long mono = OpenXRRigBridge.IsAvailable ? OpenXRRigBridge.XrTimeToMonotonicNs(xrTs) : 0;
            if (mono == 0) { WarnOnce(ref _warnedNoDepth, "[SF-Recorder] Depth timestamps cannot be converted — depth skipped."); return; }
            if (_depth.Count > 0 && (mono == _depth[^1].MonoNs || mono - _lastDepthReadNs < DepthMinStepNs)) return;

            Texture tex = null;
            foreach (var t in args.externalTextures)
                if (t.texture != null && t.texture.graphicsFormat == GraphicsFormat.R32_SFloat) { tex = t.texture; break; }
            if (tex == null) return;

            if (_writers == null)
            {
                _depthW = tex.width; _depthH = tex.height;
                _writers = new Lz4DepthStreamWriter[2];
                for (int e = 0; e < 2; e++)
                    _writers[e] = new Lz4DepthStreamWriter(Path.Combine(TempDir, Eyes[e] + ".bin"),
                                                           Path.Combine(TempDir, Eyes[e] + "_sizes.bin"),
                                                           _depthW * _depthH * 4);
            }

            UpdateExtrinsic(poses[0]);

            var sample = new DepthSample { MonoNs = mono, Pending = 2 };
            for (int e = 0; e < 2; e++) { sample.Views[e] = poses[e]; sample.Fovs[e] = fovs[e]; }
            _depth.Add(sample);
            if (_depth.Count > 6) _depth.RemoveAt(0);
            _lastDepthReadNs = mono;

            for (int e = 0; e < 2; e++)
            {
                int eye = e;
                AsyncGPUReadback.Request(tex, 0, 0, _depthW, 0, _depthH, eye, 1, req =>
                {
                    if (!req.hasError)
                    {
                        // Android XR depth buffers start with the bottom row (as XR_ANDROID_depth_texture's
                        // sample code implies); SFZ stores the top row first, so flip while copying.
                        var data = req.GetData<byte>();
                        var buf  = new byte[data.Length];
                        int row  = _depthW * 4;
                        for (int r = 0; r < _depthH; r++)
                            NativeArray<byte>.Copy(data, r * row, buf, (_depthH - 1 - r) * row, row);
                        sample.Data[eye] = buf;
                    }
                    sample.Pending--;
                });
            }
        }

        DepthSample NearestDepth(long mono)
        {
            DepthSample best = null;
            long bestGap = MaxDepthGapNs;
            foreach (var d in _depth)
            {
                if (d.Pending > 0) continue;
                long gap = Math.Abs(d.MonoNs - mono);
                if (gap <= bestGap) { best = d; bestGap = gap; }
            }
            return best;
        }

        void WriteDepth(DepthSample d)
        {
            for (int e = 0; e < 2; e++)
            {
                if (d.Index[e] != -2) continue;
                if (d.Data[e] != null && _writers[e].TryAppend(d.Data[e]))
                {
                    d.Index[e] = _depthWritten[e]++;
                    d.Data[e]  = null;   // written; free the readback copy
                }
                else d.Index[e] = -1;
            }
        }

        // Camera-from-head offset ≈ left depth view relative to the head. The head pose (camera
        // transform) and the view pose are not sampled at exactly the same instant, so single
        // samples are noisy under head motion: use the per-axis median of the first samples.
        // Nominal until a calibration exists.
        void UpdateExtrinsic(Pose leftView)
        {
            if (_extPosSamples.Count >= ExtrinsicSamples || _cam == null) return;
            var head = new Pose(_cam.transform.localPosition, _cam.transform.localRotation);
            var inv  = Quaternion.Inverse(head.rotation);
            _extPosSamples.Add(inv * (leftView.position - head.position));
            _extRotSamples.Add(inv * leftView.rotation);
            if (_extPosSamples.Count < ExtrinsicSamples) return;

            float Median(Func<Vector3, float> axis)
            {
                var v = _extPosSamples.ConvertAll(p => axis(p));
                v.Sort();
                return v[v.Count / 2];
            }
            _extPos = new Vector3(Median(p => p.x), Median(p => p.y), Median(p => p.z));
            _extRot = _extRotSamples[_extRotSamples.Count / 2];
            Debug.Log($"[SF-Recorder] Camera-from-head offset (median of {ExtrinsicSamples} left depth views): " +
                      $"pos={_extPos:F4} rot={_extRot.eulerAngles:F2}");
        }

        sealed class DoneProxy : AndroidJavaProxy
        {
            readonly ManualResetEventSlim _done;
            public DoneProxy(ManualResetEventSlim done) : base("com.sensorflex.recorder.IEncoderDoneListener") { _done = done; }
            void onEncoderDone(bool success)
            {
                if (!success) Debug.LogWarning("[SF-Recorder] Android XR color encoder finished with errors.");
                _done.Set();
            }
        }
    }
}
#endif
