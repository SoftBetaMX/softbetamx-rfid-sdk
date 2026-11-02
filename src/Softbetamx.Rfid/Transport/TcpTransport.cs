using System.Net.Sockets;

namespace Softbetamx.Rfid.Transport;

/// <summary>TCP client transport (the reader or its serial-to-Ethernet converter is the server).</summary>
public sealed class TcpTransport : IRfidTransport
{
    private readonly TimeSpan _connectTimeout;
    private TcpClient? _client;
    private NetworkStream? _stream;

    /// <summary>Creates the transport. Nothing is opened until <see cref="OpenAsync"/>.</summary>
    public TcpTransport(string host, int port, TimeSpan? connectTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Host = host;
        Port = port;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Remote host.</summary>
    public string Host { get; }

    /// <summary>Remote port.</summary>
    public int Port { get; }

    /// <inheritdoc />
    public string Description => $"tcp://{Host}:{Port}";

    /// <inheritdoc />
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await CloseAsync().ConfigureAwait(false);
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        try
        {
            await client.ConnectAsync(Host, Port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Timeout connecting to {Description}");
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
        _stream = client.GetStream();
    }

    /// <inheritdoc />
    public Task CloseAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        _stream = null;
        client?.Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        (_stream ?? throw new IOException("Transport is not open")).WriteAsync(data, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        (_stream ?? throw new IOException("Transport is not open")).ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
