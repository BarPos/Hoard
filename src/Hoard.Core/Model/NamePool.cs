using System.Text;

namespace Hoard.Core.Model;

/// <summary>
/// Compact storage for entry names (plan.md §4.1): UTF-8 bytes in 64 KB chunks, each name
/// prefixed with a 1–2 byte length. A name is addressed by a 31-bit handle
/// (chunk index &lt;&lt; 16 | offset), so a node needs 4 bytes instead of a reference to a
/// separate UTF-16 string object (≈ 50–70 bytes for a typical file name).
/// <para>
/// Each scanner thread appends through its own <see cref="Writer"/>, so writes need no lock
/// except when a writer takes a fresh chunk. Chunks never move, so reading is safe while
/// writers append.
/// </para>
/// </summary>
public sealed class NamePool
{
    private const int ChunkBits = 16;
    private const int ChunkSize = 1 << ChunkBits;
    private const int ChunkMask = ChunkSize - 1;
    private const int MaxChunks = 1 << (31 - ChunkBits);
    /// <summary>Longest name accepted, in UTF-8 bytes (file-system names are ≤ 255 bytes; root paths can be longer).</summary>
    public const int MaxNameBytes = 16_383;

    private byte[]?[] _chunks = new byte[]?[16];
    private int _chunkCount;
    private readonly Lock _gate = new();

    /// <summary>Appends names for one thread. Not thread-safe; create one per thread.</summary>
    public sealed class Writer
    {
        internal byte[]? Buffer;
        internal int Chunk;
        internal int Pos;
    }

    public Writer CreateWriter() => new();

    /// <summary>Bytes held by the pool (allocated chunks).</summary>
    public long AllocatedBytes => (long)Volatile.Read(ref _chunkCount) * ChunkSize;

    public int Add(Writer w, ReadOnlySpan<char> name)
    {
        int len = Encoding.UTF8.GetByteCount(name);
        if (len > MaxNameBytes)
        {
            throw new ArgumentException($"Name longer than {MaxNameBytes} UTF-8 bytes.", nameof(name));
        }
        int need = len + (len < 0x80 ? 1 : 2);
        if (w.Buffer is null || w.Pos + need > ChunkSize)
        {
            NewChunk(w);
        }
        byte[] buf = w.Buffer!;
        int start = w.Pos;
        int p = start;
        if (len < 0x80)
        {
            buf[p++] = (byte)len;
        }
        else
        {
            buf[p++] = (byte)(0x80 | (len >> 8));
            buf[p++] = (byte)len;
        }
        Encoding.UTF8.GetBytes(name, buf.AsSpan(p, len));
        w.Pos = p + len;
        return (w.Chunk << ChunkBits) | start;
    }

    public string Get(int handle)
    {
        byte[] buf = Volatile.Read(ref _chunks)[handle >> ChunkBits]!;
        int p = handle & ChunkMask;
        int len = buf[p++];
        if ((len & 0x80) != 0)
        {
            len = ((len & 0x7F) << 8) | buf[p++];
        }
        return Encoding.UTF8.GetString(buf, p, len);
    }

    private void NewChunk(Writer w)
    {
        lock (_gate)
        {
            if (_chunkCount == MaxChunks)
            {
                throw new InvalidOperationException("Name pool is full (2 GB of names).");
            }
            if (_chunkCount == _chunks.Length)
            {
                var bigger = new byte[]?[_chunks.Length * 2];
                Array.Copy(_chunks, bigger, _chunks.Length);
                Volatile.Write(ref _chunks, bigger);
            }
            var chunk = new byte[ChunkSize];
            _chunks[_chunkCount] = chunk;
            w.Buffer = chunk;
            w.Chunk = _chunkCount;
            w.Pos = 0;
            Volatile.Write(ref _chunkCount, _chunkCount + 1);
        }
    }
}
