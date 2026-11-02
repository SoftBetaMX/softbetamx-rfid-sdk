using Softbetamx.Rfid.Drivers.In805;

namespace Softbetamx.Rfid.Tests.Fakes;

/// <summary>Simulated IN805 (0xA0 protocol), answers like the real device (frames from pruebas-lector).</summary>
internal sealed class FakeIn805Device : FakeDeviceTransport
{
    private readonly A0FrameParser _parser = new();
    private int _workAntenna; // 0-based

    /// <summary>Tags per antenna (1..4): EPC hex and raw RSSI byte.</summary>
    public Dictionary<int, List<(string Epc, byte Rssi)>> Tags { get; } = new();

    public HashSet<int> MissingAntennas { get; } = [];

    public byte[] PowerResponse = [0x21];

    protected override void OnHostBytes(byte[] bytes)
    {
        foreach (var f in _parser.Append(bytes))
        {
            var d = f.Data;
            Push(f.Command switch
            {
                0x72 => Reply(0x72, 0x81, 0x01),
                0x7B => Reply(0x7B, 0x01, 37),
                0x79 => Reply(0x79, 0x01, 0x07, 0x3B),
                0x77 => Reply(0x77, PowerResponse),
                0x74 => SetAntenna(d[0]),
                0x66 => Reply(0x66, d[0] > 33 ? (byte)0x48 : (byte)0x10),
                0x7E => Reply(0x7E, (byte)(30 + _workAntenna)),
                0x7A => Reply(0x7A, d[0] > 2 ? (byte)0x4B : (byte)0x10),
                0x60 => Reply(0x60, 0x00, 0x01),
                0x61 => Reply(0x61, d[0] is 3 or 4 ? (byte)0x10 : (byte)0x41),
                0x8B => Inventory(),
                _ => Reply(f.Command, 0x11),
            });
        }
    }

    private byte[] SetAntenna(byte ant)
    {
        _workAntenna = ant;
        return Reply(0x74, 0x10);
    }

    private byte[] Inventory()
    {
        int antenna = _workAntenna + 1;
        if (MissingAntennas.Contains(antenna)) return Reply(0x8B, 0x22);

        var output = new List<byte>();
        output.AddRange(Reply(0x7B, 0x01, 37)); // a frame of another command in the middle: must be ignored
        var tags = Tags.GetValueOrDefault(antenna) ?? [];
        foreach (var (epc, rssi) in tags)
        {
            var epcBytes = Convert.FromHexString(epc);
            int pc = (epcBytes.Length / 2) << 11;
            var data = new List<byte> { (byte)((7 << 2) | _workAntenna), (byte)(pc >> 8), (byte)pc };
            data.AddRange(epcBytes);
            data.Add(rssi);
            output.AddRange(A0Frame.Build(0x8B, data.ToArray(), 0x01));
        }
        int total = tags.Count;
        output.AddRange(Reply(0x8B, (byte)_workAntenna, 0x00, 0x60, 0, 0, (byte)(total >> 8), (byte)total));
        return output.ToArray();
    }

    private static byte[] Reply(byte cmd, params byte[] data) => A0Frame.Build(cmd, data, 0x01);
}
