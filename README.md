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
