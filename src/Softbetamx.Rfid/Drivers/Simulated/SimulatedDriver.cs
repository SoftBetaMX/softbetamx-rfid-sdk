using Softbetamx.Rfid.Internal;

namespace Softbetamx.Rfid.Drivers.Simulated;

/// <summary>
/// In-memory reader. Each simulated tag is "seen" by one antenna (round-robin over the tag list); a tag is only
/// reported while its antenna is enabled. RSSI drops with lower power. Continuous mode reports every visible tag
/// about every 100 ms.
/// </summary>
internal sealed class SimulatedDriver : IReaderDriver
{
    internal static readonly string[] DefaultTags =
    [
        "E2801191A5030060C1A4F3E1",
        "E28068940000502600000001",
        "E28068940000502600000002",
        "E2000017570D02072180353E",
        "300833B2DDD9014000000000",
    ];

    private readonly IDriverHost _host;
    private readonly string[] _tags;
    private readonly Random _random = new(805);
    private readonly bool[] _relays = new bool[2];
    private AntennaConfig[] _antennas;
    private Gen2Settings _gen2 = new(0, Gen2Target.A, null, 107);
    private BuzzerMode _buzzer = BuzzerMode.Quiet;
    private CancellationTokenSource? _continuousCts;
    private Task? _continuousLoop;
    private bool _connected;

    public SimulatedDriver(ReaderOptions options, IDriverHost host)
    {
        _host = host;
        _tags = (options.SimulatedTags is { Count: > 0 } t ? t : DefaultTags).Select(e => e.Replace(" ", "").ToUpperInvariant()).ToArray();
        Capabilities = ReaderCapabilities.For(ReaderModel.Simulated);
        _antennas = Enumerable.Range(1, Capabilities.AntennaPorts).Select(p => new AntennaConfig(p, 30, p == 1)).ToArray();
    }

    public ReaderCapabilities Capabilities { get; }

    public IReadOnlyList<int> MissingAntennas => [];

    public BuzzerMode Buzzer => _buzzer;

    public IReadOnlyList<bool> Relays => _relays;

    public Task ConnectAsync(CancellationToken ct)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        await StopContinuousAsync(CancellationToken.None).ConfigureAwait(false);
        _connected = false;
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());

    public Task<ReaderInfo> GetInfoAsync(CancellationToken ct) => Task.FromResult(new ReaderInfo
    {
        Model = ReaderModel.Simulated,
        Firmware = "SIM-1.0",
        Hardware = $"Simulated reader, {Capabilities.AntennaPorts} ports",
        SerialNumber = "SIM000001",
        TemperatureC = 30,
        Region = "Simulated FCC 902-928 MHz",
        Powers = _antennas.Select(a => new AntennaPower(a.Port, a.PowerDbm, a.PowerDbm)).ToArray(),
        MinPowerDbm = Capabilities.MinPowerDbm,
        MaxPowerDbm = Capabilities.MaxPowerDbm,
        AntennaPorts = Capabilities.AntennaPorts,
        ConnectedAntennas = Enumerable.Range(1, Capabilities.AntennaPorts).ToArray(),
    });

    public Task<IReadOnlyList<AntennaConfig>> GetAntennasAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AntennaConfig>>(_antennas.ToArray());

    public Task SetAntennasAsync(IReadOnlyList<AntennaConfig> antennas, CancellationToken ct)
    {
        _antennas = _antennas
            .Select(a => antennas.FirstOrDefault(n => n.Port == a.Port) ?? a with { Enabled = false })
            .ToArray();
        return Task.CompletedTask;
    }

    public Task<Gen2Settings> GetGen2Async(CancellationToken ct) => Task.FromResult(_gen2);

    public Task SetGen2Async(Gen2Settings settings, CancellationToken ct)
    {
        _gen2 = settings with { RfMode = settings.RfMode ?? _gen2.RfMode };
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<TagRead>> InventoryOnceAsync(TimeSpan duration, CancellationToken ct)
    {
        await Task.Delay(duration, ct).ConfigureAwait(false);
        int rounds = Math.Max(1, (int)(duration.TotalMilliseconds / 50));
        return Visible().Select(t => t with { Count = _gen2.Session == 0 ? rounds : 1 }).ToArray();
    }

    public Task StartContinuousAsync(CancellationToken ct)
    {
        _continuousCts = new CancellationTokenSource();
        var token = _continuousCts.Token;
        _continuousLoop = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    foreach (var tag in Visible()) _host.OnTag(tag);
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        return Task.CompletedTask;
    }

    public async Task StopContinuousAsync(CancellationToken ct)
    {
        _continuousCts?.Cancel();
        if (_continuousLoop is { } loop)
        {
            try { await loop.ConfigureAwait(false); } catch { }
            _continuousLoop = null;
        }
    }

    public Task SetBuzzerAsync(BuzzerMode mode, CancellationToken ct)
    {
        _buzzer = mode;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AntennaReturnLoss>> GetReturnLossAsync(IReadOnlyList<int> antennas, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AntennaReturnLoss>>(antennas
            .Select(a => new AntennaReturnLoss(a, 20, [new ReturnLossPoint(902_750, 19.5), new ReturnLossPoint(927_250, 20.5)]))
            .ToArray());

    public Task SetRelayAsync(int relay, bool on, CancellationToken ct)
    {
        _relays[relay - 1] = on;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<bool>> GetInputsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<bool>>([false, false]);

    private IEnumerable<TagRead> Visible()
    {
        if (!_connected) yield break;
        var now = DateTimeOffset.Now;
        for (int i = 0; i < _tags.Length; i++)
        {
            var antenna = _antennas[i % _antennas.Length];
            if (!antenna.Enabled) continue;
            int rssi;
            lock (_random) rssi = -40 - (int)(33 - antenna.PowerDbm) - _random.Next(0, 15);
            yield return new TagRead
            {
                Epc = _tags[i],
                Antenna = antenna.Port,
                Rssi = rssi,
                Timestamp = now,
                Pc = (_tags[i].Length / 4) << 11,
                FrequencyKhz = 902_750 + 500 * (i % 50),
            };
        }
    }
}
