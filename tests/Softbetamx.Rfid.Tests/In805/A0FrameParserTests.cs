using Softbetamx.Rfid.Drivers.In805;

namespace Softbetamx.Rfid.Tests.In805;

public class A0FrameParserTests
{
    private const string Firmware = "A0 05 01 72 81 01 66";
    private const string Power = "A0 04 01 77 21 C3";
    private const string Tag = "A0 13 01 8B D2 30 00 E2 00 00 17 57 0D 02 07 21 80 35 3E 53 F2";

    [Fact]
    public void Parses_single_frame()
    {
        var frames = new A0FrameParser().Append(Hex.Parse(Firmware));
        var f = Assert.Single(frames);
        Assert.Equal(0x01, f.Address);
        Assert.Equal(0x72, f.Command);
        Assert.Equal(new byte[] { 0x81, 0x01 }, f.Data);
    }

    [Fact]
    public void Parses_several_frames_in_one_chunk()
    {
        var frames = new A0FrameParser().Append(Hex.Parse(Firmware + " " + Power + " " + Tag));
        Assert.Equal(new byte[] { 0x72, 0x77, 0x8B }, frames.Select(f => f.Command).ToArray());
    }

    [Fact]
    public void Reassembles_frames_split_across_reads()
    {
        var bytes = Hex.Parse(Tag + " " + Firmware);
        var parser = new A0FrameParser();
        var frames = new List<A0Frame>();
        // Un byte por lectura: el peor caso.
        foreach (var b in bytes) frames.AddRange(parser.Append([b]));

        Assert.Equal(2, frames.Count);
        Assert.Equal(0x8B, frames[0].Command);
        Assert.Equal(19 - 3, frames[0].Data.Length);
        Assert.Equal(0x72, frames[1].Command);
    }

    [Fact]
    public void Splits_in_the_middle_of_header_and_data()
    {
        var bytes = Hex.Parse(Tag);
        var parser = new A0FrameParser();
        Assert.Empty(parser.Append(bytes.AsSpan(0, 1)));
        Assert.Empty(parser.Append(bytes.AsSpan(1, 7)));
        var f = Assert.Single(parser.Append(bytes.AsSpan(8)));
        Assert.Equal(0x8B, f.Command);
    }

    [Fact]
    public void Skips_garbage_before_header()
    {
        var parser = new A0FrameParser();
        var frames = parser.Append(Hex.Parse("00 13 FF 55 " + Firmware));
        Assert.Equal(0x72, Assert.Single(frames).Command);
        Assert.Equal(4, parser.DiscardedBytes);
    }

    [Fact]
    public void Resyncs_after_bad_checksum()
    {
        // Trama con checksum corrupto seguida de una válida.
        var frames = new A0FrameParser().Append(Hex.Parse("A0 05 01 72 81 01 00 " + Power));
        var f = Assert.Single(frames);
        Assert.Equal(0x77, f.Command);
        Assert.Equal(new byte[] { 0x21 }, f.Data);
    }

    [Fact]
    public void Resyncs_on_false_header_inside_garbage()
    {
        // 0xA0 suelto con Len que "se come" el inicio de la trama real: hay que descartar solo un byte.
        var frames = new A0FrameParser().Append(Hex.Parse("A0 04 " + Firmware));
        Assert.Equal(0x72, Assert.Single(frames).Command);
    }

    [Fact]
    public void Ignores_impossible_length()
    {
        var frames = new A0FrameParser().Append(Hex.Parse("A0 00 A0 01 " + Power));
        Assert.Equal(0x77, Assert.Single(frames).Command);
    }
}
