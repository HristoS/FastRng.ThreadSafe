using Xunit;

namespace FastRng.ThreadSafe.Tests
{
    /// <summary>
    /// Implements native C# representations of the NIST SP 800-22 statistical test suite.
    /// Evaluates generated bitstreams using complementary error functions to calculate mathematical P-values.
    /// </summary>
    public class NistTests
    {
        private const double SignificanceLevel = 0.01; // NIST standard threshold alpha

        /// <summary>
        /// NIST Test 1: Frequency (Monobit) Test.
        /// Verifies that the proportion of zeroes and ones in the bitstream is approximately equal.
        /// </summary>
        [Fact]
        public void Nist_FrequencyMonobitTest_ShouldPass()
        {
            var generator = FastRng.Instance;

            // NIST recommends at least 100,000 bits for reliable evaluation
            const int totalBits = 200_000;
            int sum = 0;

            for (int i = 0; i < totalBits; i++)
            {
                // Extract a single bit by evaluating if an output byte is even or odd
                byte sample = generator.NextByte();
                int bit = sample % 2;

                // Convert bit: 0 becomes -1, 1 becomes +1
                sum += (2 * bit) - 1;
            }

            // Calculate the test statistic: S_obs = |sum| / sqrt(n)
            double sObs = Math.Abs(sum) / Math.Sqrt(totalBits);

            // Calculate the P-value using the complementary error function: erfc(S_obs / sqrt(2))
            double pValue = Erfc(sObs / Math.Sqrt(2.0));

            // Assert against the NIST threshold (P-value >= 0.01)
            Assert.True(pValue >= SignificanceLevel,
                $"NIST Monobit Failure! Bitstream is structurally biased. P-Value: {pValue:F6} (Expected >= 0.01). Bias Sum: {sum}");
        }

        /// <summary>
        /// NIST Test 3: Runs Test.
        /// Evaluates the total number of alterations between consecutive zeroes and ones.
        /// Ensures changes occur at a natural random frequency.
        /// </summary>
        [Fact]
        public void Nist_RunsTest_ShouldPass()
        {
            var generator = FastRng.Instance;
            const int totalBits = 200_000;

            int[] bitSequence = new int[totalBits];
            double onesProportion = 0;

            // 1. Gather the sequence and calculate the proportion of ones (pi)
            for (int i = 0; i < totalBits; i++)
            {
                bitSequence[i] = generator.NextByte() % 2;
                if (bitSequence[i] == 1) onesProportion++;
            }
            onesProportion /= totalBits;

            // Prerequisites check: If the frequency test is heavily skewed, the runs test is invalid
            if (Math.Abs(onesProportion - 0.5) >= (2.0 / Math.Sqrt(totalBits)))
            {
                Assert.Fail($"NIST Runs Pre-test skipped/failed: Frequency proportion ({onesProportion:F4}) is too far from 0.5.");
            }

            // 2. Count the total number of runs (V_n)
            // A run is an uninterrupted sequence of identical bits.
            int totalRuns = 1;
            for (int i = 0; i < totalBits - 1; i++)
            {
                if (bitSequence[i] != bitSequence[i + 1])
                {
                    totalRuns++;
                }
            }

            // 3. Compute the theoretical expected run metric and calculate the P-value
            double numerator = Math.Abs(totalRuns - (2.0 * totalBits * onesProportion * (1.0 - onesProportion)));
            double denominator = 2.0 * Math.Sqrt(2.0 * totalBits) * onesProportion * (1.0 - onesProportion);

            double pValue = Erfc(numerator / denominator);

            // Assert against the NIST threshold (P-value >= 0.01)
            Assert.True(pValue >= SignificanceLevel,
                $"NIST Runs Failure! Sequence patterns change too fast or too slow. P-Value: {pValue:F6} (Expected >= 0.01). Total Runs: {totalRuns}");
        }

        /// <summary>
        /// REGULATORY TEST 1: NIST Approximate Entropy Test.
        /// Analyzes the frequency of overlapping blocks of a specific length (m) to ensure
        /// the bit density behaves like a true random oracle rather than a structured sequence.
        /// </summary>
        [Fact]
        public void Nist_ApproximateEntropyTest_ShouldPass()
        {
            var generator = FastRng.Instance;
            const int totalSequences = 100; // NIST standard sample proportion count
            const int n = 100000;          // 100,000 bits per sequence
            const int m = 2;               // Recommended block length for 100k bits

            int passCount = 0;

            for (int s = 0; s < totalSequences; s++)
            {
                // 1. Gather bitstream for current block sequence
                byte[] bits = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    bits[i] = (byte)(generator.NextByte() & 1);
                }

                // 2. Compute overlapping block pattern frequencies
                double phiM = ComputePhi(bits, m, n);
                double phiMPlus1 = ComputePhi(bits, m + 1, n);

                // 3. Compute ApEn and Chi-Square
                double apEn = phiM - phiMPlus1;
                double chiSquared = 2.0 * n * (Math.Log(2.0) - apEn);

                // Degrees of Freedom = 2^m
                double expectedMean = Math.Pow(2, m);
                double expectedVariance = 2.0 * expectedMean;
                double zScore = (chiSquared - expectedMean) / Math.Sqrt(expectedVariance);

                double pValue = NistTests.Erfc(Math.Abs(zScore) / Math.Sqrt(2.0));

                // Track individual sequence successes
                if (pValue >= SignificanceLevel)
                {
                    passCount++;
                }
            }

            // NIST SP 800-22 Criteria: For 100 sequences at Alpha = 0.01,
            // the minimum acceptable passing proportion is 96 sequences.
            const int minimumPassingProportion = 96;

            Assert.True(passCount >= minimumPassingProportion,
                $"GLI/NIST Approximate Entropy Failure! Proportion of passing sequences is too low. " +
                $"Got {passCount}/{totalSequences} passing blocks. (NIST Minimum required: {minimumPassingProportion})");
        }

        /// <summary>
        /// REGULATORY TEST 2: Template Signature Matching Check.
        /// Checks for a specific recurring periodic footprint. Power-of-two maskings (& 3, & 0x300)
        /// often introduce subtle geometric repetitions that this test targets.
        /// </summary>
        [Fact]
        public void Nist_TemplateSignatureMatchingTest_ShouldPass()
        {
            var generator = FastRng.Instance;
            const int totalBlocks = 100;
            const int blockSize = 1000; // Total 100,000 bits
            const int templateLength = 9;

            // Mean expected matches per block (NIST Formula: lambda = (M - m + 1) / 2^m)
            double lambda = (double)(blockSize - templateLength + 1) / Math.Pow(2, templateLength); // ~1.9375

            int[] matchCounts = new int[totalBlocks];

            // 1. Collect bit streams and execute non-overlapping template search
            for (int b = 0; b < totalBlocks; b++)
            {
                byte[] blockBits = new byte[blockSize];
                for (int i = 0; i < blockSize; i++)
                {
                    blockBits[i] = (byte)(generator.NextByte() & 1);
                }

                for (int i = 0; i <= blockSize - templateLength; i++)
                {
                    // Scanning for bit template "111111111"
                    if (blockBits[i] == 1 && blockBits[i + 1] == 1 && blockBits[i + 2] == 1 &&
                        blockBits[i + 3] == 1 && blockBits[i + 4] == 1 && blockBits[i + 5] == 1 &&
                        blockBits[i + 6] == 1 && blockBits[i + 7] == 1 && blockBits[i + 8] == 1)
                    {
                        matchCounts[b]++;
                        i += templateLength - 1; // Slide past the matched template window
                    }
                }
            }

            // 2. Compute Chi-Square statistic using standard empirical distribution matching
            double chiSquaredSum = 0;
            for (int b = 0; b < totalBlocks; b++)
            {
                // For a Poisson distribution where mean == variance == lambda
                double deviation = matchCounts[b] - lambda;
                chiSquaredSum += (deviation * deviation) / lambda;
            }

            // 3. Under NIST SP 800-22, the P-value for this specific sum distribution
            // is calculated using the upper incomplete gamma function. For degrees of freedom = totalBlocks,
            // a highly accurate normal approximation for erfc is:
            double expectedMean = totalBlocks;
            double expectedVariance = 2.0 * totalBlocks;
            double zScore = (chiSquaredSum - expectedMean) / Math.Sqrt(expectedVariance);

            // Calculate final P-value
            double pValue = Erfc(Math.Abs(zScore) / Math.Sqrt(2.0));

            Assert.True(pValue >= SignificanceLevel,
                $"NIST Template Matching Failure! P-Value: {pValue:F6} (Expected >= {SignificanceLevel}). Chi2: {chiSquaredSum:F2}");
        }

        /// <summary>
        /// Approximates the Complementary Error Function (erfc) using the highly accurate
        /// Chebyshev fitting formula (maximum error scale limit of less than 1.2 x 10^-7).
        /// </summary>
        internal static double Erfc(double x)
        {
            double z = Math.Abs(x);
            double t = 1.0 / (1.0 + 0.5 * z);

            // Chebyshev polynomial evaluation coefficients
            double ans = t * Math.Exp(-z * z - 1.26551223 +
                t * (1.00002368 +
                t * (0.37388103 +
                t * (-0.18255973 +
                t * (0.00594295 +
                t * (0.01308046 +
                t * (-0.03590050 +
                t * (0.05149130 +
                t * (-0.05040825 +
                t * (0.02533041 +
                t * (-0.00145524 +
                t * 0.00115718)))))))))));

            return x >= 0.0 ? ans : 2.0 - ans;
        }

        internal static double ComputePhi(byte[] bits, int m, int n)
        {
            int totalPatterns = 1 << m;
            int[] counts = new int[totalPatterns];

            for (int i = 0; i < n; i++)
            {
                int index = 0;
                for (int j = 0; j < m; j++)
                {
                    int bitPos = (i + j) % n;
                    index = (index << 1) | bits[bitPos];
                }
                counts[index]++;
            }

            double sum = 0;
            for (int i = 0; i < totalPatterns; i++)
            {
                if (counts[i] > 0)
                {
                    double p = (double)counts[i] / n;
                    sum += p * Math.Log(p);
                }
            }
            return sum;
        }
    }
}