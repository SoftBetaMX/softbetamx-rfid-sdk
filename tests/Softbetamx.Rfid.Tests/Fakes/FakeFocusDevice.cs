using System.Text;
using Softbetamx.Rfid.Drivers.Focus;
using Softbetamx.Rfid.Tests.Focus;

namespace Softbetamx.Rfid.Tests.Fakes;

/// <summary>Simulated Focus module (EX10 protocol) answering with frames captured from the real device.</summary>
internal sealed class FakeFocusDevice : FakeDeviceTransport
{
    private readonly object _lock = new();
    private CancellationTokenSource? _async;

    public List<int> InventoryAntennas { get; } = [1, 2];
    public Dictionary<int, ushort> PowerCentiDbm { get; } = new() { [1] = 3300, [2] = 3300, [3] = 3300, [4] = 3300 };
    public byte[] Session = [0x00];
    public byte[] Target = [0x01, 0x00];
    public byte[] RfMode = [0x6B];
    public byte[] Q = [0x00];
    public bool AsyncRunning => _async is not null;
    public bool SendHeartbeats { get; set; } = true;
    public string? LastIoSet;

    /// <summary>Answer 0x22 with "no tag found" (0x0400).</summary>
    public bool NoTags { get; set; }

    protected override void OnClose() => StopAsyncLoop();

    protected override void OnHostBytes(byte[] bytes)
    {
        var ascii = Encoding.ASCII.GetString(bytes);
        if (ascii == "IOGET") { Push(Hex.Parse(FocusCaptures.RxIoGet)); return; }
        if (ascii.StartsWith("IOSET")) { LastIoSet = Convert.ToHexString(bytes, 5, bytes.Length - 5); Push(Encoding.ASCII.GetBytes("IOSETOK")); return; }

        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(bytes.Length, bytes[1] + 5);
        Assert.Equal(Ex10Crc.Compute(bytes.AsSpan(1, bytes.Length - 3)), (ushort)((bytes[^2] << 8) | bytes[^1]));
        byte cmd = bytes[2];
        var d = bytes[3..^2];

        // Real module: any command during async inventory stops it and is answered with status 0xAA49.
        if (AsyncRunning && !(cmd == 0xAA && d[11] == 0x49))
        {
            StopAsyncLoop();
            Push(Reply(cmd, 0xAA49, []));
            return;
        }

        switch (cmd)
        {
            case 0x03: Push(Hex.Parse(FocusCaptures.RxVersion)); break;
            case 0x04: Push(Reply(0x04, 0, Body(FocusCaptures.RxVersion))); break;
            case 0x10: Push(Hex.Parse(FocusCaptures.RxSerial)); break;
            case 0x72: Push(Hex.Parse(FocusCaptures.RxTemperature)); break;
            case 0x67: Push(Hex.Parse(FocusCaptures.RxRegion)); break;
            case 0x93: Push(Reply(0x93, 0, [])); break;
            case 0x2A: Push(Reply(0x2A, 0, [])); break;
            case 0x61: Push(GetAntennas(d[0])); break;
            case 0x62: Push(Hex.Parse(FocusCaptures.RxPowerInfo)); break;
            case 0x91: SetAntennas(d); Push(Reply(0x91, 0, [])); break;
            case 0x6B: Push(Reply(0x6B, 0, [0x05, d[1], .. Gen2(d[1])])); break;
            case 0x9B: SetGen2(d); Push(Reply(0x9B, 0, [])); break;
            case 0x22: Push(NoTags ? Reply(0x22, 0x0400, []) : Hex.Parse(FocusCaptures.RxSyncInventory4)); break;
            case 0x29: Push(Hex.Parse(FocusCaptures.RxTagBuffer4)); break;
            case 0xAA: Extended(d); break;
            default: Push(Reply(cmd, 0x0101, [])); break;
        }
    }

    private void Extended(byte[] d)
    {
        Assert.Equal("Moduletech", Encoding.ASCII.GetString(d, 0, 10));
        Assert.Equal(0xBB, d[^1]);
        ushort sub = (ushort)((d[10] << 8) | d[11]);
        switch (sub)
        {
            case 0xAA49:
                StopAsyncLoop();
                Push(Hex.Parse(FocusCaptures.RxStop));
                break;
            case 0xAA48:
                Push(Hex.Parse(FocusCaptures.RxAsyncAck));
                StartAsyncLoop();
                break;
            case 0xAA4A:
            {
                // Echo the antenna like the real module, reuse the captured sweep.
                var data = Body(FocusCaptures.RxReturnLossAnt1);
                data[14] = d[14];
                Push(Reply(0xAA, 0, data));
                break;
            }
            default:
                Push(Reply(0xAA, 0x0101, []));
                break;
        }
    }

    private void StartAsyncLoop()
    {
        var cts = new CancellationTokenSource();
        lock (_lock) _async = cts;
        _ = Task.Run(async () =>
        {
            if (SendHeartbeats) Push(Hex.Parse(FocusCaptures.RxHeartbeatA003));
            while (!cts.IsCancellationRequested)
            {
                Push(Hex.Parse(FocusCaptures.RxAsyncTag1));
                Push(Hex.Parse(FocusCaptures.RxAsyncTag2));
                Push(Hex.Parse(FocusCaptures.RxAsyncRoundEnd));
                try { await Task.Delay(20, cts.Token); } catch { }
            }
        });
    }

    private void StopAsyncLoop()
    {
        lock (_lock)
        {
            _async?.Cancel();
            _async = null;
        }
    }

    private byte[] GetAntennas(byte option)
    {
        var data = new List<byte> { option };
        switch (option)
        {
            case 0x02:
                foreach (var a in InventoryAntennas) { data.Add((byte)a); data.Add((byte)a); }
                break;
            case 0x03:
                foreach (var (port, p) in PowerCentiDbm.OrderBy(k => k.Key))
                    data.AddRange([(byte)port, (byte)(p >> 8), (byte)p, (byte)(p >> 8), (byte)p]);
                break;
            case 0x05:
                return Hex.Parse(FocusCaptures.RxConnected);
        }
        return Reply(0x61, 0, data.ToArray());
    }

    private void SetAntennas(byte[] d)
    {
        if (d[0] == 0x02)
        {
            InventoryAntennas.Clear();
            for (int i = 1; i + 1 < d.Length; i += 2) InventoryAntennas.Add(d[i]);
        }
        else if (d[0] == 0x03)
        {
            for (int i = 1; i + 4 < d.Length; i += 5) PowerCentiDbm[d[i]] = (ushort)((d[i + 1] << 8) | d[i + 2]);
        }
    }

    private byte[] Gen2(byte parameter) => parameter switch
    {
        0x00 => Session,
        0x01 => Target,
        0x02 => RfMode,
        0x12 => Q,
        _ => [],
    };

    private void SetGen2(byte[] d)
    {
        var values = d[2..];
        switch (d[1])
        {
            case 0x00: Session = values; break;
            case 0x01: Target = values; break;
            case 0x02: RfMode = values; break;
            case 0x12: Q = values; break;
        }
    }

    /// <summary>Data field of a captured RX frame.</summary>
    private static byte[] Body(string hex)
    {
        var f = Hex.Parse(hex);
        return f[5..^2];
    }

    public static byte[] Reply(byte cmd, ushort status, byte[] data)
    {
        var f = new byte[data.Length + 7];
        f[0] = 0xFF;
        f[1] = (byte)data.Length;
        f[2] = cmd;
        f[3] = (byte)(status >> 8);
        f[4] = (byte)status;
        data.CopyTo(f, 5);
        ushort crc = Ex10Crc.Compute(f.AsSpan(1, f.Length - 3));
        f[^2] = (byte)(crc >> 8);
        f[^1] = (byte)crc;
        return f;
    }
}
