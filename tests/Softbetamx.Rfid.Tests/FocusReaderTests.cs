using System.Collections.Concurrent;
using Softbetamx.Rfid.Drivers.Focus;
using Softbetamx.Rfid.Tests.Fakes;
using static Softbetamx.Rfid.Tests.Focus.FocusCaptures;

namespace Softbetamx.Rfid.Tests;

public class FocusReaderTests
{
    private static string H(string hex) => Hex.Format(Hex.Parse(hex));

    private static (RfidReader Reader, FakeFocusDevice Device) Create()
    {
        var device = new FakeFocusDevice();
        var reader = RfidReader.Create(new ReaderOptions
        {
            Model = ReaderModel.Focus,
            Transport = device,
            CommandTimeout = TimeSpan.FromMilliseconds(800),
        });
        return (reader, device);
    }

    [Fact]
    public async Task Connect_runs_the_init_sequence_and_refines_capabilities()
    {
        var (reader, device) = Create();
        await using var _ = reader;
        var states = new ConcurrentQueue<ReaderConnectionState>();
        reader.StateChanged += (_, e) => states.Enqueue(e.NewState);

        await reader.ConnectAsync();

        Assert.Equal(ReaderConnectionState.Connected, reader.State);
        Assert.Equal([ReaderConnectionState.Connecting, ReaderConnectionState.Connected], states);
        var tx = device.Received.ToArray();
        Assert.Equal(H(TxStop), tx[0]);              // stop a leftover async inventory first
        Assert.Equal(H(TxVersion), tx[1]);
        Assert.Equal(H("FF 00 04 1D 0B"), tx[2]);    // boot to application
        Assert.Equal(H("FF 02 93 00 05 51 7D"), tx[3]); // Gen2
        Assert.Equal(4, reader.Capabilities.AntennaPorts);
        Assert.Equal(33.0, reader.Capabilities.MaxPowerDbm);
        Assert.False(reader.Capabilities.Buzzer);
    }

    [Fact]
    public async Task Tag_buffer_is_made_unique_by_antenna_and_restored_on_disconnect()
    {
        var (reader, device) = Create();
        await using var _ = reader;
        await reader.ConnectAsync();
        Assert.Equal(0x00, device.AntennaUnique);
        Assert.Contains(H("FF 03 9A 01 00 00"), device.Received.Select(r => r[..^4]));

        await reader.DisconnectAsync();
        Assert.Equal(0x01, device.AntennaUnique);
    }

    [Fact]
    public async Task Info_reports_module_values()
    {
        var (reader, _) = Create();
        await using var _r = reader;
        await reader.ConnectAsync();

        var info = await reader.GetInfoAsync();

        Assert.Equal(ReaderModel.Focus, info.Model);
        Assert.Equal("25.07.14.00", info.Firmware);
        Assert.Contains("E710", info.Hardware);
        Assert.Equal(26, info.TemperatureC);
        Assert.Equal("NA (0x01)", info.Region);
        Assert.Equal(4, info.Powers.Count);
        Assert.All(info.Powers, p => Assert.Equal(33.0, p.ReadPowerDbm));
        Assert.Equal([1, 2], info.ConnectedAntennas);
        Assert.Equal((0.0, 33.0), (info.MinPowerDbm, info.MaxPowerDbm));
    }

    [Fact]
    public async Task Set_antennas_sends_temporary_power_then_inventory_list()
    {
        var (reader, device) = Create();
        await using var _r = reader;
        await reader.ConnectAsync();

        await reader.SetAntennasAsync([new(1, PowerDbm: 30), new(2, PowerDbm: 27)]);

        Assert.Equal([1, 2], device.InventoryAntennas);
        Assert.Equal(3000, device.PowerCentiDbm[1]);
        Assert.Equal(2700, device.PowerCentiDbm[2]);
        Assert.Equal(3300, device.PowerCentiDbm[3]);
        var cfg = await reader.GetAntennasAsync();
        Assert.Equal([true, true, false, false], cfg.Select(c => c.Enabled));
        Assert.Equal([30.0, 27.0, 33.0, 33.0], cfg.Select(c => c.PowerDbm));
        // 0x91 option 4 (with settling) and AA40 (save to flash) are never used.
        Assert.DoesNotContain(device.Received, f => f.StartsWith("FF") && f.Length > 8 && f.Substring(4, 4) == "9104");
        Assert.DoesNotContain(device.Received, f => f.Contains("4D6F64756C6574656368AA40"));
    }

    [Theory]
    [InlineData(0, 30.0)]
    [InlineData(5, 30.0)]
    [InlineData(1, 34.0)]
    [InlineData(1, -1.0)]
    public async Task Set_antennas_validates_ports_and_power(int port, double power)
    {
        var (reader, _) = Create();
        await using var _r = reader;
        await reader.ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => reader.SetAntennasAsync([new(port, power)]));
    }

    [Fact]
    public async Task Gen2_round_trip()
    {
        var (reader, device) = Create();
        await using var _r = reader;
        await reader.ConnectAsync();

        Assert.Equal(new Gen2Settings(0, Gen2Target.A, null, 107), await reader.GetGen2Async());
        await reader.SetGen2Async(new Gen2Settings(1, Gen2Target.AB, 4, 113));
        Assert.Equal(new Gen2Settings(1, Gen2Target.AB, 4, 113), await reader.GetGen2Async());
        Assert.Contains(H(ManualSetSession1), device.Received);
        Assert.Contains(H("FF 04 9B 05 01 00 00 A3 FD"), device.Received);
    }

    [Fact]
    public async Task Inventory_once_reads_the_tag_buffer()
    {
        var (reader, device) = Create();
        await using var _r = reader;
        await reader.ConnectAsync();

        var tags = await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(500));

        Assert.Equal(4, tags.Count);
        Assert.Contains(tags, t => t.Epc == "E2000017570D02072180353E" && t.Count == 9 && t.Antenna == 2);
        Assert.Contains(H(TxClearBuffer), device.Received);
        Assert.Contains(H(TxSyncInventory500), device.Received);
        Assert.Contains(H(TxTagBuffer3F), device.Received);
    }

    [Fact]
    public async Task Inventory_once_with_no_tags_returns_empty()
    {
        var (reader, device) = Create();
        await using var _r = reader;
        device.NoTags = true;
        await reader.ConnectAsync();
        Assert.Empty(await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Continuous_raises_tag_events_and_stops_cleanly()
    {
        var (reader, device) = Create();
        await using var _r = reader;
        await reader.ConnectAsync();
        var reads = new ConcurrentQueue<TagRead>();
        reader.TagRead += (_, t) => reads.Enqueue(t);

        await reader.StartContinuousAsync();
        Assert.True(reader.IsContinuousRunning);
        await Task.Delay(200);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetInfoAsync());
        await reader.StopAsync();

        Assert.False(reader.IsContinuousRunning);
        Assert.False(device.AsyncRunning);
        Assert.True(reads.Count >= 4);
        Assert.Contains(reads, t => t.Epc == "E2806894000040262FE8A9D3" && t.Antenna == 1);
        Assert.Contains(reads, t => t.Epc == "303402891057605B0292A1BF" && t.Antenna == 2);
        Assert.DoesNotContain(reads, t => t.Epc.Length < 8); // round-end notifications are not tags
        Assert.Contains(device.Received, r => r.StartsWith(H("FF 13 AA 4D 6F 64 75 6C 65 74 65 63 68 AA 48 00 3F 00 80 03")));

        // Commands work again after stop.
        Assert.Equal(26, (await reader.GetInfoAsync()).TemperatureC);
    }

    [Fact]
    public async Task Continuous_detects_lost_heartbeat()
    {
        var old = FocusDriver.HeartbeatTimeout;
        FocusDriver.HeartbeatTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var device = new SilentAsyncFocusDevice();
            await using var reader = RfidReader.Create(new ReaderOptions { Model = ReaderModel.Focus, Transport = device });
            var fatal = new TaskCompletionSource<ReaderErrorEventArgs>();
            reader.Error += (_, e) => { if (e.IsFatal) fatal.TrySetResult(e); };
            await reader.ConnectAsync();
            await reader.StartContinuousAsync();

            var error = await fatal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("Timeout", error.Message);
            Assert.Equal(ReaderConnectionState.Faulted, reader.State);
            Assert.False(reader.IsContinuousRunning);
        }
        finally
        {
            FocusDriver.HeartbeatTimeout = old;
        }
    }

    [Fact]
    public async Task Return_loss_parses_the_sweep_per_antenna()
    {
        var (reader, device) = Create();
        await using var _r = reader;
        await reader.ConnectAsync();

        var rl = await reader.GetReturnLossAsync([1, 2]);

        Assert.Equal([1, 2], rl.Select(r => r.Antenna));
        Assert.All(rl, r => Assert.Equal(50, r.Points.Count));
        Assert.Contains(H(TxReturnLossAnt1), device.Received);
    }

    [Fact]
    public async Task Buzzer_is_not_supported()
    {
        var (reader, _) = Create();
        await using var _r = reader;
        await reader.ConnectAsync();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => reader.SetBuzzerAsync(BuzzerMode.PerTag));
        Assert.Contains("Focus", ex.Message);
        Assert.Contains("buzzer", ex.Message);
    }

    [Fact]
    public async Task Inputs_and_relays_use_the_ascii_board_commands()
    {
        var (reader, device) = Create();
        await using var _r = reader;
        await reader.ConnectAsync();

        Assert.Equal([true, true, true, true], await reader.GetInputsAsync());
        await reader.SetRelayAsync(2, true);
        Assert.Equal("010201", device.LastIoSet);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.SetRelayAsync(5, true));
    }

    [Fact]
    public async Task Device_drop_faults_and_reconnect_recovers()
    {
        var (reader, device) = Create();
        await using var _r = reader;
        var fault = new TaskCompletionSource();
        reader.StateChanged += (_, e) => { if (e.NewState == ReaderConnectionState.Faulted) fault.TrySetResult(); };
        await reader.ConnectAsync();

        device.Drop();
        await fault.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetInfoAsync());

        await reader.ConnectAsync();
        Assert.Equal(ReaderConnectionState.Connected, reader.State);
        Assert.Equal(2, device.Opens);
    }

    [Fact]
    public async Task Command_timeout_faults()
    {
        var (reader, device) = Create();
        await using var _r = reader;
        await reader.ConnectAsync();
        device.SilentPrefixes.Add("FF0072");

        await Assert.ThrowsAsync<TimeoutException>(() => reader.GetInfoAsync());
        Assert.Equal(ReaderConnectionState.Faulted, reader.State);
    }

    [Fact]
    public async Task Concurrent_calls_are_serialized()
    {
        var (reader, _) = Create();
        await using var _r = reader;
        await reader.ConnectAsync();

        var tasks = Enumerable.Range(0, 5).Select(_ => reader.GetInfoAsync()).ToArray();
        var inv = reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(100));
        await Task.WhenAll(tasks);
        Assert.Equal(4, (await inv).Count);
        Assert.All(tasks, t => Assert.Equal(26, t.Result.TemperatureC));
    }

    /// <summary>Acknowledges the async start but never sends anything afterwards.</summary>
    private sealed class SilentAsyncFocusDevice : FakeDeviceTransport
    {
        protected override void OnHostBytes(byte[] bytes)
        {
            if (bytes.Length > 16 && bytes[2] == 0xAA && bytes[14] == 0x48)
            {
                Push(Hex.Parse(RxAsyncAck));
                return;
            }
            // Delegate everything else to the regular fake.
            var reply = Respond(bytes);
            if (reply is not null) Push(reply);
        }

        private static byte[]? Respond(byte[] bytes) => bytes[2] switch
        {
            0x03 => Hex.Parse(RxVersion),
            0x04 => FakeFocusDevice.Reply(0x04, 0, Hex.Parse(RxVersion)[5..^2]),
            0x93 => FakeFocusDevice.Reply(0x93, 0, []),
            0x62 => Hex.Parse(RxPowerInfo),
            0x6A => FakeFocusDevice.Reply(0x6A, 0, [0x01, 0x00, 0x00]),
            0x67 => Hex.Parse(RxRegion),
            0xAA => Hex.Parse(RxStop),
            _ => null,
        };
    }
}
