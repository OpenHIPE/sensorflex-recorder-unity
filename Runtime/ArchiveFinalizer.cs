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
    // Packages a completed capture temp folder into a single SFZ archive (SFZ 2.0, sensor rig).
    //
    // For each sensor in meta.Sensors with a file, tempDir holds that file (color.mp4, depth.bin,
    // depth_left.bin, …); block depth sensors also have a SizesFile with one int32 per block.
    //
    // Output zip entries (all under "session/"):
    //   session.json, frames.jsonl — Deflate
    //   sensor files               — Stored (already compressed)
    //
    // The temp folder is deleted after successful packaging.
    internal static class ArchiveFinalizer
    {
        public static Task<string[]> FinalizeAsync(
            string tempDir,
            string outputDir,
            SfzSessionMetadata meta,
            List<SfzFrame> frames,
            CaptureBackend backend)
        {
            return Task.Run(() => Finalize(tempDir, outputDir, meta, frames, backend));
        }

        // ── Core (background thread) ───────────────────────────────────────

        static string[] Finalize(
            string tempDir,
            string outputDir,
            SfzSessionMetadata meta,
            List<SfzFrame> frames,
            CaptureBackend backend)
        {
            // Wait for the backend's encoders to flush (video encoders + depth queues).
            backend.WaitForFinish(timeoutMs: 60_000);

            if (!Directory.Exists(tempDir))
                throw new DirectoryNotFoundException($"[SF-Recorder] Temp dir not found: {tempDir}");

            Directory.CreateDirectory(outputDir);

            // ── Block sizes per block-depth sensor ─────────────────────────
            var blockSizes = new Dictionary<string, int[]>();
            foreach (var sensor in meta.Sensors)
                if (!string.IsNullOrEmpty(sensor.SizesFile))
                    blockSizes[sensor.Name] = ReadBlockSizes(sensor.SizesFile);

            // ── Build text files ───────────────────────────────────────────
            byte[] sessionJsonBytes = SfzSerializer.BuildSessionJson(meta);
            byte[] framesJsonlBytes = SfzSerializer.BuildFramesJsonl(frames, blockSizes);

            // ── Write SFZ archive ──────────────────────────────────────────
            string outPath = Path.Combine(outputDir, meta.SessionId + ".sfz");
            if (File.Exists(outPath)) File.Delete(outPath);

            using (var zipStream = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            using (var archive   = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                WriteBytes(archive, "session/session.json", sessionJsonBytes, SysCompressionLevel.Optimal);
                WriteBytes(archive, "session/frames.jsonl", framesJsonlBytes, SysCompressionLevel.Optimal);
                foreach (var sensor in meta.Sensors)
                    if (!string.IsNullOrEmpty(sensor.File))
                        CopyFile(archive, "session/" + sensor.File, Path.Combine(tempDir, sensor.File),
                                 SysCompressionLevel.NoCompression);
            }

            long kb = new FileInfo(outPath).Length / 1024;
            Debug.Log($"[SF-Recorder] SFZ written: {outPath} ({kb} KB) frames={frames.Count} sensors={meta.Sensors.Count}");

            TryDeleteDir(tempDir);
            return new[] { outPath };
        }

        // ── Helpers ────────────────────────────────────────────────────────

        // A sizes file is a flat array of int32 (little-endian), one per written block.
        static int[] ReadBlockSizes(string path)
        {
            if (!File.Exists(path)) return Array.Empty<int>();

            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            var sizes = new int[fs.Length / 4];
            for (int i = 0; i < sizes.Length; i++)
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
