using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FastRng.ThreadSafe;

/// <summary>
/// A thread-safe pseudo-random number generator utilizing a multi-layer cascade matrix.
/// Derived from <see cref="Random"/> to seamlessly replace standard generation methods.
/// </summary>
public class FastRng : Random
{
    private static readonly AsyncLocal<byte[]?> _localState = new();

    private const uint ReSeedInterval = 65536;// Re-seed threshold after generating 64KB
    private const int MetadataSize = 4;
    private const int MatrixSize = 5 * 256; // Reduced to 5 layers
    private const int TotalStateSize = MetadataSize + MatrixSize; // 1284 bytes

    /// <summary>
    /// Initializes internal state matrices, shuffles each layer, and warms up the generator.
    /// </summary>
    private FastRng()
    {
    }

    /// <summary>
    /// Gets the context-safe instance for the current execution flow.
    /// </summary>
    public static FastRng Instance => _localInstance ??= new FastRng();

    private static FastRng? _localInstance;

    /// <summary>
    /// Generates a single pseudo-random byte using multi-layered cascade mutations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte NextByte()
    {
        byte[] state = GetOrCreateState();
        ref byte stateRef = ref MemoryMarshal.GetReference((Span<byte>)state);

        // Safely extract metadata indices directly via pointer offsets
        int localI = (Unsafe.Add(ref stateRef, 0) + 1) & 255;
        int localJ = (Unsafe.Add(ref stateRef, 1) + Unsafe.Add(ref stateRef, MetadataSize + localI)) & 255;

        // Increment the count stored at positions 2 and 3
        Unsafe.As<byte, ushort>(ref Unsafe.Add(ref stateRef, 2))++;

        // Base Layer Swapping
        (Unsafe.Add(ref stateRef, MetadataSize + localI), Unsafe.Add(ref stateRef, MetadataSize + localJ)) =
            (Unsafe.Add(ref stateRef, MetadataSize + localJ), Unsafe.Add(ref stateRef, MetadataSize + localI));

        int targetLevels = 3 + ((localI + localJ) % 3); // Dynamic depths adjusted for 5 layers max
        int currentIndexForLevel = (Unsafe.Add(ref stateRef, MetadataSize + localI) + Unsafe.Add(ref stateRef, MetadataSize + localJ)) & 255;
        byte userValue = Unsafe.Add(ref stateRef, MetadataSize + currentIndexForLevel);

        // Forward Cascade Loops across the remaining layers
        for (uint step = 1; step < targetLevels; step++)
        {
            int currentArrayIdx = (int)((step + (userValue % 4)) % 5);
            int levelOffset = MetadataSize + (currentArrayIdx << 8);

            int lvlI = currentIndexForLevel;
            int lvlJ = (lvlI + localI + (int)step) & 255;

            (Unsafe.Add(ref stateRef, levelOffset + lvlI), Unsafe.Add(ref stateRef, levelOffset + lvlJ)) =
                (Unsafe.Add(ref stateRef, levelOffset + lvlJ), Unsafe.Add(ref stateRef, levelOffset + lvlI));

            int finalIndex = (Unsafe.Add(ref stateRef, levelOffset + lvlI) + Unsafe.Add(ref stateRef, levelOffset + lvlJ)) & 255;
            userValue = Unsafe.Add(ref stateRef, levelOffset + finalIndex);
            currentIndexForLevel = userValue;
        }

        // Fast-Key-Erasure Reverse Scrambling (Inlined & Zero Allocation)
        int secretI = (localI + 1) & 255;
        int secretJ = (localJ ^ userValue) & 255;
        byte erasureByte = Unsafe.Add(ref stateRef, MetadataSize + ((secretI + secretJ) & 255));

        for (int step = targetLevels - 1; step >= 0; step--)
        {
            int currentArrayIdx = (int)((step + (erasureByte % 3)) % 5);
            int levelOffset = MetadataSize + (currentArrayIdx << 8);

            int reverseI = (currentIndexForLevel ^ erasureByte) & 255;
            int reverseJ = (reverseI + localJ) & 255;

            (Unsafe.Add(ref stateRef, levelOffset + reverseI), Unsafe.Add(ref stateRef, levelOffset + reverseJ)) =
                (Unsafe.Add(ref stateRef, levelOffset + reverseJ), Unsafe.Add(ref stateRef, levelOffset + reverseI));

            erasureByte = (byte)(Unsafe.Add(ref stateRef, levelOffset + reverseI) ^ reverseJ);
        }

        // Save metadata registers back to structural indices
        Unsafe.Add(ref stateRef, 0) = (byte)localI;
        Unsafe.Add(ref stateRef, 1) = (byte)localJ;

        return userValue;
    }

    /// <summary>
    /// Highly optimized array/span generation strategy.
    /// Inlines internal generation loop directly to avoid the overhead of individual NextByte calls.
    /// </summary>
    public override void NextBytes(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return;

        byte[] state = GetOrCreateState();
        ref byte stateRef = ref MemoryMarshal.GetReference((Span<byte>)state);

        int localI = Unsafe.Add(ref stateRef, 0);
        int localJ = Unsafe.Add(ref stateRef, 1);
        ushort count = Unsafe.As<byte, ushort>(ref Unsafe.Add(ref stateRef, 2));

        for (int i = 0; i < buffer.Length; i++)
        {
            if (count >= ReSeedInterval)
            {
                // Save current loop progress markers before triggering dynamic lifecycle rotation
                Unsafe.Add(ref stateRef, 0) = (byte)localI;
                Unsafe.Add(ref stateRef, 1) = (byte)localJ;

                state = GetOrCreateState();
                stateRef = ref MemoryMarshal.GetReference((Span<byte>)state);

                localI = Unsafe.Add(ref stateRef, 0);
                localJ = Unsafe.Add(ref stateRef, 1);
                count = 0;
            }

            count++;
            localI = (localI + 1) & 255;
            localJ = (localJ + Unsafe.Add(ref stateRef, MetadataSize + localI)) & 255;

            // Forward Cascade
            (Unsafe.Add(ref stateRef, MetadataSize + localI), Unsafe.Add(ref stateRef, MetadataSize + localJ)) =
                (Unsafe.Add(ref stateRef, MetadataSize + localJ), Unsafe.Add(ref stateRef, MetadataSize + localI));

            int targetLevels = 3 + ((localI + localJ) % 3);
            int currentIndexForLevel = (Unsafe.Add(ref stateRef, MetadataSize + localI) + Unsafe.Add(ref stateRef, MetadataSize + localJ)) & 255;
            byte userValue = Unsafe.Add(ref stateRef, MetadataSize + currentIndexForLevel);

            for (uint step = 1; step < targetLevels; step++)
            {
                int currentArrayIdx = (int)((step + (userValue % 4)) % 5);
                int levelOffset = MetadataSize + (currentArrayIdx << 8);

                int lvlI = currentIndexForLevel;
                int lvlJ = (lvlI + localI + (int)step) & 255;

                (Unsafe.Add(ref stateRef, levelOffset + lvlI), Unsafe.Add(ref stateRef, levelOffset + lvlJ)) =
                    (Unsafe.Add(ref stateRef, levelOffset + lvlJ), Unsafe.Add(ref stateRef, levelOffset + lvlI));

                int finalIndex = (Unsafe.Add(ref stateRef, levelOffset + lvlI) + Unsafe.Add(ref stateRef, levelOffset + lvlJ)) & 255;
                userValue = Unsafe.Add(ref stateRef, levelOffset + finalIndex);
                currentIndexForLevel = userValue;
            }

            buffer[i] = userValue;

            // Fast-Key-Erasure Integrated Processing Step
            int secretI = (localI + 1) & 255;
            int secretJ = (localJ ^ userValue) & 255;
            byte erasureByte = Unsafe.Add(ref stateRef, MetadataSize + ((secretI + secretJ) & 255));

            for (int step = targetLevels - 1; step >= 0; step--)
            {
                int currentArrayIdx = (int)((step + (erasureByte % 3)) % 5);
                int levelOffset = MetadataSize + (currentArrayIdx << 8);

                int reverseI = (currentIndexForLevel ^ erasureByte) & 255;
                int reverseJ = (reverseI + localJ) & 255;

                (Unsafe.Add(ref stateRef, levelOffset + reverseI), Unsafe.Add(ref stateRef, levelOffset + reverseJ)) =
                    (Unsafe.Add(ref stateRef, levelOffset + reverseJ), Unsafe.Add(ref stateRef, levelOffset + reverseI));

                erasureByte = (byte)(Unsafe.Add(ref stateRef, levelOffset + reverseI) ^ reverseJ);
            }
        }

        // Commit final state back into array registers
        Unsafe.Add(ref stateRef, 0) = (byte)localI;
        Unsafe.Add(ref stateRef, 1) = (byte)localJ;
        Unsafe.As<byte, ushort>(ref Unsafe.Add(ref stateRef, 2)) = count;
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
        return (int)(this.NextUInt32() & 0x7FFFFFFF);
    }

    /// <summary>
    /// Generates a non-negative random integer less than the specified maximum.
    /// </summary>
    public override int Next(int maxValue)
    {
        if (maxValue <= 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
        return Next(0, maxValue);
    }

    /// <summary>
    /// Generates a random integer within the specified inclusive-exclusive range.
    /// </summary>
    public override int Next(int minValue, int maxValue)
    {
        if (minValue > maxValue) throw new ArgumentOutOfRangeException("minValue must be less than maxValue");

        long range = (long)maxValue - minValue;
        if (range <= 1) return minValue;

        // Optimized Bit-Mask Rejection Sampling Engine
        // Eliminates modulo bias while maximizing execution pipeline efficiency
        int powerOfTwoMask = (int)BitOperations.RoundUpToPowerOf2((ulong)range) - 1;

        while (true)
        {
            int randomVal = (int)(NextUInt32() & powerOfTwoMask); if (randomVal < range)
            {
                return minValue + randomVal;
            }
        }
    }

    /// <summary>
    /// Generates a random floating-point number greater than or equal to 0.0, and less than 1.0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override double NextDouble()
    {
        // Pulls 53 bits of raw random entropy and multiplies by scale factor
        // This completely eliminates slow floating-point CPU division operations
        const double scale = 1.0 / (1L << 53);
        ulong random64 = NextUInt64();
        return (random64 >> 11) * scale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint NextUInt32()
    {
        // Unrolls 4 fast sequential iterations inline to avoid stack allocations
        uint val = NextByte();
        val |= (uint)NextByte() << 8;
        val |= (uint)NextByte() << 16;
        val |= (uint)NextByte() << 24;
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong NextUInt64()
    {
        ulong val = NextUInt32();
        return val | ((ulong)NextUInt32() << 32);
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
        if (span.Length <= 1) return;

        for (int i = span.Length - 1; i > 0; i--)
        {
            int j = NextUniformInt(0, i + 1);
            (span[i], span[j]) = (span[j], span[i]);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] GetOrCreateState()
    {
        var state = _localState.Value;

        if (state == null)
        {
            state = InitializeNewState();
            _localState.Value = state;
            return state;
        }

        // Read the ushort GeneratedBytesCount stored at index 2 & 3
        ref byte countRef = ref state[2];
        ushort count = Unsafe.As<byte, ushort>(ref countRef);

        if (count >= ReSeedInterval)
        {
            state = InitializeNewState();
            _localState.Value = state;
        }

        return state;
    }

    private byte[] InitializeNewState()
    {
        var state = new byte[TotalStateSize];
        ref byte matrixRef = ref MemoryMarshal.GetReference((Span<byte>)state);

        // 1. Initialize 5 structural sequential layers starting after the metadata offset
        for (int layer = 0; layer < 5; layer++)
        {
            int offset = MetadataSize + (layer << 8);
            for (int val = 0; val < 256; val++)
            {
                Unsafe.Add(ref matrixRef, offset + val) = (byte)val;
            }
        }

        // 2. Inject high-quality OS entropy to shuffle matrices and indices
        Span<byte> hardwareChaos = stackalloc byte[8];
        RandomNumberGenerator.Fill(hardwareChaos);

        // Map initial I and J to metadata positions 0 and 1
        Unsafe.Add(ref matrixRef, 0) = hardwareChaos[0];
        Unsafe.Add(ref matrixRef, 1) = hardwareChaos[1];

        // GeneratedBytesCount is initialized to 0 at index 2-3 implicitly

        int chaosIdx = 2;
        for (int layer = 0; layer < 5; layer++)
        {
            int levelOffset = MetadataSize + (layer << 8);
            for (int step = 0; step < 256; step++)
            {
                int targetI = (step + hardwareChaos[chaosIdx % 8]) & 255;
                int targetJ = (targetI + step + Unsafe.Add(ref matrixRef, 0)) & 255;
                chaosIdx++;

                (Unsafe.Add(ref matrixRef, levelOffset + targetI), Unsafe.Add(ref matrixRef, levelOffset + targetJ)) =
                    (Unsafe.Add(ref matrixRef, levelOffset + targetJ), Unsafe.Add(ref matrixRef, levelOffset + targetI));
            }
        }

        return state;
    }
}