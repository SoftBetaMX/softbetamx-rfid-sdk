using System.Collections.Concurrent;

namespace Softbetamx.Rfid.Tests;

public class SimulatedAndApiTests
{
    [Fact]
    public async Task Simulated_reader_works_without_hardware()
    {
        await using var reader = RfidReader.Create(new ReaderOptions { Model = ReaderModel.Simulated });
        await reader.ConnectAsync();

        var info = await reader.GetInfoAsync();
        Assert.Equal(ReaderModel.Simulated, info.Model);

        await reader.SetAntennasAsync([new(1, PowerDbm: 30), new(2, PowerDbm: 27)]);
        var tags = await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(100));
        Assert.NotEmpty(tags);
        Assert.All(tags, t => Assert.Contains(t.Antenna, new[] { 1, 2 }));

        var reads = new ConcurrentQueue<TagRead>();
        reader.TagRead += (_, t) => reads.Enqueue(t);
        await reader.StartContinuousAsync();
        await Task.Delay(250);
        await reader.StopAsync();
        Assert.NotEmpty(reads);

        await reader.SetBuzzerAsync(BuzzerMode.PerTag);
        Assert.Equal(4, (await reader.GetReturnLossAsync()).Count);
        await reader.SetGen2Async(new Gen2Settings(1, Gen2Target.AB, 5));
        Assert.Equal(new Gen2Settings(1, Gen2Target.AB, 5, 107), await reader.GetGen2Async());
    }

    [Fact]
    public async Task Simulated_reports_custom_tags()
    {
        await using var reader = RfidReader.Create(new ReaderOptions { Model = ReaderModel.Simulated, SimulatedTags = ["AABBCCDD"] });
        await reader.ConnectAsync();
        var tag = Assert.Single(await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(10)));
        Assert.Equal("AABBCCDD", tag.Epc);
    }

    [Fact]
    public void Hardware_models_require_an_endpoint()
    {
        Assert.Throws<ArgumentException>(() => RfidReader.Create(new ReaderOptions { Model = ReaderModel.Focus }));
        Assert.Throws<ArgumentException>(() => RfidReader.Create(new ReaderOptions { Model = ReaderModel.In805 }));
    }

    [Fact]
    public async Task Commands_require_connection()
    {
        await using var reader = RfidReader.Create(new ReaderOptions { Model = ReaderModel.Simulated });
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetInfoAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public async Task Continuous_blocks_other_commands_and_start_twice()
    {
        await using var reader = RfidReader.Create(new ReaderOptions { Model = ReaderModel.Simulated });
        await reader.ConnectAsync();
        await reader.StartContinuousAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.StartContinuousAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.SetAntennasAsync([new(1, 20)]));
        await reader.StopAsync();
        await reader.StopAsync(); // idempotent
        await reader.SetAntennasAsync([new(1, 20)]);
    }

    [Fact]
    public async Task Argument_validation()
    {
        await using var reader = RfidReader.Create(new ReaderOptions { Model = ReaderModel.Simulated });
        await reader.ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => reader.SetAntennasAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.SetAntennasAsync([new(1, 20), new(1, 25)]));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.SetAntennasAsync([new(1, 20, Enabled: false)]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.InventoryOnceAsync(TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.SetGen2Async(new Gen2Settings(Session: 4)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.SetGen2Async(new Gen2Settings(Q: 16)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.GetReturnLossAsync([5]));
    }

    [Theory]
    [InlineData(ReaderModel.Focus, false, 4, 4)]
    [InlineData(ReaderModel.In805, true, 2, 2)]
    public void Capabilities_per_model(ReaderModel model, bool buzzer, int relays, int inputs)
    {
        var caps = ReaderCapabilities.For(model);
        Assert.Equal(buzzer, caps.Buzzer);
        Assert.Equal(relays, caps.Relays);
        Assert.Equal(inputs, caps.Inputs);
        Assert.True(caps.ReturnLoss);
        Assert.Equal(4, caps.AntennaPorts);
    }

    [Fact]
    public void Default_tcp_ports_per_model()
    {
        Assert.Equal(8080, new ReaderOptions { Model = ReaderModel.Focus }.DefaultPort);
        Assert.Equal(4001, new ReaderOptions { Model = ReaderModel.In805 }.DefaultPort);
        var t = (Softbetamx.Rfid.Transport.TcpTransport)new ReaderOptions { Model = ReaderModel.Focus, Host = "10.0.0.1" }.CreateTransport();
        Assert.Equal("tcp://10.0.0.1:8080", t.Description);
        var s = new ReaderOptions { Model = ReaderModel.In805, SerialPort = "COM9", BaudRate = 57600 }.CreateTransport();
        Assert.Equal("serial://COM9@57600", s.Description);
    }

    [Fact]
    public async Task Connect_to_closed_tcp_port_faults()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await using var reader = RfidReader.Create(new ReaderOptions { Model = ReaderModel.Focus, Host = "127.0.0.1", Port = port });
        await Assert.ThrowsAnyAsync<Exception>(() => reader.ConnectAsync());
        Assert.Equal(ReaderConnectionState.Faulted, reader.State);
    }
}
