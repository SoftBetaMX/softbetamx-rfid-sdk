using System.Text;
using Softbetamx.Rfid.Internal;

namespace Softbetamx.Rfid.Drivers.Focus;

/// <summary>
/// A frame received from an EX10-protocol module: <c>FF | Len | Cmd | Status(2) | Data(Len) | CRC16</c>,
/// or an ASCII reply from the communication board that shares the socket (<c>IOGET</c>+state, <c>IOSETOK</c>).
/// </summary>
internal sealed class Ex10Frame
{
    public Ex10Frame(byte command, ushort status, byte[] data)
    {
        Command = command;
        Status = status;
        Data = data;
    }

    private Ex10Frame(string ascii, byte[] data)
    {
        Ascii = ascii;
        Data = data;
    }

    public byte Command { get; }

    public ushort Status { get; }

    /// <summary>Data field (after the status code). For ASCII replies: the bytes after the keyword.</summary>
    public byte[] Data { get; }

    /// <summary>ASCII keyword ("IOGET", "IOSETOK") for board replies; null for protocol frames.</summary>
    public string? Ascii { get; }

    public bool IsAscii => Ascii is not null;

    public static Ex10Frame AsciiReply(string keyword, byte[] data) => new(keyword, data);

    /// <summary>For extended (0xAA) frames: true if the data starts with "Moduletech" + <paramref name="subcommand"/>.</summary>
    public bool IsExtendedReply(ushort subcommand) =>
        Command == Ex10Protocol.ExtendedCommand
        && Data.Length >= Ex10Protocol.Marker.Length + 2
        && Data.AsSpan(0, Ex10Protocol.Marker.Length).SequenceEqual(Ex10Protocol.Marker)
        && Data[Ex10Protocol.Marker.Length] == (byte)(subcommand >> 8)
        && Data[Ex10Protocol.Marker.Length + 1] == (byte)subcommand;

    public override string ToString() => IsAscii
        ? $"{Ascii}+{Convert.ToHexString(Data)}"
        : $"cmd=0x{Command:X2} status=0x{Status:X4} data={Convert.ToHexString(Data)}";
}

/// <summary>CRC-16 of the EX10 protocol: polynomial 0x1021, initial 0xFFFF, data bits shifted in from the right.</summary>
internal static class Ex10Crc
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        int reg = 0xFFFF;
        foreach (byte b in data)
        {
            for (int i = 7; i >= 0; i--)
            {
                bool msb = (reg & 0x8000) != 0;
                reg = ((reg << 1) & 0xFFFF) | ((b >> i) & 1);
                if (msb) reg ^= 0x1021;
            }
        }
        return (ushort)reg;
    }
}

/// <summary>
/// Reassembles EX10 frames from a byte stream: tolerates split reads, skips garbage, resyncs on a bad CRC
/// by dropping one byte, and recognizes the ASCII board replies.
/// </summary>
internal sealed class Ex10FrameParser : IFrameParser<Ex10Frame>
{
    private static readonly byte[] IoGet = Encoding.ASCII.GetBytes("IOGET");
    private static readonly byte[] IoSetOk = Encoding.ASCII.GetBytes("IOSETOK");

    private byte[] _buffer = new byte[1024];
    private int _count;

    public long DiscardedBytes { get; private set; }

    public List<Ex10Frame> Append(ReadOnlySpan<byte> chunk)
    {
        if (_count + chunk.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_count + chunk.Length, _buffer.Length * 2));
        chunk.CopyTo(_buffer.AsSpan(_count));
        _count += chunk.Length;

        var frames = new List<Ex10Frame>();
        int pos = 0;
        while (pos < _count)
        {
            var rest = _buffer.AsSpan(pos, _count - pos);

            // ASCII replies of the communication board.
            int ascii = MatchAscii(rest, out var asciiFrame);
            if (ascii > 0)
            {
                frames.Add(asciiFrame!);
                pos += ascii;
                continue;
            }
            if (ascii < 0) break; // partial ASCII reply: wait for more bytes

            if (rest[0] != Ex10Protocol.Header)
            {
                pos++;
                DiscardedBytes++;
                continue;
            }
            if (rest.Length < 7) break;
            int total = rest[1] + 7;
            if (rest.Length < total) break;

            var raw = rest[..total];
            ushort crc = (ushort)((raw[^2] << 8) | raw[^1]);
            if (Ex10Crc.Compute(raw[1..^2]) != crc)
            {
                pos++;
                DiscardedBytes++;
                continue;
            }

            frames.Add(new Ex10Frame(raw[2], (ushort)((raw[3] << 8) | raw[4]), raw[5..^2].ToArray()));
            pos += total;
        }

        if (pos > 0)
        {
            Array.Copy(_buffer, pos, _buffer, 0, _count - pos);
            _count -= pos;
        }
        return frames;
    }

    /// <summary>&gt;0: bytes consumed by a complete ASCII reply; 0: not ASCII; &lt;0: incomplete ASCII reply.</summary>
    private static int MatchAscii(ReadOnlySpan<byte> rest, out Ex10Frame? frame)
    {
        frame = null;
        if (rest[0] != (byte)'I') return 0;

        if (rest.Length >= IoSetOk.Length && rest[..IoSetOk.Length].SequenceEqual(IoSetOk))
        {
            frame = Ex10Frame.AsciiReply("IOSETOK", []);
            return IoSetOk.Length;
        }
        if (rest.Length >= IoGet.Length + 1 && rest[..IoGet.Length].SequenceEqual(IoGet))
        {
            frame = Ex10Frame.AsciiReply("IOGET", [rest[IoGet.Length]]);
            return IoGet.Length + 1;
        }
        // A prefix of one of the keywords at the very end of the buffer: wait for the rest.
        if (IsPrefix(rest, IoSetOk) || IsPrefix(rest, IoGet) || (rest.Length == IoGet.Length && rest.SequenceEqual(IoGet)))
            return -1;
        return 0;
    }

    private static bool IsPrefix(ReadOnlySpan<byte> rest, byte[] keyword) =>
        rest.Length < keyword.Length && keyword.AsSpan(0, rest.Length).SequenceEqual(rest);
}
