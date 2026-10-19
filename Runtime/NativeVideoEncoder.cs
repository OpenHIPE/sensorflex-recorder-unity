using System;
using System.Runtime.InteropServices;
using System.Threading;
using AOT;

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Concurrent;
using System.IO;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
#endif

namespace SensorFlex.Recorder
{
    // P/Invoke / JNI bridge to platform-native video encoders.
    // All public methods are safe to call on any platform; non-platform builds get no-op stubs.
    //
    // Call pattern:
    //   LogCapabilities(rgbCodec, depthCodec)         (once at Awake)
    //   StartRgbSession / StartDepthLz4Writer          (at first frame, mutually exclusive with StartDepthVideoSession)
    //     or StartDepthVideoSession
    //   AppendRgbFrame / AppendDepthLz4Frame           (each frame, main thread)
    //     or AppendDepthVideoFrame
    //   (Android: AcquireRgbSlot + SubmitRgbSlot / AcquireDepthVideoSlot + SubmitDepthVideoSlot
    //    replace the Append*Frame calls for video; StartDepthLz4Writer takes bytesPerPixel.)
    //   FinishRgbSession / FinishDepthSession           (at StopCapture)
    //   WaitForBothFinished                             (in ArchiveFinalizer, background thread)
    internal static class NativeVideoEncoder
    {
#if UNITY_IOS && !UNITY_EDITOR

        // ── Depth mode ─────────────────────────────────────────────────────────

        enum DepthMode { None, Lz4, Video }
        static DepthMode _depthMode;

        // ── Native function imports ────────────────────────────────────────────

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void EncoderDoneCallback(int success);

        // Capability check / prewarm
        [DllImport("__Internal")] static extern void    SFLogCapabilities(int rgbCodec, int depthCodec);
        [DllImport("__Internal")] static extern void SFPrewarmEncoders();
        [DllImport("__Internal")] static extern int  SFRgbEncoderIsReady();
        [DllImport("__Internal")] static extern int  SFDepthVideoEncoderIsReady();

        // RGB (codec: 0=H264, 1=HEVC)
        [DllImport("__Internal")] static extern void    SFRgbEncoder_Start(string path, int w, int h, int codec);
        [DllImport("__Internal")] static extern int     SFRgbEncoder_AppendFrame(IntPtr pY,    int strideY,
                                                                                  IntPtr pCbCr, int strideCbCr,
                                                                                  int w, int h, long tsNs);
        [DllImport("__Internal")] static extern void    SFRgbEncoder_Finish(EncoderDoneCallback cb);

        // Depth LZ4
        [DllImport("__Internal")] static extern void    SFDepthLz4_Start(string binPath, string sizesPath, int w, int h, int algo);
        [DllImport("__Internal")] static extern int     SFDepthLz4_AppendFrame(IntPtr pF32, int stride, int w, int h);
        [DllImport("__Internal")] static extern void    SFDepthLz4_Finish(EncoderDoneCallback cb);

        // Depth Video (codec: 0=H264, 1=HEVC)
        [DllImport("__Internal")] static extern void    SFDepthVideo_Start(string mp4Path, int w, int h, int codec, float maxDepthMeters);
        [DllImport("__Internal")] static extern int     SFDepthVideo_AppendFrame(IntPtr pF32, int stride, int w, int h, long tsNs);
        [DllImport("__Internal")] static extern void    SFDepthVideo_Finish(EncoderDoneCallback cb);

        // ── Completion events ──────────────────────────────────────────────────

        static readonly ManualResetEventSlim _rgbDone   = new ManualResetEventSlim(false);
        static readonly ManualResetEventSlim _depthDone = new ManualResetEventSlim(false);

        static readonly EncoderDoneCallback _onRgbDone   = OnRgbDone;
        static readonly EncoderDoneCallback _onDepthDone = OnDepthDone;

        [MonoPInvokeCallback(typeof(EncoderDoneCallback))]
        static void OnRgbDone(int success)   => _rgbDone.Set();

        [MonoPInvokeCallback(typeof(EncoderDoneCallback))]
        static void OnDepthDone(int success) => _depthDone.Set();

        // ── Public API ─────────────────────────────────────────────────────────

        public static void LogCapabilities(SfzRgbCodec rgbCodec, SfzDepthCodec depthCodec)
            => SFLogCapabilities((int)rgbCodec, (int)depthCodec);

        public static void PrewarmEncoders() => SFPrewarmEncoders();

        public static bool IsRgbReady        => SFRgbEncoderIsReady()       != 0;
        public static bool IsDepthVideoReady => SFDepthVideoEncoderIsReady() != 0;

        public static void StartRgbSession(string mp4Path, int width, int height, SfzRgbCodec codec)
        {
            _rgbDone.Reset();
            _depthDone.Reset();  // Reset at session start so WaitForBothFinished works even if depth never starts
            _depthMode = DepthMode.None;
            SFRgbEncoder_Start(mp4Path, width, height, (int)codec);
        }

        public static bool AppendRgbFrame(IntPtr pY, int strideY, IntPtr pCbCr, int strideCbCr,
                                           int width, int height, long timestampNs)
            => SFRgbEncoder_AppendFrame(pY, strideY, pCbCr, strideCbCr, width, height, timestampNs) != 0;

        public static void FinishRgbSession() => SFRgbEncoder_Finish(_onRgbDone);

        public static void StartDepthLz4Writer(string binPath, string sizesPath, int width, int height,
                                                SfzDepthCodec codec = SfzDepthCodec.LZ4)
        {
            _depthMode = DepthMode.Lz4;
            _depthDone.Reset();
            int algo = codec == SfzDepthCodec.Zstd ? 1 : 0;
            SFDepthLz4_Start(binPath, sizesPath, width, height, algo);
        }

        public static bool AppendDepthLz4Frame(IntPtr pF32, int stride, int width, int height)
            => SFDepthLz4_AppendFrame(pF32, stride, width, height) != 0;

        public static void StartDepthVideoSession(string mp4Path, int width, int height,
                                                   SfzDepthCodec codec, float depthMaxMeters)
        {
            _depthMode = DepthMode.Video;
            _depthDone.Reset();
            SFDepthVideo_Start(mp4Path, width, height, (int)codec, depthMaxMeters);
        }

        public static bool AppendDepthVideoFrame(IntPtr pF32, int stride, int width, int height, long tsNs)
            => SFDepthVideo_AppendFrame(pF32, stride, width, height, tsNs) != 0;

        // Unified finish — dispatches to whichever depth encoder is active.
        public static void FinishDepthSession()
        {
            switch (_depthMode)
            {
                case DepthMode.Lz4:
                    SFDepthLz4_Finish(_onDepthDone);
                    break;
                case DepthMode.Video:
                    SFDepthVideo_Finish(_onDepthDone);
                    break;
                default:
                    // No depth encoder started — signal done immediately.
                    _depthDone.Set();
                    break;
            }
        }

        public static void WaitForBothFinished(int timeoutMs)
        {
            _rgbDone.Wait(timeoutMs);
            _depthDone.Wait(timeoutMs);
        }

#elif UNITY_ANDROID && !UNITY_EDITOR

        // ── Depth mode ─────────────────────────────────────────────────────────

        enum DepthMode { None, Lz4, Video }
        static DepthMode _depthMode;

        // ── Java encoders + direct-ByteBuffer slot rings ───────────────────────
        //
        // Each SFVideoEncoderAndroid owns SlotCount direct ByteBuffers holding one tightly
        // packed NV12 frame each. C# caches their native addresses once, writes a frame
        // straight into a free slot, and submits the slot index — no managed arrays cross
        // JNI. When every slot is in flight, AcquireSlot returns -1 and the caller drops
        // the frame (bounded memory).

        const int SlotCount = 4;

        sealed class SlotEncoder
        {
            readonly AndroidJavaObject   _java;
            readonly AndroidJavaObject[] _slotRefs = new AndroidJavaObject[SlotCount];
            readonly IntPtr[]            _slotPtrs = new IntPtr[SlotCount];
            AndroidDoneProxy             _doneProxy;   // kept alive until the callback fires

            public unsafe SlotEncoder(string mp4Path, int width, int height, int codec, bool isDepth)
            {
                _java = new AndroidJavaObject("com.sensorflex.recorder.SFVideoEncoderAndroid");
                bool ok = _java.Call<bool>("start", mp4Path, width, height, codec, isDepth, SlotCount);
                if (!ok) return;
                for (int i = 0; i < SlotCount; i++)
                {
                    _slotRefs[i] = _java.Call<AndroidJavaObject>("getSlot", i);
                    _slotPtrs[i] = (IntPtr)AndroidJNI.GetDirectBufferAddress(_slotRefs[i].GetRawObject());
                }
            }

            public int Acquire(out IntPtr dst)
            {
                int slot = _java.Call<int>("acquireSlot");
                dst = slot >= 0 ? _slotPtrs[slot] : IntPtr.Zero;
                if (slot >= 0 && dst == IntPtr.Zero) { _java.Call("releaseSlot", slot); return -1; }
                return slot;
            }

            public bool Submit(int slot, long timestampNs)
                => _java.Call<bool>("submitSlot", slot, timestampNs / 1000L);

            public void Finish(ManualResetEventSlim done)
            {
                _doneProxy = new AndroidDoneProxy(done);
                _java.Call("finish", _doneProxy);
            }
        }

        static SlotEncoder _rgbEncoder;
        static SlotEncoder _depthVideoEncoder;

        // ── Depth LZ4 producer-consumer (bounded, pooled buffers) ──────────────

        const int DepthPoolSize = 8;

        static BlockingCollection<byte[]> _depthLz4Queue;
        static ConcurrentQueue<byte[]>    _depthLz4Free;
        static Thread                     _depthLz4Thread;
        static FileStream                 _depthBinStream;
        static FileStream                 _depthSizStream;
        static int                        _depthLz4RowBytes;
        static int                        _depthLz4FrameBytes;
        static byte[]                     _depthLz4EncodeBuf;

        // Completion events.
        static readonly ManualResetEventSlim _rgbDone   = new ManualResetEventSlim(false);
        static readonly ManualResetEventSlim _depthDone = new ManualResetEventSlim(false);

        // ── AndroidJavaProxy for done callbacks ────────────────────────────────

        sealed class AndroidDoneProxy : AndroidJavaProxy
        {
            readonly ManualResetEventSlim _done;
            public AndroidDoneProxy(ManualResetEventSlim done)
                : base("com.sensorflex.recorder.IEncoderDoneListener")
            { _done = done; }

            void onEncoderDone(bool success)
            {
                if (!success) Debug.LogWarning("[SF] Android encoder finished with errors.");
                _done.Set();
            }
        }

        // ── Public API ─────────────────────────────────────────────────────────

        public static void PrewarmEncoders() { }   // MediaCodec starts fast; no warm-up needed
        public static bool IsRgbReady        => true;
        public static bool IsDepthVideoReady => true;

        public static void LogCapabilities(SfzRgbCodec rgbCodec, SfzDepthCodec depthCodec)
        {
            try
            {
                using var cls = new AndroidJavaClass("com.sensorflex.recorder.SFVideoEncoderAndroid");
                bool hevcAvail = cls.CallStatic<bool>("isCodecAvailable", "video/hevc");
                bool h264Avail = cls.CallStatic<bool>("isCodecAvailable", "video/avc");
                Debug.Log($"[SF] LogCapabilities: requested RGB={rgbCodec} Depth={depthCodec} " +
                          $"HEVC={hevcAvail} H264={h264Avail}");
                if (rgbCodec   == SfzRgbCodec.HEVC   && !hevcAvail) Debug.LogWarning("[SF] HEVC encoder requested for RGB but not available on this device.");
                if (depthCodec == SfzDepthCodec.HEVC  && !hevcAvail) Debug.LogWarning("[SF] HEVC encoder requested for Depth but not available on this device.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SF] LogCapabilities failed: {e.Message}");
            }
        }

        // ── RGB ────────────────────────────────────────────────────────────────

        public static void StartRgbSession(string mp4Path, int width, int height, SfzRgbCodec codec)
        {
            _rgbDone.Reset();
            _depthDone.Reset();  // Reset at session start so WaitForBothFinished works even if depth never starts
            _depthMode  = DepthMode.None;
            _rgbEncoder = new SlotEncoder(mp4Path, width, height, (int)codec, isDepth: false);
        }

        // Returns a slot index and the address to write one NV12 frame into, or -1 if busy.
        public static int AcquireRgbSlot(out IntPtr dst)
        {
            dst = IntPtr.Zero;
            return _rgbEncoder != null ? _rgbEncoder.Acquire(out dst) : -1;
        }

        public static bool SubmitRgbSlot(int slot, long timestampNs)
            => _rgbEncoder != null && _rgbEncoder.Submit(slot, timestampNs);

        public static void FinishRgbSession()
        {
            var enc = _rgbEncoder;
            _rgbEncoder = null;
            if (enc == null) { _rgbDone.Set(); return; }
            enc.Finish(_rgbDone);
        }

        // ── Depth LZ4 (uint16 or float32 rows, bytesPerPixel) ──────────────────

        public static void StartDepthLz4Writer(string binPath, string sizesPath, int width, int height,
                                                int bytesPerPixel)
        {
            _depthMode          = DepthMode.Lz4;
            _depthDone.Reset();
            _depthLz4RowBytes   = width * bytesPerPixel;
            _depthLz4FrameBytes = _depthLz4RowBytes * height;
            _depthLz4EncodeBuf  = new byte[_depthLz4FrameBytes + (_depthLz4FrameBytes >> 3) + 256];

            _depthLz4Queue = new BlockingCollection<byte[]>(DepthPoolSize);
            _depthLz4Free  = new ConcurrentQueue<byte[]>();
            for (int i = 0; i < DepthPoolSize; i++)
                _depthLz4Free.Enqueue(new byte[_depthLz4FrameBytes]);

            _depthBinStream = new FileStream(binPath,   FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            _depthSizStream = new FileStream(sizesPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096);

            _depthLz4Thread = new Thread(DepthLz4Worker) { IsBackground = true, Name = "SF-DepthLZ4" };
            _depthLz4Thread.Start();
        }

        // Copies one de-strided frame into a pooled buffer and queues it. false = dropped.
        public static unsafe bool AppendDepthLz4Frame(IntPtr src, int rowStride, int width, int height)
        {
            if (_depthLz4Queue == null || _depthLz4Queue.IsAddingCompleted) return false;
            if (!_depthLz4Free.TryDequeue(out var buf)) return false;

            fixed (byte* d = buf)
            {
                byte* s = (byte*)src;
                if (rowStride == _depthLz4RowBytes)
                    UnsafeUtility.MemCpy(d, s, _depthLz4FrameBytes);
                else
                    for (int row = 0; row < height; row++)
                        UnsafeUtility.MemCpy(d + row * _depthLz4RowBytes, s + row * rowStride, _depthLz4RowBytes);
            }

            if (_depthLz4Queue.TryAdd(buf)) return true;
            _depthLz4Free.Enqueue(buf);
            return false;
        }

        // ── Depth video (NV12 slots, uint8-normalised by the caller) ───────────

        public static void StartDepthVideoSession(string mp4Path, int width, int height,
                                                   SfzDepthCodec codec, float depthMaxMeters)
        {
            _depthMode = DepthMode.Video;
            _depthDone.Reset();
            int javaCodec = codec == SfzDepthCodec.HEVC ? 1 : 0;
            _depthVideoEncoder = new SlotEncoder(mp4Path, width, height, javaCodec, isDepth: true);
        }

        public static int AcquireDepthVideoSlot(out IntPtr dst)
        {
            dst = IntPtr.Zero;
            return _depthVideoEncoder != null ? _depthVideoEncoder.Acquire(out dst) : -1;
        }

        public static bool SubmitDepthVideoSlot(int slot, long timestampNs)
            => _depthVideoEncoder != null && _depthVideoEncoder.Submit(slot, timestampNs);

        // Unified finish for whichever depth encoder is active.
        public static void FinishDepthSession()
        {
            switch (_depthMode)
            {
                case DepthMode.Lz4:
                    _depthLz4Queue?.CompleteAdding();
                    _depthLz4Thread?.Join();
                    _depthBinStream?.Close();
                    _depthSizStream?.Close();
                    _depthLz4Queue  = null;
                    _depthLz4Thread = null;
                    _depthBinStream = null;
                    _depthSizStream = null;
                    _depthDone.Set();
                    break;
                case DepthMode.Video:
                    var enc = _depthVideoEncoder;
                    _depthVideoEncoder = null;
                    if (enc != null) enc.Finish(_depthDone);
                    else             _depthDone.Set();
                    break;
                default:
                    _depthDone.Set();
                    break;
            }
            _depthMode = DepthMode.None;
        }

        public static void WaitForBothFinished(int timeoutMs)
        {
            _rgbDone.Wait(timeoutMs);
            _depthDone.Wait(timeoutMs);
        }

        // ── Background LZ4 worker thread ───────────────────────────────────────

        static void DepthLz4Worker()
        {
            var sizeBytes = new byte[4];
            foreach (var rawFrame in _depthLz4Queue.GetConsumingEnumerable())
            {
                int encoded = Lz4BlockEncoder.Encode(
                    rawFrame, 0, _depthLz4FrameBytes,
                    _depthLz4EncodeBuf, 0, _depthLz4EncodeBuf.Length);
                _depthLz4Free.Enqueue(rawFrame);

                // Always write a size entry (0 on failure) so depth_sizes.bin stays aligned
                // with frames that reported HasDepth = true.
                if (encoded > 0)
                    _depthBinStream.Write(_depthLz4EncodeBuf, 0, encoded);
                else
                    encoded = 0;
                sizeBytes[0] = (byte)(encoded         & 0xFF);
                sizeBytes[1] = (byte)((encoded >>  8) & 0xFF);
                sizeBytes[2] = (byte)((encoded >> 16) & 0xFF);
                sizeBytes[3] = (byte)((encoded >> 24) & 0xFF);
                _depthSizStream.Write(sizeBytes, 0, 4);
            }
        }

#else

        // ── Stubs for Editor / unsupported platforms ───────────────────────────

        public static void PrewarmEncoders() { }
        public static bool IsRgbReady        => true;
        public static bool IsDepthVideoReady => true;

        public static void LogCapabilities(SfzRgbCodec _, SfzDepthCodec __) { }

        public static void StartRgbSession(string _, int __, int ___, SfzRgbCodec ____) { }
        public static bool AppendRgbFrame(IntPtr _, int __, IntPtr ___, int ____,
                                           int _____, int ______, long _______) => false;
        public static void FinishRgbSession() { }

        public static void StartDepthLz4Writer(string _, string __, int ___, int ____,
                                                SfzDepthCodec _____ = SfzDepthCodec.LZ4) { }
        public static bool AppendDepthLz4Frame(IntPtr _, int __, int ___, int ____) => false;

        public static void StartDepthVideoSession(string _, int __, int ___,
                                                   SfzDepthCodec ____, float _____) { }
        public static bool AppendDepthVideoFrame(IntPtr _, int __, int ___, int ____, long _____) => false;

        public static void FinishDepthSession() { }

        public static void WaitForBothFinished(int timeoutMs) { }

#endif
    }
}
