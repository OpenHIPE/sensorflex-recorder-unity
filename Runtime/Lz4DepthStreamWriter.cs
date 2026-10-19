using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace SensorFlex.Recorder
{
    // One depth stream → <name>.bin (independent standard LZ4 blocks) + <name>_sizes.bin
    // (one int32 compressed size per block, 0 on failure). Frames are copied into pooled
    // buffers and compressed on a background thread; when every buffer is in flight,
    // TryAppend returns false and the caller drops the sample (bounded memory).
    internal sealed class Lz4DepthStreamWriter
    {
        readonly BlockingCollection<byte[]> _queue;
        readonly ConcurrentQueue<byte[]>    _free = new();
        readonly FileStream _bin, _sizes;
        readonly Thread     _worker;
        readonly int        _frameBytes;
        readonly byte[]     _encodeBuf;
        int _accepted;

        public int Accepted => _accepted;

        public Lz4DepthStreamWriter(string binPath, string sizesPath, int frameBytes, int poolSize = 6)
        {
            _frameBytes = frameBytes;
            _encodeBuf  = new byte[frameBytes + (frameBytes >> 3) + 256];
            _queue      = new BlockingCollection<byte[]>(poolSize);
            for (int i = 0; i < poolSize; i++) _free.Enqueue(new byte[frameBytes]);

            _bin    = new FileStream(binPath,   FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            _sizes  = new FileStream(sizesPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096);
            _worker = new Thread(Work) { IsBackground = true, Name = "SF-DepthLZ4-" + Path.GetFileNameWithoutExtension(binPath) };
            _worker.Start();
        }

        // Copies one frame (exactly frameBytes) and queues it. false = dropped (writer busy).
        public bool TryAppend(ReadOnlySpan<byte> frame)
        {
            if (_queue.IsAddingCompleted || frame.Length != _frameBytes) return false;
            if (!_free.TryDequeue(out var buf)) return false;
            frame.CopyTo(buf);
            if (_queue.TryAdd(buf)) { _accepted++; return true; }
            _free.Enqueue(buf);
            return false;
        }

        // Drains the queue and closes the files. Blocks until done.
        public void Finish()
        {
            if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
            _worker.Join();
            _bin.Close();
            _sizes.Close();
        }

        void Work()
        {
            var sizeBytes = new byte[4];
            foreach (var raw in _queue.GetConsumingEnumerable())
            {
                int encoded = Lz4BlockEncoder.Encode(raw, 0, _frameBytes, _encodeBuf, 0, _encodeBuf.Length);
                _free.Enqueue(raw);
                if (encoded > 0) _bin.Write(_encodeBuf, 0, encoded);
                else encoded = 0;
                sizeBytes[0] = (byte)encoded;
                sizeBytes[1] = (byte)(encoded >> 8);
                sizeBytes[2] = (byte)(encoded >> 16);
                sizeBytes[3] = (byte)(encoded >> 24);
                _sizes.Write(sizeBytes, 0, 4);
            }
        }
    }
}
