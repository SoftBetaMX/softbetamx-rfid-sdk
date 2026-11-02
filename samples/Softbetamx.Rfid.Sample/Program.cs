// Softbetamx.Rfid sample / live test.
//
//   dotnet run --project samples/Softbetamx.Rfid.Sample -- simulated
//   dotnet run --project samples/Softbetamx.Rfid.Sample -- focus 192.168.1.100 [port] [continuousSeconds] [powerDbm]
//   dotnet run --project samples/Softbetamx.Rfid.Sample -- in805 192.168.250.10 [port]
//   dotnet run --project samples/Softbetamx.Rfid.Sample -- focus COM3
//
// Steps: connect, info, inventory once on antennas 1+2, continuous inventory (reads/s), return loss of
// antenna 1, then restores the antenna and Gen2 settings read at the start. Every change is temporary.

using System.Collections.Concurrent;
using System.Diagnostics;
using Softbetamx.Rfid;

var model = args.Length > 0 ? Enum.Parse<ReaderModel>(args[0], ignoreCase: true) : ReaderModel.Simulated;
var options = new ReaderOptions { Model = model };
if (model != ReaderModel.Simulated)
{
    if (args.Length < 2) { Console.WriteLine("Usage: <focus|in805|simulated> <host|COMx> [port] [continuousSeconds]"); return 1; }
    if (args[1].StartsWith("COM", StringComparison.OrdinalIgnoreCase) || args[1].StartsWith("/dev/")) options.SerialPort = args[1];
    else options.Host = args[1];
    if (args.Length > 2 && int.TryParse(args[2], out var port) && port > 0) options.Port = port;
}
var continuousSeconds = args.Length > 3 ? int.Parse(args[3]) : 10;
var powerDbm = args.Length > 4 ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 30;

await using var reader = RfidReader.Create(options);
reader.StateChanged += (_, e) => Console.WriteLine($"[state] {e.OldState} -> {e.NewState} {e.Reason}");
reader.Error += (_, e) => Console.WriteLine($"[error{(e.IsFatal ? ", fatal" : "")}] {e.Message}");

var sw = Stopwatch.StartNew();
await reader.ConnectAsync();
Console.WriteLine($"Connected in {sw.ElapsedMilliseconds} ms");

var info = await reader.GetInfoAsync();
Console.WriteLine($"Firmware  : {info.Firmware}");
Console.WriteLine($"Hardware  : {info.Hardware}");
Console.WriteLine($"Serial    : {info.SerialNumber}");
Console.WriteLine($"Temp      : {info.TemperatureC} C");
Console.WriteLine($"Region    : {info.Region}");
Console.WriteLine($"Power     : {string.Join(", ", info.Powers.Select(p => $"ant{p.Port}={p.ReadPowerDbm} dBm"))} (range {info.MinPowerDbm}-{info.MaxPowerDbm})");
Console.WriteLine($"Connected : {(info.ConnectedAntennas is null ? "n/a" : string.Join(",", info.ConnectedAntennas))}");
var caps = reader.Capabilities;
Console.WriteLine($"Caps      : ports={caps.AntennaPorts} buzzer={caps.Buzzer} relays={caps.Relays} inputs={caps.Inputs} returnLoss={caps.ReturnLoss} staticQ={caps.Gen2StaticQ}");

// Save what we will touch.
var originalAntennas = await reader.GetAntennasAsync();
var originalGen2 = await reader.GetGen2Async();
Console.WriteLine($"Original antennas: {Describe(originalAntennas)}");
Console.WriteLine($"Original Gen2    : {originalGen2}");

try
{
    // ---- inventory once, antennas 1 and 2
    await reader.SetAntennasAsync([new(1, PowerDbm: powerDbm), new(2, PowerDbm: powerDbm)]);
    sw.Restart();
    var tags = await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(1000));
    Console.WriteLine($"\nInventoryOnce 1000 ms (ant 1+2 @ {powerDbm} dBm): {tags.Count} entries, {tags.Sum(t => t.Count)} reads, {sw.ElapsedMilliseconds} ms");
    foreach (var t in tags.OrderBy(t => t.Antenna).ThenBy(t => t.Epc))
        Console.WriteLine($"  ant{t.Antenna} {t.Epc} rssi={t.Rssi} x{t.Count} f={t.FrequencyKhz} kHz");

    // ---- continuous
    var reads = new ConcurrentQueue<TagRead>();
    reader.TagRead += (_, t) => reads.Enqueue(t);
    sw.Restart();
    await reader.StartContinuousAsync();
    for (int s = 1; s <= continuousSeconds; s++)
    {
        await Task.Delay(1000);
        Console.WriteLine($"  t={s,2}s reads={reads.Count}");
    }
    await reader.StopAsync();
    var elapsed = sw.Elapsed.TotalSeconds;
    var all = reads.ToArray();
    Console.WriteLine($"\nContinuous {elapsed:0.0} s: {all.Length} reads ({all.Length / elapsed:0.0} reads/s), {all.Select(r => r.Epc).Distinct().Count()} unique EPCs");
    foreach (var g in all.GroupBy(r => r.Antenna).OrderBy(g => g.Key))
        Console.WriteLine($"  ant{g.Key}: {g.Count()} reads, {g.Select(r => r.Epc).Distinct().Count()} tags, avg rssi {g.Average(r => r.Rssi):0.0}");

    // ---- return loss of antenna 1
    if (caps.ReturnLoss)
    {
        sw.Restart();
        var rl = (await reader.GetReturnLossAsync([1]))[0];
        Console.WriteLine($"\nReturn loss ant1: {rl.ReturnLossDb:0.0} dB (VSWR {rl.Vswr:0.00}), {rl.Points.Count} points, " +
                          $"min {rl.Points.Min(p => p.ReturnLossDb):0.0} / max {rl.Points.Max(p => p.ReturnLossDb):0.0} dB, {sw.ElapsedMilliseconds} ms");
    }

    // ---- read-only peripheral check
    if (caps.Inputs > 0)
        Console.WriteLine($"Inputs: {string.Join(",", (await reader.GetInputsAsync()).Select(b => b ? 1 : 0))}");
}
finally
{
    if (reader.State == ReaderConnectionState.Connected)
    {
        await reader.StopAsync();
        await reader.SetAntennasAsync(originalAntennas);
        await reader.SetGen2Async(originalGen2 with { RfMode = caps.Gen2RfMode ? originalGen2.RfMode : null });
        var nowAnt = await reader.GetAntennasAsync();
        var nowGen2 = await reader.GetGen2Async();
        Console.WriteLine($"\nRestored antennas: {Describe(nowAnt)}");
        Console.WriteLine($"Restored Gen2    : {nowGen2}");
        Console.WriteLine($"Match original   : {nowAnt.SequenceEqual(originalAntennas) && nowGen2 == originalGen2}");
    }
}
return 0;

static string Describe(IEnumerable<AntennaConfig> antennas) =>
    string.Join(", ", antennas.Select(a => $"{a.Port}:{a.PowerDbm}dBm{(a.Enabled ? "*" : "")}"));
