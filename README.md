# SensorFlex Unity Recorder

`com.sensorflex.recorder.unity` records live AR Foundation sessions (color, depth, pose,
intrinsics) and packages them as SFZ v2.0 archives that the SensorFlex Player can replay.

| Platform | AR provider | Color | Depth |
|---|---|---|---|
| iOS | ARKit | H.264 / HEVC via AVFoundation | LiDAR float32 m → LZ4 / Zstd / HEVC / H.264 |
| Android | ARCore | H.264 / HEVC via MediaCodec | uint16 mm → LZ4 / H.264 / HEVC |
| Editor / other | — | not captured (pose + metadata only) | not captured |

## Architecture

```
                               ARSensorFlexRecorder (MonoBehaviour)
                                            │ standby / start / stop
                                            ▼
┌───────────────────────────────── CaptureCoordinator ──────────────────────────────────┐
│ lifecycle · pose · intrinsics · FrameRecords · duration limit · session metadata      │
│                                            │                                          │
│                           CaptureBackend (chosen at construction)                     │
│             ┌──────────────────────────────┼──────────────────────────────┐           │
│             ▼                              ▼                              ▼           │
│     IosCaptureBackend             AndroidCaptureBackend          NullCaptureBackend   │
│                                                                  (Editor: warns)      │
└─────────────┼──────────────────────────────┼──────────────────────────────────────────┘
              │                              │
              ▼                              ▼
┌───────────────────────────────┐  ┌──────────────────────────────────────────────┐
│ ARKit                         │  │ ARCore                                       │
│ color: YCbCr 420 bi-planar    │  │ color: YUV_420_888 (Y, U, V planes,          │
│        (Y + interleaved CbCr) │  │        pixelStride 1 or 2)                   │
│ depth: DepthFloat32, metres   │  │ depth: DepthUint16, millimetres              │
└───────┬───────────────┬───────┘  └───────┬──────────────────────────┬───────────┘
        │ color         │ depth            │ color                    │ depth
        │ (plane ptrs,  │ (plane ptr,      ▼                          │ (plane ptr,
        │  zero-copy)   │  zero-copy)  ┌────────────────────────┐     │  zero-copy)
        │               │              │ Yuv888ToNv12 (unsafe)  │     │
        │               │              │ stride-aware → NV12    │     │
        │               │              └───────────┬────────────┘     │
        │               │                          │ write into free  │
        │               │                          ▼ slot             │
        │               │              ┌────────────────────────┐     │
        │               │              │ Direct ByteBuffer ring │     │
        │               │              │ [0][1] … [7]  8 slots  │     │
        │               │              │ full → drop + warn     │     │
        │               │              └───────────┬────────────┘     │
        │               │                          │ appendFrame      │
        │               │                          │ (slot, ts)       │
        ▼ P/Invoke      ▼ P/Invoke                 ▼ JNI (ints only)  ▼
┌─────────────────────────────────────┐  ┌──────────────────────────────────────────┐
│ SFVideoEncoder.mm (ObjC)            │  │ SFVideoEncoderAndroid.java  │ C# (Mono)  │
│                                     │  │                             │            │
│ RGB:  CVPixelBuffer (420f)          │  │ RGB: HandlerThread          │ Depth LZ4: │
│       → AVAssetWriter H.264/HEVC    │  │  getInputImage() → write    │  worker    │
│       (serial GCD queue)            │  │  planes per device layout   │  thread +  │
│                                     │  │  → MediaCodec H.264/HEVC    │  bounded   │
│ Depth LZ4/Zstd:                     │  │  → MediaMuxer               │  queue     │
│       compression_encode_buffer     │  │  slot released to ring      │  → Lz4Block│
│       (serial GCD queue)            │  │                             │  Encoder   │
│ Depth video:                        │  │ Depth video:                │            │
│   HEVC → vImage float16             │  │   uint16 mm → uint8 norm    │            │
│          (OneComponent16Half)       │  │   → MediaCodec H.264/HEVC   │            │
│   H264 → uint8 norm (420f)          │  │                             │            │
└──────────────────┬──────────────────┘  └──────────────────┬──────────────────────┘
                   ▼                                         ▼
┌──────────────────────────────────────────────────────────────────────────────────┐
│ TempDir:  rgb.mp4 · depth.bin + depth_sizes.bin  (LZ4/Zstd)  |  depth.mp4 (video)│
└────────────────────────────────────────┬─────────────────────────────────────────┘
                                         ▼
                   ArchiveFinalizer → <session_id>.sfz  (SFZ v2.0 zip)
                     session/session.json · frames.jsonl · rgb.mp4 · depth.*
```

## Depth encodings in `session.json`

`depth.encoding` says how to decode the bytes; `depth.units` says what a decoded value means.

| Platform | `DepthCodec` | `encoding` | `units` | File |
|---|---|---|---|---|
| iOS | LZ4 (default) | `lz4_float32` | `meters` | `depth.bin` |
| iOS | Zstd | `zstd_float32` | `meters` | `depth.bin` |
| iOS | HEVC | `hevc_float16` | `meters` | `depth.mp4` |
| iOS / Android | H264 | `h264_uint8_norm` | `meters` (× `depth_max_meters` / 255) | `depth.mp4` |
| Android | LZ4 (default) | `lz4_uint16` | `millimeters` | `depth.bin` |
| Android | HEVC | `hevc_uint8_norm` | `meters` (× `depth_max_meters` / 255) | `depth.mp4` |

Android stores ARCore depth at native precision (uint16 mm). Zstd is not available on
Android; it records as LZ4. A value of `0` means no depth on both platforms.

For `depth.bin`, each frame is one independent compressed block; `frames.jsonl` gives its
`depth_off` / `depth_sz` byte range. Video depth uses a running `depth_frame` index instead.

## Frame alignment

Each `frames.jsonl` line carries `rgb_frame`: that record's frame index in `rgb.mp4`, or `-1`
if its color frame was dropped (encoder busy). Readers must map video frames to records
through `rgb_frame` rather than assuming video frame N is record N. Archives written before
this field existed have no `rgb_frame`; treat them as 1:1.

## Layout

- `Runtime/` — recorder component, capture coordinator, platform backends, encoders, SFZ packaging
- `Plugins/iOS/` — `SFVideoEncoder.mm` (AVFoundation / VideoToolbox / Compression)
- `Plugins/Android/` — `SFVideoEncoderAndroid.java` (MediaCodec / MediaMuxer)
- `Editor/` — iOS post-build step (links `libcompression`)
- `Docs/` — architecture notes
