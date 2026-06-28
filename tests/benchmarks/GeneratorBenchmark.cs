using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using System.Security.Cryptography;

namespace FastRng.ThreadSafe.Benchmarks;

// Masks allocation profiling metrics and statistical variance indicators in the terminal output
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median")]
public class GeneratorBenchmark
{
    private const int BufferSize = 65536; // Adjusted to exactly 64KB (2^16) for bulk pipeline throughput metrics
    private byte[] _sharedBuffer = null!;
    private System.Random _systemRandom = null!;

    // Allocated statically on startup to prevent individual Crypto RNG heap allocations
    // from injecting garbage collector noise into tight microsecond calculation measurements
    private byte[] _singleByteOutputBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sharedBuffer = new byte[BufferSize];
        _systemRandom = new System.Random();
        _singleByteOutputBuffer = new byte[1];
    }

    // === TEST 1: SINGLE SCALAR METRIC EVALUATION ===
    [Benchmark(Baseline = true)]
    public int SystemRandom_Next() => _systemRandom.Next(0, 256);

    [Benchmark]
    public byte FastRng_NextByte() => FastRng.Instance.NextByte();

    [Benchmark]
    public byte CryptoRandom_NextByte()
    {
        // Extracts a single strong cryptographic entropy element directly from the OS kernel context
        RandomNumberGenerator.Fill(_singleByteOutputBuffer);
        return _singleByteOutputBuffer[0];
    }

    // === TEST 2: BULK DATA ARRAYS THROUGHPUT (64KB BUFFER) ===
    [Benchmark]
    public void SystemRandom_NextBytes() => _systemRandom.NextBytes(_sharedBuffer);

    [Benchmark]
    public void FastRng_NextBytes() => FastRng.Instance.NextBytes(_sharedBuffer);

    [Benchmark]
    public void CryptoRandom_NextBytes()
    {
        // populates the entire 64KB destination backplane using block-level OS entropy streams
        RandomNumberGenerator.Fill(_sharedBuffer);
    }
}

public class Program
{
    public static void Main(string[] args) => BenchmarkRunner.Run<GeneratorBenchmark>();
}