using System;
using System.IO;
using System.Threading;

/// <summary>
/// In-memory stream that is filled by a download while it's being read.
/// Reads wait until the needed bytes have arrived, so playback can start
/// after the first few hundred KB instead of after the whole file.
/// </summary>
public sealed class ProgressiveStream : Stream
{
    private readonly object _lock = new();
    private byte[] _data;
    private long _written;
    private long _position;
    private bool _complete;
    private Exception? _error;
    private readonly long _expectedLength;

    public ProgressiveStream(long expectedLength)
    {
        _expectedLength = Math.Max(0, expectedLength);
        _data = new byte[_expectedLength > 0 && _expectedLength < int.MaxValue ? _expectedLength : 1024 * 1024];
    }

    public long BytesWritten { get { lock (_lock) return _written; } }
    public bool IsComplete { get { lock (_lock) return _complete; } }

    // ---------------- writer side ----------------

    public void Append(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_written + count > _data.Length)
            {
                var bigger = new byte[Math.Max(_data.Length * 2, _written + count)];
                Buffer.BlockCopy(_data, 0, bigger, 0, (int)_written);
                _data = bigger;
            }
            Buffer.BlockCopy(buffer, offset, _data, (int)_written, count);
            _written += count;
            Monitor.PulseAll(_lock);
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            _complete = true;
            Monitor.PulseAll(_lock);
        }
    }

    public void Fail(Exception error)
    {
        lock (_lock)
        {
            _error = error;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Waits until at least <paramref name="bytes"/> arrived (or the download ended).</summary>
    public bool WaitForBytes(long bytes, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_lock)
        {
            while (_written < bytes && !_complete && _error == null)
            {
                ct.ThrowIfCancellationRequested();
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return false;
                Monitor.Wait(_lock, left < TimeSpan.FromMilliseconds(200) ? left : TimeSpan.FromMilliseconds(200));
            }
            if (_error != null) throw new IOException("Download failed", _error);
            return true;
        }
    }

    /// <summary>Copy of everything downloaded (use after Complete).</summary>
    public byte[] ToArray()
    {
        lock (_lock)
        {
            var copy = new byte[_written];
            Buffer.BlockCopy(_data, 0, copy, 0, (int)_written);
            return copy;
        }
    }

    // ---------------- reader side ----------------

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            lock (_lock) return _complete ? _written : Math.Max(_expectedLength, _written);
        }
    }

    public override long Position
    {
        get { lock (_lock) return _position; }
        set { lock (_lock) _position = Math.Max(0, value); }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            // Wait for the full request: Windows' decoder treats a short read as "end of file".
            long need = _position + count;
            while (_written < need && !_complete && _error == null)
                Monitor.Wait(_lock, 200);

            if (_error != null) throw new IOException("Download failed", _error);

            long available = _written - _position;
            if (available <= 0) return 0;
            int n = (int)Math.Min(count, available);
            Buffer.BlockCopy(_data, (int)_position, buffer, offset, n);
            _position += n;
            return n;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_lock)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => _position
            };
            if (_position < 0) _position = 0;
            return _position;
        }
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
