using Microsoft.Extensions.Logging;
using Softbetamx.Rfid.Transport;

namespace Softbetamx.Rfid;

/// <summary>How to reach a reader. Set either <see cref="Host"/> (TCP), <see cref="SerialPort"/> or <see cref="Transport"/>.</summary>
public sealed class ReaderOptions
{
    /// <summary>Reader model (selects the protocol driver).</summary>
    public ReaderModel Model { get; set; }

    /// <summary>Host name or IP address for TCP.</summary>
    public string? Host { get; set; }

    /// <summary>TCP port. Default: 8080 for <see cref="ReaderModel.Focus"/>, 4001 for <see cref="ReaderModel.In805"/>.</summary>
    public int? Port { get; set; }

    /// <summary>Serial port name, e.g. "COM3" or "/dev/ttyUSB0".</summary>
    public string? SerialPort { get; set; }

    /// <summary>Serial baud rate (8N1). Default 115200.</summary>
    public int BaudRate { get; set; } = 115200;

    /// <summary>Custom transport (advanced; e.g. a fake for tests). Takes precedence over <see cref="Host"/> and <see cref="SerialPort"/>.</summary>
    public IRfidTransport? Transport { get; set; }

    /// <summary>Maximum time to open the link. Default 5 s.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum time to wait for the answer to a regular command. Default 3.5 s.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMilliseconds(3500);

    /// <summary>Optional logger. Raw frames are logged at <see cref="LogLevel.Trace"/>.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>EPCs the <see cref="ReaderModel.Simulated"/> reader reports (hex). Default: five sample EPCs.</summary>
    public IReadOnlyList<string>? SimulatedTags { get; set; }

    internal int DefaultPort => Model switch
    {
        ReaderModel.Focus => 8080,
        ReaderModel.In805 => 4001,
        _ => 0,
    };

    internal IRfidTransport CreateTransport()
    {
        if (Transport is not null) return Transport;
        if (!string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(SerialPort))
            throw new ArgumentException("Set either Host (TCP) or SerialPort, not both.");
        if (!string.IsNullOrWhiteSpace(Host))
            return new TcpTransport(Host, Port ?? DefaultPort, ConnectTimeout);
        if (!string.IsNullOrWhiteSpace(SerialPort))
            return new SerialTransport(SerialPort, BaudRate);
        throw new ArgumentException($"ReaderOptions for {Model} need Host, SerialPort or Transport.");
    }
}
