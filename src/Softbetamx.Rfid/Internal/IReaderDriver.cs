using Microsoft.Extensions.Logging;

namespace Softbetamx.Rfid.Internal;

/// <summary>Callbacks from a driver to the <see cref="RfidReader"/> facade.</summary>
internal interface IDriverHost
{
    ILogger Logger { get; }

    /// <summary>A tag was read during continuous inventory.</summary>
    void OnTag(TagRead tag);

    /// <summary>Non-fatal problem (e.g. antenna missing, callback error).</summary>
    void OnError(string message, Exception? exception);

    /// <summary>The link was lost; the driver is unusable until reconnected.</summary>
    void OnFault(string message, Exception? exception);

    /// <summary>Continuous inventory ended without <see cref="IReaderDriver.StopContinuousAsync"/> (reader error).</summary>
    void OnContinuousEnded(string reason);
}

/// <summary>
/// One protocol driver. The facade guarantees: one call at a time, connected state, no other command while
/// continuous inventory runs, arguments validated against <see cref="Capabilities"/>.
/// Adding a reader model means adding one implementation of this interface.
/// </summary>
internal interface IReaderDriver : IAsyncDisposable
{
    ReaderCapabilities Capabilities { get; }

    Task ConnectAsync(CancellationToken ct);

    Task DisconnectAsync();

    Task<ReaderInfo> GetInfoAsync(CancellationToken ct);

    Task<IReadOnlyList<AntennaConfig>> GetAntennasAsync(CancellationToken ct);

    /// <summary>Applies the full antenna configuration (temporarily, never to flash).</summary>
    Task SetAntennasAsync(IReadOnlyList<AntennaConfig> antennas, CancellationToken ct);

    Task<Gen2Settings> GetGen2Async(CancellationToken ct);

    Task SetGen2Async(Gen2Settings settings, CancellationToken ct);

    Task<IReadOnlyList<TagRead>> InventoryOnceAsync(TimeSpan duration, CancellationToken ct);

    Task StartContinuousAsync(CancellationToken ct);

    Task StopContinuousAsync(CancellationToken ct);

    Task SetBuzzerAsync(BuzzerMode mode, CancellationToken ct);

    Task<IReadOnlyList<AntennaReturnLoss>> GetReturnLossAsync(IReadOnlyList<int> antennas, CancellationToken ct);

    Task SetRelayAsync(int relay, bool on, CancellationToken ct);

    Task<IReadOnlyList<bool>> GetInputsAsync(CancellationToken ct);
}
