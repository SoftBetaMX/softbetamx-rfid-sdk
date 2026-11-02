using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Softbetamx.Rfid.Transport;

namespace Softbetamx.Rfid.Internal;

/// <summary>Turns a byte stream into protocol frames. Used only by the receive loop (not thread-safe).</summary>
internal interface IFrameParser<TFrame>
{
    List<TFrame> Append(ReadOnlySpan<byte> chunk);
}

/// <summary>
/// One open connection: a transport, a background receive loop that parses frames into a channel,
/// and helpers to send a frame and wait for a matching answer. Callers must serialize commands.
/// </summary>
internal sealed class FrameLink<TFrame> : IAsyncDisposable where TFrame : class
{
    private readonly IRfidTransport _transport;
    private readonly IFrameParser<TFrame> _parser;
    private readonly ILogger _logger;
    private readonly Action<string, Exception?> _onFault;
    private readonly Channel<TFrame> _frames = Channel.CreateUnbounded<TFrame>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private int _closed;

    public FrameLink(IRfidTransport transport, IFrameParser<TFrame> parser, ILogger logger, Action<string, Exception?> onFault)
    {
        _transport = transport;
        _parser = parser;
        _logger = logger;
        _onFault = onFault;
    }

    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    public string Description => _transport.Description;

    public async Task OpenAsync(CancellationToken ct)
    {
        await _transport.OpenAsync(ct).ConfigureAwait(false);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        if (IsClosed) throw new IOException("Connection is closed");
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("TX {Frame}", Convert.ToHexString(bytes));
        try
        {
            await _transport.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = $"Send error on {Description}: {ex.Message}";
            Fault(message, ex);
            throw new IOException(message, ex);
        }
    }

    /// <summary>Waits for the next frame that satisfies <paramref name="match"/>, skipping others.</summary>
    /// <param name="faultOnTimeout">Close the link on timeout (an unanswered command usually means a dead link).</param>
    public async Task<TFrame> ReceiveAsync(Func<TFrame, bool> match, TimeSpan timeout, CancellationToken ct, bool faultOnTimeout = true)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var frame = await _frames.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
                if (match(frame)) return frame;
                _logger.LogDebug("Skipping unexpected frame {Frame}", frame);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var message = $"Timeout ({timeout.TotalMilliseconds:0} ms) waiting for the reader on {Description}";
            if (faultOnTimeout) Fault(message, null);
            throw new TimeoutException(message);
        }
        catch (ChannelClosedException ex)
        {
            throw new IOException("Connection is closed", ex.InnerException ?? ex);
        }
    }

    /// <summary>Discards frames already received (e.g. late answers to a cancelled command).</summary>
    public int Drain()
    {
        int n = 0;
        while (_frames.Reader.TryRead(out var stale))
        {
            n++;
            _logger.LogDebug("Discarding stale frame {Frame}", stale);
        }
        return n;
    }

    /// <summary>Discards frames until the link is quiet for <paramref name="quiet"/> or <paramref name="max"/> elapses.</summary>
    public async Task DrainUntilQuietAsync(TimeSpan quiet, TimeSpan max, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + max;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await ReceiveAsync(_ => true, quiet, ct, faultOnTimeout: false).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return;
            }
        }
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { await _transport.CloseAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Error closing transport"); }
        _frames.Writer.TryComplete();
        if (_receiveLoop is { } loop)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* already reported */ }
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[4096];
        string? error = null;
        Exception? exception = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _transport.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    error = $"Connection closed by the reader ({Description})";
                    break;
                }
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("RX {Bytes}", Convert.ToHexString(buffer, 0, n));
                foreach (var frame in _parser.Append(buffer.AsSpan(0, n)))
                    _frames.Writer.TryWrite(frame);
            }
        }
        catch (Exception ex) when (!IsClosed)
        {
            error = $"Receive error on {Description}: {ex.Message}";
            exception = ex;
        }
        catch
        {
            // Intentional close.
        }

        if (error is not null) Fault(error, exception);
    }

    private void Fault(string message, Exception? ex)
    {
        if (IsClosed) return;
        _logger.LogWarning(ex, "Link fault: {Message}", message);
        _ = CloseAsync();
        try { _onFault(message, ex); } catch (Exception cbEx) { _logger.LogWarning(cbEx, "Fault callback threw"); }
    }
}
