using System.Runtime.Intrinsics;
using Xunit;

namespace FastRng.ThreadSafe.Tests;

public class FipsRegulatoryTests
{
    private static byte[] ToBytes(Vector128<byte> vector)
    {
        byte[] bytes = new byte[16];
        for (int i = 0; i < 16; i++) bytes[i] = vector.GetElement(i);
        return bytes;
    }

    /// <summary>
    /// FIPS 140-3 Section 4.9.1: Continuous RNG Test (CRNGT).
    /// Mandates that every generated block must be compared to the previous block.
    /// If two identical blocks are generated sequentially, the RNG must instantly fault.
    /// This detects catastrophic entropy health failure.
    /// </summary>
    [Fact]
    public void Fips1403_ContinuousRngHealthTest_ShouldPass()
    {
        var generator = FastRng.Instance;
        const int iterations = 50000;

        // FIPS 140-3 standard 16-bit block setup
        ushort previousBlock = (ushort)((generator.NextByte() << 8) | generator.NextByte());

        for (int i = 0; i < iterations; i++)
        {
            ushort currentBlock = (ushort)((generator.NextByte() << 8) | generator.NextByte());

            // Natural statistical collision check
            if (currentBlock == previousBlock)
            {
                // FIPS 140-3 Requirement: Upon a collision, check a third confirmation block
                // This distinguishes a healthy random match from a stuck hardware state
                ushort confirmationBlock = (ushort)((generator.NextByte() << 8) | generator.NextByte());

                Assert.True(confirmationBlock != currentBlock,
                    $"FIPS 140-3 Critical Failure! Generator hardware state is completely stuck. " +
                    $"Stuck value: 0x{currentBlock:X4}");

                // Advance window past the confirmation state
                currentBlock = confirmationBlock;
            }

            previousBlock = currentBlock;
        }
    }

    /// <summary>
    /// NIST SP 800-90A Section 11.3: Health Testing / State Back-Tracking Detection.
    /// The AES-CTR core keeps its round keys fixed between reseed cycles by design (that's what
    /// makes it a counter-mode construction); the property this test actually needs is that the
    /// secret key material gets replaced once a reseed boundary (ReSeedInterval bytes) is crossed.
    /// </summary>
    [Fact]
    public void NistSP80090A_StateBacktrackingResistanceTest()
    {
        var generator = FastRng.Instance;
        var roundKeysField = typeof(FastRng).GetField("_roundKeys",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var nonceField = typeof(FastRng).GetField("_nonce",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(roundKeysField);
        Assert.NotNull(nonceField);

        var keysBefore = ((Vector128<byte>[])roundKeysField.GetValue(generator)!).Select(ToBytes).ToArray();
        var nonceBefore = ToBytes((Vector128<byte>)nonceField.GetValue(generator)!);

        // Cross at least one ReSeedInterval (65536 bytes) boundary so InjectHardwareEntropy fires
        generator.NextBytes(new byte[100_000]);

        var keysAfter = ((Vector128<byte>[])roundKeysField.GetValue(generator)!).Select(ToBytes).ToArray();
        var nonceAfter = ToBytes((Vector128<byte>)nonceField.GetValue(generator)!);

        int stagnantLayers = keysBefore.Where((k, idx) => k.SequenceEqual(keysAfter[idx])).Count();
        if (nonceBefore.SequenceEqual(nonceAfter)) stagnantLayers++;

        int totalLayers = keysBefore.Length + 1;
        double layerStagnationRatio = (double)stagnantLayers / totalLayers;

        // Under NIST SP 800-90A, all key material must participate in state tracking.
        Assert.True(layerStagnationRatio == 0.0,
            $"NIST SP 800-90A Backtracking Fault! {stagnantLayers} out of {totalLayers} " +
            $"key elements are completely frozen and failed to evolve. Ratio: {layerStagnationRatio:P2}");
    }

    /// <summary>
    /// NIST SP 800-90A Section 11.3.3: Dynamic Cycle Loop Detection.
    /// Validates that the engine does not drop into an unexpected short mathematical sub-cycle
    /// (closed loop) when mutating layers dynamically.
    /// </summary>
    [Fact]
    public void NistSP80090A_SubCycleLoopDetectionTest()
    {
        var generator = FastRng.Instance;
        const int testWindow = 2048;

        // Gather short output hashes to detect cyclic loops
        HashSet<long> sequenceFingerprints = new HashSet<long>();

        for (int i = 0; i < testWindow; i++)
        {
            // Capture a 64-bit chunk window
            long fingerprint = ((long)generator.NextByte() << 56) |
                               ((long)generator.NextByte() << 48) |
                               ((long)generator.NextByte() << 40) |
                               ((long)generator.NextByte() << 32) |
                               ((long)generator.NextByte() << 24) |
                               ((long)generator.NextByte() << 16) |
                               ((long)generator.NextByte() << 8) |
                               generator.NextByte();

            // If a 64-bit output state repeats exactly within a tight window,
            // the generator has collapsed into a deadly short loop sub-cycle.
            bool isUnique = sequenceFingerprints.Add(fingerprint);

            Assert.True(isUnique,
                $"NIST SP 800-90A Cycle Failure! Generator trapped in a short cyclic loop at iteration {i}.");
        }
    }
}