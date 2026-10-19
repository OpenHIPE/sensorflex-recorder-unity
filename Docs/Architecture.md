# SensorFlex Recorder Architecture

`com.sensorflex.recorder.unity` is the recording-side counterpart to `com.sensorflex.player.unity`.

The recorder captures AR Foundation session data in real time on iOS (ARKit) and Android (ARCore),
hands color and depth to platform encoders while recording, and packages the result into one
SFZ 2.0 archive after recording stops. The archive format is specified in
`com.sensorflex.player.unity/Docs/SensorFlexFormat.md`.

## Goals

- Record AR Foundation data in real time without stalling frame delivery.
- Use the platform's hardware video encoders (VideoToolbox / MediaCodec) for color.
- Keep depth at sensor precision by default (ARKit float32 m, ARCore uint16 mm, LZ4-compressed).
- Produce archives readable by the player and the Python tools (`data_processing/sfz_reader.py`).
- Treat archive creation as a post-recording finalization step.

## Non-Goals

- Zipping or other heavy work on the per-frame capture path.
- Per-frame files on disk.

## System Overview

```
                     ARSensorFlexRecorder (MonoBehaviour)
                                       │ standby / start / stop
┌─ CaptureCoordinator ────────────────────────────────────────────────────────┐
│ lifecycle · pose · intrinsics · duration limit · session metadata           │
│ FrameRecords: color index in color.mp4, -1 if the image was dropped         │
│ CaptureBackend per platform (Editor: NullCaptureBackend, pose only)         │
└─────────────────┬───────────────────────────────────────┬───────────────────┘
                  ▼                                       ▼
┌─ IosCaptureBackend (ARKit) ───────┐ ┌─ AndroidCaptureBackend (ARCore) ──────┐
│ color  YCbCr 420 bi-planar        │ │ color  YUV_420_888, pixelStride 1|2   │
│ depth  float32 metres             │ │ depth  uint16 millimetres             │
│ plane pointers passed directly    │ │ color → Yuv420ToNv12 ─┐ write into    │
│ (no managed copy)                 │ │ depth video → uint8 ──┘ a free slot   │
│                                   │ │   (8 direct ByteBuffers per stream;   │
│                                   │ │    none free → frame dropped)         │
│                                   │ │ depth LZ4 → pooled buffer → C# worker │
│                                   │ │   thread → Lz4BlockEncoder            │
└─────────────────┬─────────────────┘ └───────────────────┬───────────────────┘
                  ▼ P/Invoke                              ▼ JNI (slot index)
┌─ SFVideoEncoder.mm (serial GCD) ──┐ ┌─ SFVideoEncoderAndroid.java ──────────┐
│ RGB → AVAssetWriter H.264/HEVC    │ │ one per video stream (RGB, depth)     │
│ depth LZ4/Zstd →                  │ │ slot → codec input Image              │
│   compression_encode_buffer       │ │   (NV12 row copy, else per-pixel)     │
│ depth video → HEVC float16        │ │ → MediaCodec H.264/HEVC → MediaMuxer  │
│   or H.264 uint8 norm             │ │                                       │
└─────────────────┬─────────────────┘ └───────────────────┬───────────────────┘
                  └───────────────────┬───────────────────┘
                                      ▼
        TempDir: color.mp4 · depth.bin + depth_sizes.bin | depth.mp4
                                      ▼
             ArchiveFinalizer → <session_id>.sfz (SFZ v2.0 zip)
    session.json · frames.jsonl · color.mp4 · depth.bin | depth.mp4
```

## Recording Flow

```
Standby (after Start / previous recording)
  CaptureCoordinator.BeginStandby()      subscribe to frames, raise depth mode
  backend.TryDetectDims() each frame     ARCore: switch to ≤1080p camera config first
  backend.StartEngines()                 open encoders with the detected sizes

StartRecording
  CaptureCoordinator.Begin()             reset counters, subscribe OnCameraFrameReceived

Each AR frame (main thread)
  ├─ backend.EncodeColor(relativeNs)     → true if handed to the encoder
  ├─ backend.EncodeDepth(relativeNs)     → true if queued
  ├─ pose (Camera.transform), intrinsics (native › FOV estimate, cached)
  └─ FrameRecords.Add(... ColorIndex = next index or -1 ...)

StopRecording (or max duration)
  ├─ backend.Finish()                    seal encoders (async)
  └─ ArchiveFinalizer.FinalizeAsync      background Task
        ├─ NativeVideoEncoder.WaitForBothFinished(60 s)
        ├─ session.json (sensors: color, depth) + frames.jsonl (per-sensor blocks;
        │    depth_sizes.bin → depth off / sz)
        ├─ <session_id>.sfz: json DEFLATE, color.mp4 / depth.* STORED
        └─ delete temp folder
```

## Runtime Modules

| Module | Role |
|---|---|
| `ARSensorFlexRecorder` | Scene-facing MonoBehaviour on `XROrigin`: settings, standby state machine, start/stop, events. |
| `CaptureCoordinator` | Per-session lifecycle, pose, intrinsics, `FrameRecords` (incl. color index), duration limit, session metadata. No platform code. |
| `CaptureBackend` | Platform half of capture: `IosCaptureBackend`, `AndroidCaptureBackend`, `NullCaptureBackend` (Editor). Owns XRCpuImage layouts and encoder calls; reports `depth.encoding` / `units`. |
| `NativeVideoEncoder` | Static bridge: P/Invoke into `SFVideoEncoder.mm` (iOS), JNI slot rings + C# LZ4 worker (Android), stubs elsewhere. |
| `SFVideoEncoder.mm` | iOS: AVAssetWriter H.264/HEVC for color; `compression_encode_buffer` LZ4/Zstd or VideoToolbox video for depth. |
| `SFVideoEncoderAndroid.java` | Android: MediaCodec + MediaMuxer fed from direct `ByteBuffer` slots; writes into the codec's input `Image` per its strides. |
| `Lz4BlockEncoder` | C# LZ4 raw-block encoder (standard block format) used for Android depth. |
| `ArchiveFinalizer` / `SfzSerializer` | Build `session.json` / `frames.jsonl` and the `.sfz` zip. |

## Threading Model

| Thread | Work |
|---|---|
| Main thread | AR Foundation callbacks, plane access, color conversion (Android), record accumulation |
| iOS serial GCD queues | Color/depth video encoding, depth LZ4/Zstd compression + file writes |
| Android `HandlerThread` per stream | Slot → codec input copy, MediaCodec drain, MediaMuxer writes |
| Android `SF-DepthLZ4` thread | Depth LZ4 compression + `depth.bin` writes (bounded queue, pooled buffers) |
| Task thread | `ArchiveFinalizer` — wait for encoders, JSON, zip |

## Coordinate System

Unity world space (left-handed, +Y up, +Z forward, metres). Pose `pos` / `rot` are read directly
from `Camera.transform`.

## Temp Folder

`Application.temporaryCachePath/SF-Recorder/{sessionId}/` holds `color.mp4`, `depth.bin` +
`depth_sizes.bin` (or `depth.mp4`) while recording; it is deleted after finalization.

## Scene Setup

```
XROrigin  ← ARSensorFlexRecorder
  └─ Camera
       ├─ ARCameraManager
       └─ AROcclusionManager  (optional, for depth)
```

`Assets/RecordingSample.unity` is a ready-made scene for both ARKit and ARCore builds.

## Future Work

- IMU capture into `imu.jsonl` (the format already defines it; the dataset converters write it)
- Scanned mesh export via `ARMeshManager` as a `scene_mesh` attachment
- Partial session recovery on next app launch
