using System.Collections.Concurrent;
using Softbetamx.Rfid.Tests.Fakes;

namespace Softbetamx.Rfid.Tests;

public class In805ReaderTests
{
    private static (RfidReader Reader, FakeIn805Device Device) Create()
    {
        var device = new FakeIn805Device();
        var reader = RfidReader.Create(new ReaderOptions { Model = ReaderModel.In805, Transport = device, CommandTimeout = TimeSpan.FromMilliseconds(800) });
        return (reader, device);
    }

    [Fact]
    public async Task Connect_and_info()
    {
        var (reader, device) = Create();
        await using var _ = reader;
        await reader.ConnectAsync();

        Assert.Equal("A003FF72EC", device.Received.First());
        var info = await reader.GetInfoAsync();
        Assert.Equal("129.01", info.Firmware);
        Assert.Equal(37, info.TemperatureC);
        Assert.Equal("FCC 902-928 MHz", info.Region);
        Assert.Equal(4, info.Powers.Count);
        Assert.All(info.Powers, p => Assert.Equal(33, p.ReadPowerDbm));
        Assert.True(reader.Capabilities.Buzzer);
    }

    [Fact]
    public async Task Initial_antenna_config_comes_from_the_reader_power()
    {
        var (reader, device) = Create();
        await using var _ = reader;
        device.PowerResponse = [30, 31, 32, 33];
        await reader.ConnectAsync();

        var cfg = await reader.GetAntennasAsync();
        Assert.Equal([30.0, 31.0, 32.0, 33.0], cfg.Select(c => c.PowerDbm));
        Assert.All(cfg, c => Assert.True(c.Enabled));
    }

    [Fact]
    public async Task Missing_antenna_is_reported_once_until_it_reconnects()
    {
        var (reader, device) = Create();
        await using var _ = reader;
        device.Tags[1] = [("E2806894000050262FE85DD3", 0x5B)];
        device.MissingAntennas.Add(2);
        var errors = new ConcurrentQueue<string>();
        reader.Error += (_, e) => errors.Enqueue(e.Message);
        await reader.ConnectAsync();
        await reader.SetAntennasAsync([new(1, 30), new(2, 30)]);

        for (int i = 0; i < 3; i++) await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(1));
        Assert.Single(errors, e => e.Contains("antenna 2"));

        device.MissingAntennas.Remove(2);
        await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(1));
        device.MissingAntennas.Add(2);
        await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, errors.Count(e => e.Contains("antenna 2")));
    }

    [Fact]
    public async Task Missing_antennas_lists_ports_currently_reported_missing()
    {
        var (reader, device) = Create();
        await using var _ = reader;
        device.MissingAntennas.Add(2);
        device.MissingAntennas.Add(4);
        await reader.ConnectAsync();
        await reader.SetAntennasAsync([new(1, 30), new(2, 30), new(4, 30)]);
        Assert.Empty(reader.MissingAntennas);

        await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(1));
        Assert.Equal([2, 4], reader.MissingAntennas);

        // Stays listed in the following rounds (even though the error is not raised again).
        await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(1));
        Assert.Equal([2, 4], reader.MissingAntennas);

        // Antenna 2 answers again.
        device.MissingAntennas.Remove(2);
        await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(1));
        Assert.Equal([4], reader.MissingAntennas);

        // A disabled port is not reported.
        await reader.SetAntennasAsync([new(1, 30), new(2, 30)]);
        Assert.Empty(reader.MissingAntennas);
    }

    [Fact]
    public async Task Missing_antennas_is_empty_for_simulated_reader()
    {
        await using var reader = RfidReader.Create(new ReaderOptions { Model = ReaderModel.Simulated });
        await reader.ConnectAsync();
        await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(1));
        Assert.Empty(reader.MissingAntennas);
    }

    [Fact]
    public async Task Inventory_once_applies_antenna_power_and_session_per_round()
    {
        var (reader, device) = Create();
        await using var _ = reader;
        device.Tags[1] = [("E2806894000050262FE85DD3", 0x5B), ("E2000017570D02072180353E", 0x53)];
        device.Tags[2] = [("E28069950000500D5BE7D92C", 0x60)];
        device.MissingAntennas.Add(4);
        var errors = new ConcurrentQueue<string>();
        reader.Error += (_, e) => errors.Enqueue(e.Message);
        await reader.ConnectAsync();
        await reader.SetAntennasAsync([new(1, 20), new(2, 21), new(4, 23)]);
        await reader.SetGen2Async(new Gen2Settings(Session: 1, Target: Gen2Target.A));
        while (device.Received.TryDequeue(out var _)) { }

        var tags = await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(1));

        Assert.Equal(3, tags.Count);
        Assert.Contains(tags, t => t.Epc == "E2806894000050262FE85DD3" && t.Antenna == 1 && t.Rssi == -38);
        Assert.Contains(tags, t => t.Epc == "E28069950000500D5BE7D92C" && t.Antenna == 2 && t.Rssi == -33);
        Assert.Contains(errors, e => e.Contains("antenna 4"));

        var tx = device.Received.ToArray();
        Assert.StartsWith("A004FF7400", tx[0]);
        Assert.StartsWith("A004FF6614", tx[1]); // 20 dBm temporary
        Assert.StartsWith("A006FF8B0100", tx[2]); // S1, target A
        Assert.StartsWith("A004FF7401", tx[3]);
        Assert.StartsWith("A004FF6615", tx[4]);
        Assert.StartsWith("A004FF7403", tx[6]);
        Assert.StartsWith("A004FF6617", tx[7]);
        Assert.DoesNotContain(device.Received, f => f.StartsWith("A004FF76")); // never power to flash
    }

    [Fact]
    public async Task Inventory_once_aggregates_repeated_reads()
    {
        var (reader, device) = Create();
        await using var _ = reader;
        device.Tags[1] = [("E2806894000050262FE85DD3", 0x5B)];
        await reader.ConnectAsync();
        await reader.SetAntennasAsync([new(1, 30)]);

        var tags = await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(150));

        var tag = Assert.Single(tags);
        Assert.True(tag.Count > 1);
    }

    [Fact]
    public async Task Continuous_raises_events_until_stopped()
    {
        var (reader, device) = Create();
        await using var _ = reader;
        device.Tags[1] = [("E2806894000050262FE85DD3", 0x5B)];
        device.Tags[3] = [("E2000017570D02072180353E", 0x53)];
        await reader.ConnectAsync();
        await reader.SetAntennasAsync([new(1, 30), new(3, 30)]);
        var reads = new ConcurrentQueue<TagRead>();
        reader.TagRead += (_, t) => reads.Enqueue(t);

        await reader.StartContinuousAsync();
        await Task.Delay(200);
        await reader.StopAsync();
        int count = reads.Count;
        await Task.Delay(100);

        Assert.True(count >= 2);
        Assert.Equal(count, reads.Count); // no events after stop
        Assert.Contains(reads, r => r.Antenna == 3);
        Assert.Equal("129.01", (await reader.GetInfoAsync()).Firmware);
    }

    [Fact]
    public async Task Buzzer_return_loss_and_gpio()
    {
        var (reader, device) = Create();
        await using var _ = reader;
        await reader.ConnectAsync();

        await reader.SetBuzzerAsync(BuzzerMode.PerRound);
        await reader.SetBuzzerAsync(BuzzerMode.Quiet);
        Assert.Contains("A004FF7A01E2", device.Received);
        Assert.Contains("A004FF7A00E3", device.Received);

        var rl = await reader.GetReturnLossAsync();
        Assert.Equal([30.0, 31.0, 32.0, 33.0], rl.Select(r => r.ReturnLossDb));
        Assert.Contains("A004FF7E21BE", device.Received);

        await reader.SetRelayAsync(1, true);
        Assert.Contains(device.Received, f => f.StartsWith("A005FF610301"));
        Assert.Equal([false, true], await reader.GetInputsAsync());
    }

    [Theory]
    [InlineData(Gen2Target.AB, null, null)]
    [InlineData(Gen2Target.A, 4, null)]
    [InlineData(Gen2Target.A, null, 107)]
    public async Task Unsupported_gen2_options_throw(Gen2Target target, int? q, int? rfMode)
    {
        var (reader, _) = Create();
        await using var _r = reader;
        await reader.ConnectAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => reader.SetGen2Async(new Gen2Settings(0, target, q, rfMode)));
    }
}
