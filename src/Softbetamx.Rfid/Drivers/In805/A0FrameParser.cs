using Softbetamx.Rfid.Internal;

namespace Softbetamx.Rfid.Drivers.In805;

/// <summary>
/// Arma tramas 0xA0 a partir de un flujo de bytes: acumula tramas partidas entre lecturas,
/// descarta basura antes de 0xA0 y se resincroniza ante un checksum inválido descartando un byte.
/// No es thread-safe (lo usa solo el ciclo de recepción).
/// </summary>
internal sealed class A0FrameParser : IFrameParser<A0Frame>
{
    private byte[] _buffer = new byte[1024];
    private int _count;

    /// <summary>Bytes descartados por basura o checksum inválido (diagnóstico).</summary>
    public long DiscardedBytes { get; private set; }

    public List<A0Frame> Append(ReadOnlySpan<byte> chunk)
    {
        EnsureCapacity(_count + chunk.Length);
        chunk.CopyTo(_buffer.AsSpan(_count));
        _count += chunk.Length;

        var frames = new List<A0Frame>();
        int pos = 0;
        while (true)
        {
            int head = Array.IndexOf(_buffer, A0Frame.Head, pos, _count - pos);
            if (head < 0)
            {
                DiscardedBytes += _count - pos;
                pos = _count;
                break;
            }
            DiscardedBytes += head - pos;
            pos = head;

            if (_count - pos < 2) break;
            int len = _buffer[pos + 1];
            if (len < 3)
            {
                // Len imposible (debe incluir Addr, Cmd y Check): no es una cabecera real.
                pos++;
                DiscardedBytes++;
                continue;
            }
            int total = len + 2;
            if (_count - pos < total) break;

            var raw = _buffer.AsSpan(pos, total);
            if (A0Frame.Checksum(raw[..^1]) != raw[^1])
            {
                pos++;
                DiscardedBytes++;
                continue;
            }

            frames.Add(new A0Frame(raw[2], raw[3], raw[4..^1].ToArray()));
            pos += total;
        }

        // Compactar lo pendiente al inicio del búfer.
        if (pos > 0)
        {
            Array.Copy(_buffer, pos, _buffer, 0, _count - pos);
            _count -= pos;
        }
        return frames;
    }

    public void Reset() => _count = 0;

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer.Length) return;
        Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));
    }
}
