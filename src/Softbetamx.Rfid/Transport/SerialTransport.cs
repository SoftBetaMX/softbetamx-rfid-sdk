using System.IO.Ports;

namespace Softbetamx.Rfid.Transport;

/// <summary>Serial-port transport (8 data bits, no parity, 1 stop bit) based on <see cref="System.IO.Ports.SerialPort"/>.</summary>
public sealed class SerialTransport : IRfidTransport
{
    private SerialPort? _port;
    private Stream? _stream;

    /// <summary>Creates the transport. Nothing is opened until <see cref="OpenAsync"/>.</summary>
    public SerialTransport(string portName, int baudRate = 115200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        if (baudRate <= 0) throw new ArgumentOutOfRangeException(nameof(baudRate));
        PortName = portName;
        BaudRate = baudRate;
    }

    /// <summary>Port name, e.g. "COM3".</summary>
    public string PortName { get; }

    /// <summary>Baud rate.</summary>
    public int BaudRate { get; }

    /// <inheritdoc />
    public string Description => $"serial://{PortName}@{BaudRate}";

    /// <inheritdoc />
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await CloseAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var port = new SerialPort(PortName, BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000,
        };
        port.Open();
        port.DiscardInBuffer();
        _port = port;
        _stream = port.BaseStream;
    }

    /// <inheritdoc />
    public Task CloseAsync()
    {
        var port = Interlocked.Exchange(ref _port, null);
        _stream = null;
        if (port is not null)
        {
            try { port.Close(); } catch (IOException) { }
            port.Dispose();
        }
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
