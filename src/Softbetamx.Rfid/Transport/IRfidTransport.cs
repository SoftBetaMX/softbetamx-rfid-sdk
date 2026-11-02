namespace Softbetamx.Rfid.Transport;

/// <summary>
/// Raw byte link to a reader (TCP socket, serial port, or a fake in tests).
/// One reader uses one transport; the library calls <see cref="ReadAsync"/> from a single background loop
/// and <see cref="WriteAsync"/> from one command at a time.
/// </summary>
public interface IRfidTransport : IAsyncDisposable
{
    /// <summary>Human-readable endpoint, e.g. "tcp://192.168.1.100:8080".</summary>
    string Description { get; }

    /// <summary>Opens the link.</summary>
    Task OpenAsync(CancellationToken cancellationToken);

    /// <summary>Closes the link. Must be idempotent and make a pending <see cref="ReadAsync"/> finish.</summary>
    Task CloseAsync();

    /// <summary>Writes all bytes.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Reads available bytes. Returns 0 when the link was closed by the remote end.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}
