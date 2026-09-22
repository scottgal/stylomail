using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace StyloMail.Adaptive.Profiles;

/// <summary>
/// Answers "have we ever seen this recipient?" in fixed space, with no false negatives.
/// </summary>
/// <remarks>
/// The bounded set in <see cref="RecipientHistory"/> answers this well until it is truncated, and
/// truncation is permanent — so novelty would go dark precisely on the accounts with the widest
/// reach, which are the ones most likely to be compromised. A signal that vanishes exactly where it
/// is needed is worse than no signal, because it still looks present.
///
/// <para>
/// <b>The direction of the error is what makes this acceptable.</b> A Bloom filter has no false
/// negatives for membership: anything added is always found. Its false positives treat a new
/// recipient as already known, so its mistakes <em>miss</em> novelty rather than inventing it.
/// Inventing novelty would manufacture the most alarming signal in the profile, on a question we
/// could not actually answer, and that signal is the one most likely to lead to an irreversible
/// action. Missing it is the harmless direction.
/// </para>
///
/// <para>
/// <b>Hashing is deterministic by construction.</b> Keys are digested with SHA-256 rather than
/// <see cref="string.GetHashCode()"/>, whose per-process seed randomisation would make a restarted
/// process disagree with any persisted filter about what it already contains.
/// </para>
///
/// <para>
/// <b>An unpopulated filter is not evidence of absence.</b> <see cref="MightContain"/> is only
/// meaningful over a filter that has been fed everything it should have seen; a freshly constructed
/// one will report "not present" for recipients it has simply never been told about. Callers must
/// track whether the filter was populated rather than reading emptiness as knowledge.
/// </para>
/// </remarks>
public sealed class RecipientBloomFilter
{
    private readonly ulong[] _bits;
    private readonly int _bitCount;
    private readonly int _hashCount;

    /// <summary>
    /// Sizes a filter for the expected number of distinct recipients at a target false-positive rate.
    /// </summary>
    public RecipientBloomFilter(int expectedCapacity, double falsePositiveRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedCapacity);

        if (falsePositiveRate is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(falsePositiveRate),
                falsePositiveRate,
                "A false-positive rate must lie strictly between 0 and 1.");
        }

        // The standard sizing: m = -n ln p / (ln 2)^2, k = (m/n) ln 2.
        var bits = (int)Math.Ceiling(-expectedCapacity * Math.Log(falsePositiveRate) / (Math.Log(2) * Math.Log(2)));
        _bitCount = Math.Max(bits, 64);
        _hashCount = Math.Max(1, (int)Math.Round((double)_bitCount / expectedCapacity * Math.Log(2)));
        _bits = new ulong[(_bitCount + 63) / 64];
    }

    private RecipientBloomFilter(int bitCount, int hashCount)
    {
        _bitCount = bitCount;
        _hashCount = hashCount;
        _bits = new ulong[(bitCount + 63) / 64];
    }

    /// <summary>Bits allocated. Fixed at construction, whatever is recorded afterwards.</summary>
    public int BitCount => _bitCount;

    /// <summary>
    /// Keys recorded <em>by this instance</em>. Not a membership count — duplicates and collisions
    /// blur it — and it restarts at zero for a filter restored from bytes, so it must never be read
    /// as "how many recipients are known".
    /// </summary>
    public int Count { get; private set; }

    public void Add(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var (first, second) = Hashes(key);
        for (var i = 0; i < _hashCount; i++)
        {
            Set(Index(first, second, i));
        }

        Count++;
    }

    /// <summary>
    /// True when the key may have been recorded. <b>False is reliable; true is not.</b>
    /// </summary>
    public bool MightContain(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var (first, second) = Hashes(key);
        for (var i = 0; i < _hashCount; i++)
        {
            if (!IsSet(Index(first, second, i)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Serialises the filter for storage. Fixed size: four bytes of header plus the bit array.
    /// </summary>
    /// <remarks>
    /// A filter that is not persisted cannot answer anything after a restart, and an empty filter
    /// reports every recipient as novel — manufacturing the exact alarm this structure exists to
    /// avoid, at scale, on every restart. Persistence is not an optimisation here; the guarantee
    /// depends on it.
    /// </remarks>
    public byte[] ToBytes()
    {
        var buffer = new byte[8 + (_bits.Length * sizeof(ulong))];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, _bitCount);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), _hashCount);

        for (var i = 0; i < _bits.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8 + (i * sizeof(ulong))), _bits[i]);
        }

        return buffer;
    }

    /// <summary>Restores a filter written by <see cref="ToBytes"/>.</summary>
    public static RecipientBloomFilter FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            throw new ArgumentException("A serialised filter must carry at least its header.", nameof(bytes));
        }

        var bitCount = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var hashCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        var words = (bitCount + 63) / 64;

        if (bytes.Length != 8 + (words * sizeof(ulong)))
        {
            throw new ArgumentException("Serialised filter length does not match its header.", nameof(bytes));
        }

        var filter = new RecipientBloomFilter(bitCount, hashCount);
        for (var i = 0; i < words; i++)
        {
            filter._bits[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(8 + (i * sizeof(ulong)))..]);
        }

        return filter;
    }

    /// <summary>
    /// Kirchhoff-Mitzenmacher double hashing: two independent hashes generate <c>k</c> indices
    /// without paying for <c>k</c> digests.
    /// </summary>
    private int Index(ulong first, ulong second, int i)
    {
        var combined = first + ((ulong)i * second);
        return (int)(combined % (ulong)_bitCount);
    }

    private static (ulong First, ulong Second) Hashes(string key)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), digest);

        var first = BinaryPrimitives.ReadUInt64LittleEndian(digest);
        var second = BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]);

        // A zero step would make every index identical and collapse the filter to one bit.
        return (first, second | 1UL);
    }

    private void Set(int index) => _bits[index >> 6] |= 1UL << (index & 63);

    private bool IsSet(int index) => (_bits[index >> 6] & (1UL << (index & 63))) != 0;
}
