namespace Softbetamx.Rfid;

/// <summary>
/// What a reader model can do. Calling an operation that the model does not support throws
/// <see cref="NotSupportedException"/>. Some values (ports, power range) are refined after
/// <see cref="RfidReader.ConnectAsync"/> with what the device reports.
/// </summary>
public sealed record ReaderCapabilities
{
    /// <summary>Number of antenna ports.</summary>
    public required int AntennaPorts { get; init; }

    /// <summary>Minimum transmit power in dBm.</summary>
    public required double MinPowerDbm { get; init; }

    /// <summary>Maximum transmit power in dBm.</summary>
    public required double MaxPowerDbm { get; init; }

    /// <summary>Each antenna can have its own power.</summary>
    public bool PerAntennaPower { get; init; } = true;

    /// <summary>The reader has a controllable buzzer (<see cref="RfidReader.SetBuzzerAsync"/>).</summary>
    public bool Buzzer { get; init; }

    /// <summary>Number of relay/digital outputs controllable with <see cref="RfidReader.SetRelayAsync"/> (0 = none).</summary>
    public int Relays { get; init; }

    /// <summary>Number of digital inputs readable with <see cref="RfidReader.GetInputsAsync"/> (0 = none).</summary>
    public int Inputs { get; init; }

    /// <summary>Antenna return-loss measurement (<see cref="RfidReader.GetReturnLossAsync"/>).</summary>
    public bool ReturnLoss { get; init; }

    /// <summary>Gen2 session can be selected.</summary>
    public bool Gen2Session { get; init; } = true;

    /// <summary>Dynamic targets (<see cref="Gen2Target.AB"/>, <see cref="Gen2Target.BA"/>) are supported.</summary>
    public bool Gen2DynamicTarget { get; init; }

    /// <summary>Static Q can be set.</summary>
    public bool Gen2StaticQ { get; init; }

    /// <summary>RF mode / link profile can be set.</summary>
    public bool Gen2RfMode { get; init; }

    /// <summary>Continuous (reader-driven or driver-driven) inventory.</summary>
    public bool ContinuousInventory { get; init; } = true;

    /// <summary>Tag reads include the carrier frequency.</summary>
    public bool ReportsFrequency { get; init; }

    /// <summary>Tag reads include the phase.</summary>
    public bool ReportsPhase { get; init; }

    /// <summary>The reader reports its temperature.</summary>
    public bool Temperature { get; init; } = true;

    /// <summary>Default capabilities of a model, before connecting.</summary>
    public static ReaderCapabilities For(ReaderModel model) => model switch
    {
        ReaderModel.Focus => new ReaderCapabilities
        {
            AntennaPorts = 4,
            MinPowerDbm = 0,
            MaxPowerDbm = 33,
            Buzzer = false,
            Relays = 4,
            Inputs = 4,
            ReturnLoss = true,
            Gen2DynamicTarget = true,
            Gen2StaticQ = true,
            Gen2RfMode = true,
            ReportsFrequency = true,
            ReportsPhase = true,
        },
        ReaderModel.In805 => new ReaderCapabilities
        {
            AntennaPorts = 4,
            MinPowerDbm = 0,
            MaxPowerDbm = 33,
            Buzzer = true,
            Relays = 2,
            Inputs = 2,
            ReturnLoss = true,
            Gen2DynamicTarget = false,
            Gen2StaticQ = false,
            Gen2RfMode = false,
        },
        ReaderModel.Simulated => new ReaderCapabilities
        {
            AntennaPorts = 4,
            MinPowerDbm = 0,
            MaxPowerDbm = 33,
            Buzzer = true,
            Relays = 2,
            Inputs = 2,
            ReturnLoss = true,
            Gen2DynamicTarget = true,
            Gen2StaticQ = true,
            Gen2RfMode = true,
            ReportsFrequency = true,
            ReportsPhase = false,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown reader model"),
    };
}
