using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace WordCraftPdf.Internal;

/// <summary>
/// Keeps the last <c>capacity</c> bytes written to a worker's stderr, so error messages can
/// quote it without the host accumulating unbounded output. Thread-safe.
/// </summary>
internal sealed class StderrTail
{
    private readonly byte[] _ring;
    private int _start;   // index of the oldest byte
    private int _count;   // bytes held (≤ capacity)
    private long _total;  // bytes ever appended

    public StderrTail(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _ring = new byte[capacity];
    }

    public int Capacity => _ring.Length;

    /// <summary>Total bytes ever appended (including those already dropped).</summary>
    public long TotalBytes
    {
        get { lock (_ring) return _total; }
    }

    public void Append(byte[] data, int offset, int count)
    {
        lock (_ring)
        {
            _total += count;
            if (count >= _ring.Length)
            {
                // Only the last `capacity` bytes survive.
                Buffer.BlockCopy(data, offset + count - _ring.Length, _ring, 0, _ring.Length);
                _start = 0;
                _count = _ring.Length;
                return;
            }
            int end = (_start + _count) % _ring.Length;
            int first = Math.Min(count, _ring.Length - end);
            Buffer.BlockCopy(data, offset, _ring, end, first);
            Buffer.BlockCopy(data, offset + first, _ring, 0, count - first);
            int overflow = _count + count - _ring.Length;
            if (overflow > 0)
            {
                _start = (_start + overflow) % _ring.Length;
                _count = _ring.Length;
            }
            else
            {
                _count += count;
            }
        }
    }

    /// <summary>The retained text (UTF-8, invalid sequences replaced), trimmed.</summary>
    public override string ToString()
    {
        byte[] copy;
        bool truncated;
        lock (_ring)
        {
            copy = new byte[_count];
            int first = Math.Min(_count, _ring.Length - _start);
            Buffer.BlockCopy(_ring, _start, copy, 0, first);
            Buffer.BlockCopy(_ring, 0, copy, first, _count - first);
            truncated = _total > _count;
        }
        var text = Encoding.UTF8.GetString(copy).Trim();
        return truncated ? "…" + text : text;
    }

    /// <summary>Copy <paramref name="stream"/> into the tail until end of stream or failure.</summary>
    public async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (n <= 0)
                    break;
                Append(buffer, 0, n);
            }
        }
        catch
        {
            // The worker exited or the stream was disposed: nothing more to keep.
        }
    }
}
