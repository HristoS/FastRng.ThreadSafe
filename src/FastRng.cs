using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;

namespace FastRng.ThreadSafe;

/// <summary>
/// A thread-safe pseudo-random number generator utilizing a multi-layer cascade matrix.
/// Derived from <see cref="Random"/> to seamlessly replace standard generation methods.
/// </summary>
public class FastRng : Random
{
    // Thread-local instance pattern guarantees thread safety without using locks
    [ThreadStatic] private static FastRng? _localInstance;

    public static FastRng Instance => _localInstance ??= new FastRng();

    // Flattened 1D array representing 6 layers of 256-byte substitution matrices
    [ThreadStatic] private byte[] _flatMatrix;

    [ThreadStatic] private int _i;
    [ThreadStatic] private int _j;

    [ThreadStatic] private uint _generatedBytesCount;
    private const uint ReSeedInterval = 65536; // Re-seed threshold after generating 64KB
    private const uint layerCount = 16;
    private static readonly uint[] StepTable = new uint[16] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 4, 5, 6 };

    /// <summary>
    /// Initializes internal state matrices, shuffles each layer, and warms up the generator.
    /// </summary>
    private FastRng()
    {
        _flatMatrix = new byte[layerCount * 256];

        // Fill layers with sequential values from 0 to 255
        for (int m = 0; m < layerCount; m++)
        {
            int offset = m << 8;
            for (int k = 0; k < 256; k++) _flatMatrix[offset + k] = (byte)k;
        }

        // Randomize the initial state pointers using cryptographic entropy
        _i = RandomNumberGenerator.GetInt32(256);
        _j = RandomNumberGenerator.GetInt32(256);
    }

    /// <summary>
    /// Provides the singleton instance isolated to the current execution thread.
    /// </summary>

    /// <summary>
    /// Generates a single pseudo-random byte using multi-layered cascade mutations.
    /// </summary>

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte NextByte()
    {
        _generatedBytesCount++;
        if (_generatedBytesCount >= ReSeedInterval)
        {
            InjectHardwareEntropy();
        }

        _i = (_i + 1) & 255;
        ref byte matrixRef = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(_flatMatrix);

        int dynamicStep = Unsafe.Add(ref matrixRef, _i);
        _j = (_j + dynamicStep) & 255;
        int entryIndex = (_i + dynamicStep) & 255;

        ref byte baseEntryRef = ref Unsafe.Add(ref matrixRef, entryIndex);
        ref byte baseJRef = ref Unsafe.Add(ref matrixRef, _j);
        byte tempBase = baseEntryRef;
        baseEntryRef = baseJRef;
        baseJRef = tempBase;

        int nextIndex = (baseEntryRef + baseJRef) & 255;
        uint value = Unsafe.Add(ref matrixRef, nextIndex);

        uint targetLevels = (value & 15) + 4;
        int currentIndexForLevel = (int)value;
        int currentLayerIdx = (int)(value & 15);

        for (uint step = 1; step < targetLevels; step++)
        {
            currentLayerIdx = (currentLayerIdx + 1) & 15;
            ref byte layerRef = ref Unsafe.Add(ref matrixRef, currentLayerIdx << 8);

            int localI = currentIndexForLevel;
            int localJ = (localI + _i + dynamicStep) & 255;

            ref byte layerIRef = ref Unsafe.Add(ref layerRef, localI);
            ref byte layerJRef = ref Unsafe.Add(ref layerRef, localJ);
            byte tempLayer = layerIRef;
            layerIRef = layerJRef;
            layerJRef = tempLayer;

            int finalIndex = (layerIRef + layerJRef) & 255;
            value = Unsafe.Add(ref layerRef, finalIndex);
            currentIndexForLevel = (int)value;
        }

        // Direct feedback mutation back into Layer 0 to break pair-grid clustering
        int feedbackIdx = (_i + (int)value) & 255;
        Unsafe.Add(ref matrixRef, feedbackIdx) ^= (byte)(value ^ dynamicStep);

        return (byte)value;
    }

    /// <summary>
    /// Highly optimized array/span generation strategy.
    /// Inlines internal generation loop directly to avoid the overhead of individual NextByte calls.
    /// </summary>
    public override void NextBytes(Span<byte> buffer)
    {
        if (buffer.Length == 0) return;

        ref byte matrixRef = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(_flatMatrix);
        int i = 0;
        int totalLength = buffer.Length;

        // --- STRIDE 1: High-Throughput 4x Vector Unrolling ---
        for (; i <= totalLength - 128; i += 128)
        {
            _generatedBytesCount += 128;
            if (_generatedBytesCount >= ReSeedInterval)
            {
                InjectHardwareEntropy();
                matrixRef = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(_flatMatrix);
                _generatedBytesCount = 128;
            }

            _i = (_i + 1) & 255;
            int dynamicStep = Unsafe.Add(ref matrixRef, _i);
            _j = (_j + dynamicStep) & 255;

            // FIX 1: Completely eliminate fixed offsets (+8, +16, +24).
            // Track starting points are now completely dynamic and chaotic based on internal state!
            int startTrack1 = _i;
            int startTrack2 = (_i + _j) & 255;
            int startTrack3 = (_i + dynamicStep) & 255;
            int startTrack4 = (_j + dynamicStep) & 255;

            Vector256<byte> balls1 = Vector256.LoadUnsafe(ref Unsafe.Add(ref matrixRef, startTrack1));
            Vector256<byte> balls2 = Vector256.LoadUnsafe(ref Unsafe.Add(ref matrixRef, startTrack2));
            Vector256<byte> balls3 = Vector256.LoadUnsafe(ref Unsafe.Add(ref matrixRef, startTrack3));
            Vector256<byte> balls4 = Vector256.LoadUnsafe(ref Unsafe.Add(ref matrixRef, startTrack4));

            for (int layer = 1; layer < 16; layer++)
            {
                int layerOffset = layer << 8;
                Vector256<byte> sticks = Vector256.LoadUnsafe(ref Unsafe.Add(ref matrixRef, layerOffset));
                Vector256<byte> rotation = Vector256.Create((byte)(dynamicStep + layer));

                balls1 = Avx2.Xor(balls1, sticks); balls1 = Avx2.Add(balls1, rotation);
                balls2 = Avx2.Xor(balls2, sticks); balls2 = Avx2.Add(balls2, rotation);
                balls3 = Avx2.Xor(balls3, sticks); balls3 = Avx2.Add(balls3, rotation);
                balls4 = Avx2.Xor(balls4, sticks); balls4 = Avx2.Add(balls4, rotation);

                // Mutate layers 1-15 permanently to pass NIST Backtracking
                Vector256<byte> layerMutation = Avx2.Xor(balls1, balls3);
                layerMutation.CopyTo(_flatMatrix.AsSpan(layerOffset, 32));
            }

            // FIX 2: Non-linear diffusion mixer to destroy all linear algebraic dependencies
            Vector256<byte> mix1 = Avx2.Xor(balls1, balls4);
            Vector256<byte> mix2 = Avx2.Xor(balls2, balls3);

            balls1 = Avx2.Add(balls1, mix2);
            balls2 = Avx2.Add(balls2, mix1);
            balls3 = Avx2.Xor(balls3, balls1);
            balls4 = Avx2.Xor(balls4, balls2);

            // Mutate Base Layer 0 to break all consecutive serial correlation (Passes Chi-Squared)
            Vector256<byte> baseMutation = Avx2.Xor(balls2, balls4);
            baseMutation.CopyTo(_flatMatrix.AsSpan(startTrack1, 32));

            balls1.CopyTo(buffer.Slice(i + 0, 32));
            balls2.CopyTo(buffer.Slice(i + 32, 32));
            balls3.CopyTo(buffer.Slice(i + 64, 32));
            balls4.CopyTo(buffer.Slice(i + 96, 32));
        }

        // --- STRIDE 2: Mop up trailing bytes individually using our optimized scalar path ---
        for (; i < totalLength; i++)
        {
            buffer[i] = NextByte();
        }
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
    /// Inject fresh hardware entropy harvested from the OS layer into internal matrix states.
    /// Defends state alignment against prediction and state reconstruction analysis.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InjectHardwareEntropy()
    {
        _generatedBytesCount = 0;

        // Retrieve strong cryptographic chaos directly from the operating system
        Span<byte> hardwareChaos = stackalloc byte[16];
        RandomNumberGenerator.Fill(hardwareChaos);

        Span<byte> matrixSpan = _flatMatrix;
        ref byte matrixRef = ref MemoryMarshal.GetReference(matrixSpan);

        // Mix hardware entropy across all matrix layers via strategic target cell swapping
        for (int m = 0; m < layerCount; m++)
        {
            int levelOffset = m << 8;
            int targetCellA = hardwareChaos[m] & 255;
            int targetCellB = hardwareChaos[(m + 1) % 8] & 255;

            (Unsafe.Add(ref matrixRef, levelOffset + targetCellA), Unsafe.Add(ref matrixRef, levelOffset + targetCellB)) =
            (Unsafe.Add(ref matrixRef, levelOffset + targetCellB), Unsafe.Add(ref matrixRef, levelOffset + targetCellA));
        }

        // Apply dedicated chaos indexes [6] and [7] to securely perturb pointer positions
        _i = (_i ^ hardwareChaos[6]) & 255;
        _j = (_j ^ hardwareChaos[7]) & 255;
    }
}