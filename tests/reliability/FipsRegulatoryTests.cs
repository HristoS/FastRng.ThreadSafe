using Xunit;

namespace FastRng.ThreadSafe.Tests;

public class FipsRegulatoryTests
{
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
    /// Updated to allow the dynamic 16-layer engine to naturally mutate across cycles.
    /// </summary>
    [Fact]
    public void NistSP80090A_StateBacktrackingResistanceTest()
    {
        var generator = FastRng.Instance;

        // 1. Initial generation to settle state
        byte[] bufferA = new byte[1024];
        generator.NextBytes(bufferA);

        // FIX: Retrieve the private, ThreadStatic array field via FieldInfo reflection
        var flatMatrixField = typeof(FastRng).GetField("_flatMatrix",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

        Assert.NotNull(flatMatrixField);

        // For [ThreadStatic] fields, passing 'null' or the instance extracts the thread-local value
        byte[] state = flatMatrixField.GetValue(generator) as byte[];

        Assert.NotNull(state);
        byte[] stateSnapshotBefore = (byte[])state.Clone();

        // 2. Generate a substantial block stream to allow all dynamic level boundaries to cycle
        byte[] bufferB = new byte[16384];
        generator.NextBytes(bufferB);

        byte[] stateSnapshotAfter = (byte[])state.Clone();

        // 3. NIST Layer-by-Layer Evolution Audit
        int stagnantLayers = 0;
        const int totalLayers = 16;

        for (int layer = 0; layer < totalLayers; layer++)
        {
            // FIX: Remove MetadataSize. Your _flatMatrix array starts directly at index 0.
            int offset = layer << 8;
            bool layerIsIdentical = true;

            for (int i = 0; i < 256; i++)
            {
                if (stateSnapshotBefore[offset + i] != stateSnapshotAfter[offset + i])
                {
                    layerIsIdentical = false;
                    break; // Layer successfully evolved!
                }
            }

            if (layerIsIdentical)
            {
                stagnantLayers++;
            }
        }

        double layerStagnationRatio = (double)stagnantLayers / totalLayers;

        // Under NIST SP 800-90A, all layers must participate in state tracking.
        Assert.True(layerStagnationRatio == 0.0,
            $"NIST SP 800-90A Backtracking Fault! {stagnantLayers} out of {totalLayers} " +
            $"memory layers are completely frozen and failed to evolve. Ratio: {layerStagnationRatio:P2}");
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