using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using Aes = System.Runtime.Intrinsics.X86.Aes;

namespace FastRng.ThreadSafe;

/// <summary>
/// A thread-safe pseudo-random number generator built on a reduced-round AES-NI counter-mode
/// construction (in the style of Random123's ARS generator). Derived from <see cref="Random"/>
/// to seamlessly replace standard generation methods.
/// </summary>
public class FastRng : Random
{
    // Thread-local instance pattern guarantees thread safety without using locks
    [ThreadStatic] private static FastRng? _localInstance;

    public static FastRng Instance => _localInstance ??= new FastRng();

    // Reduced-round AES (ARS-style): each 16-byte block gets 1 whitening XOR + this many AES
    // rounds. Full AES uses 10 rounds for adversarial security; 5 is what Random123's ARS-5
    // generator uses and is well past the point where SP800-22-style statistical batteries
    // stop finding structure, while being noticeably cheaper per block than full AES.
    private const int Rounds = 5;

    // Independent counter lanes generated per chunk. AESENC has ~4-7 cycle latency but 1/cycle
    // throughput; running several independent lanes back-to-back keeps the pipeline full instead
    // of stalling on each block's latency.
    private const int LaneCount = 8;
    private const int ChunkSize = LaneCount * 16; // 128 bytes per chunk

    private readonly Vector128<byte>[] _roundKeys = new Vector128<byte>[Rounds + 1];
    private Vector128<byte> _nonce;
    private ulong _counter;

    private uint _generatedBytesCount;
    private const uint ReSeedInterval = 65536; // Re-seed threshold after generating 64KB

    // Single-byte requests are served from a pool refilled one AES chunk at a time, instead of
    // running the AES pipeline down to a single 16-byte block per call.
    private const int PoolSize = ChunkSize;
    private readonly byte[] _pool = new byte[PoolSize];
    private int _poolPos = PoolSize;

    /// <summary>
    /// Seeds the AES round keys, nonce and counter from OS-provided cryptographic entropy.
    /// </summary>
    private FastRng()
    {
        Span<byte> seed = stackalloc byte[(Rounds + 1) * 16 + 16];
        RandomNumberGenerator.Fill(seed);

        for (int k = 0; k <= Rounds; k++)
        {
            _roundKeys[k] = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(seed.Slice(k * 16, 16)));
        }

        _nonce = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(seed.Slice((Rounds + 1) * 16, 16)));
        _counter = 0;
    }

    /// <summary>
    /// Generates a single pseudo-random byte, popped from a pool refilled one AES chunk at a
    /// time by the same generator that backs <see cref="NextBytes(Span{byte})"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte NextByte()
    {
        if (_poolPos >= PoolSize)
        {
            GenerateChunk(_pool);
            _poolPos = 0;
        }

        return _pool[_poolPos++];
    }

    /// <summary>
    /// Highly optimized array/span generation strategy.
    /// Inlines internal generation loop directly to avoid the overhead of individual NextByte calls.
    /// </summary>
    public override void NextBytes(Span<byte> buffer)
    {
        if (buffer.Length == 0) return;

        int i = 0;
        int totalLength = buffer.Length;

        for (; i <= totalLength - ChunkSize; i += ChunkSize)
        {
            GenerateChunk(buffer.Slice(i, ChunkSize));
        }

        for (; i < totalLength; i++)
        {
            buffer[i] = NextByte();
        }
    }

    /// <summary>
    /// Produces exactly <see cref="ChunkSize"/> bytes by running <see cref="LaneCount"/>
    /// independent AES-CTR blocks. Shared by both <see cref="NextBytes(Span{byte})"/> and the
    /// <see cref="NextByte"/> pool refill.
    /// </summary>
    private void GenerateChunk(Span<byte> dest)
    {
        _generatedBytesCount += ChunkSize;
        if (_generatedBytesCount >= ReSeedInterval)
        {
            InjectHardwareEntropy();
        }

        ulong baseCounter = _counter;

        for (int lane = 0; lane < LaneCount; lane++)
        {
            Vector128<byte> counterBlock = Vector128.Create(baseCounter + (ulong)lane, 0UL).AsByte();
            Vector128<byte> block = Sse2.Xor(counterBlock, _nonce);

            block = Sse2.Xor(block, _roundKeys[0]);
            for (int r = 1; r < Rounds; r++)
            {
                block = Aes.Encrypt(block, _roundKeys[r]);
            }
            block = Aes.EncryptLast(block, _roundKeys[Rounds]);

            block.CopyTo(dest.Slice(lane * 16, 16));
        }

        _counter = baseCounter + LaneCount;
    }

    /// <summary>
    /// Fills the elements of a specified array of bytes with random numbers.
    /// </summary>
    public override void NextBytes(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        NextBytes(buffer.AsSpan());
    }

    /// <summary>
    /// Generates a non-negative random integer less than <see cref="int.MaxValue"/>.
    /// </summary>
    public override int Next()
    {
        // Extract 31 bits to ensure the value is always non-negative
        return (int)(this.NextUInt64() & 0x7FFFFFFF);
    }

    /// <summary>
    /// Generates a non-negative random integer less than the specified maximum.
    /// </summary>
    public override int Next(int maxValue)
    {
        if (maxValue < 0) throw new ArgumentOutOfRangeException(nameof(maxValue), "Value must be non-negative.");
        if (maxValue == 0) return 0;
        return this.NextUniformInt(0, maxValue);
    }

    /// <summary>
    /// Generates a random integer within the specified inclusive-exclusive range.
    /// </summary>
    public override int Next(int minValue, int maxValue)
    {
        if (minValue > maxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(minValue), "MinValue must be less than or equal to maxValue.");
        }
        if (minValue == maxValue) return minValue;

        // Use uint range to cleanly support intervals stretching across negative/positive int bounds
        uint range = (uint)(maxValue - minValue);
        if (range == 1) return minValue;

        // Fetch an ultra-fast 32-bit random sample from your underlying 64-bit cascade
        uint sample = (uint)(NextUInt64() & 0xFFFFFFFFUL);

        // Lemire's Fast Range reduction: (sample * range) / 2^32
        ulong product = (ulong)sample * (ulong)range;
        uint remainder = (uint)product;

        // If the remainder falls below the range, check against rejection threshold to eliminate bias
        if (remainder < range)
        {
            // Rejection threshold loop - only executes for rare boundary values
            uint threshold = ((uint)-(int)range) % range; // Modulo here is safe as it's compile/rare state bound
            while (remainder < threshold)
            {
                sample = (uint)(NextUInt64() & 0xFFFFFFFFUL);
                product = (ulong)sample * (ulong)range;
                remainder = (uint)product;
            }
        }

        // Shift down by 32 bits to get the fast uniform result in exactly 1 CPU instruction cycle
        return (int)(minValue + (int)(product >> 32));
    }

    /// <summary>
    /// Generates a random floating-point number greater than or equal to 0.0, and less than 1.0.
    /// </summary>
    public override double NextDouble()
    {
        // Equivalent to standard 53-bit resolution mapping for IEEE 754 doubles
        return (NextUInt64() & 0x001FFFFFFFFFFFFFUL) * (1.0 / 9007199254740992.0);
    }

    /// <summary>
    /// Private utility method to compose a 64-bit unsigned integer using the internal span loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong NextUInt64()
    {
        Unsafe.SkipInit(out ulong value);
        Span<byte> buffer = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));
        this.NextBytes(buffer);
        return value;
    }

    /// <summary>
    /// Selects an index from an array of weights. Higher weights have a higher chance of selection.
    /// Crucial for slot machine reel configurations and virtual wheel layouts.
    /// </summary>
    public int NextWeightedIndex(ReadOnlySpan<int> weights)
    {
        if (weights.IsEmpty) throw new ArgumentException("Weights span cannot be empty.", nameof(weights));

        long totalWeight = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] < 0) throw new ArgumentException("Weights cannot be negative.");
            totalWeight += weights[i];
        }

        if (totalWeight == 0) throw new ArgumentException("Total sum of weights must be greater than zero.");

        // Generate a random roll across the total weight spectrum
        double roll = NextDouble() * totalWeight;
        double runningSum = 0;

        for (int i = 0; i < weights.Length; i++)
        {
            runningSum += weights[i];
            if (roll < runningSum) return i;
        }

        return weights.Length - 1;
    }

    /// <summary>
    /// Generates a perfectly uniform random integer between minValue (inclusive) and maxValue (exclusive).
    /// Eliminates modulo and floating-point bias using mathematical rejection sampling.
    /// </summary>
    public int NextUniformInt(int minValue, int maxValue)
    {
        if (minValue >= maxValue)
            throw new ArgumentOutOfRangeException(nameof(minValue), "MinValue must be less than maxValue.");

        uint range = (uint)(maxValue - minValue);
        if (range == 1) return minValue;

        // Calculate the rejection threshold to eliminate bias
        uint limit = uint.MaxValue - (uint.MaxValue % range);
        uint sample;

        do
        {
            // Re-use your ultra-fast 64-bit sequence
            sample = (uint)(NextUInt64() & 0xFFFFFFFFUL);
        }
        while (sample >= limit);

        return (int)(minValue + (sample % range));
    }

    /// <summary>
    /// Shuffles an entire span in place using an unbiased Fisher-Yates algorithm.
    /// Perfect for card games and reel sets.
    /// </summary>
    public void Shuffle<T>(Span<T> span)
    {
        int length = span.Length;
        if (length <= 1) return;

        // Count backward using basic index arithmetic for aggressive loop evaluation optimizations
        for (int i = length - 1; i > 0; i--)
        {
            // Leverage the zero-modulo uniform integer mapping method directly
            // to find a target index between 0 (inclusive) and i + 1 (exclusive)
            int j = Next(0, i + 1);

            // Swap using an explicit stack-isolated temporary reference variable.
            // This avoids any potential heap-allocation or struct-copying traps of Tuples.
            T temp = span[i];
            span[i] = span[j];
            span[j] = temp;
        }
    }

    /// <summary>
    /// Shuffles an array in place using an unbiased Fisher-Yates algorithm.
    /// </summary>
    public void Shuffle<T>(T[] array)
    {
        ArgumentNullException.ThrowIfNull(array);
        Shuffle(array.AsSpan());
    }

    /// <summary>
    /// Injects fresh hardware entropy harvested from the OS layer into the AES round keys and
    /// nonce. Defends against long-run state reconstruction: even if an observer inferred the
    /// current key material from output, it stops applying after the next injection.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InjectHardwareEntropy()
    {
        _generatedBytesCount = 0;

        Span<byte> hardwareChaos = stackalloc byte[(Rounds + 1) * 16 + 16];
        RandomNumberGenerator.Fill(hardwareChaos);

        for (int k = 0; k <= Rounds; k++)
        {
            Vector128<byte> freshKey = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(hardwareChaos.Slice(k * 16, 16)));
            _roundKeys[k] = Sse2.Xor(_roundKeys[k], freshKey);
        }

        Vector128<byte> freshNonce = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(hardwareChaos.Slice((Rounds + 1) * 16, 16)));
        _nonce = Sse2.Xor(_nonce, freshNonce);
    }
}
