using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using SysCompressionLevel = System.IO.Compression.CompressionLevel;
using ZipArchive          = System.IO.Compression.ZipArchive;
using ZipArchiveMode      = System.IO.Compression.ZipArchiveMode;

namespace SensorFlex.Recorder
{
    // Packages a completed capture temp folder into a single SFZ archive (SFZ v2.0).
    //
    // Expected files in tempDir:
    //   color.mp4        — H.264/HEVC video (iOS SFRgbEncoder / Android MediaCodec)
    //   depth.bin        — compressed depth blocks (iOS float32 m, Android uint16 mm)
    //   depth_sizes.bin  — int32 compressed byte count per frame with depth
    //   depth.mp4        — video depth (HEVC/H264 codecs) instead of depth.bin
    //
    // Output zip entries (all under "session/"):
    //   session/session.json   — metadata (Deflate)
    //   session/frames.jsonl   — per-frame camera+depth records (Deflate)
    //   session/color.mp4      — video (Stored, already compressed)
    //   session/depth.bin      — depth blocks (Stored, already compressed)
    //
    // The temp folder is deleted after successful packaging.
    internal static class ArchiveFinalizer
    {
        public static Task<string[]> FinalizeAsync(
            string tempDir,
            string outputDir,
            SfzSessionMetadata meta,
            List<SfzFrameRecord> frameRecords)
        {
            return Task.Run(() => Finalize(tempDir, outputDir, meta, frameRecords));
        }

        // ── Core (background thread) ───────────────────────────────────────

        static string[] Finalize(
            string tempDir,
            string outputDir,
            SfzSessionMetadata meta,
            List<SfzFrameRecord> frameRecords)
        {
            // Wait for native encoders to flush (RGB HEVC + depth LZ4 queue drain).
            NativeVideoEncoder.WaitForBothFinished(timeoutMs: 60_000);

            if (!Directory.Exists(tempDir))
                throw new DirectoryNotFoundException($"[SF-Recorder] Temp dir not found: {tempDir}");

            Directory.CreateDirectory(outputDir);

            bool isVideoDepth = meta.DepthCodec == SfzDepthCodec.HEVC || meta.DepthCodec == SfzDepthCodec.H264;

            // ── Read depth compressed sizes (LZ4 only) ─────────────────────
            int[] depthSizes = null;
            if (!isVideoDepth)
            {
                string depthSizesPath = Path.Combine(tempDir, "depth_sizes.bin");
                depthSizes = ReadDepthSizes(depthSizesPath, frameRecords.Count);
            }

            // ── Build text files ───────────────────────────────────────────
            byte[] sessionJsonBytes = SfzSerializer.BuildSessionJson(meta);
            byte[] framesJsonlBytes = SfzSerializer.BuildFramesJsonl(meta, frameRecords, depthSizes);

            // ── Write SFZ archive ──────────────────────────────────────────
            string outPath = Path.Combine(outputDir, meta.SessionId + ".sfz");
            if (File.Exists(outPath)) File.Delete(outPath);

            using (var zipStream = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            using (var archive   = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                WriteBytes(archive, "session/session.json", sessionJsonBytes, SysCompressionLevel.Optimal);
                WriteBytes(archive, "session/frames.jsonl", framesJsonlBytes, SysCompressionLevel.Optimal);
                CopyFile(archive, "session/" + CaptureBackend.ColorFileName,
                         Path.Combine(tempDir, CaptureBackend.ColorFileName), SysCompressionLevel.NoCompression);
                if (isVideoDepth)
                    CopyFile(archive, "session/depth.mp4", Path.Combine(tempDir, "depth.mp4"), SysCompressionLevel.NoCompression);
                else
                    CopyFile(archive, "session/depth.bin", Path.Combine(tempDir, "depth.bin"), SysCompressionLevel.NoCompression);
            }

            long kb = new FileInfo(outPath).Length / 1024;
            Debug.Log($"[SF-Recorder] SFZ written: {outPath} ({kb} KB) frames={frameRecords.Count}");

            TryDeleteDir(tempDir);
            return new[] { outPath };
        }

        // ── Helpers ────────────────────────────────────────────────────────

        // depth_sizes.bin is a flat array of int32 (little-endian), one per frame that
        // reported HasDepth. If the file doesn't exist (depth not recorded), returns empty.
        static int[] ReadDepthSizes(string path, int frameCount)
        {
            if (!File.Exists(path)) return Array.Empty<int>();

            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            int count = (int)Math.Min(frameCount, fs.Length / 4);
            var sizes = new int[count];
            for (int i = 0; i < count; i++)
                sizes[i] = br.ReadInt32();

            return sizes;
        }

        static void WriteBytes(ZipArchive archive, string entryName, byte[] bytes,
                               SysCompressionLevel compression)
        {
            var entry = archive.CreateEntry(entryName, compression);
            using var es = entry.Open();
            es.Write(bytes, 0, bytes.Length);
        }

        static void CopyFile(ZipArchive archive, string entryName, string srcPath,
                              SysCompressionLevel compression)
        {
            if (!File.Exists(srcPath))
            {
                Debug.LogWarning($"[SF-Recorder] File not found, skipping zip entry: {srcPath}");
                return;
            }
            var entry = archive.CreateEntry(entryName, compression);
            using var es  = entry.Open();
            using var src = File.OpenRead(srcPath);
            src.CopyTo(es);
        }

        static void TryDeleteDir(string path)
        {
            try   { Directory.Delete(path, recursive: true); }
            catch (Exception e)
            {
                Debug.LogWarning($"[SF-Recorder] Could not delete temp dir '{path}': {e.Message}");
            }
        }
    }
}
