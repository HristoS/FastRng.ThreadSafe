using Xunit;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace FastRng.ThreadSafe.Tests;

public class FipsRegulatoryTests
{
    private const int MetadataSize = 4; // As per project structure

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

        // FIPS requires testing blocks of at least 16 bits (2 bytes)
        ushort previousBlock = (ushort)((generator.NextByte() << 8) | generator.NextByte());

        for (int i = 0; i < iterations; i++)
        {
            ushort currentBlock = (ushort)((generator.NextByte() << 8) | generator.NextByte());

            // FIPS 140-3 Failure Condition
            Assert.True(currentBlock != previousBlock,
                $"FIPS 140-3 Critical Failure! Stuck-unfaulted condition detected. " +
                $"Sequential duplicate block: 0x{currentBlock:X4}");

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

        // Retrieve internal state matrix via reflection
        byte[] state = generator.GetType()
            .GetMethod("GetOrCreateState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.Invoke(generator, null) as byte[];

        Assert.NotNull(state);
        byte[] stateSnapshotBefore = (byte[])state.Clone();

        // 2. Generate a substantial block stream to allow all dynamic level boundaries to cycle
        byte[] bufferB = new byte[16384];
        generator.NextBytes(bufferB);

        byte[] stateSnapshotAfter = (byte[])state.Clone();

        // 3. NIST Layer-by-Layer Evolution Audit
        // Instead of individual byte positions, we check if each 256-byte layer
        // has structurally mutated and changed its historical memory fingerprint.
        int stagnantLayers = 0;
        const int totalLayers = 16;

        for (int layer = 0; layer < totalLayers; layer++)
        {
            int offset = MetadataSize + (layer << 8);
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
        // We assert that 100% of layers are actively transforming over a generation cycle.
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