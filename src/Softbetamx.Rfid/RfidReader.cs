using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Softbetamx.Rfid.Drivers.Focus;
using Softbetamx.Rfid.Drivers.In805;
using Softbetamx.Rfid.Drivers.Simulated;
using Softbetamx.Rfid.Internal;

namespace Softbetamx.Rfid;

/// <summary>
/// A UHF RFID reader. One API for every <see cref="ReaderModel"/>.
/// </summary>
/// <remarks>
/// <para>Thread-safe: operations are serialized (one command at a time per connection).</para>
/// <para>Every setting (power, antennas, Gen2) is temporary: it lives in the reader RAM or in this object and is
/// lost when the reader restarts. Nothing is written to the reader flash, except where a method says so.</para>
/// <para>While continuous inventory runs, only <see cref="StopAsync"/>, <see cref="DisconnectAsync"/> and the
/// properties are allowed; other operations throw <see cref="InvalidOperationException"/>.</para>
/// </remarks>
/// <example>
/// <code>
/// await using var reader = RfidReader.Create(new ReaderOptions { Model = ReaderModel.Focus, Host = "192.168.1.100" });
/// await reader.ConnectAsync();
/// await reader.SetAntennasAsync([new(1, PowerDbm: 30), new(2, PowerDbm: 27)]);
/// var tags = await reader.InventoryOnceAsync(TimeSpan.FromMilliseconds(500));
/// </code>
/// </example>
public sealed class RfidReader : IAsyncDisposable
{
    private readonly IReaderDriver _driver;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly ILogger _logger;
    private ReaderConnectionState _state = ReaderConnectionState.Disconnected;
    private volatile bool _continuous;
    private bool _disposed;

    private RfidReader(ReaderOptions options, Func<IDriverHost, IReaderDriver> driverFactory)
    {
        Options = options;
        _logger = options.Logger ?? NullLogger.Instance;
        _driver = driverFactory(new Host(this));
    }

    /// <summary>Creates a reader for <see cref="ReaderOptions.Model"/>. Nothing is opened until <see cref="ConnectAsync"/>.</summary>
    /// <exception cref="ArgumentException">The options are incomplete (no Host, SerialPort or Transport for a hardware model).</exception>
    public static RfidReader Create(ReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Model != ReaderModel.Simulated && options.Transport is null
            && string.IsNullOrWhiteSpace(options.Host) && string.IsNullOrWhiteSpace(options.SerialPort))
            throw new ArgumentException($"ReaderOptions for {options.Model} need Host, SerialPort or Transport.", nameof(options));

        return options.Model switch
        {
            ReaderModel.Focus => new RfidReader(options, h => new FocusDriver(options, h)),
            ReaderModel.In805 => new RfidReader(options, h => new In805Driver(options, h)),
            ReaderModel.Simulated => new RfidReader(options, h => new SimulatedDriver(options, h)),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Model, "Unknown reader model"),
        };
    }

    /// <summary>Options this reader was created with.</summary>
    public ReaderOptions Options { get; }

    /// <summary>Reader model.</summary>
    public ReaderModel Model => Options.Model;

    /// <summary>What this model can do (refined with device data after <see cref="ConnectAsync"/>).</summary>
    public ReaderCapabilities Capabilities => _driver.Capabilities;

    /// <summary>Current connection state.</summary>
    public ReaderConnectionState State
    {
        get { lock (_stateLock) return _state; }
    }

    /// <summary><c>true</c> while continuous inventory is running.</summary>
    public bool IsContinuousRunning => _continuous;

    /// <summary>
    /// Raised for every tag read during continuous inventory (<see cref="StartContinuousAsync"/>), on a background thread.
    /// Keep handlers fast; exceptions thrown by handlers are reported through <see cref="Error"/>.
    /// </summary>
    public event EventHandler<TagRead>? TagRead;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event EventHandler<ReaderStateChangedEventArgs>? StateChanged;

    /// <summary>Raised on asynchronous problems: lost link (fatal), antenna missing, continuous inventory stopped by the reader, handler errors.</summary>
    public event EventHandler<ReaderErrorEventArgs>? Error;

    // ------------------------------------------------------------------ connection

    /// <summary>Opens the link and initializes the reader. Can be called again to recover from <see cref="ReaderConnectionState.Faulted"/>.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _continuous = false;
            SetState(ReaderConnectionState.Connecting, null);
            try
            {
                await _driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { await _driver.DisconnectAsync().ConfigureAwait(false); } catch { }
                SetState(ex is OperationCanceledException ? ReaderConnectionState.Disconnected : ReaderConnectionState.Faulted, ex.Message);
                throw;
            }
            SetState(ReaderConnectionState.Connected, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops continuous inventory if running and closes the link. Idempotent.</summary>
    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_continuous && State == ReaderConnectionState.Connected)
            {
                try { await _driver.StopContinuousAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Stop before disconnect failed"); }
            }
            _continuous = false;
            await _driver.DisconnectAsync().ConfigureAwait(false);
            SetState(ReaderConnectionState.Disconnected, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await DisconnectAsync().ConfigureAwait(false);
        await _driver.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
    }

    // ------------------------------------------------------------------ info / configuration

    /// <summary>Firmware, hardware, serial number, temperature, region and power of the reader.</summary>
    public Task<ReaderInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
        RunAsync(ct => _driver.GetInfoAsync(ct), cancellationToken);

    /// <summary>Current configuration of every antenna port (power and whether it takes part in inventories).</summary>
    public Task<IReadOnlyList<AntennaConfig>> GetAntennasAsync(CancellationToken cancellationToken = default) =>
        RunAsync(ct => _driver.GetAntennasAsync(ct), cancellationToken);

    /// <summary>
    /// Sets which antennas take part in inventories and their power. Ports not listed are disabled
    /// (their power is left as it was). Temporary: never saved to the reader flash.
    /// </summary>
    /// <exception cref="ArgumentException">Empty list, port out of range, duplicated port, power out of range, or no port enabled.</exception>
    public Task SetAntennasAsync(IEnumerable<AntennaConfig> antennas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(antennas);
        var list = antennas.ToArray();
        var caps = Capabilities;
        if (list.Length == 0) throw new ArgumentException("At least one antenna is required.", nameof(antennas));
        if (list.Select(a => a.Port).Distinct().Count() != list.Length) throw new ArgumentException("Duplicated antenna port.", nameof(antennas));
        foreach (var a in list)
        {
            if (a.Port < 1 || a.Port > caps.AntennaPorts)
                throw new ArgumentException($"Antenna port {a.Port} is out of range 1..{caps.AntennaPorts} for {Model}.", nameof(antennas));
            if (double.IsNaN(a.PowerDbm) || a.PowerDbm < caps.MinPowerDbm || a.PowerDbm > caps.MaxPowerDbm)
                throw new ArgumentException($"Power {a.PowerDbm} dBm on antenna {a.Port} is out of range {caps.MinPowerDbm}..{caps.MaxPowerDbm} dBm for {Model}.", nameof(antennas));
        }
        if (!list.Any(a => a.Enabled)) throw new ArgumentException("At least one antenna must be enabled.", nameof(antennas));
        return RunAsync(ct => _driver.SetAntennasAsync(list, ct), cancellationToken);
    }

    /// <summary>Current Gen2 session, target, Q and RF mode.</summary>
    public Task<Gen2Settings> GetGen2Async(CancellationToken cancellationToken = default) =>
        RunAsync(ct => _driver.GetGen2Async(ct), cancellationToken);

    /// <summary>Sets Gen2 session/target/Q (and RF mode if not null). Temporary: never saved to the reader flash.</summary>
    /// <exception cref="NotSupportedException">The model does not support a requested option (dynamic target, static Q, RF mode).</exception>
    public Task SetGen2Async(Gen2Settings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var caps = Capabilities;
        if (settings.Session is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(settings), "Session must be 0..3.");
        if (settings.Q is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(settings), "Q must be 0..15 (or null for dynamic).");
        if (settings.RfMode is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(settings), "RfMode must be 0..255.");
        if (settings.Target is Gen2Target.AB or Gen2Target.BA && !caps.Gen2DynamicTarget) throw Unsupported("dynamic Gen2 targets (AB/BA)");
        if (settings.Q is not null && !caps.Gen2StaticQ) throw Unsupported("static Gen2 Q");
        if (settings.RfMode is not null && !caps.Gen2RfMode) throw Unsupported("setting the RF mode");
        return RunAsync(ct => _driver.SetGen2Async(settings, ct), cancellationToken);
    }

    // ------------------------------------------------------------------ inventory

    /// <summary>
    /// Runs one timed inventory on the enabled antennas and returns one entry per EPC and antenna
    /// (<see cref="TagRead.Count"/> = times read, <see cref="TagRead.Rssi"/> = strongest). Does not raise <see cref="TagRead"/>.
    /// </summary>
    /// <param name="duration">Inventory time, 1 ms .. 65.5 s.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<IReadOnlyList<TagRead>> InventoryOnceAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be between 1 ms and 65535 ms.");
        return RunAsync(ct => _driver.InventoryOnceAsync(duration, ct), cancellationToken);
    }

    /// <summary>
    /// Starts continuous inventory; every read raises <see cref="TagRead"/> until <see cref="StopAsync"/>.
    /// Focus: reader-driven asynchronous inventory with heartbeat. IN805: driver-driven back-to-back rounds.
    /// </summary>
    public async Task StartContinuousAsync(CancellationToken cancellationToken = default)
    {
        if (!Capabilities.ContinuousInventory) throw Unsupported("continuous inventory");
        await RunAsync(async ct =>
        {
            await _driver.StartContinuousAsync(ct).ConfigureAwait(false);
            _continuous = true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops continuous inventory. Does nothing if it is not running.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_continuous) return;
            _continuous = false;
            if (State == ReaderConnectionState.Connected)
                await _driver.StopContinuousAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ peripherals / diagnostics

    /// <summary>
    /// Sets the buzzer behaviour. Requires <see cref="ReaderCapabilities.Buzzer"/>.
    /// IN805: the reader firmware stores this setting in its flash (there is no temporary variant),
    /// so call it only when the value really changes.
    /// </summary>
    /// <exception cref="NotSupportedException">The model has no buzzer (e.g. Focus).</exception>
    public Task SetBuzzerAsync(BuzzerMode mode, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.Buzzer) throw Unsupported("a buzzer");
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return RunAsync(ct => _driver.SetBuzzerAsync(mode, ct), cancellationToken);
    }

    /// <summary>
    /// Measures the return loss of the given antennas (default: every port). Focus sweeps the whole band
    /// (about 2-25 s per antenna); IN805 measures at 915 MHz.
    /// </summary>
    public Task<IReadOnlyList<AntennaReturnLoss>> GetReturnLossAsync(IEnumerable<int>? antennas = null, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.ReturnLoss) throw Unsupported("return-loss measurement");
        var ports = (antennas ?? Enumerable.Range(1, Capabilities.AntennaPorts)).ToArray();
        foreach (var p in ports)
            if (p < 1 || p > Capabilities.AntennaPorts)
                throw new ArgumentOutOfRangeException(nameof(antennas), $"Antenna port {p} is out of range 1..{Capabilities.AntennaPorts}.");
        return RunAsync(ct => _driver.GetReturnLossAsync(ports, ct), cancellationToken);
    }

    /// <summary>Switches a relay / digital output (1-based). Requires <see cref="ReaderCapabilities.Relays"/> &gt; 0.</summary>
    /// <remarks>Focus: relays 1-4 of the communication board. IN805: relay 1 = GPIO3, relay 2 = GPIO4.</remarks>
    public Task SetRelayAsync(int relay, bool on, CancellationToken cancellationToken = default)
    {
        if (Capabilities.Relays == 0) throw Unsupported("relays");
        if (relay < 1 || relay > Capabilities.Relays)
            throw new ArgumentOutOfRangeException(nameof(relay), $"Relay must be 1..{Capabilities.Relays}.");
        return RunAsync(ct => _driver.SetRelayAsync(relay, on, ct), cancellationToken);
    }

    /// <summary>Reads the digital inputs (index 0 = input 1; <c>true</c> = high level). Requires <see cref="ReaderCapabilities.Inputs"/> &gt; 0.</summary>
    public Task<IReadOnlyList<bool>> GetInputsAsync(CancellationToken cancellationToken = default)
    {
        if (Capabilities.Inputs == 0) throw Unsupported("digital inputs");
        return RunAsync(ct => _driver.GetInputsAsync(ct), cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    internal IReaderDriver Driver => _driver;

    private NotSupportedException Unsupported(string feature) => new($"{Model} reader does not support {feature}.");

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        await RunAsync<bool>(async c => { await action(c).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = State;
            if (state != ReaderConnectionState.Connected)
                throw new InvalidOperationException($"Reader is not connected (state: {state}).");
            if (_continuous)
                throw new InvalidOperationException("Continuous inventory is running; call StopAsync first.");
            return await action(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetState(ReaderConnectionState state, string? reason)
    {
        ReaderConnectionState old;
        lock (_stateLock)
        {
            old = _state;
            if (old == state) return;
            _state = state;
        }
        try { StateChanged?.Invoke(this, new ReaderStateChangedEventArgs(old, state, reason)); }
        catch (Exception ex) { _logger.LogWarning(ex, "StateChanged handler threw"); }
    }

    private void RaiseError(string message, Exception? exception, bool fatal)
    {
        try { Error?.Invoke(this, new ReaderErrorEventArgs(message, exception, fatal)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error handler threw"); }
    }

    private sealed class Host(RfidReader reader) : IDriverHost
    {
        public ILogger Logger => reader._logger;

        public void OnTag(TagRead tag)
        {
            try { reader.TagRead?.Invoke(reader, tag); }
            catch (Exception ex) { reader.RaiseError($"TagRead handler threw: {ex.Message}", ex, false); }
        }

        public void OnError(string message, Exception? exception)
        {
            reader._logger.LogWarning(exception, "{Message}", message);
            reader.RaiseError(message, exception, false);
        }

        public void OnFault(string message, Exception? exception)
        {
            reader._continuous = false;
            lock (reader._stateLock)
            {
                if (reader._state is not (ReaderConnectionState.Connected or ReaderConnectionState.Connecting)) return;
            }
            reader.SetState(ReaderConnectionState.Faulted, message);
            reader.RaiseError(message, exception, true);
        }

        public void OnContinuousEnded(string reason)
        {
            reader._continuous = false;
            reader._logger.LogWarning("{Reason}", reason);
            reader.RaiseError(reason, null, false);
        }
    }
}
