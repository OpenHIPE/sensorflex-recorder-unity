using UnityEngine;

namespace SensorFlex.Recorder
{
    // Per-frame record kept in memory during recording.
    // Serialized into frames.jsonl at finalization time.
    internal struct SfzFrameRecord
    {
        public int        FrameIndex;
        public long       TimestampNs;
        public Vector3    Position;
        public Quaternion Rotation;
        public bool       HasIntrinsics;
        public float      Fx, Fy, Cx, Cy;
        public bool       HasColor;
        public bool       HasDepth;
    }

    // Session-level metadata collected at StopCapture time.
    internal struct SfzSessionMetadata
    {
        public string SessionId;
        public string StartTimeUtc;   // ISO-8601, e.g. "2026-05-30T10:00:00.000Z"
        public string DeviceModel;
        public string DeviceOs;
        public string ArFramework;
        public int    Fps;
        public int    FrameCount;
        // Dimensions populated from the first successful frame.
        public bool   HasRgb;
        public int    RgbWidth, RgbHeight;
        public bool   HasDepth;
        public int    DepthWidth, DepthHeight;
        public string      DepthSensor;    // e.g. "arkit_lidar", "arcore_environment_depth"
        public string      DepthEncoding;  // e.g. "lz4_float32", "lz4_uint16" — from CaptureBackend
        public string      DepthUnits;     // "meters" or "millimeters"
        public SfzRgbCodec   RgbCodec;
        public SfzDepthCodec DepthCodec;
        public float         DepthMaxMeters;
    }
}
