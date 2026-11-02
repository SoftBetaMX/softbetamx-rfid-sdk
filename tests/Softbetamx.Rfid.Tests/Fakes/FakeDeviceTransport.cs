using System.Collections.Concurrent;
using System.Threading.Channels;
using Softbetamx.Rfid.Transport;

namespace Softbetamx.Rfid.Tests.Fakes;

/// <summary>In-memory transport whose remote end is a simulated device (no sockets).</summary>
internal abstract class FakeDeviceTransport : IRfidTransport
{
    private Channel<byte[]> _toHost = Channel.CreateUnbounded<byte[]>();
    private byte[] _pending = [];
    private int _pendingOffset;

    public string Description => $"fake://{GetType().Name}";

    /// <summary>Every write from the host, in hex.</summary>
    public ConcurrentQueue<string> Received { get; } = new();

    public int Opens;

    public bool IsOpen { get; private set; }

    /// <summary>Commands (first bytes in hex) the device ignores, to test timeouts.</summary>
    public HashSet<string> SilentPrefixes { get; } = [];

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        _toHost = Channel.CreateUnbounded<byte[]>();
        _pending = [];
        IsOpen = true;
        Interlocked.Increment(ref Opens);
        OnOpen();
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        IsOpen = false;
        OnClose();
        _toHost.Writer.TryComplete();
        return Task.CompletedTask;
    }

    /// <summary>Simulates the device dropping the connection.</summary>
    public void Drop() => _toHost.Writer.TryComplete();

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!IsOpen) throw new IOException("closed");
        var bytes = data.ToArray();
        var hex = Convert.ToHexString(bytes);
        Received.Enqueue(hex);
        if (SilentPrefixes.Any(hex.StartsWith)) return ValueTask.CompletedTask;
        OnHostBytes(bytes);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_pendingOffset >= _pending.Length)
        {
            try
            {
                _pending = await _toHost.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
            _pendingOffset = 0;
        }
        int n = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsMemory(_pendingOffset, n).CopyTo(buffer);
        _pendingOffset += n;
        return n;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    protected void Push(byte[] bytes) => _toHost.Writer.TryWrite(bytes);

    protected virtual void OnOpen() { }

    protected virtual void OnClose() { }

    protected abstract void OnHostBytes(byte[] bytes);
}
