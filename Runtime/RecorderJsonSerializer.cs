using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SensorFlex.Recorder
{
    // Builds SFZ 2.0 metadata files (sensor-rig layout; com.sensorflex.player.unity/Docs/SensorFlexFormat.md).
    //
    // session.json — session metadata and the sensor list:
    //   { "sfz_version":"2.0", "session_id":"...", ..., "frame_source":"color",
    //     "sensors": { "color": {"type":"color","file":"color.mp4","encoding":"hevc_mp4","width":W,"height":H},
    //                  "depth": {"type":"depth","file":"depth.bin","encoding":"lz4_uint16",...} } }
    //
    // frames.jsonl — one line per frame, one block per sensor with a sample:
    //   {"frame":0,"ts":T,"color":{"ts":T,"index":0,"pos":[..],"rot":[..],"fx":..,"fy":..,"cx":..,"cy":..},
    //    "depth":{"ts":T,"index":0,"pos":[..],"rot":[..],"fx":..,...,"off":0,"sz":12345}}
    //
    // Block depth sensors get off/sz from their per-block sizes (one int32 per written block,
    // in index order): off = sum of the sizes of the blocks before it.
    internal static class SfzSerializer
    {
        // ── session.json ──────────────────────────────────────────────────────

        public static byte[] BuildSessionJson(SfzSessionMetadata meta)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\n");
            AppendStr(sb, "sfz_version",    "2.0",              1, true);
            AppendStr(sb, "session_id",     meta.SessionId,     1, true);
            AppendStr(sb, "start_time_utc", meta.StartTimeUtc,  1, true);

            sb.Append("  \"device\": {\n");
            AppendStr(sb, "model",        meta.DeviceModel,  2, true);
            AppendStr(sb, "os",           meta.DeviceOs,     2, true);
            AppendStr(sb, "ar_framework", meta.ArFramework,  2, false);
            sb.Append("  },\n");

            AppendInt(sb, "actual_fps",   meta.Fps,          1, true);
            AppendInt(sb, "frame_count",  meta.FrameCount,   1, true);
            AppendStr(sb, "frame_source", meta.FrameSource,  1, true);

            sb.Append("  \"sensors\": {");
            var sensors = meta.Sensors ?? new List<SfzSensorInfo>();
            for (int i = 0; i < sensors.Count; i++)
            {
                var s = sensors[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    \"");
                AppendEscaped(sb, s.Name);
                sb.Append("\": { \"type\": \"");
                AppendEscaped(sb, s.Type);
                sb.Append('"');
                if (!string.IsNullOrEmpty(s.File))
                {
                    AppendField(sb, "file",     s.File);
                    AppendField(sb, "encoding", s.Encoding);
                    sb.Append(", \"width\": ").Append(s.Width);
                    sb.Append(", \"height\": ").Append(s.Height);
                    if (s.Type == "depth")
                    {
                        AppendField(sb, "units",  s.Units ?? "meters");
                        AppendField(sb, "source", s.Source ?? "unknown");
                        if (s.DepthMaxMeters > 0f)
                            sb.Append(", \"depth_max_meters\": ").Append(F(s.DepthMaxMeters));
                    }
                }
                sb.Append(" }");
            }
            sb.Append("\n  }\n");
            sb.Append("}\n");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // ── frames.jsonl ──────────────────────────────────────────────────────

        // blockSizes: per block-depth sensor name, the compressed size of each written block.
        public static byte[] BuildFramesJsonl(List<SfzFrame> frames, Dictionary<string, int[]> blockSizes)
        {
            // Prefix sums → byte offset of each block.
            var offsets = new Dictionary<string, long[]>();
            foreach (var kv in blockSizes)
            {
                var off = new long[kv.Value.Length];
                long acc = 0;
                for (int i = 0; i < off.Length; i++) { off[i] = acc; acc += kv.Value[i]; }
                offsets[kv.Key] = off;
            }

            var sb = new StringBuilder(frames.Count * 360);
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                sb.Append("{\"frame\":").Append(i);
                sb.Append(",\"ts\":").Append(frame.TimestampNs);

                foreach (var kv in frame.Samples)
                {
                    var s = kv.Value;
                    sb.Append(",\"");
                    AppendEscaped(sb, kv.Key);
                    sb.Append("\":{\"ts\":").Append(s.TimestampNs);
                    sb.Append(",\"index\":").Append(s.Index);
                    sb.Append(",\"pos\":[")
                      .Append(F(s.Position.x)).Append(',')
                      .Append(F(s.Position.y)).Append(',')
                      .Append(F(s.Position.z))
                      .Append(']');
                    sb.Append(",\"rot\":[")
                      .Append(F(s.Rotation.x)).Append(',')
                      .Append(F(s.Rotation.y)).Append(',')
                      .Append(F(s.Rotation.z)).Append(',')
                      .Append(F(s.Rotation.w))
                      .Append(']');
                    if (s.HasIntrinsics)
                    {
                        sb.Append(",\"fx\":").Append(F(s.Fx));
                        sb.Append(",\"fy\":").Append(F(s.Fy));
                        sb.Append(",\"cx\":").Append(F(s.Cx));
                        sb.Append(",\"cy\":").Append(F(s.Cy));
                    }
                    if (blockSizes.TryGetValue(kv.Key, out var sizes))
                    {
                        bool has = s.Index >= 0 && s.Index < sizes.Length;
                        sb.Append(",\"off\":").Append(has ? offsets[kv.Key][s.Index] : 0);
                        sb.Append(",\"sz\":").Append(has ? sizes[s.Index] : 0);
                    }
                    sb.Append('}');
                }

                sb.Append("}\n");
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        static void AppendField(StringBuilder sb, string key, string value)
        {
            sb.Append(", \"").Append(key).Append("\": \"");
            AppendEscaped(sb, value ?? string.Empty);
            sb.Append('"');
        }

        static void AppendStr(StringBuilder sb, string key, string value, int indent, bool comma)
        {
            sb.Append(indent == 2 ? "    " : "  ")
              .Append('"').Append(key).Append("\": \"");
            AppendEscaped(sb, value ?? string.Empty);
            sb.Append('"');
            if (comma) sb.Append(',');
            sb.Append('\n');
        }

        static void AppendInt(StringBuilder sb, string key, int value, int indent, bool comma)
        {
            sb.Append(indent == 2 ? "    " : "  ")
              .Append('"').Append(key).Append("\": ").Append(value);
            if (comma) sb.Append(',');
            sb.Append('\n');
        }

        static void AppendFloat(StringBuilder sb, string key, float value, int indent, bool comma)
        {
            sb.Append(indent == 2 ? "    " : "  ")
              .Append('"').Append(key).Append("\": ").Append(F(value));
            if (comma) sb.Append(',');
            sb.Append('\n');
        }

        static void AppendEscaped(StringBuilder sb, string s)
        {
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:   sb.Append(c);      break;
                }
            }
        }

        // 9 significant digits preserves float32 values exactly on round-trip.
        static string F(float v) => v.ToString("G9", CultureInfo.InvariantCulture);
    }
}
