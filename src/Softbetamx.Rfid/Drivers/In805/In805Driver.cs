using Microsoft.Extensions.Logging;
using Softbetamx.Rfid.Internal;

namespace Softbetamx.Rfid.Drivers.In805;

/// <summary>
/// Driver for the IN805 reader (Impinj E710, 0xA0 protocol "UHF RFID Reader Serial Interface V4.2.1").
/// Antenna/power/Gen2 settings live in the driver and are applied before every inventory round with
/// temporary commands (0x74 work antenna, 0x66 temporary power, 0x8B session/target), so they survive
/// reconnections and never touch the reader flash. Exception: the buzzer (0x7A) is stored by the reader firmware.
/// </summary>
internal sealed class In805Driver : IReaderDriver
{
    /// <summary>Inventory rounds per 0x8B command.</summary>
    internal const byte InventoryRepeat = 5;

    /// <summary>Maximum silence while waiting for 0x8B packets.</summary>
    internal static TimeSpan InventoryPacketTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private readonly ReaderOptions _options;
    private readonly IDriverHost _host;
    private FrameLink<A0Frame>? _link;
    private AntennaConfig[] _antennas = [];
    private Gen2Settings _gen2 = new();
    // Antenas ya reportadas como desconectadas: se avisa una vez, no en cada ronda.
    private readonly HashSet<int> _missingReported = [];
    private Task? _continuousLoop;
    private CancellationTokenSource? _continuousCts;

    public In805Driver(ReaderOptions options, IDriverHost host)
    {
        _options = options;
        _host = host;
        Capabilities = ReaderCapabilities.For(ReaderModel.In805);
    }

    public ReaderCapabilities Capabilities { get; }

    private ILogger Log => _host.Logger;

    private FrameLink<A0Frame> Link => _link is { IsClosed: false } l ? l : throw new IOException("Not connected");

    public async Task ConnectAsync(CancellationToken ct)
    {
        await DisconnectAsync().ConfigureAwait(false);
        var link = new FrameLink<A0Frame>(_options.CreateTransport(), new A0FrameParser(), Log, (m, e) => _host.OnFault(m, e));
        _link = link;
        await link.OpenAsync(ct).ConfigureAwait(false);

        var firmware = A0Commands.ParseFirmware((await RequestAsync(A0Commands.GetFirmware, [], ct).ConfigureAwait(false)).Data);

        // Start from what the reader reports so the first inventory does not change its power.
        if (_antennas.Length == 0)
        {
            var power = A0Commands.ParsePower((await RequestAsync(A0Commands.GetOutputPower, [], ct).ConfigureAwait(false)).Data);
            _antennas = Enumerable.Range(1, Capabilities.AntennaPorts)
                .Select(p => new AntennaConfig(p, power.Count == 1 ? power[0] : power[p - 1], true))
                .ToArray();
        }
        Log.LogInformation("Connected to IN805 reader {Endpoint}, firmware {Firmware}", link.Description, firmware);
    }

    public async Task DisconnectAsync()
    {
        _continuousCts?.Cancel();
        if (_continuousLoop is { } loop)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); } catch { }
            _continuousLoop = null;
        }
        var link = Interlocked.Exchange(ref _link, null);
        if (link is not null) await link.CloseAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());

    public async Task<ReaderInfo> GetInfoAsync(CancellationToken ct)
    {
        var firmware = A0Commands.ParseFirmware((await RequestAsync(A0Commands.GetFirmware, [], ct).ConfigureAwait(false)).Data);
        var temperature = A0Commands.ParseTemperature((await RequestAsync(A0Commands.GetTemperature, [], ct).ConfigureAwait(false)).Data);
        var region = A0Commands.ParseRegion((await RequestAsync(A0Commands.GetRegion, [], ct).ConfigureAwait(false)).Data);
        var power = A0Commands.ParsePower((await RequestAsync(A0Commands.GetOutputPower, [], ct).ConfigureAwait(false)).Data);
        var powers = power.Count == 1
            ? Enumerable.Range(1, Capabilities.AntennaPorts).Select(p => new AntennaPower(p, power[0], power[0])).ToArray()
            : power.Select((v, i) => new AntennaPower(i + 1, v, v)).ToArray();

        return new ReaderInfo
        {
            Model = ReaderModel.In805,
            Firmware = firmware,
            TemperatureC = temperature,
            Region = region,
            Powers = powers,
            MinPowerDbm = Capabilities.MinPowerDbm,
            MaxPowerDbm = Capabilities.MaxPowerDbm,
            AntennaPorts = Capabilities.AntennaPorts,
        };
    }

    public Task<IReadOnlyList<AntennaConfig>> GetAntennasAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AntennaConfig>>(_antennas.ToArray());

    public Task SetAntennasAsync(IReadOnlyList<AntennaConfig> antennas, CancellationToken ct)
    {
        var map = _antennas.ToDictionary(a => a.Port);
        foreach (var a in antennas) map[a.Port] = a with { PowerDbm = Math.Round(a.PowerDbm) };
        // Ports not mentioned are disabled (the list passed in is the full configuration).
        _antennas = map.Values
            .Select(a => antennas.Any(n => n.Port == a.Port) ? a : a with { Enabled = false })
            .OrderBy(a => a.Port)
            .ToArray();
        return Task.CompletedTask;
    }

    public Task<Gen2Settings> GetGen2Async(CancellationToken ct) => Task.FromResult(_gen2);

    public Task SetGen2Async(Gen2Settings settings, CancellationToken ct)
    {
        if (settings.Target is Gen2Target.AB or Gen2Target.BA)
            throw new NotSupportedException("IN805: dynamic Gen2 targets (AB/BA) are not supported; use A or B.");
        if (settings.Q is not null)
            throw new NotSupportedException("IN805: static Q is not supported by this driver; use Q = null (dynamic).");
        if (settings.RfMode is not null)
            throw new NotSupportedException("IN805: changing the RF mode is not supported by this driver (it is stored in flash by the reader).");
        _gen2 = settings;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<TagRead>> InventoryOnceAsync(TimeSpan duration, CancellationToken ct)
    {
        var merged = new Dictionary<(string, int), TagRead>();
        var started = DateTime.UtcNow;
        do
        {
            await InventoryRoundAsync(t =>
            {
                var key = (t.Epc, t.Antenna);
                merged[key] = merged.TryGetValue(key, out var prev)
                    ? prev with { Count = prev.Count + 1, Rssi = Math.Max(prev.Rssi, t.Rssi), Timestamp = t.Timestamp }
                    : t;
            }, ct).ConfigureAwait(false);
        }
        while (DateTime.UtcNow - started < duration);
        return merged.Values.ToArray();
    }

    public Task StartContinuousAsync(CancellationToken ct)
    {
        _ = Link;
        _continuousCts = new CancellationTokenSource();
        var token = _continuousCts.Token;
        _continuousLoop = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                    await InventoryRoundAsync(_host.OnTag, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _host.OnContinuousEnded($"Continuous inventory stopped: {ex.Message}");
            }
        });
        return Task.CompletedTask;
    }

    public async Task StopContinuousAsync(CancellationToken ct)
    {
        _continuousCts?.Cancel();
        if (_continuousLoop is { } loop)
        {
            try { await loop.WaitAsync(InventoryPacketTimeout + TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); } catch (TimeoutException) { }
            _continuousLoop = null;
        }
        _link?.Drain();
    }

    public async Task SetBuzzerAsync(BuzzerMode mode, CancellationToken ct)
    {
        await ExpectSuccessAsync(A0Commands.SetBeeperMode, [(byte)mode], ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AntennaReturnLoss>> GetReturnLossAsync(IReadOnlyList<int> antennas, CancellationToken ct)
    {
        var result = new List<AntennaReturnLoss>();
        foreach (int antenna in antennas)
        {
            await ExpectSuccessAsync(A0Commands.SetWorkAntenna, [(byte)(antenna - 1)], ct).ConfigureAwait(false);
            var frame = await RequestAsync(A0Commands.GetReturnLoss, [A0Commands.ReturnLossFreq915], ct).ConfigureAwait(false);
            if (frame.Data.Length != 1)
                throw new RfidReaderException($"Unexpected response to 0x7E: {Convert.ToHexString(frame.Data)}");
            double db = frame.Data[0];
            result.Add(new AntennaReturnLoss(antenna, db, [new ReturnLossPoint(915_000, db)]));
        }
        return result;
    }

    public async Task SetRelayAsync(int relay, bool on, CancellationToken ct)
    {
        // Relay 1 = GPIO3, relay 2 = GPIO4.
        await ExpectSuccessAsync(A0Commands.WriteGpio, [(byte)(relay + 2), (byte)(on ? 1 : 0)], ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<bool>> GetInputsAsync(CancellationToken ct)
    {
        var frame = await RequestAsync(A0Commands.ReadGpio, [], ct).ConfigureAwait(false);
        if (frame.Data.Length != 2)
            throw new RfidReaderException($"Unexpected response to 0x60: {Convert.ToHexString(frame.Data)}");
        return [frame.Data[0] != 0, frame.Data[1] != 0];
    }

    // ------------------------------------------------------------------ inventory round

    /// <summary>One 0x8B round on every enabled antenna.</summary>
    private async Task InventoryRoundAsync(Action<TagRead> onTag, CancellationToken ct)
    {
        foreach (var antenna in _antennas.Where(a => a.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            await ExpectSuccessAsync(A0Commands.SetWorkAntenna, [(byte)(antenna.Port - 1)], ct).ConfigureAwait(false);
            await ExpectSuccessAsync(A0Commands.SetTemporaryPower, [(byte)Math.Clamp((int)Math.Round(antenna.PowerDbm), 0, 33)], ct).ConfigureAwait(false);
            await SendAsync(A0Commands.CustomizedSessionTargetInventory,
                [(byte)_gen2.Session, (byte)(_gen2.Target == Gen2Target.B ? 1 : 0), InventoryRepeat], ct).ConfigureAwait(false);

            while (true)
            {
                var frame = await Link.ReceiveAsync(f => f.Command == A0Commands.CustomizedSessionTargetInventory,
                    InventoryPacketTimeout, ct).ConfigureAwait(false);
                switch (A0Commands.ParseInventoryPacket(frame))
                {
                    case InventoryTag tag:
                        onTag(new TagRead
                        {
                            Epc = tag.Epc,
                            Antenna = tag.Antenna,
                            Rssi = tag.Rssi,
                            Pc = tag.Pc,
                            Timestamp = DateTimeOffset.Now,
                        });
                        continue;
                    case InventorySummary:
                        lock (_missingReported)
                            if (_missingReported.Remove(antenna.Port))
                                Log.LogInformation("IN805: antenna {Antenna} is connected again", antenna.Port);
                        break;
                    case InventoryError { Code: A0Commands.AntennaMissing }:
                        bool first;
                        lock (_missingReported) first = _missingReported.Add(antenna.Port);
                        if (first) _host.OnError($"IN805: antenna {antenna.Port} reported missing (0x22)", null);
                        break;
                    case InventoryError error:
                        _host.OnError($"IN805: inventory on antenna {antenna.Port} failed: {A0Commands.ErrorName(error.Code)}", null);
                        break;
                    default:
                        Log.LogDebug("Ignoring unrecognized 0x8B packet {Frame}", frame);
                        continue;
                }
                break;
            }
        }
    }

    // ------------------------------------------------------------------ commands

    private async Task ExpectSuccessAsync(byte cmd, byte[] data, CancellationToken ct)
    {
        var frame = await RequestAsync(cmd, data, ct).ConfigureAwait(false);
        if (frame.Data is not [A0Commands.Success])
        {
            var detail = frame.Data.Length == 1 ? A0Commands.ErrorName(frame.Data[0]) : Convert.ToHexString(frame.Data);
            throw new RfidReaderException($"Command 0x{cmd:X2} failed: {detail}", frame.Data.Length == 1 ? frame.Data[0] : null);
        }
    }

    private async Task<A0Frame> RequestAsync(byte cmd, byte[] data, CancellationToken ct)
    {
        await SendAsync(cmd, data, ct).ConfigureAwait(false);
        return await Link.ReceiveAsync(f => f.Command == cmd, _options.CommandTimeout, ct).ConfigureAwait(false);
    }

    private async Task SendAsync(byte cmd, byte[] data, CancellationToken ct)
    {
        var link = Link;
        link.Drain();
        await link.SendAsync(A0Frame.Build(cmd, data), ct).ConfigureAwait(false);
    }
}
