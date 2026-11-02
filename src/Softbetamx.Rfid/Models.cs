namespace Softbetamx.Rfid;

/// <summary>Reader hardware family. Each value maps to an internal protocol driver.</summary>
public enum ReaderModel
{
    /// <summary>Focus reader: ST-M714 module (Impinj E710) speaking the EX10 "Moduletech" protocol (frames start with 0xFF, CRC-16).</summary>
    Focus,

    /// <summary>IN805 reader (Impinj E710) speaking the 0xA0 protocol ("UHF RFID Reader Serial Interface V4.2.1").</summary>
    In805,

    /// <summary>In-memory simulated reader. No hardware or transport needed; useful for demos and tests.</summary>
    Simulated,
}

/// <summary>Connection state of a <see cref="RfidReader"/>.</summary>
public enum ReaderConnectionState
{
    /// <summary>Not connected (initial state, or after <see cref="RfidReader.DisconnectAsync"/>).</summary>
    Disconnected,

    /// <summary><see cref="RfidReader.ConnectAsync"/> is in progress.</summary>
    Connecting,

    /// <summary>Connected and ready for commands.</summary>
    Connected,

    /// <summary>The link failed (I/O error, timeout or lost heartbeat). Call <see cref="RfidReader.ConnectAsync"/> again to recover.</summary>
    Faulted,
}

/// <summary>Buzzer behaviour.</summary>
public enum BuzzerMode
{
    /// <summary>Silent.</summary>
    Quiet = 0,

    /// <summary>One beep after every inventory round that found at least one tag.</summary>
    PerRound = 1,

    /// <summary>One beep per tag read. Meant for testing only: it slows the reader down.</summary>
    PerTag = 2,
}

/// <summary>Gen2 inventoried-flag target.</summary>
public enum Gen2Target
{
    /// <summary>Static target A.</summary>
    A,

    /// <summary>Static target B.</summary>
    B,

    /// <summary>Dynamic: start with A and flip to B when no more tags answer.</summary>
    AB,

    /// <summary>Dynamic: start with B and flip to A when no more tags answer.</summary>
    BA,
}

/// <summary>Configuration of one antenna port.</summary>
/// <param name="Port">Physical antenna port, starting at 1.</param>
/// <param name="PowerDbm">Transmit power in dBm (the readers have 1 dB real resolution).</param>
/// <param name="Enabled">Whether the port takes part in inventories.</param>
public sealed record AntennaConfig(int Port, double PowerDbm, bool Enabled = true);

/// <summary>Gen2 (ISO 18000-63) inventory parameters.</summary>
/// <param name="Session">Session 0..3. S0 = presence (tags answer every round); S1 = counting (each tag answers once per persistence window).</param>
/// <param name="Target">Inventoried-flag target.</param>
/// <param name="Q">Static Q value 0..15, or <c>null</c> for dynamic Q (reader adapts it).</param>
/// <param name="RfMode">Reader-specific RF mode / link profile value, or <c>null</c> to leave it unchanged. On the Focus (E7xx) it is MODE_ID + 100, e.g. 107.</param>
public sealed record Gen2Settings(int Session = 0, Gen2Target Target = Gen2Target.A, int? Q = null, int? RfMode = null);

/// <summary>One tag observation.</summary>
public sealed record TagRead
{
    /// <summary>EPC in upper-case hexadecimal, no separators.</summary>
    public required string Epc { get; init; }

    /// <summary>Antenna port (1-based) that read the tag.</summary>
    public required int Antenna { get; init; }

    /// <summary>RSSI in dBm (negative).</summary>
    public required int Rssi { get; init; }

    /// <summary>Host time when the read was received.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Gen2 protocol-control word, when the reader reports it.</summary>
    public int? Pc { get; init; }

    /// <summary>How many times the tag was read in this report (1 for a single read; aggregated in <see cref="RfidReader.InventoryOnceAsync"/>).</summary>
    public int Count { get; init; } = 1;

    /// <summary>Carrier frequency in kHz, when the reader reports it.</summary>
    public int? FrequencyKhz { get; init; }

    /// <summary>Backscatter phase in degrees (0..360), when the reader reports it.</summary>
    public double? PhaseDegrees { get; init; }
}

/// <summary>Power configured on one antenna port.</summary>
/// <param name="Port">Antenna port, starting at 1.</param>
/// <param name="ReadPowerDbm">Power used for inventory/read operations.</param>
/// <param name="WritePowerDbm">Power used for write operations (same as read on readers with a single value).</param>
public sealed record AntennaPower(int Port, double ReadPowerDbm, double WritePowerDbm);

/// <summary>Static and live information about the reader.</summary>
public sealed record ReaderInfo
{
    /// <summary>Reader model.</summary>
    public required ReaderModel Model { get; init; }

    /// <summary>Firmware version as reported by the reader.</summary>
    public required string Firmware { get; init; }

    /// <summary>Hardware description (chip, ports, certification), when available.</summary>
    public string? Hardware { get; init; }

    /// <summary>Serial number, when available.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>Module temperature in °C, when available.</summary>
    public int? TemperatureC { get; init; }

    /// <summary>Frequency region, e.g. "FCC 902-928 MHz" or "NA (0x01)".</summary>
    public required string Region { get; init; }

    /// <summary>Power the reader reports for each antenna port (one entry if the reader keeps a single global value).</summary>
    public required IReadOnlyList<AntennaPower> Powers { get; init; }

    /// <summary>Minimum supported power in dBm.</summary>
    public required double MinPowerDbm { get; init; }

    /// <summary>Maximum supported power in dBm.</summary>
    public required double MaxPowerDbm { get; init; }

    /// <summary>Number of antenna ports.</summary>
    public required int AntennaPorts { get; init; }

    /// <summary>Ports the reader detects as connected, when it can tell (Focus: closed-loop detection).</summary>
    public IReadOnlyList<int>? ConnectedAntennas { get; init; }
}

/// <summary>Return loss measured at one frequency.</summary>
/// <param name="FrequencyKhz">Frequency in kHz.</param>
/// <param name="ReturnLossDb">Return loss in dB (higher is better; ~0 means open/disconnected).</param>
public sealed record ReturnLossPoint(int FrequencyKhz, double ReturnLossDb);

/// <summary>Return loss of one antenna port.</summary>
/// <param name="Antenna">Antenna port.</param>
/// <param name="ReturnLossDb">Average return loss in dB over <paramref name="Points"/> (higher is better; above ~10 dB is a good match).</param>
/// <param name="Points">Per-frequency measurements (a single point on readers that measure one frequency).</param>
public sealed record AntennaReturnLoss(int Antenna, double ReturnLossDb, IReadOnlyList<ReturnLossPoint> Points)
{
    /// <summary>Voltage standing-wave ratio derived from <see cref="ReturnLossDb"/>.</summary>
    public double Vswr => VswrFromReturnLoss(ReturnLossDb);

    /// <summary>Converts a return loss in dB to VSWR. Returns <see cref="double.PositiveInfinity"/> for 0 dB.</summary>
    public static double VswrFromReturnLoss(double returnLossDb)
    {
        if (returnLossDb <= 0) return double.PositiveInfinity;
        double gamma = Math.Pow(10, -returnLossDb / 20);
        return (1 + gamma) / (1 - gamma);
    }
}

/// <summary>Data of the <see cref="RfidReader.Error"/> event.</summary>
public sealed class ReaderErrorEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public ReaderErrorEventArgs(string message, Exception? exception = null, bool isFatal = false)
    {
        Message = message;
        Exception = exception;
        IsFatal = isFatal;
    }

    /// <summary>Human-readable description.</summary>
    public string Message { get; }

    /// <summary>Underlying exception, if any.</summary>
    public Exception? Exception { get; }

    /// <summary><c>true</c> when the connection was lost (state becomes <see cref="ReaderConnectionState.Faulted"/>).</summary>
    public bool IsFatal { get; }
}

/// <summary>Data of the <see cref="RfidReader.StateChanged"/> event.</summary>
public sealed class ReaderStateChangedEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    public ReaderStateChangedEventArgs(ReaderConnectionState oldState, ReaderConnectionState newState, string? reason)
    {
        OldState = oldState;
        NewState = newState;
        Reason = reason;
    }

    /// <summary>Previous state.</summary>
    public ReaderConnectionState OldState { get; }

    /// <summary>New state.</summary>
    public ReaderConnectionState NewState { get; }

    /// <summary>Why the state changed (error message on <see cref="ReaderConnectionState.Faulted"/>).</summary>
    public string? Reason { get; }
}

/// <summary>Error reported by the reader or by the protocol layer.</summary>
public class RfidReaderException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RfidReaderException(string message, int? statusCode = null, Exception? inner = null) : base(message, inner)
    {
        StatusCode = statusCode;
    }

    /// <summary>Reader status/error code, when the reader returned one.</summary>
    public int? StatusCode { get; }
}
