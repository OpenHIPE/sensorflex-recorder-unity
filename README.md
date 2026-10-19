# SensorFlex Unity Recorder

`com.sensorflex.recorder.unity` records live AR Foundation sessions (color, depth, pose,
intrinsics) and packages them as SFZ v2.0 archives that the SensorFlex Player can replay.

| Platform | AR provider | Color | Depth |
|---|---|---|---|
| iOS | ARKit | H.264 / HEVC via AVFoundation | LiDAR float32 m → LZ4 / Zstd / HEVC / H.264 |
| Android | ARCore | H.264 / HEVC via MediaCodec | uint16 mm → LZ4 / H.264 / HEVC |
| Editor / other | — | not captured (pose + metadata only) | not captured |

## Architecture

See [Docs/Architecture.md](Docs/Architecture.md) for the capture pipeline diagram (ARKit and ARCore paths).

## What a recording contains

Archives follow the SFZ 2.0 spec (`com.sensorflex.player.unity/Docs/SensorFlexFormat.md`). The
recorder writes a handheld rig with two sensors:

- `color` — `color.mp4`; it is the `frame_source`. Its block in each `frames.jsonl` line holds the
  camera pose and intrinsics, and `index` = the frame in `color.mp4` (`-1` if the encoder dropped it).
- `depth` — captured from the color camera's viewpoint, so its block repeats the color pose and
  timestamp, with intrinsics scaled to the depth resolution. Omitted on frames without depth.

| Platform | `DepthCodec` | depth `encoding` | `units` | File |
|---|---|---|---|---|
| iOS | LZ4 (default) | `lz4_float32` | `meters` | `depth.bin` |
| iOS | Zstd | `zstd_float32` | `meters` | `depth.bin` |
| iOS | HEVC | `hevc_float16` | `meters` | `depth.mp4` |
| iOS / Android | H264 | `h264_uint8_norm` | `meters` (× `depth_max_meters` / 255) | `depth.mp4` |
| Android | LZ4 (default) | `lz4_uint16` | `millimeters` | `depth.bin` |
| Android | HEVC | `hevc_uint8_norm` | `meters` (× `depth_max_meters` / 255) | `depth.mp4` |

Android stores ARCore depth at native precision (uint16 mm). Zstd is not available on
Android; it records as LZ4. A value of `0` means no depth on both platforms. For `depth.bin`,
each sample is one independent compressed block at the depth block's `off` / `sz`.

## Layout

- `Runtime/` — recorder component, capture coordinator, platform backends, encoders, SFZ packaging
- `Plugins/iOS/` — `SFVideoEncoder.mm` (AVFoundation / VideoToolbox / Compression)
- `Plugins/Android/` — `SFVideoEncoderAndroid.java` (MediaCodec / MediaMuxer)
- `Editor/` — iOS post-build step (links `libcompression`)
- `Docs/` — architecture notes
