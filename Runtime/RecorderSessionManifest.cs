using System.Collections.Generic;
using UnityEngine;

namespace SensorFlex.Recorder
{
    // Handheld-rig record (ARKit / ARCore), one per AR camera frame. HandheldRig turns these
    // into the generic SfzFrame list at StopCapture.
    internal struct SfzFrameRecord
    {
        public int        FrameIndex;
        public long       TimestampNs;
        public Vector3    Position;
        public Quaternion Rotation;
        public bool       HasIntrinsics;
        public float      Fx, Fy, Cx, Cy;
        public bool       HasColor;
        public int        ColorIndex;    // frame index in color.mp4, or -1 if the image was dropped
        public bool       HasDepth;
    }

    // One sensor of the rig, as listed in session.json "sensors".
    internal sealed class SfzSensorInfo
    {
        public string Name;              // key in session.json / frames.jsonl, e.g. "color", "depth_left"
        public string Type;              // "color" | "depth"
        public string File;              // null when the sensor captured no data
        public string Encoding;
        public int    Width, Height;
        public string Units;             // depth only
        public string Source;            // depth only
        public float  DepthMaxMeters;    // *_uint8_norm only (0 = omit)

        // Not serialized: temp-dir file with one int32 compressed size per block (block depth).
        public string SizesFile;
    }

    // One sensor's sample within a frame (a frames.jsonl block).
    internal struct SfzSample
    {
        public long       TimestampNs;
        public int        Index;         // sample index in the sensor's stream; -1 = data dropped
        public Vector3    Position;
        public Quaternion Rotation;
        public bool       HasIntrinsics;
        public float      Fx, Fy, Cx, Cy;
    }

    // One frames.jsonl line: the frame source's timestamp and a block per sensor with a sample.
    internal sealed class SfzFrame
    {
        public long TimestampNs;
        public readonly List<KeyValuePair<string, SfzSample>> Samples = new(3);

        public void Add(string sensor, SfzSample sample) => Samples.Add(new KeyValuePair<string, SfzSample>(sensor, sample));
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
        public string FrameSource;
        public List<SfzSensorInfo> Sensors;
    }
}
