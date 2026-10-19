using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SensorFlex.Recorder
{
    // Builds SFZ v2.0 metadata files.
    //
    // session.json — compact, metadata-only (no frame data array):
    //   { "sfz_version":"2.0", "session_id":"...", "start_time_utc":"...", "device":{...},
    //     "actual_fps":57, "frame_count":998,
    //     "rgb":{"width":W,"height":H,"encoding":"hevc_mp4","file":"rgb.mp4"},
    //     "depth":{"width":W,"height":H,"encoding":"lz4_float32","file":"depth.bin",
    //              "units":"meters","sensor":"arkit_lidar"} }
    //   (Android LZ4 depth: "encoding":"lz4_uint16", "units":"millimeters")
    //
    // frames.jsonl — one compact JSON line per frame:
    //   {"frame":0,"ts":1234567890,"pos":[x,y,z],"rot":[x,y,z,w],
    //    "fx":1500,"fy":1500,"cx":960,"cy":720,"depth_off":0,"depth_sz":12345,"rgb_frame":0}
    //
    // rgb_frame is the record's frame index in rgb.mp4, or -1 if its color frame was
    // dropped. Readers must map video frames to records through it (not assume equality).
    //
    // depth_off / depth_sz are byte offsets/sizes into depth.bin.
    // If a frame has no depth, depth_sz == 0 and depth_off points past the last byte.
    // depth_sizes.bin holds one entry per frame with HasDepth == true (in order), so
    // frames without depth do not consume an entry.
    internal static class SfzSerializer
    {
        // ── session.json ──────────────────────────────────────────────────────

        public static byte[] BuildSessionJson(SfzSessionMetadata meta)
        {
            var sb = new StringBuilder(512);
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

            if (meta.HasRgb)
            {
                string rgbEncoding = meta.RgbCodec == SfzRgbCodec.H264 ? "h264_mp4" : "hevc_mp4";
                sb.Append("  \"rgb\": {\n");
                AppendInt(sb, "width",    meta.RgbWidth,  2, true);
                AppendInt(sb, "height",   meta.RgbHeight, 2, true);
                AppendStr(sb, "encoding", rgbEncoding,    2, true);
                AppendStr(sb, "file",     "rgb.mp4",      2, false);
                sb.Append("  }");
                if (meta.HasDepth) sb.Append(',');
                sb.Append('\n');
            }

            if (meta.HasDepth)
            {
                string depthEncoding = meta.DepthEncoding ?? "lz4_float32";
                bool   isVideoDepth  = meta.DepthCodec == SfzDepthCodec.HEVC || meta.DepthCodec == SfzDepthCodec.H264;
                string depthFile     = isVideoDepth ? "depth.mp4" : "depth.bin";
                bool   emitDepthMax  = depthEncoding.EndsWith("_uint8_norm");

                sb.Append("  \"depth\": {\n");
                AppendInt(sb, "width",    meta.DepthWidth,  2, true);
                AppendInt(sb, "height",   meta.DepthHeight, 2, true);
                AppendStr(sb, "encoding", depthEncoding,    2, true);
                AppendStr(sb, "file",     depthFile,        2, true);
                if (emitDepthMax)
                    AppendFloat(sb, "depth_max_meters", meta.DepthMaxMeters, 2, true);
                AppendStr(sb, "units",  meta.DepthUnits ?? "meters", 2, true);
                AppendStr(sb, "sensor",
                    meta.DepthSensor ?? "arcore_environment_depth", 2, false);
                sb.Append("  }\n");
            }

            sb.Append("}\n");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // ── frames.jsonl ──────────────────────────────────────────────────────

        // depthSizes holds the compressed byte count of each depth block, one per frame
        // with HasDepth (LZ4/Zstd only). For video depth codecs (HEVC/H264), depthSizes
        // is null and a running depth_frame counter is emitted instead of depth_off/depth_sz.
        public static byte[] BuildFramesJsonl(List<SfzFrameRecord> frames, int[] depthSizes,
                                               SfzDepthCodec depthCodec)
        {
            bool isVideoDepth = depthCodec == SfzDepthCodec.HEVC || depthCodec == SfzDepthCodec.H264;
            var sb = new StringBuilder(frames.Count * 140);
            long depthOffset  = 0L;
            int  depthFrameN  = 0;
            int  depthBlockN  = 0;

            for (int i = 0; i < frames.Count; i++)
            {
                var f   = frames[i];
                int dsz = 0;
                if (!isVideoDepth && f.HasDepth && depthSizes != null && depthBlockN < depthSizes.Length)
                    dsz = depthSizes[depthBlockN++];

                sb.Append("{\"frame\":").Append(f.FrameIndex);
                sb.Append(",\"ts\":").Append(f.TimestampNs);

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

                if (f.HasIntrinsics)
                {
                    sb.Append(",\"fx\":").Append(F(f.Fx));
                    sb.Append(",\"fy\":").Append(F(f.Fy));
                    sb.Append(",\"cx\":").Append(F(f.Cx));
                    sb.Append(",\"cy\":").Append(F(f.Cy));
                }

                sb.Append(",\"rgb_frame\":").Append(f.RgbFrame);

                if (isVideoDepth)
                {
                    sb.Append(",\"depth_frame\":").Append(depthFrameN);
                    if (f.HasDepth) depthFrameN++;
                }
                else
                {
                    sb.Append(",\"depth_off\":").Append(depthOffset);
                    sb.Append(",\"depth_sz\":").Append(dsz);
                    depthOffset += dsz;
                }

                sb.Append("}\n");
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
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
