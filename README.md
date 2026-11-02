# Softbetamx.Rfid

One async .NET API for UHF RFID readers. Pick the model, open the link, read tags; the protocol details stay inside the library.

| Model | Hardware | Protocol | Default link |
|---|---|---|---|
| `ReaderModel.Focus` | Focus reader, ST-M714 module (Impinj E710) | EX10 "Moduletech" (`FF Len Cmd … CRC16`) | TCP `:8080` or serial |
| `ReaderModel.In805` | IN805 4-port reader (Impinj E710) | 0xA0 ("UHF RFID Reader Serial Interface V4.2.1") | TCP `:4001` or serial |
| `ReaderModel.Simulated` | none (in memory) | — | — |

Targets **net8.0** and **net10.0**. MIT license.

## Quick start

```csharp
using Softbetamx.Rfid;

await using var reader = RfidReader.Create(new ReaderOptions
{
    Model = ReaderModel.Focus,           // or ReaderModel.In805 / ReaderModel.Simulated
    Host = "192.168.1.100", Port = 8080  // or SerialPort = "COM3", BaudRate = 115200
});
await reader.ConnectAsync();

var info = await reader.GetInfoAsync();  // firmware, hardware, temperature, region, power per antenna
await reader.SetAntennasAsync([new(1, PowerDbm: 30), new(2, PowerDbm: 27)]);
await reader.SetGen2Async(new Gen2Settings(Session: 0, Target: Gen2Target.A, Q: null));

// Timed inventory: one entry per EPC and antenna, with read count and strongest RSSI.
var tags = await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(500));

// Continuous inventory: one event per read.
reader.TagRead += (_, tag) => Console.WriteLine($"{tag.Epc} ant{tag.Antenna} {tag.Rssi} dBm");
await reader.StartContinuousAsync();
await Task.Delay(10_000);
await reader.StopAsync();

if (reader.Capabilities.Buzzer) await reader.SetBuzzerAsync(BuzzerMode.PerRound);
var returnLoss = await reader.GetReturnLossAsync([1]);
```

## Design rules

- **Temporary settings.** Power, antennas and Gen2 parameters go to the reader RAM (Focus) or are re-applied by the driver before every round (IN805). Nothing is written to flash. The one exception is `SetBuzzerAsync` on the IN805: its firmware always stores the buzzer mode in flash.
- **Capabilities.** `reader.Capabilities` says what the model can do (`Buzzer`, `Relays`, `Inputs`, `ReturnLoss`, `Gen2StaticQ`, power range, ports…). Calling something the model cannot do throws `NotSupportedException` with a clear message.
- **One command at a time.** All calls are serialized per connection, so the object is safe to share between threads. While continuous inventory runs, only `StopAsync`/`DisconnectAsync` are accepted (other calls throw `InvalidOperationException`).
- **Events.** `TagRead` (continuous reads), `StateChanged` (`Disconnected → Connecting → Connected → Faulted`), `Error` (lost link, missing antenna, reader-side stop, handler exceptions). After `Faulted`, call `ConnectAsync` again.
- **Missing antennas.** `reader.MissingAntennas` lists the enabled ports the reader currently reports as not connected (IN805: error 0x22 in the last round on that port; the `Error` event fires once per disconnection). Focus does not report it during inventory: use `GetInfoAsync().ConnectedAntennas`.
- **Transports.** `TcpTransport`, `SerialTransport` (System.IO.Ports), or your own `IRfidTransport` through `ReaderOptions.Transport` (useful for tests).

## Differences between models

| | Focus (EX10) | IN805 (0xA0) |
|---|---|---|
| Inventory once | Reader-timed sync inventory (`0x22`) + tag buffer (`0x29`) | Driver repeats `0x8B` rounds per antenna until the time is up |
| Continuous | Reader-driven async inventory (`AA48`), heartbeat every ~15 s, link considered lost after 40 s of silence | Driver-driven back-to-back rounds |
| Read metadata | count, RSSI, antenna, frequency, phase, PC | RSSI, antenna, PC |
| Gen2 | session, static/dynamic target, static/dynamic Q, RF mode | session, target A/B (Q stays dynamic) |
| Power | 0.01 dBm units (1 dB real resolution), range read from the module | integer dBm, 0–33 |
| Return loss | full-band sweep (50 points in FCC/NA, ~2 s per antenna) | single value at 915 MHz |
| Buzzer | no | yes (stored in flash by the reader) |
| Relays / inputs | 4 / 4 via the communication board (`IOSET`/`IOGET`) | 2 (GPIO3/4) / 2 (GPIO1/2) |

On connect, the Focus driver stops any leftover async inventory, boots the module application and selects Gen2. If the tag buffer was configured to ignore the antenna (other software does this), it switches it back to "unique by antenna" in RAM and restores the previous value on `DisconnectAsync`.

## Simulated reader

`ReaderModel.Simulated` needs no hardware. It reports five sample EPCs (or `ReaderOptions.SimulatedTags`), honours enabled antennas and power, and supports every operation. Good for demos, UI work and unit tests.

## Sample

`samples/Softbetamx.Rfid.Sample` connects, prints info, runs an inventory on antennas 1 and 2, a 10 s continuous inventory with reads/s, a return-loss sweep of antenna 1, and restores the original antenna and Gen2 settings:

```
dotnet run --project samples/Softbetamx.Rfid.Sample -- simulated
dotnet run --project samples/Softbetamx.Rfid.Sample -- focus 192.168.1.100 8080 10 30
dotnet run --project samples/Softbetamx.Rfid.Sample -- in805 192.168.250.10 4001
```

## Build

```
dotnet build
dotnet test
dotnet pack src/Softbetamx.Rfid -c Release   # → artifacts/Softbetamx.Rfid.0.1.0.nupkg
```

The protocol code was written from the vendors' protocol manuals and from frames captured on real devices; the unit tests replay those frames.
