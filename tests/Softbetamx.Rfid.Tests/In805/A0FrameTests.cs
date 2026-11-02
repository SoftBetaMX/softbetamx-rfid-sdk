using Softbetamx.Rfid.Drivers.In805;

namespace Softbetamx.Rfid.Tests.In805;

/// <summary>Construcción de tramas contra TX reales capturados en pruebas-lector/resultados.</summary>
public class A0FrameTests
{
    [Theory]
    [InlineData(0x72, "", "A0 03 FF 72 EC")] // firmware
    [InlineData(0x79, "", "A0 03 FF 79 E5")] // región
    [InlineData(0x77, "", "A0 03 FF 77 E7")] // potencia
    [InlineData(0x7B, "", "A0 03 FF 7B E3")] // temperatura
    [InlineData(0x74, "00", "A0 04 FF 74 00 E9")] // antena de trabajo 1
    [InlineData(0x74, "03", "A0 04 FF 74 03 E6")] // antena de trabajo 4
    [InlineData(0x66, "05", "A0 04 FF 66 05 F2")] // potencia temporal 5 dBm
    [InlineData(0x66, "21", "A0 04 FF 66 21 D6")] // potencia temporal 33 dBm
    [InlineData(0x7E, "21", "A0 04 FF 7E 21 BE")] // pérdida de retorno 915 MHz
    [InlineData(0x8B, "00 00 0A", "A0 06 FF 8B 00 00 0A C6")] // inventario S0, A, repeat 10
    [InlineData(0x8B, "00 00 05", "A0 06 FF 8B 00 00 05 CB")]
    public void Build_matches_real_tx_frames(byte cmd, string data, string expected)
    {
        var frame = A0Frame.Build(cmd, Hex.Parse(data));
        Assert.Equal(Hex.Format(Hex.Parse(expected)), Hex.Format(frame));
    }

    [Theory]
    [InlineData("A0 05 01 72 81 01 66")]
    [InlineData("A0 06 01 79 01 07 3B 9D")]
    [InlineData("A0 04 01 77 21 C3")]
    [InlineData("A0 05 01 7B 01 25 B9")]
    public void Checksum_of_real_rx_frames_is_valid(string rx)
    {
        var bytes = Hex.Parse(rx);
        Assert.Equal(bytes[^1], A0Frame.Checksum(bytes.AsSpan(0, bytes.Length - 1)));
    }

    [Fact]
    public void ToBytes_round_trips_reader_address()
    {
        var frame = new A0Frame(0x01, 0x72, [0x81, 0x01]);
        Assert.Equal("A0050172810166", Hex.Format(frame.ToBytes()));
        Assert.Equal(5, frame.Length);
    }
}
