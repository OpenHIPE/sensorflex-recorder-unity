using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SensorFlex.Recorder
{
    // Builds SFZ 2.0 metadata files (sensor-rig layout; com.sensorflex.player.unity/Docs/SensorFlexFormat.md).
    //
    // session.json — session metadata and the sensor list:
    //   { "sfz_version":"2.0", "session_id":"...", ..., "frame_source":"color",
    //     "sensors": {
    //       "color": {"type":"color","file":"color.mp4","encoding":"hevc_mp4","width":W,"height":H},
    //       "depth": {"type":"depth","file":"depth.bin","encoding":"lz4_uint16","width":w,"height":h,
    //                 "units":"millimeters","source":"arcore_environment_depth"} } }
    //
    // frames.jsonl — one line per frame, one block per sensor:
    //   {"frame":0,"ts":T,
    //    "color":{"ts":T,"index":0,"pos":[..],"rot":[..],"fx":..,"fy":..,"cx":..,"cy":..},
    //    "depth":{"ts":T,"index":0,"pos":[..],"rot":[..],"fx":..,"fy":..,"cx":..,"cy":..,"off":0,"sz":12345}}
    //
    // Handheld rig: depth is captured from the color camera's viewpoint at the frame's time, so
    // the depth block repeats the color pose and timestamp with intrinsics scaled to the depth
    // resolution. color.index is the frame's index in color.mp4 (-1 = dropped); depth.index
    // counts depth samples. depth_sizes.bin holds one entry per frame with HasDepth == true.
    internal static class SfzSerializer
    {
        const string ColorSensor = "color";
        const string DepthSensor = "depth";

        static bool IsVideoDepth(SfzDepthCodec codec) => codec == SfzDepthCodec.HEVC || codec == SfzDepthCodec.H264;

        // ── session.json ──────────────────────────────────────────────────────

        public static byte[] BuildSessionJson(SfzSessionMetadata meta)
        {
            var sb = new StringBuilder(768);
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
            AppendStr(sb, "frame_source", ColorSensor,       1, true);

            sb.Append("  \"sensors\": {\n");

            // The color sensor always exists: it is the frame source and carries the camera
            // pose. Without captured video it has no file (all its blocks have index -1).
            sb.Append("    \"").Append(ColorSensor).Append("\": { \"type\": \"color\"");
            if (meta.HasRgb)
            {
                string colorEncoding = meta.RgbCodec == SfzRgbCodec.H264 ? "h264_mp4" : "hevc_mp4";
                sb.Append(", \"file\": \"").Append(CaptureBackend.ColorFileName).Append('"');
                sb.Append(", \"encoding\": \"").Append(colorEncoding).Append('"');
                sb.Append(", \"width\": ").Append(meta.RgbWidth);
                sb.Append(", \"height\": ").Append(meta.RgbHeight);
            }
            sb.Append(" }");

            if (meta.HasDepth)
            {
                string depthEncoding = meta.DepthEncoding ?? "lz4_float32";
                string depthFile     = IsVideoDepth(meta.DepthCodec) ? "depth.mp4" : "depth.bin";
                sb.Append(",\n    \"").Append(DepthSensor).Append("\": { \"type\": \"depth\"");
                sb.Append(", \"file\": \"").Append(depthFile).Append('"');
                sb.Append(", \"encoding\": \"").Append(depthEncoding).Append('"');
                sb.Append(", \"width\": ").Append(meta.DepthWidth);
                sb.Append(", \"height\": ").Append(meta.DepthHeight);
                sb.Append(", \"units\": \"");
                AppendEscaped(sb, meta.DepthUnits ?? "meters");
                sb.Append("\", \"source\": \"");
                AppendEscaped(sb, meta.DepthSensor ?? "arfoundation_environment_depth");
                sb.Append('"');
                if (depthEncoding.EndsWith("_uint8_norm"))
                    sb.Append(", \"depth_max_meters\": ").Append(F(meta.DepthMaxMeters));
                sb.Append(" }");
            }

            sb.Append("\n  }\n");
            sb.Append("}\n");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // ── frames.jsonl ──────────────────────────────────────────────────────

        // depthSizes holds the compressed byte count of each depth block, one per frame with
        // HasDepth (block encodings only; null for video depth).
        public static byte[] BuildFramesJsonl(SfzSessionMetadata meta, List<SfzFrameRecord> frames, int[] depthSizes)
        {
            bool isVideoDepth = IsVideoDepth(meta.DepthCodec);
            float sx = meta.RgbWidth  > 0 ? (float)meta.DepthWidth  / meta.RgbWidth  : 0f;
            float sy = meta.RgbHeight > 0 ? (float)meta.DepthHeight / meta.RgbHeight : 0f;

            var  sb          = new StringBuilder(frames.Count * 320);
            long depthOffset = 0L;
            int  depthIndex  = 0;

            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];

                sb.Append("{\"frame\":").Append(f.FrameIndex);
                sb.Append(",\"ts\":").Append(f.TimestampNs);

                // color block — the camera pose lives here even when the image was dropped.
                sb.Append(",\"").Append(ColorSensor).Append("\":{");
                AppendSampleHeader(sb, f, meta.HasRgb ? f.ColorIndex : -1);
                if (f.HasIntrinsics)
                    AppendIntrinsics(sb, f.Fx, f.Fy, f.Cx, f.Cy);
                sb.Append('}');

                // depth block — only for frames that produced a depth sample.
                if (meta.HasDepth && f.HasDepth)
                {
                    sb.Append(",\"").Append(DepthSensor).Append("\":{");
                    AppendSampleHeader(sb, f, depthIndex);
                    if (f.HasIntrinsics && sx > 0f && sy > 0f)
                        AppendIntrinsics(sb, f.Fx * sx, f.Fy * sy, f.Cx * sx, f.Cy * sy);
                    if (!isVideoDepth)
                    {
                        int dsz = depthSizes != null && depthIndex < depthSizes.Length ? depthSizes[depthIndex] : 0;
                        sb.Append(",\"off\":").Append(depthOffset);
                        sb.Append(",\"sz\":").Append(dsz);
                        depthOffset += dsz;
                    }
                    sb.Append('}');
                    depthIndex++;
                }

                sb.Append("}\n");
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        static void AppendSampleHeader(StringBuilder sb, SfzFrameRecord f, int index)
        {
            sb.Append("\"ts\":").Append(f.TimestampNs);
            sb.Append(",\"index\":").Append(index);
            sb.Append(",\"pos\":[")
              .Append(F(f.Position.x)).Append(',')
              .Append(F(f.Position.y)).Append(',')
              .Append(F(f.Position.z))
              .Append(']');
            sb.Append(",\"rot\":[")
              .Append(F(f.Rotation.x)).Append(',')
              .Append(F(f.Rotation.y)).Append(',')
              .Append(F(f.Rotation.z)).Append(',')
              .Append(F(f.Rotation.w))
              .Append(']');
        }

        static void AppendIntrinsics(StringBuilder sb, float fx, float fy, float cx, float cy)
        {
            sb.Append(",\"fx\":").Append(F(fx));
            sb.Append(",\"fy\":").Append(F(fy));
            sb.Append(",\"cx\":").Append(F(cx));
            sb.Append(",\"cy\":").Append(F(cy));
        }

        // ── Helpers ────────────────────────────────────────────────────────────

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
