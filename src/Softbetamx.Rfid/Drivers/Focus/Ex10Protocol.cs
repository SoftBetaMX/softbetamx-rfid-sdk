using System.Buffers.Binary;
using System.Text;

namespace Softbetamx.Rfid.Drivers.Focus;

/// <summary>
/// Frame builders and response decoders of the EX10 "Moduletech" protocol
/// ("EX10 series module data communication protocol", 2023-05-30). All multi-byte values are big-endian.
/// </summary>
internal static class Ex10Protocol
{
    public const byte Header = 0xFF;
    public const byte ExtendedCommand = 0xAA;
    public const byte ExtendedTerminator = 0xBB;

    /// <summary>"Moduletech": subcommand marker of extended (0xAA) commands.</summary>
    public static readonly byte[] Marker = Encoding.ASCII.GetBytes("Moduletech");

    /// <summary>"XTSJ": heartbeat marker during asynchronous inventory.</summary>
    public static readonly byte[] HeartbeatMarker = Encoding.ASCII.GetBytes("XTSJ");

    // Regular commands.
    public const byte GetVersion = 0x03;
    public const byte BootFirmware = 0x04;
    public const byte GetSerialNumber = 0x10;
    public const byte SyncInventory = 0x22;
    public const byte GetTagBuffer = 0x29;
    public const byte ClearTagBuffer = 0x2A;
    public const byte GetAntennaConfig = 0x61;
    public const byte GetPowerInfo = 0x62;
    public const byte GetHopping = 0x65;
    public const byte GetRegion = 0x67;
    public const byte GetProtocolConfig = 0x6B;
    public const byte GetTemperature = 0x72;
    public const byte SetAntennaPort = 0x91;
    public const byte SetProtocol = 0x93;
    public const byte SetProtocolConfig = 0x9B;

    // Extended subcommands.
    public const ushort AsyncStart = 0xAA48;
    public const ushort AsyncStop = 0xAA49;
    public const ushort ReturnLossDetect = 0xAA4A;

    // Status codes.
    public const ushort StatusOk = 0x0000;
    public const ushort StatusNoTagFound = 0x0400;

    /// <summary>Gen2 "protocol value" used by 0x93/0x9B/0x6B.</summary>
    public const byte ProtocolGen2 = 0x05;

    public const byte Gen2ParamSession = 0x00;
    public const byte Gen2ParamTarget = 0x01;
    public const byte Gen2ParamRfMode = 0x02;
    public const byte Gen2ParamQ = 0x12;

    /// <summary>Metadata requested from the reader: read count, RSSI, antenna, frequency, timestamp, phase.</summary>
    public const ushort InventoryMetadata = 0x003F;

    /// <summary>Async search flags: heartbeat on (high-byte BIT7); low byte as in the manual example (0x03).</summary>
    public const ushort AsyncSearchFlags = 0x8003;

    [Flags]
    public enum Metadata : ushort
    {
        ReadCount = 0x0001,
        Rssi = 0x0002,
        Antenna = 0x0004,
        Frequency = 0x0008,
        Timestamp = 0x0010,
        Phase = 0x0020,
        Protocol = 0x0040,
        TagData = 0x0080,
    }

    // ------------------------------------------------------------------ builders

    /// <summary><c>FF | Len | Cmd | Data | CRC16(Len..Data)</c>.</summary>
    public static byte[] Build(byte command, ReadOnlySpan<byte> data = default)
    {
        if (data.Length > 255) throw new ArgumentOutOfRangeException(nameof(data), "EX10 data field is limited to 255 bytes");
        var frame = new byte[data.Length + 5];
        frame[0] = Header;
        frame[1] = (byte)data.Length;
        frame[2] = command;
        data.CopyTo(frame.AsSpan(3));
        ushort crc = Ex10Crc.Compute(frame.AsSpan(1, data.Length + 2));
        frame[^2] = (byte)(crc >> 8);
        frame[^1] = (byte)crc;
        return frame;
    }

    /// <summary>Extended command: Cmd 0xAA, Data = "Moduletech" | Sub(2) | SubData | SubCRC | 0xBB; SubCRC = (Σ Sub..SubData) &amp; 0xFF.</summary>
    public static byte[] BuildExtended(ushort subcommand, ReadOnlySpan<byte> subData = default)
    {
        var data = new byte[Marker.Length + 2 + subData.Length + 2];
        Marker.CopyTo(data, 0);
        int p = Marker.Length;
        data[p++] = (byte)(subcommand >> 8);
        data[p++] = (byte)subcommand;
        subData.CopyTo(data.AsSpan(p));
        p += subData.Length;
        int sum = 0;
        for (int i = Marker.Length; i < p; i++) sum += data[i];
        data[p++] = (byte)sum;
        data[p] = ExtendedTerminator;
        return Build(ExtendedCommand, data);
    }

    public static byte[] SyncInventoryCommand(TimeSpan duration)
    {
        ushort ms = (ushort)Math.Clamp((int)Math.Round(duration.TotalMilliseconds), 1, ushort.MaxValue);
        return Build(SyncInventory, [0x00, 0x00, 0x00, (byte)(ms >> 8), (byte)ms]);
    }

    public static byte[] GetTagBufferCommand(ushort metadata = InventoryMetadata) =>
        Build(GetTagBuffer, [(byte)(metadata >> 8), (byte)metadata, 0x00]);

    public static byte[] AsyncStartCommand(ushort metadata = InventoryMetadata, ushort searchFlags = AsyncSearchFlags) =>
        BuildExtended(AsyncStart, [(byte)(metadata >> 8), (byte)metadata, 0x00, (byte)(searchFlags >> 8), (byte)searchFlags]);

    public static byte[] AsyncStopCommand() => BuildExtended(AsyncStop);

    /// <summary>Return-loss sweep of one antenna over the whole band. Power field is ignored by the module (0x07D0 as recommended).</summary>
    public static byte[] ReturnLossCommand(int antenna, byte band) =>
        BuildExtended(ReturnLossDetect, [0x07, 0xD0, (byte)antenna, band, 0x00]);

    /// <summary>0x91 option 2: antennas used by inventory (TX/RX pairs).</summary>
    public static byte[] SetInventoryAntennasCommand(IEnumerable<int> antennas)
    {
        var data = new List<byte> { 0x02 };
        foreach (var a in antennas) { data.Add((byte)a); data.Add((byte)a); }
        return Build(SetAntennaPort, data.ToArray());
    }

    /// <summary>0x91 option 3: read/write power per antenna in 0.01 dBm (RAM only).</summary>
    public static byte[] SetAntennaPowerCommand(IEnumerable<AntennaConfig> antennas)
    {
        var data = new List<byte> { 0x03 };
        foreach (var a in antennas)
        {
            ushort cdbm = ToCentiDbm(a.PowerDbm);
            data.Add((byte)a.Port);
            data.Add((byte)(cdbm >> 8)); data.Add((byte)cdbm);
            data.Add((byte)(cdbm >> 8)); data.Add((byte)cdbm);
        }
        return Build(SetAntennaPort, data.ToArray());
    }

    public static byte[] SetSessionCommand(int session) =>
        Build(SetProtocolConfig, [ProtocolGen2, Gen2ParamSession, (byte)session]);

    public static byte[] SetTargetCommand(Gen2Target target) => target switch
    {
        Gen2Target.A => Build(SetProtocolConfig, [ProtocolGen2, Gen2ParamTarget, 0x01, 0x00]),
        Gen2Target.B => Build(SetProtocolConfig, [ProtocolGen2, Gen2ParamTarget, 0x01, 0x01]),
        Gen2Target.AB => Build(SetProtocolConfig, [ProtocolGen2, Gen2ParamTarget, 0x00, 0x00]),
        Gen2Target.BA => Build(SetProtocolConfig, [ProtocolGen2, Gen2ParamTarget, 0x00, 0x01]),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    public static byte[] SetQCommand(int? q) => q is { } value
        ? Build(SetProtocolConfig, [ProtocolGen2, Gen2ParamQ, 0x01, (byte)value])
        : Build(SetProtocolConfig, [ProtocolGen2, Gen2ParamQ, 0x00]);

    public static byte[] SetRfModeCommand(int rfMode) =>
        Build(SetProtocolConfig, [ProtocolGen2, Gen2ParamRfMode, (byte)rfMode]);

    public static byte[] GetProtocolConfigCommand(byte parameter) =>
        Build(GetProtocolConfig, [ProtocolGen2, parameter]);

    public static ushort ToCentiDbm(double dbm) => (ushort)Math.Clamp((int)Math.Round(dbm * 100), 0, ushort.MaxValue);

    // ------------------------------------------------------------------ decoders

    public sealed record VersionInfo(byte[] Bootloader, byte[] Hardware, byte[] FirmwareDate, byte[] FirmwareVersion)
    {
        public string Firmware => string.Join('.', FirmwareVersion.Select(b => b.ToString("X2")));

        public int AntennaPorts => (Hardware[1] & 0x0F) switch
        {
            0 => 1, 1 => 2, 2 => 4, 3 => 8, 4 => 16, 5 => 32, _ => 4,
        };

        public string Chip => Hardware[0] switch
        {
            0x31 => "E710", 0x32 => "E510", 0x33 => "E310", 0x34 => "E910", _ => $"chip 0x{Hardware[0]:X2}",
        };

        public string Certification => CertificationName(Hardware[2]);

        public string HardwareDescription =>
            $"{Chip}, {AntennaPorts} ports, certification {Certification}, HW {Convert.ToHexString(Hardware)}, FW date {Convert.ToHexString(FirmwareDate)}";
    }

    public static VersionInfo ParseVersion(byte[] data)
    {
        Require(data.Length >= 16, GetVersion, data);
        return new VersionInfo(data[0..4], data[4..8], data[8..12], data[12..16]);
    }

    /// <summary>0x10: year(4) + serial(8). Returned as hex because the encoding varies between batches.</summary>
    public static string ParseSerialNumber(byte[] data)
    {
        Require(data.Length >= 12, GetSerialNumber, data);
        return Convert.ToHexString(data, 0, 12);
    }

    public static int ParseTemperature(byte[] data)
    {
        Require(data.Length >= 1, GetTemperature, data);
        return (sbyte)data[0];
    }

    public static string ParseRegion(byte[] data, out byte code)
    {
        Require(data.Length >= 1, GetRegion, data);
        code = data[0];
        return $"{RegionName(code)} (0x{code:X2})";
    }

    /// <summary>0x61 option 2: enabled inventory antennas.</summary>
    public static IReadOnlyList<int> ParseInventoryAntennas(byte[] data)
    {
        Require(data.Length >= 1 && data[0] == 0x02 && (data.Length - 1) % 2 == 0, GetAntennaConfig, data);
        var result = new List<int>();
        for (int i = 1; i + 1 < data.Length; i += 2) result.Add(data[i]);
        return result;
    }

    /// <summary>0x61 option 3: per-antenna read/write power (0.01 dBm).</summary>
    public static IReadOnlyList<AntennaPower> ParseAntennaPowers(byte[] data)
    {
        Require(data.Length >= 1 && data[0] == 0x03 && (data.Length - 1) % 5 == 0, GetAntennaConfig, data);
        var result = new List<AntennaPower>();
        for (int i = 1; i + 4 < data.Length; i += 5)
        {
            result.Add(new AntennaPower(
                data[i],
                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 1)) / 100.0,
                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 3)) / 100.0));
        }
        return result;
    }

    /// <summary>0x61 option 5: connection status of each antenna.</summary>
    public static IReadOnlyList<int> ParseConnectedAntennas(byte[] data)
    {
        Require(data.Length >= 1 && data[0] == 0x05 && (data.Length - 1) % 2 == 0, GetAntennaConfig, data);
        var result = new List<int>();
        for (int i = 1; i + 1 < data.Length; i += 2)
            if (data[i + 1] == 1) result.Add(data[i]);
        return result;
    }

    /// <summary>0x62 option 1: current, max and min power in dBm.</summary>
    public static (double Current, double Max, double Min) ParsePowerInfo(byte[] data)
    {
        Require(data.Length >= 7 && data[0] == 0x01, GetPowerInfo, data);
        return (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(1)) / 100.0,
                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(3)) / 100.0,
                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(5)) / 100.0);
    }

    /// <summary>0x6B answer: <c>05 | param | values…</c>.</summary>
    public static byte[] ParseProtocolConfig(byte[] data, byte parameter)
    {
        Require(data.Length >= 3 && data[0] == ProtocolGen2 && data[1] == parameter, GetProtocolConfig, data);
        return data[2..];
    }

    public static Gen2Target ParseTarget(byte[] values)
    {
        Require(values.Length >= 2, GetProtocolConfig, values);
        bool isStatic = values[0] == 0x01;
        return (isStatic, values[1]) switch
        {
            (true, 0) => Gen2Target.A,
            (true, _) => Gen2Target.B,
            (false, 0) => Gen2Target.AB,
            (false, _) => Gen2Target.BA,
        };
    }

    public static int? ParseQ(byte[] values)
    {
        Require(values.Length >= 1, GetProtocolConfig, values);
        if (values[0] == 0x00) return null;
        Require(values.Length >= 2, GetProtocolConfig, values);
        return values[1];
    }

    /// <summary>0x22 answer: option(1) | search flags(2) | tags found (1, or 4 when more than 255).</summary>
    public static int ParseTagsFound(byte[] data)
    {
        Require(data.Length >= 4, SyncInventory, data);
        if (data.Length >= 7) return (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(3));
        return data[3];
    }

    /// <summary>0x29 answer: metadata(2) | read option(1) | count(1) | tag blocks (EPC length in bits, 2 bytes).</summary>
    public static IReadOnlyList<TagRead> ParseTagBuffer(byte[] data, DateTimeOffset now)
    {
        Require(data.Length >= 4, GetTagBuffer, data);
        var metadata = (Metadata)BinaryPrimitives.ReadUInt16BigEndian(data);
        int count = data[3];
        int pos = 4;
        var tags = new List<TagRead>(count);
        for (int i = 0; i < count; i++)
        {
            var tag = ParseTagBlock(data, ref pos, metadata, epcLengthInBits: true, now, out _);
            if (tag is not null) tags.Add(tag);
        }
        return tags;
    }

    public enum AsyncPacketKind { Tag, Heartbeat, RoundEnd, StartAck, StopReply, Other }

    /// <summary>Classifies an 0xAA frame received during asynchronous inventory.</summary>
    public static AsyncPacketKind ClassifyAsync(Ex10Frame frame, DateTimeOffset now, out TagRead? tag)
    {
        tag = null;
        if (frame.Command != ExtendedCommand) return AsyncPacketKind.Other;
        if (frame.IsExtendedReply(AsyncStop)) return AsyncPacketKind.StopReply;
        if (frame.IsExtendedReply(AsyncStart)) return AsyncPacketKind.StartAck;
        var d = frame.Data;
        if (d.Length >= 4 && d.AsSpan(0, 4).SequenceEqual(HeartbeatMarker)) return AsyncPacketKind.Heartbeat;
        if (d.Length < 3) return AsyncPacketKind.Other;

        var metadata = (Metadata)BinaryPrimitives.ReadUInt16BigEndian(d);
        int pos = 2;
        tag = ParseTagBlock(d, ref pos, metadata, epcLengthInBits: false, now, out bool roundEnd);
        if (roundEnd) return AsyncPacketKind.RoundEnd;
        return tag is null ? AsyncPacketKind.Other : AsyncPacketKind.Tag;
    }

    /// <summary>
    /// Parses one "Tag EPC and Meta Data" block. Metadata fields come in bit order; then EPC length
    /// (0x29: 2 bytes in bits; 0xAA: 1 byte in bytes; both include PC and the trailing 2 bytes), PC, EPC,
    /// and 2 bytes that are the tag CRC (or the start phase when phase is requested).
    /// An EPC length of 5 bytes in async mode is the "polling cycle" notification.
    /// </summary>
    internal static TagRead? ParseTagBlock(byte[] d, ref int pos, Metadata metadata, bool epcLengthInBits, DateTimeOffset now, out bool roundEnd)
    {
        try
        {
            return ParseTagBlockCore(d, ref pos, metadata, epcLengthInBits, now, out roundEnd);
        }
        catch (IndexOutOfRangeException ex)
        {
            throw new RfidReaderException($"Truncated tag block in {Convert.ToHexString(d)}", inner: ex);
        }
    }

    private static TagRead? ParseTagBlockCore(byte[] d, ref int pos, Metadata metadata, bool epcLengthInBits, DateTimeOffset now, out bool roundEnd)
    {
        roundEnd = false;
        int count = 1, rssi = 0, antenna = 1;
        int? frequency = null;
        double? phase = null;

        if (metadata.HasFlag(Metadata.ReadCount)) count = d[pos++];
        if (metadata.HasFlag(Metadata.Rssi)) rssi = (sbyte)d[pos++];
        if (metadata.HasFlag(Metadata.Antenna))
        {
            antenna = d[pos++] & 0x0F;
            if (antenna == 0) antenna = 16;
        }
        if (metadata.HasFlag(Metadata.Frequency)) { frequency = (d[pos] << 16) | (d[pos + 1] << 8) | d[pos + 2]; pos += 3; }
        if (metadata.HasFlag(Metadata.Timestamp)) pos += 4;
        if (metadata.HasFlag(Metadata.Phase))
        {
            int raw = ((d[pos] & 0x0F) << 8) | d[pos + 1];
            phase = raw * 360.0 / 4096;
            pos += 2;
        }
        if (metadata.HasFlag(Metadata.Protocol)) pos += 1;
        if (metadata.HasFlag(Metadata.TagData))
        {
            int bits = (d[pos] << 8) | d[pos + 1];
            pos += 2 + (bits + 7) / 8;
        }

        int epcBlock;
        if (epcLengthInBits) { epcBlock = ((d[pos] << 8) | d[pos + 1]) / 8; pos += 2; }
        else epcBlock = d[pos++];

        if (!epcLengthInBits && epcBlock == 5)
        {
            roundEnd = true;
            pos += 5;
            return null;
        }
        if (epcBlock < 4 || pos + epcBlock > d.Length)
            throw new RfidReaderException($"Malformed tag block (EPC length {epcBlock}) in {Convert.ToHexString(d)}");

        int pc = (d[pos] << 8) | d[pos + 1];
        string epc = Convert.ToHexString(d, pos + 2, epcBlock - 4);
        pos += epcBlock;

        return new TagRead
        {
            Epc = epc,
            Antenna = antenna,
            Rssi = rssi,
            Timestamp = now,
            Pc = pc,
            Count = count,
            FrequencyKhz = frequency,
            PhaseDegrees = phase,
        };
    }

    /// <summary>AA4A answer: "Moduletech" | AA 4A | echo(5) | M × (frequency kHz (3) | return loss 0.1 dB (1)).</summary>
    public static AntennaReturnLoss ParseReturnLoss(byte[] data, int antenna)
    {
        int start = Marker.Length + 2 + 5;
        Require(data.Length >= start, ExtendedCommand, data);
        var points = new List<ReturnLossPoint>();
        for (int i = start; i + 4 <= data.Length; i += 4)
        {
            int khz = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
            points.Add(new ReturnLossPoint(khz, data[i + 3] / 10.0));
        }
        double avg = points.Count == 0 ? 0 : Math.Round(points.Average(p => p.ReturnLossDb), 1);
        return new AntennaReturnLoss(antenna, avg, points);
    }

    // ------------------------------------------------------------------ names

    public static string StatusName(ushort status) => status switch
    {
        0x0000 => "success",
        0x0100 => "invalid length",
        0x0101 => "invalid command or application not running",
        0x0102 => "incomplete command",
        0x0103 => "power too high",
        0x0104 => "invalid frequency",
        0x0105 => "invalid parameter",
        0x0106 => "power too low",
        0x0109 => "not implemented",
        0x010A => "invalid baud rate",
        0x0400 => "no tag found",
        0x0401 => "protocol error",
        0x0402 => "invalid protocol",
        0x0403 => "write locked",
        0x0404 => "no data read",
        0x0405 => "AFE not on / module in bootloader",
        0x0406 => "write failed",
        0x0409 => "invalid address",
        0x040A => "general tag error",
        0x040B => "data too large",
        0x0420 => "Gen2 other error",
        0x0423 => "Gen2 overrun / bad PC",
        0x0424 => "Gen2 memory locked",
        0x042B => "Gen2 insufficient power on tag",
        0x042F => "Gen2 non-specific error",
        0x0430 => "Gen2 unknown error",
        0x0500 => "invalid frequency (AHAL)",
        0x0501 => "channel occupied",
        0x0502 => "transmitter on",
        0x0503 => "antenna not connected",
        0x0504 => "temperature exceeded",
        0x0505 => "high return loss",
        0x0600 => "tag buffer: not enough tags",
        0x0601 => "tag buffer full",
        0x0602 => "tag buffer: repeated tag",
        0x0603 => "tag buffer: invalid index",
        0x7F00 => "system error",
        0xAA49 => "asynchronous inventory was interrupted",
        0xAA59 => "EX asynchronous inventory was interrupted",
        _ => "unknown status",
    };

    public static string RegionName(byte code) => code switch
    {
        0x01 => "NA", 0x02 => "EU", 0x03 => "KR", 0x04 => "IN", 0x05 => "JP", 0x06 => "PRC", 0x07 => "EU2",
        0x08 => "EU3/CE_LOW", 0x09 => "KR", 0x0A => "PRC2", 0x0B => "JP", 0x0C => "CE_HIGH", 0x0D => "HK",
        0x0E => "TW", 0x0F => "MY", 0x10 => "ZA", 0x11 => "BR", 0x12 => "TH", 0x13 => "SG", 0x14 => "AU",
        0x16 => "UY", 0x17 => "VN", 0x18 => "IL", 0x19 => "PH", 0x1A => "ID", 0x1B => "NZ", 0x1C => "PE",
        0x1D => "RU", 0x1E => "CE_LOW_HIGH", 0xFF => "OPEN",
        _ => "unknown",
    };

    public static string CertificationName(byte code) => code switch
    {
        0x00 => "CHINA", 0x01 => "FCC", 0x02 => "JAPAN", 0x03 => "CE_LOW", 0x04 => "KOREA", 0x05 => "CE_HIGH",
        0x06 => "HK", 0x07 => "TAIWAN", 0x08 => "MALAYSIA", 0x09 => "SOUTH_AFRICA", 0x0A => "BRAZIL",
        0x0B => "THAILAND", 0x0C => "SINGAPORE", 0x0D => "AUSTRALIA", 0x0E => "INDIA", 0x0F => "URUGUAY",
        0x10 => "VIETNAM", 0x11 => "ISRAEL", 0x12 => "PHILIPPINES", 0x13 => "INDONESIA", 0x14 => "NEW_ZEALAND",
        0x15 => "PERU", 0x16 => "RUSSIA", 0x17 => "CE_LOW_HIGH",
        _ => $"0x{code:X2}",
    };

    private static void Require(bool condition, byte command, byte[] data)
    {
        if (!condition)
            throw new RfidReaderException($"Unexpected answer to command 0x{command:X2}: {Convert.ToHexString(data)}");
    }
}
