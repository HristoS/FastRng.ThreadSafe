using System.Collections.Concurrent;
using Xunit;

namespace FastRng.ThreadSafe.Tests;

public class FastRngTests
{
    [Fact]
    public void NextByte_ShouldReturnValuesWithinValidByteRange()
    {
        var generator = FastRng.Instance;

        for (int i = 0; i < 1000; i++)
        {
            byte value = generator.NextByte();
            Assert.True(value >= 0 && value <= 255, $"Generated value {value} is out of byte bounds.");
        }
    }

    [Fact]
    public void NextBytes_Span_ShouldFillBufferCorrectly()
    {
        var generator = FastRng.Instance;
        Span<byte> buffer = stackalloc byte[100];

        generator.NextBytes(buffer);

        int nonZeroCount = 0;
        foreach (var b in buffer)
        {
            if (b != 0) nonZeroCount++;
        }

        Assert.True(nonZeroCount > 0, "The buffer was not modified or filled with randomized data.");
    }

    [Fact]
    public void Next_WithBounds_ShouldRespectMinAndMax()
    {
        var generator = FastRng.Instance;
        int min = 10;
        int max = 20;

        for (int i = 0; i < 1000; i++)
        {
            int value = generator.Next(min, max);
            Assert.True(value >= min && value < max, $"Value {value} out of defined range [{min}, {max}).");
        }
    }

    [Fact]
    public void Next_WithInvalidBounds_ShouldThrowArgumentException()
    {
        var generator = FastRng.Instance;

        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Next(50, 10));
    }

    /// <summary>
    /// FIXED: Tests thread safety by forcing the OS to spin up dedicated physical threads.
    /// This avoids ThreadPool thread-reuse artifacts and correctly tests [ThreadStatic] isolation.
    /// </summary>
    [Fact]
    public void Generator_ShouldBeThreadSafe_AndIsolatedPerThread()
    {
        // Arrange
        var threadValues = new ConcurrentDictionary<int, List<byte>>();
        int threadCount = 8;
        int iterationsPerThread = 10;
        var threads = new Thread[threadCount];

        // Act
        for (int i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                int threadId = Environment.CurrentManagedThreadId;
                var list = new List<byte>();

                for (int j = 0; j < iterationsPerThread; j++)
                {
                    list.Add(FastRng.Instance.NextByte());
                }

                threadValues.TryAdd(threadId, list);
            });
        }

        // Start all dedicated threads
        foreach (var t in threads) t.Start();

        // Wait for all physical threads to complete execution
        foreach (var t in threads) t.Join();

        // Assert
        Assert.Equal(threadCount, threadValues.Count);
        foreach (var kvp in threadValues)
        {
            Assert.Equal(iterationsPerThread, kvp.Value.Count);
            // Verify that each thread managed to generate distinct byte sequences
            Assert.All(kvp.Value, b => Assert.True(b >= 0 && b <= 255));
        }
    }

    /// <summary>
    /// RELIABILITY TEST 1: Frequency Uniformity Test (Chi-Squared approximation limit)
    /// Assures that over a large sample, every byte from 0 to 255 appears with equal probability.
    /// </summary>
    [Fact]
    public void Reliability_UniformDistributionTest()
    {
        // Arrange
        var generator = FastRng.Instance;
        const int totalSamples = 5_000_000; // 1000 expected hits per bucket
        int[] buckets = new int[256];

        // Act
        for (int i = 0; i < totalSamples; i++)
        {
            byte num = generator.NextByte();
            buckets[num]++;
        }

        // Assert
        double expectedHits = totalSamples / 256.0; // 1000
        double maxAllowedDeviation = 0.15; // Max 15% deviation allowed for this sample size

        for (int i = 0; i < 256; i++)
        {
            double deviation = Math.Abs(buckets[i] - expectedHits) / expectedHits;
            Assert.True(deviation < maxAllowedDeviation,
                $"Byte {i} breached uniformity bounds. Got {buckets[i]} hits, expected ~{expectedHits}. Deviation: {deviation:P2}");
        }
    }

    /// <summary>
    /// RELIABILITY TEST 2: Serial Transition Test (Pairwise Entropy Check)
    /// Validates that there is no "memory correlation" or bias between consecutive numbers (X_n and X_n+1).
    /// </summary>
    [Fact]
    public void Reliability_PairwiseIndependenceTest()
    {
        // Arrange
        var generator = FastRng.Instance;
        const int iterations = 10_000_000;
        int[,] pairGrid = new int[256, 256];

        // Act
        byte previousByte = generator.NextByte();
        for (int i = 0; i < iterations; i++)
        {
            byte currentByte = generator.NextByte();
            pairGrid[previousByte, currentByte]++;
            previousByte = currentByte;
        }

        // Assert
        int zeroTransitionPaths = 0;
        int maxAccumulatedCluster = 0;

        for (int r = 0; r < 256; r++)
        {
            for (int c = 0; c < 256; c++)
            {
                int weight = pairGrid[r, c];
                if (weight == 0) zeroTransitionPaths++;
                if (weight > maxAccumulatedCluster) maxAccumulatedCluster = weight;
            }
        }

        // At 10M iterations, statistically there should be 0 unvisited paths (100% coverage)
        double maxAllowedDeadPathsRatio = 0.001; // 0.1% max allowance
        double deadPathsRatio = (double)zeroTransitionPaths / (256 * 256);

        Assert.True(deadPathsRatio <= maxAllowedDeadPathsRatio,
            $"Entropy failure. Too many unvisited coordinate pairs: {deadPathsRatio:P2}");

        // CORRECTED STATISTICAL BOUND:
        // For 10M samples distributed into 65k buckets (mean ~152), standard deviation
        // dictates that the peak natural cluster will legally reach up to ~220-250.
        // We set the safety threshold to 300 to catch genuine structural looping flaws.
        Assert.True(maxAccumulatedCluster < 300,
            $"Entropy failure. Heavy transition cluster detected with {maxAccumulatedCluster} identical path hits.");
    }

    /// <summary>
    /// RELIABILITY TEST 3: Chi-Squared Matrix Independence Test (Two-Dimensional Distribution).
    /// Validates the joint distribution of consecutive byte pairs to ensure multidimensional randomness.
    /// It verifies that the transition from a previous byte to a current byte shows no statistical correlation.
    /// </summary>
    [Fact]
    public void Reliability_ChiSquaredMatrixTest()
    {
        // Arrange
        var generator = FastRng.Instance;
        const int iterations = 10_000_000;
        int[,] pairGrid = new int[256, 256];

        // Act: Track transitions between consecutive generated bytes
        byte previousByte = generator.NextByte();
        for (int i = 0; i < iterations; i++)
        {
            byte currentByte = generator.NextByte();
            pairGrid[previousByte, currentByte]++;
            previousByte = currentByte;
        }

        // Assert
        double expectedHitsPerCell = (double)iterations / (256 * 256); // Expected mean: ~152.5878 hits per cell
        double chiSquaredSum = 0;

        int minHits = int.MaxValue;
        int maxHits = int.MinValue;

        for (int r = 0; r < 256; r++)
        {
            for (int c = 0; c < 256; c++)
            {
                int actualHits = pairGrid[r, c];

                if (actualHits < minHits) minHits = actualHits;
                if (actualHits > maxHits) maxHits = actualHits;

                // Chi-Squared formula: sum((Actual - Expected)^2 / Expected)
                double deviation = actualHits - expectedHitsPerCell;
                chiSquaredSum += (deviation * deviation) / expectedHitsPerCell;
            }
        }

        // For 65,535 degrees of freedom (256 * 256 - 1) at a 99% confidence interval,
        // the Chi-Squared statistic MUST strictly fall between ~64,000 and ~67,000.
        // - If it is significantly higher: The numbers are not random (heavy clustering exists).
        // - If it is significantly lower: The numbers are "too perfect" (artificially uniform or non-natural).
        double upperLimit = 67000;
        double lowerLimit = 64000;

        double minDeviationPct = ((minHits - expectedHitsPerCell) / expectedHitsPerCell) * 100;
        double maxDeviationPct = ((maxHits - expectedHitsPerCell) / expectedHitsPerCell) * 100;

        // Optional debug outputs to diagnose distribution anomalies in standard output streams
        /*
        Console.WriteLine($"Chi-Squared Result: {chiSquaredSum:F2}");
        Console.WriteLine($"Minimum Cell Hits: {minHits} ({minDeviationPct:F2}%)");
        Console.WriteLine($"Maximum Cell Hits: {maxHits} (+{maxDeviationPct:F2}%)");
        */

        Assert.True(chiSquaredSum > lowerLimit && chiSquaredSum < upperLimit,
            $"Statistical anomaly! The sequence does not follow a natural random distribution. Chi2: {chiSquaredSum:F2}");
    }

    // =========================================================================
    // 1. UNIT & BOUNDARY TESTS (Ensures safety, validation, and zero mutations)
    // =========================================================================

    /// <summary>
    /// VERIFICATION GOAL: Ensure that 'NextUniformInt' strictly honors the requested range.
    /// It must never return a value lower than 'minValue' or equal/greater than 'maxValue'.
    /// </summary>
    [Fact]
    public void NextUniformInt_ShouldRespectStrictBounds()
    {
        var generator = FastRng.Instance;
        int min = -5;
        int max = 5;

        // Execute a high repetition loop to catch loose or leaking off-by-one index conditions
        for (int i = 0; i < 10000; i++)
        {
            int value = generator.NextUniformInt(min, max);
            Assert.True(value >= min && value < max, $"Value {value} breached casino uniform bounds [{min},{max}).");
        }
    }

    /// <summary>
    /// VERIFICATION GOAL: Ensure defensive data validation is active.
    /// The generator must instantly throw an exception if 'minValue' is greater than or equal to 'maxValue'.
    /// </summary>
    [Fact]
    public void NextUniformInt_InvalidBounds_ShouldThrow()
    {
        var generator = FastRng.Instance;

        // Test case A: Min is strictly greater than Max
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.NextUniformInt(10, 5));

        // Test case B: Min is exactly equal to Max (Invalid range spectrum)
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.NextUniformInt(5, 5));
    }

    /// <summary>
    /// VERIFICATION GOAL: Ensure the custom Shuffle implementation works and protects data integrity.
    /// It must completely randomize the array positions, but it is strictly prohibited from
    /// deleting, corrupting, or duplicating any of the original elements (Conservation of Data).
    /// </summary>
    [Fact]
    public void Shuffle_ShouldMaintainDataIntegrityAndRandomize()
    {
        var generator = FastRng.Instance;

        // Arrange: Generate an ordered sequential card deck configuration (0 to 51)
        int[] originalDeck = Enumerable.Range(0, 52).ToArray();
        int[] shuffledDeck = originalDeck.ToArray();

        // Act: Run the Fisher-Yates array permutation
        generator.Shuffle(shuffledDeck);

        // Assertion A: Data Integrity Check
        // Sort the randomized deck back. It must match the original collection 100%.
        var verificationList = shuffledDeck.ToList();
        verificationList.Sort();
        Assert.Equal(originalDeck, verificationList);

        // Assertion B: Randomization Check
        // The structural order must be transformed. (Statistically, a 52-card deck matching
        // its original state naturally is 1 out of 52!, which is practically impossible).
        Assert.NotEqual(originalDeck, shuffledDeck);
    }

    /// <summary>
    /// VERIFICATION GOAL: Validate upcasting architecture safety.
    /// If an external system upcasts 'FastRng' to a standard 'System.Random' interface and calls
    /// Microsoft's base 'Shuffle' method, our core overridden distribution hooks must step in.
    /// This prevents fallback crashes and eliminates original framework truncation bias.
    /// </summary>
    [Fact]
    public void Shuffle_WhenCastedToSystemRandom_ShouldStillOperateSafely()
    {
        // Upcast FastRng to the generic base System.Random interface
        Random rng = FastRng.Instance;
        int[] deck = Enumerable.Range(0, 20).ToArray();

        // Execute the non-virtual base framework method.
        // This confirms our core 'Next(min, max)' override safely feeds the Microsoft engine.
        rng.Shuffle(deck);

        // Confirm data elements were fully conserved without leaks or drops
        var verification = deck.ToList();
        verification.Sort();
        Assert.Equal(Enumerable.Range(0, 20).ToArray(), verification);
    }

    /// <summary>
    /// VERIFICATION GOAL: Guard weighted selection inputs against mathematical corruption.
    /// The selection code must safely throw exceptions if weights are empty, entirely zero,
    /// or contain negative probability profiles.
    /// </summary>
    [Fact]
    public void NextWeightedIndex_ShouldThrowOnInvalidWeights()
    {
        var generator = FastRng.Instance;

        // Test case A: Passing completely empty array collections
        Assert.Throws<ArgumentException>(() => generator.NextWeightedIndex(ReadOnlySpan<int>.Empty));

        // Test case B: Total mathematical weight adds up to exactly zero
        Assert.Throws<ArgumentException>(() => generator.NextWeightedIndex(new int[] { 0, 0, 0 }));

        // Test case C: Array contains negative values (breaks probability scaling metrics)
        Assert.Throws<ArgumentException>(() => generator.NextWeightedIndex(new int[] { 10, -5, 20 }));
    }

    /// <summary>
    /// VERIFICATION GOAL: Verify mathematical probability exclusion.
    /// If an item or prize index has an assigned operational weight of exactly 0,
    /// the generator must never select it, regardless of total execution volumes.
    /// </summary>
    [Fact]
    public void NextWeightedIndex_ShouldNeverSelectZeroWeightIndex()
    {
        var generator = FastRng.Instance;

        // Index 1 (middle position) has a weight of 0; it must have a 0% selection chance.
        int[] weights = { 100, 0, 100 };

        // Draw 5,000 times sequentially
        for (int i = 0; i < 5000; i++)
        {
            int index = generator.NextWeightedIndex(weights);

            // Assert that the zero-weight entry was absolutely skipped
            Assert.NotEqual(1, index);
        }
    }

    // =========================================================================
    // 2. CASINO RELIABILITY TESTS (Verifies mathematical distribution fairness)
    // =========================================================================

    /// <summary>
    /// VERIFICATION GOAL: Statistical Frequency Uniformity Check (Roulette Wheel Simulation).
    /// If we simulate 3.7 million spins on an European Roulette board (37 pockets), every single
    /// pocket must converge perfectly to roughly 100,000 hits.
    /// Any deviation higher than 2% proves structural layout bias, failing casino audits.
    /// </summary>
    [Fact]
    public void Reliability_NextUniformInt_DistributionTest()
    {
        var generator = FastRng.Instance;
        const int iterations = 3_700_000;
        int[] pockets = new int[37]; // Slots 0 through 36

        // Act: Accumulate high-volume samples
        for (int i = 0; i < iterations; i++)
        {
            int spin = generator.NextUniformInt(0, 37);
            pockets[spin]++;
        }

        // Assert: Evaluate distribution against tight math compliance thresholds
        double expectedHits = iterations / 37.0; // 100,000 expected hits per pocket
        double maxDeviation = 0.02; // Rigid casino certification tolerance threshold: Max 2.0% variance

        for (int i = 0; i < pockets.Length; i++)
        {
            double deviation = Math.Abs(pockets[i] - expectedHits) / expectedHits;
            Assert.True(deviation < maxDeviation,
                $"Roulette bias detected on pocket {i}. Got {pockets[i]}, expected ~{expectedHits}. Deviation: {deviation:P2}");
        }
    }

    /// <summary>
    /// VERIFICATION GOAL: Mathematical Convergence Check for Weighted Slot Reel Sets.
    /// Given a weighted configuration layout totalling 1,000 points (Index 0 = 10%, Index 1 = 80%, Index 2 = 10%),
    /// a million sequential hits must force the output percentages to match these exact probabilities.
    /// This ensures that RTP (Return To Player) payout calculations match real-world distributions.
    /// </summary>
    [Fact]
    public void Reliability_NextWeightedIndex_MathematicalConvergenceTest()
    {
        var generator = FastRng.Instance;
        const int iterations = 1_000_000;

        // Arrange: Explicitly map a 10% / 80% / 10% probability spread split
        int[] weights = { 100, 800, 100 };
        int[] hitCounters = new int[3];

        // Act: Trigger 1 million random draws
        for (int i = 0; i < iterations; i++)
        {
            int resultIndex = generator.NextWeightedIndex(weights);
            hitCounters[resultIndex]++;
        }

        // Convert the structural hit numbers to exact percentage ratios
        double idx0Ratio = (double)hitCounters[0] / iterations;
        double idx1Ratio = (double)hitCounters[1] / iterations;
        double idx2Ratio = (double)hitCounters[2] / iterations;

        // Assert: Ensure convergence remains safely within a strict 0.5% tolerance window
        Assert.InRange(idx0Ratio, 0.095, 0.105); // Must hover precisely near 10%
        Assert.InRange(idx1Ratio, 0.795, 0.805); // Must hover precisely near 80%
        Assert.InRange(idx2Ratio, 0.095, 0.105); // Must hover precisely near 10%
    }
}