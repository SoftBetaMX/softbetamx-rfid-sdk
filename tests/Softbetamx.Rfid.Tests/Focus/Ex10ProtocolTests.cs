using Softbetamx.Rfid.Drivers.Focus;
using static Softbetamx.Rfid.Tests.Focus.FocusCaptures;

namespace Softbetamx.Rfid.Tests.Focus;

public class Ex10ProtocolTests
{
    private static Ex10Frame Frame(string hex) => Assert.Single(new Ex10FrameParser().Append(Hex.Parse(hex)));

    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    // ------------------------------------------------------------------ CRC and builders

    [Fact]
    public void Crc_of_get_version_matches_manual() => Assert.Equal(0x1D0C, Ex10Crc.Compute([0x00, 0x03]));

    [Theory]
    [InlineData(TxVersion, 0x03, "")]
    [InlineData(TxSerial, 0x10, "")]
    [InlineData(TxTemperature, 0x72, "")]
    [InlineData(TxRegion, 0x67, "")]
    [InlineData(TxInvAntennas, 0x61, "02")]
    [InlineData(TxPowers, 0x61, "03")]
    [InlineData(TxConnected, 0x61, "05")]
    [InlineData(TxPowerInfo, 0x62, "01")]
    [InlineData(TxClearBuffer, 0x2A, "")]
    [InlineData(TxGetSession, 0x6B, "05 00")]
    [InlineData(TxGetQ, 0x6B, "05 12")]
    [InlineData(ManualSetSession1, 0x9B, "05 00 01")]
    public void Build_matches_real_frames(string expected, byte cmd, string data) =>
        Assert.Equal(Hex.Format(Hex.Parse(expected)), Hex.Format(Ex10Protocol.Build(cmd, Hex.Parse(data))));

    [Fact]
    public void Extended_builders_match_real_frames()
    {
        Assert.Equal(Hex.Format(Hex.Parse(TxStop)), Hex.Format(Ex10Protocol.AsyncStopCommand()));
        Assert.Equal(Hex.Format(Hex.Parse(ManualAsyncStartBF)), Hex.Format(Ex10Protocol.AsyncStartCommand(0x00BF, 0x8003)));
        Assert.Equal(Hex.Format(Hex.Parse(TxAsyncStart3FA003)), Hex.Format(Ex10Protocol.AsyncStartCommand(0x003F, 0xA003)));
        Assert.Equal(Hex.Format(Hex.Parse(TxReturnLossAnt1)), Hex.Format(Ex10Protocol.ReturnLossCommand(1, 0x01)));
    }

    [Fact]
    public void Inventory_builders_match_real_frames()
    {
        Assert.Equal(Hex.Format(Hex.Parse(TxSyncInventory500)), Hex.Format(Ex10Protocol.SyncInventoryCommand(TimeSpan.FromMilliseconds(500))));
        Assert.Equal(Hex.Format(Hex.Parse(TxTagBuffer3F)), Hex.Format(Ex10Protocol.GetTagBufferCommand()));
        Assert.Equal(Hex.Format(Hex.Parse(TxSetAntenna1)), Hex.Format(Ex10Protocol.SetInventoryAntennasCommand([1])));
        Assert.Equal(Hex.Format(Hex.Parse(TxSetAntennas12)), Hex.Format(Ex10Protocol.SetInventoryAntennasCommand([1, 2])));
    }

    [Fact]
    public void Power_command_uses_centi_dbm_for_read_and_write()
    {
        var f = Ex10Protocol.SetAntennaPowerCommand([new AntennaConfig(1, 30), new AntennaConfig(2, 27)]);
        // FF Len 91 03 | 01 0BB8 0BB8 | 02 0A8C 0A8C | CRC
        Assert.Equal("FF0B910301" + "0BB80BB8" + "02" + "0A8C0A8C", Hex.Format(f[..^2]));
    }

    [Theory]
    [InlineData(Gen2Target.A, "FF 04 9B 05 01 01 00 A2 FD")]
    [InlineData(Gen2Target.AB, "FF 04 9B 05 01 00 00 A3 FD")]
    public void Target_command_matches_known_frames(Gen2Target target, string expected) =>
        Assert.Equal(Hex.Format(Hex.Parse(expected)), Hex.Format(Ex10Protocol.SetTargetCommand(target)));

    [Fact]
    public void Q_and_rfmode_commands()
    {
        Assert.Equal(Hex.Format(Hex.Parse("FF 03 9B 05 12 00 CE E8")), Hex.Format(Ex10Protocol.SetQCommand(null)));
        Assert.Equal("FF049B05120104", Hex.Format(Ex10Protocol.SetQCommand(4)[..^2]));
        Assert.Equal(Hex.Format(Hex.Parse("FF 03 9B 05 02 71 DE 99")), Hex.Format(Ex10Protocol.SetRfModeCommand(113)));
    }

    // ------------------------------------------------------------------ parser

    [Fact]
    public void Parses_live_frames_with_valid_crc()
    {
        foreach (var hex in new[] { RxStop, RxVersion, RxSerial, RxTemperature, RxRegion, RxInvAntennas12, RxPowers, RxConnected,
                     RxPowerInfo, RxSession, RxTarget, RxRfMode, RxQ, RxSetAntennaOk, RxClearBuffer, RxSyncInventory4, RxTagBuffer4,
                     RxAsyncAck, RxHeartbeatA003, RxAsyncTag1, RxAsyncTag2, RxAsyncRoundEnd, RxReturnLossAnt1 })
        {
            var f = Frame(hex);
            Assert.Equal(0, f.Status);
            Assert.False(f.IsAscii);
        }
    }

    [Fact]
    public void Parses_manual_examples()
    {
        foreach (var hex in new[] { ManualTagBuffer15, ManualTagBufferBF, ManualAsyncTag3F, ManualAsyncTagBF, ManualHeartbeat, ManualRoundEnd })
            Frame(hex);
        var interrupted = Frame(ManualInterrupted);
        Assert.Equal(0x03, interrupted.Command);
        Assert.Equal(0xAA49, interrupted.Status);
    }

    [Fact]
    public void Reassembles_byte_by_byte_and_skips_garbage_and_bad_crc()
    {
        var bytes = Hex.Parse("00 13 " + RxTemperature + " FF 01 72 00 00 1A 00 00 " + RxAsyncTag1 + " " + RxIoGet + " " + RxRegion);
        var parser = new Ex10FrameParser();
        var frames = new List<Ex10Frame>();
        foreach (var b in bytes) frames.AddRange(parser.Append([b]));

        Assert.Equal(4, frames.Count);
        Assert.Equal(0x72, frames[0].Command);
        Assert.Equal(0xAA, frames[1].Command);
        Assert.Equal("IOGET", frames[2].Ascii);
        Assert.Equal(new byte[] { 0x0F }, frames[2].Data);
        Assert.Equal(0x67, frames[3].Command);
        Assert.True(parser.DiscardedBytes > 2);
    }

    [Fact]
    public void Recognizes_iosetok() => Assert.Equal("IOSETOK", Frame("49 4F 53 45 54 4F 4B").Ascii);

    [Fact]
    public void Extended_reply_detection()
    {
        Assert.True(Frame(RxStop).IsExtendedReply(Ex10Protocol.AsyncStop));
        Assert.False(Frame(RxStop).IsExtendedReply(Ex10Protocol.AsyncStart));
        Assert.True(Frame(RxAsyncAck).IsExtendedReply(Ex10Protocol.AsyncStart));
    }

    // ------------------------------------------------------------------ decoders

    [Fact]
    public void Version_of_live_module()
    {
        var v = Ex10Protocol.ParseVersion(Frame(RxVersion).Data);
        Assert.Equal("E710", v.Chip);
        Assert.Equal(4, v.AntennaPorts);
        Assert.Equal("CHINA", v.Certification);
        Assert.Equal("25.07.14.00", v.Firmware);
    }

    [Fact]
    public void Simple_info_values()
    {
        Assert.Equal("000000300000261600000 8C0".Replace(" ", ""), Ex10Protocol.ParseSerialNumber(Frame(RxSerial).Data));
        Assert.Equal(26, Ex10Protocol.ParseTemperature(Frame(RxTemperature).Data));
        Assert.Equal(-5, Ex10Protocol.ParseTemperature([0xFB]));
        Assert.Equal("NA (0x01)", Ex10Protocol.ParseRegion(Frame(RxRegion).Data, out var code));
        Assert.Equal(0x01, code);
    }

    [Fact]
    public void Antenna_configuration()
    {
        Assert.Equal([1, 2], Ex10Protocol.ParseInventoryAntennas(Frame(RxInvAntennas12).Data));
        Assert.Equal([1], Ex10Protocol.ParseInventoryAntennas(Frame(RxInvAntenna1).Data));
        Assert.Equal([1, 2], Ex10Protocol.ParseConnectedAntennas(Frame(RxConnected).Data));
        var powers = Ex10Protocol.ParseAntennaPowers(Frame(RxPowers).Data);
        Assert.Equal([1, 2, 3, 4], powers.Select(p => p.Port));
        Assert.All(powers, p => { Assert.Equal(33.0, p.ReadPowerDbm); Assert.Equal(33.0, p.WritePowerDbm); });
        var (current, max, min) = Ex10Protocol.ParsePowerInfo(Frame(RxPowerInfo).Data);
        Assert.Equal((33.0, 33.0, 0.0), (current, max, min));
    }

    [Fact]
    public void Gen2_values_of_live_module()
    {
        Assert.Equal(0, Ex10Protocol.ParseProtocolConfig(Frame(RxSession).Data, 0x00)[0]);
        Assert.Equal(Gen2Target.A, Ex10Protocol.ParseTarget(Ex10Protocol.ParseProtocolConfig(Frame(RxTarget).Data, 0x01)));
        Assert.Equal(107, Ex10Protocol.ParseProtocolConfig(Frame(RxRfMode).Data, 0x02)[0]);
        Assert.Null(Ex10Protocol.ParseQ(Ex10Protocol.ParseProtocolConfig(Frame(RxQ).Data, 0x12)));
        Assert.Equal(7, Ex10Protocol.ParseQ([0x01, 0x07]));
        Assert.Equal(Gen2Target.BA, Ex10Protocol.ParseTarget([0x00, 0x01]));
    }

    [Fact]
    public void Tags_found_and_buffer_of_live_inventory()
    {
        Assert.Equal(4, Ex10Protocol.ParseTagsFound(Frame(RxSyncInventory4).Data));
        var tags = Ex10Protocol.ParseTagBuffer(Frame(RxTagBuffer4).Data, Now);
        Assert.Equal(4, tags.Count);
        Assert.Equal("E2806894000040262FE8A9D3", tags[0].Epc);
        Assert.Equal(6, tags[0].Count);
        Assert.Equal(-71, tags[0].Rssi);
        Assert.Equal(2, tags[0].Antenna);
        Assert.Equal(0x0DC852, tags[0].FrequencyKhz);
        Assert.Equal(0x3000, tags[0].Pc);
        Assert.InRange(tags[0].PhaseDegrees!.Value, 0, 360);
        Assert.Equal("E2000017570D02072180353E", tags[1].Epc);
        Assert.Equal("0000000000000002EE6D4B1F", tags[2].Epc);
        Assert.Equal(0x3400, tags[2].Pc);
        Assert.Equal("303402891057605B0292A1BF", tags[3].Epc);
    }

    [Fact]
    public void Tag_buffer_manual_examples()
    {
        var t15 = Ex10Protocol.ParseTagBuffer(Frame(ManualTagBuffer15).Data, Now);
        Assert.Equal(["111122223333444455556666", "050000000000000000002354"], t15.Select(t => t.Epc));
        Assert.Equal([0x22, 0x0E], t15.Select(t => t.Count));
        Assert.All(t15, t => Assert.Equal(1, t.Antenna));

        var tbf = Ex10Protocol.ParseTagBuffer(Frame(ManualTagBufferBF).Data, Now);
        Assert.Equal(2, tbf.Count);
        Assert.Equal("1111222233334444", tbf[0].Epc);
        Assert.Equal(-29, tbf[0].Rssi);
        Assert.Equal(926250, tbf[0].FrequencyKhz);
        Assert.Equal(22, tbf[1].Epc.Length / 2);
    }

    [Fact]
    public void Async_packets_live()
    {
        Assert.Equal(Ex10Protocol.AsyncPacketKind.StartAck, Ex10Protocol.ClassifyAsync(Frame(RxAsyncAck), Now, out _));
        Assert.Equal(Ex10Protocol.AsyncPacketKind.Heartbeat, Ex10Protocol.ClassifyAsync(Frame(RxHeartbeatA003), Now, out _));
        Assert.Equal(Ex10Protocol.AsyncPacketKind.RoundEnd, Ex10Protocol.ClassifyAsync(Frame(RxAsyncRoundEnd), Now, out _));
        Assert.Equal(Ex10Protocol.AsyncPacketKind.StopReply, Ex10Protocol.ClassifyAsync(Frame(RxStop), Now, out _));

        Assert.Equal(Ex10Protocol.AsyncPacketKind.Tag, Ex10Protocol.ClassifyAsync(Frame(RxAsyncTag1), Now, out var t1));
        Assert.Equal("E2806894000040262FE8A9D3", t1!.Epc);
        Assert.Equal(1, t1.Antenna);
        Assert.Equal(-64, t1.Rssi);
        Assert.Equal(0x0DE986, t1.FrequencyKhz);

        Assert.Equal(Ex10Protocol.AsyncPacketKind.Tag, Ex10Protocol.ClassifyAsync(Frame(RxAsyncTag2), Now, out var t2));
        Assert.Equal("303402891057605B0292A1BF", t2!.Epc);
        Assert.Equal(2, t2.Antenna);
    }

    [Fact]
    public void Async_packets_manual()
    {
        Assert.Equal(Ex10Protocol.AsyncPacketKind.Tag, Ex10Protocol.ClassifyAsync(Frame(ManualAsyncTag3F), Now, out var a));
        Assert.Equal("1111201902110194", a!.Epc);
        Assert.Equal(-67, a.Rssi);
        Assert.Equal(2, a.Antenna);
        Assert.Equal(915250, a.FrequencyKhz);

        Assert.Equal(Ex10Protocol.AsyncPacketKind.Tag, Ex10Protocol.ClassifyAsync(Frame(ManualAsyncTagBF), Now, out var b));
        Assert.Equal("E200001D4001015810408273", b!.Epc);
        Assert.Equal(-45, b.Rssi);

        Assert.Equal(Ex10Protocol.AsyncPacketKind.Heartbeat, Ex10Protocol.ClassifyAsync(Frame(ManualHeartbeat), Now, out _));
        Assert.Equal(Ex10Protocol.AsyncPacketKind.RoundEnd, Ex10Protocol.ClassifyAsync(Frame(ManualRoundEnd), Now, out _));
    }

    [Fact]
    public void Return_loss_live_antenna_1()
    {
        var rl = Ex10Protocol.ParseReturnLoss(Frame(RxReturnLossAnt1).Data, 1);
        Assert.Equal(1, rl.Antenna);
        Assert.Equal(50, rl.Points.Count);
        Assert.Equal(new ReturnLossPoint(915250, 17.0), rl.Points[0]);
        Assert.All(rl.Points, p => Assert.InRange(p.FrequencyKhz, 902_000, 928_000));
        Assert.InRange(rl.ReturnLossDb, 17, 21);
        Assert.InRange(rl.Vswr, 1.1, 1.5);
    }

    [Fact]
    public void Vswr_of_manual_example() => Assert.Equal(1.67, AntennaReturnLoss.VswrFromReturnLoss(12.0), 2);

    [Fact]
    public void Status_names() => Assert.Equal("antenna not connected", Ex10Protocol.StatusName(0x0503));
}
