using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace CloudpunkVR;

// Film frames written as PNG on background threads while the game renders the next ones (Unity's EncodeToPNG runs on
// the main thread and allocated a texture and two arrays in the game's heap per frame). RGB, 8 bit, rows top-down,
// filter "Sub", zlib at its fastest level. Buffers come from a small pool; Enqueue blocks when every worker is busy
// and the queue is full, so the capture can never run ahead of the disk by more than a few frames.
internal sealed class FilmPng : IDisposable
{
    readonly BlockingCollection<(string path, byte[] rgb)> queue;
    readonly ConcurrentBag<byte[]> pool = new();
    readonly Thread[] workers;
    readonly int width, height;
    long encodeTicks, written, bytes;
    volatile string error;

    public FilmPng(int width, int height, int threads)
    {
        this.width = width; this.height = height;
        queue = new BlockingCollection<(string, byte[])>(Math.Max(2, threads * 2));
        workers = new Thread[Math.Max(1, threads)];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = new Thread(Run) { IsBackground = true, Name = "film png " + i, Priority = ThreadPriority.BelowNormal };
            workers[i].Start();
        }
    }

    public int Threads => workers.Length;
    public string Error => error;
    /// <summary>Mean encode+write time per frame on one worker (ms) and frames written so far.</summary>
    public (double ms, long frames, double mb) Stats()
    {
        long n = Interlocked.Read(ref written);
        return (n > 0 ? Interlocked.Read(ref encodeTicks) * 1000.0 / Stopwatch.Frequency / n : 0, n, Interlocked.Read(ref bytes) / 1e6 / Math.Max(1, n));
    }

    public byte[] Rent() => pool.TryTake(out var b) ? b : new byte[width * height * 3];

    public void Enqueue(string path, byte[] rgb) => queue.Add((path, rgb));

    /// <summary>Wait for every queued frame to be on disk.</summary>
    public void Dispose()
    {
        queue.CompleteAdding();
        foreach (var w in workers) w.Join(TimeSpan.FromMinutes(2));
        queue.Dispose();
    }

    void Run()
    {
        var row = new byte[1 + width * 3];
        var ms = new MemoryStream(width * height * 3 / 2);
        foreach (var (path, rgb) in queue.GetConsumingEnumerable())
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                ms.SetLength(0);
                using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                {
                    int stride = width * 3;
                    row[0] = 1;                                   // filter: Sub (difference to the pixel on the left)
                    for (int y = 0; y < height; y++)
                    {
                        int o = y * stride;
                        for (int i = 0; i < 3; i++) row[1 + i] = rgb[o + i];
                        for (int i = 3; i < stride; i++) row[1 + i] = (byte)(rgb[o + i] - rgb[o + i - 3]);
                        z.Write(row, 0, row.Length);
                    }
                }
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    fs.Write(Signature, 0, Signature.Length);
                    var ihdr = new byte[13];
                    BE(ihdr, 0, (uint)width); BE(ihdr, 4, (uint)height);
                    ihdr[8] = 8; ihdr[9] = 2;                     // 8 bit, truecolour; deflate, adaptive filtering, no interlace
                    Chunk(fs, "IHDR", ihdr, 0, 13);
                    Chunk(fs, "IDAT", ms.GetBuffer(), 0, (int)ms.Length);
                    Chunk(fs, "IEND", Array.Empty<byte>(), 0, 0);
                    Interlocked.Add(ref bytes, fs.Length);
                }
            }
            catch (Exception e) { error ??= path + ": " + e.Message; }
            pool.Add(rgb);
            Interlocked.Add(ref encodeTicks, Stopwatch.GetTimestamp() - t0);
            Interlocked.Increment(ref written);
        }
    }

    static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    static readonly uint[] Crc = MakeCrc();

    static uint[] MakeCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data, int off, int len)
    {
        var head = new byte[8];
        BE(head, 0, (uint)len);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head, 0, 8);
        s.Write(data, off, len);
        uint c = 0xFFFFFFFFu;
        for (int i = 4; i < 8; i++) c = Crc[(c ^ head[i]) & 0xFF] ^ (c >> 8);
        for (int i = off; i < off + len; i++) c = Crc[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        c ^= 0xFFFFFFFFu;
        var tail = new byte[4];
        BE(tail, 0, c);
        s.Write(tail, 0, 4);
    }
}
