using System.Text;
using Microsoft.Extensions.Logging;
using Softbetamx.Rfid.Internal;
using Softbetamx.Rfid.Transport;
using static Softbetamx.Rfid.Drivers.Focus.Ex10Protocol;

namespace Softbetamx.Rfid.Drivers.Focus;

/// <summary>
/// Driver for the Focus reader (ST-M714 / Impinj E710 module, EX10 "Moduletech" protocol) plus the ARM
/// communication board that answers ASCII <c>IOGET</c>/<c>IOSET</c> on the same socket.
/// Every setting is applied to RAM only; nothing is saved to the module flash.
/// </summary>
internal sealed class FocusDriver : IReaderDriver
{
    /// <summary>Async inventory sends a heartbeat every ~15 s; no frame for this long means the link is dead.</summary>
    internal static TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(40);

    private static readonly TimeSpan ReturnLossTimeout = TimeSpan.FromSeconds(35);

    private readonly ReaderOptions _options;
    private readonly IDriverHost _host;
    private ILogger Log => _host.Logger;
    private FrameLink<Ex10Frame>? _link;
    private Task? _continuousLoop;
    private CancellationTokenSource? _continuousCts;
    private volatile bool _stopRequested;
    private byte _regionCode = 0x01;

    public FocusDriver(ReaderOptions options, IDriverHost host)
    {
        _options = options;
        _host = host;
        Capabilities = ReaderCapabilities.For(ReaderModel.Focus);
    }

    public ReaderCapabilities Capabilities { get; private set; }

    private FrameLink<Ex10Frame> Link => _link is { IsClosed: false } l ? l : throw new IOException("Not connected");

    // ------------------------------------------------------------------ connection

    public async Task ConnectAsync(CancellationToken ct)
    {
        await DisconnectAsync().ConfigureAwait(false);
        IRfidTransport transport = _options.CreateTransport();
        var link = new FrameLink<Ex10Frame>(transport, new Ex10FrameParser(), Log, (m, e) => _host.OnFault(m, e));
        _link = link;
        await link.OpenAsync(ct).ConfigureAwait(false);

        // 1) The module may still be running an asynchronous inventory from a previous session: stop it and drain.
        await link.SendAsync(AsyncStopCommand(), ct).ConfigureAwait(false);
        try
        {
            await link.ReceiveAsync(f => f.IsExtendedReply(AsyncStop), TimeSpan.FromMilliseconds(1500), ct, faultOnTimeout: false).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.LogDebug("No answer to the initial async stop; continuing");
        }
        await link.DrainUntilQuietAsync(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

        // 2) Version (proves it is an EX10 module), 3) boot to the application, 4) Gen2 protocol.
        var version = ParseVersion(await RequestDataAsync(Build(GetVersion), ct).ConfigureAwait(false));
        var boot = await RequestAsync(Build(BootFirmware), ct, extraTimeout: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (boot.Status != StatusOk) Log.LogDebug("Boot firmware answered 0x{Status:X4} ({Name})", boot.Status, StatusName(boot.Status));
        await RequestDataAsync(Build(SetProtocol, [0x00, ProtocolGen2]), ct).ConfigureAwait(false);

        var power = ParsePowerInfo(await RequestDataAsync(Build(GetPowerInfo, [0x01]), ct).ConfigureAwait(false));
        ParseRegion(await RequestDataAsync(Build(GetRegion), ct).ConfigureAwait(false), out _regionCode);
        Capabilities = Capabilities with
        {
            AntennaPorts = version.AntennaPorts,
            MinPowerDbm = power.Min,
            MaxPowerDbm = power.Max > 0 ? power.Max : Capabilities.MaxPowerDbm,
        };
        Log.LogInformation("Connected to Focus reader {Endpoint}: {Hardware}, firmware {Firmware}",
            link.Description, version.HardwareDescription, version.Firmware);
    }

    public async Task DisconnectAsync()
    {
        _continuousCts?.Cancel();
        var link = Interlocked.Exchange(ref _link, null);
        if (link is not null) await link.CloseAsync().ConfigureAwait(false);
        if (_continuousLoop is { } loop)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            _continuousLoop = null;
        }
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());

    // ------------------------------------------------------------------ info / settings

    public async Task<ReaderInfo> GetInfoAsync(CancellationToken ct)
    {
        var version = ParseVersion(await RequestDataAsync(Build(GetVersion), ct).ConfigureAwait(false));
        string serial = ParseSerialNumber(await RequestDataAsync(Build(GetSerialNumber), ct).ConfigureAwait(false));
        int temperature = ParseTemperature(await RequestDataAsync(Build(GetTemperature), ct).ConfigureAwait(false));
        string region = ParseRegion(await RequestDataAsync(Build(GetRegion), ct).ConfigureAwait(false), out _regionCode);
        var powers = ParseAntennaPowers(await RequestDataAsync(Build(GetAntennaConfig, [0x03]), ct).ConfigureAwait(false));
        var range = ParsePowerInfo(await RequestDataAsync(Build(GetPowerInfo, [0x01]), ct).ConfigureAwait(false));
        var connected = ParseConnectedAntennas(await RequestDataAsync(Build(GetAntennaConfig, [0x05]), ct).ConfigureAwait(false));

        return new ReaderInfo
        {
            Model = ReaderModel.Focus,
            Firmware = version.Firmware,
            Hardware = version.HardwareDescription,
            SerialNumber = serial,
            TemperatureC = temperature,
            Region = region,
            Powers = powers,
            MinPowerDbm = range.Min,
            MaxPowerDbm = range.Max,
            AntennaPorts = version.AntennaPorts,
            ConnectedAntennas = connected,
        };
    }

    public async Task<IReadOnlyList<AntennaConfig>> GetAntennasAsync(CancellationToken ct)
    {
        var enabled = ParseInventoryAntennas(await RequestDataAsync(Build(GetAntennaConfig, [0x02]), ct).ConfigureAwait(false));
        var powers = ParseAntennaPowers(await RequestDataAsync(Build(GetAntennaConfig, [0x03]), ct).ConfigureAwait(false));
        return powers.Select(p => new AntennaConfig(p.Port, p.ReadPowerDbm, enabled.Contains(p.Port))).ToArray();
    }

    public async Task SetAntennasAsync(IReadOnlyList<AntennaConfig> antennas, CancellationToken ct)
    {
        // Power first (0x91 opt 3, RAM only), then the inventory antenna list (0x91 opt 2).
        await RequestDataAsync(SetAntennaPowerCommand(antennas), ct).ConfigureAwait(false);
        await RequestDataAsync(SetInventoryAntennasCommand(antennas.Where(a => a.Enabled).Select(a => a.Port)), ct).ConfigureAwait(false);
    }

    public async Task<Gen2Settings> GetGen2Async(CancellationToken ct)
    {
        async Task<byte[]> Get(byte parameter) =>
            ParseProtocolConfig(await RequestDataAsync(GetProtocolConfigCommand(parameter), ct).ConfigureAwait(false), parameter);

        int session = (await Get(Gen2ParamSession).ConfigureAwait(false))[0];
        var target = ParseTarget(await Get(Gen2ParamTarget).ConfigureAwait(false));
        int rfMode = (await Get(Gen2ParamRfMode).ConfigureAwait(false))[0];
        var q = ParseQ(await Get(Gen2ParamQ).ConfigureAwait(false));
        return new Gen2Settings(session, target, q, rfMode);
    }

    public async Task SetGen2Async(Gen2Settings settings, CancellationToken ct)
    {
        await RequestDataAsync(SetSessionCommand(settings.Session), ct).ConfigureAwait(false);
        if (settings.RfMode is { } mode) await RequestDataAsync(SetRfModeCommand(mode), ct).ConfigureAwait(false);
        await RequestDataAsync(SetQCommand(settings.Q), ct).ConfigureAwait(false);
        await RequestDataAsync(SetTargetCommand(settings.Target), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ inventory

    public async Task<IReadOnlyList<TagRead>> InventoryOnceAsync(TimeSpan duration, CancellationToken ct)
    {
        await RequestDataAsync(Build(ClearTagBuffer), ct).ConfigureAwait(false);
        var answer = await RequestAsync(SyncInventoryCommand(duration), ct, extraTimeout: duration).ConfigureAwait(false);
        if (answer.Status == StatusNoTagFound) return [];
        EnsureOk(answer, SyncInventory);
        int found = ParseTagsFound(answer.Data);

        var merged = new Dictionary<(string, int), TagRead>();
        int received = 0;
        while (received < found)
        {
            var buffer = await RequestAsync(GetTagBufferCommand(), ct).ConfigureAwait(false);
            if (buffer.Status == StatusNoTagFound) break;
            EnsureOk(buffer, GetTagBuffer);
            var tags = ParseTagBuffer(buffer.Data, DateTimeOffset.Now);
            if (tags.Count == 0) break;
            received += tags.Count;
            foreach (var t in tags)
            {
                var key = (t.Epc, t.Antenna);
                merged[key] = merged.TryGetValue(key, out var prev)
                    ? prev with { Count = prev.Count + t.Count, Rssi = Math.Max(prev.Rssi, t.Rssi) }
                    : t;
            }
        }
        return merged.Values.ToArray();
    }

    public async Task StartContinuousAsync(CancellationToken ct)
    {
        var link = Link;
        link.Drain();
        await link.SendAsync(AsyncStartCommand(), ct).ConfigureAwait(false);
        var ack = await link.ReceiveAsync(f => f.IsExtendedReply(AsyncStart) || f.IsExtendedReply(AsyncStop), _options.CommandTimeout, ct).ConfigureAwait(false);
        if (ack.Status != StatusOk)
            throw new RfidReaderException($"Reader refused continuous inventory: 0x{ack.Status:X4} {StatusName(ack.Status)}", ack.Status);

        _stopRequested = false;
        _continuousCts = new CancellationTokenSource();
        var token = _continuousCts.Token;
        _continuousLoop = Task.Run(() => ContinuousLoopAsync(link, token));
    }

    public async Task StopContinuousAsync(CancellationToken ct)
    {
        var loop = _continuousLoop;
        _stopRequested = true;
        var link = _link;
        if (link is null || link.IsClosed) return;

        await link.SendAsync(AsyncStopCommand(), ct).ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.LogWarning("Reader did not confirm the async stop within 3 s; draining");
                _continuousCts?.Cancel();
                try { await loop.ConfigureAwait(false); } catch { }
            }
        }
        _continuousLoop = null;
        await link.DrainUntilQuietAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
    }

    private async Task ContinuousLoopAsync(FrameLink<Ex10Frame> link, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Ex10Frame frame;
                try
                {
                    frame = await link.ReceiveAsync(_ => true, HeartbeatTimeout, ct, faultOnTimeout: true).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    // FrameLink already closed the link and reported the fault.
                    Log.LogWarning(ex, "No heartbeat during continuous inventory");
                    return;
                }

                if (frame.Command == ExtendedCommand && frame.Status != StatusOk && !frame.IsExtendedReply(AsyncStop))
                {
                    string reason = $"Reader stopped continuous inventory: 0x{frame.Status:X4} {StatusName(frame.Status)}";
                    _host.OnContinuousEnded(reason);
                    return;
                }

                TagRead? tag;
                AsyncPacketKind kind;
                try
                {
                    kind = ClassifyAsync(frame, DateTimeOffset.Now, out tag);
                }
                catch (RfidReaderException ex)
                {
                    _host.OnError(ex.Message, ex);
                    continue;
                }

                switch (kind)
                {
                    case AsyncPacketKind.Tag:
                        _host.OnTag(tag!);
                        break;
                    case AsyncPacketKind.StopReply:
                        if (!_stopRequested || frame.Status != StatusOk)
                            _host.OnContinuousEnded($"Reader stopped continuous inventory: 0x{frame.Status:X4} {StatusName(frame.Status)}");
                        return;
                    case AsyncPacketKind.Heartbeat:
                    case AsyncPacketKind.RoundEnd:
                    case AsyncPacketKind.StartAck:
                        break;
                    default:
                        Log.LogDebug("Ignoring frame during continuous inventory: {Frame}", frame);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            Log.LogDebug(ex, "Continuous loop ended: link closed");
        }
    }

    // ------------------------------------------------------------------ peripherals / diagnostics

    public Task SetBuzzerAsync(BuzzerMode mode, CancellationToken ct) =>
        throw new NotSupportedException("The Focus reader has no buzzer.");

    public async Task<IReadOnlyList<AntennaReturnLoss>> GetReturnLossAsync(IReadOnlyList<int> antennas, CancellationToken ct)
    {
        var result = new List<AntennaReturnLoss>();
        foreach (int antenna in antennas)
        {
            var answer = await RequestAsync(ReturnLossCommand(antenna, _regionCode), ct, extraTimeout: ReturnLossTimeout).ConfigureAwait(false);
            EnsureOk(answer, ExtendedCommand);
            result.Add(ParseReturnLoss(answer.Data, antenna));
        }
        return result;
    }

    public async Task SetRelayAsync(int relay, bool on, CancellationToken ct)
    {
        var link = Link;
        link.Drain();
        byte[] command = [.. Encoding.ASCII.GetBytes("IOSET"), 0x01, (byte)relay, (byte)(on ? 1 : 0)];
        await link.SendAsync(command, ct).ConfigureAwait(false);
        await link.ReceiveAsync(f => f.Ascii == "IOSETOK", _options.CommandTimeout, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<bool>> GetInputsAsync(CancellationToken ct)
    {
        var link = Link;
        link.Drain();
        await link.SendAsync(Encoding.ASCII.GetBytes("IOGET"), ct).ConfigureAwait(false);
        var reply = await link.ReceiveAsync(f => f.Ascii == "IOGET", _options.CommandTimeout, ct).ConfigureAwait(false);
        byte mask = reply.Data[0];
        return Enumerable.Range(0, Capabilities.Inputs).Select(i => (mask & (1 << i)) != 0).ToArray();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Ex10Frame> RequestAsync(byte[] command, CancellationToken ct, TimeSpan extraTimeout = default)
    {
        var link = Link;
        link.Drain();
        await link.SendAsync(command, ct).ConfigureAwait(false);
        byte cmd = command[2];
        ushort? sub = cmd == ExtendedCommand ? (ushort)((command[3 + Marker.Length] << 8) | command[4 + Marker.Length]) : null;
        return await link.ReceiveAsync(
            f => !f.IsAscii && f.Command == cmd && (sub is null || f.IsExtendedReply(sub.Value) || f.Status != StatusOk),
            _options.CommandTimeout + extraTimeout, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> RequestDataAsync(byte[] command, CancellationToken ct)
    {
        var frame = await RequestAsync(command, ct).ConfigureAwait(false);
        EnsureOk(frame, command[2]);
        return frame.Data;
    }

    private static void EnsureOk(Ex10Frame frame, byte command)
    {
        if (frame.Status != StatusOk)
            throw new RfidReaderException($"Command 0x{command:X2} failed: 0x{frame.Status:X4} {StatusName(frame.Status)}", frame.Status);
    }
}
