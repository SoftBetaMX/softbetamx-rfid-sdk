using Softbetamx.Rfid.Drivers.In805;

namespace Softbetamx.Rfid.Tests.In805;

/// <summary>Decodificación de respuestas reales capturadas del E710 (pruebas-lector/resultados).</summary>
public class A0CommandsTests
{
    private static A0Frame Frame(string hex) => Assert.Single(new A0FrameParser().Append(Hex.Parse(hex)));

    [Fact]
    public void Firmware() => Assert.Equal("129.01", A0Commands.ParseFirmware(Frame("A0 05 01 72 81 01 66").Data));

    [Fact]
    public void Region_fcc() => Assert.Equal("FCC 902-928 MHz", A0Commands.ParseRegion(Frame("A0 06 01 79 01 07 3B 9D").Data));

    [Fact]
    public void Power_single_value() => Assert.Equal([33], A0Commands.ParsePower(Frame("A0 04 01 77 21 C3").Data));

    [Fact]
    public void Power_four_values() =>
        Assert.Equal([30, 31, 32, 33], A0Commands.ParsePower(Frame("A0 07 01 77 1E 1F 20 21 63").Data));

    [Theory]
    [InlineData("A0 05 01 7B 01 25 B9", 37)]
    [InlineData("A0 05 01 7B 01 1C C2", 28)]
    public void Temperature(string rx, int expected) => Assert.Equal(expected, A0Commands.ParseTemperature(Frame(rx).Data));

    [Fact]
    public void Temperature_negative() => Assert.Equal(-5, A0Commands.ParseTemperature([0x00, 0x05]));

    [Fact]
    public void Error_response_on_simple_command_throws_with_name()
    {
        var ex = Assert.Throws<RfidReaderException>(() => A0Commands.ParseFirmware([0x11]));
        Assert.Contains("command_fail", ex.Message);
    }

    [Fact]
    public void Rssi_conversion() => Assert.Equal(-46, A0Commands.RssiDbm(0x53));

    // Paquetes 0x8B reales: la antena de trabajo (0x74 previo) coincide con FreqAnt & 3.
    [Theory]
    [InlineData("A0 13 01 8B 5C 30 00 E2 80 68 94 00 00 50 26 2F E8 5D D3 5B BF", "E2806894000050262FE85DD3", 1, -38)]
    [InlineData("A0 13 01 8B 41 30 00 E2 80 69 95 00 00 50 0D 5B E7 D9 2C 60 EC", "E28069950000500D5BE7D92C", 2, -33)]
    [InlineData("A0 13 01 8B 86 30 00 E2 80 69 15 00 00 50 23 FD D3 35 FA 4E 6B", "E280691500005023FDD335FA", 3, -51)]
    [InlineData("A0 13 01 8B D2 30 00 E2 00 00 17 57 0D 02 07 21 80 35 3E 53 F2", "E2000017570D02072180353E", 3, -46)]
    [InlineData("A0 13 01 8B 57 30 00 E2 80 69 95 00 00 50 0D 5B E7 D9 2C 59 DD", "E28069950000500D5BE7D92C", 4, -40)]
    // 0x8A (de p02_inventario.json) usa el mismo formato de paquete de tag.
    [InlineData("A0 13 01 8A 9E 30 00 E2 80 68 94 00 00 50 26 2F E8 91 D3 50 55", "E2806894000050262FE891D3", 3, -49)]
    public void Tag_packet(string rx, string epc, int antenna, int rssi)
    {
        var tag = Assert.IsType<InventoryTag>(A0Commands.ParseInventoryPacket(Frame(rx)));
        Assert.Equal(epc, tag.Epc);
        Assert.Equal(antenna, tag.Antenna);
        Assert.Equal(rssi, tag.Rssi);
        Assert.Equal(0x3000, tag.Pc);
    }

    [Fact]
    public void Summary_packet()
    {
        var s = Assert.IsType<InventorySummary>(A0Commands.ParseInventoryPacket(Frame("A0 0A 01 8B 02 00 60 00 00 00 3B 2D")));
        Assert.Equal(2, s.AntennaId);
        Assert.Equal(0x60, s.ReadRate);
        Assert.Equal(59, s.TotalRead);
    }

    [Fact]
    public void Empty_summary_packet()
    {
        var s = Assert.IsType<InventorySummary>(A0Commands.ParseInventoryPacket(Frame("A0 0A 01 8B 03 00 00 00 00 00 00 C7")));
        Assert.Equal(3, s.AntennaId);
        Assert.Equal(0, s.TotalRead);
    }

    [Fact]
    public void Antenna_missing_packet()
    {
        var e = Assert.IsType<InventoryError>(A0Commands.ParseInventoryPacket(Frame("A0 04 01 8B 22 AE")));
        Assert.Equal(A0Commands.AntennaMissing, e.Code);
        Assert.Equal("0x22 antenna_missing", A0Commands.ErrorName(e.Code));
    }
}
