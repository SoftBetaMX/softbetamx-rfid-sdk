namespace Softbetamx.Rfid.Drivers.In805;

/// <summary>
/// Trama del protocolo 0xA0: <c>A0 | Len | Addr | Cmd | Data... | Check</c>.
/// Len = bytes desde Addr hasta Check; Check = (-(suma de los bytes anteriores)) &amp; 0xFF.
/// </summary>
internal sealed class A0Frame
{
    public const byte Head = 0xA0;
    public const byte HostAddress = 0xFF;

    public A0Frame(byte address, byte command, byte[] data)
    {
        Address = address;
        Command = command;
        Data = data;
    }

    public byte Address { get; }
    public byte Command { get; }
    public byte[] Data { get; }

    /// <summary>Valor del campo Len (Addr + Cmd + Data + Check).</summary>
    public int Length => Data.Length + 3;

    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        int sum = 0;
        foreach (var b in bytes) sum += b;
        return (byte)(-sum & 0xFF);
    }

    /// <summary>Construye la trama completa (con cabecera y checksum) que envía el host.</summary>
    public static byte[] Build(byte command, ReadOnlySpan<byte> data, byte address = HostAddress)
    {
        if (data.Length > 252) throw new ArgumentOutOfRangeException(nameof(data), "Data too long for an A0 frame");
        var frame = new byte[data.Length + 5];
        frame[0] = Head;
        frame[1] = (byte)(data.Length + 3);
        frame[2] = address;
        frame[3] = command;
        data.CopyTo(frame.AsSpan(4));
        frame[^1] = Checksum(frame.AsSpan(0, frame.Length - 1));
        return frame;
    }

    public byte[] ToBytes() => Build(Command, Data, Address);

    public override string ToString() => Convert.ToHexString(ToBytes());
}
