# FastRng Performance Benchmarks

This directory contains the microbenchmark suite for `FastRng`, utilizing [BenchmarkDotNet](https://github.com) to measure nano-second execution latency, memory allocation behaviors, and generation throughput compared directly against the standard .NET runtime generators.

## Benchmarked Engines
* **`FastRng`**: The thread-isolated, zero-allocation, reduced-round AES-NI counter-mode generator, using `.NET 10` native hardware intrinsics (`System.Runtime.Intrinsics.X86.Aes`/`Avx2`).
* **`System.Random`**: The default pseudo-random number generator provided by the .NET core framework runtime (xoshiro256**-based, not cryptographically secure).
* **`RandomNumberGenerator`**: The framework's cryptographically secure RNG, backed by the OS CSPRNG. This is the baseline `FastRng` is designed to outperform while still using a real block cipher internally.

---

## Prerequisites

1. **.NET 10 SDK** must be installed on your machine.
2. The benchmark harness requires **Administrator / Elevated privileges** on certain operating systems (like Windows or Linux) to accurately lock CPU clock cycles and clear process interference.

---

## How to Run the Benchmarks

### 1. Using the .NET CLI (Recommended)
Navigate to the directory containing the benchmark project file (where the `.csproj` file for the benchmarks is located) and execute the command with a strict **Release configuration**:

```bash
dotnet run -c Release
```

### 2. Filtering Specific Benchmarks
If `GeneratorBenchmark.cs` contains multiple methods and you want to isolate a single test run, use the `--filter` flag:

```bash
dotnet run -c Release -- --filter *GeneratorBenchmark*
```

---

## Critical Rules for Accurate Results

* ⚠️ **Do Not Run in Debug Mode**: BenchmarkDotNet will throw an error and fail fast if you run it without `-c Release`. Unoptimized code distort compiler inlining optimizations.
* 💻 **Close Background Applications**: Close heavy resource consumers (browsers, IDE database syncs, Docker instances) while executing to prevent CPU throttling or context-switching jitter from poisoning the telemetry.
* 🔌 **Connect to Power**: If executing tests on a laptop, ensure it is plugged into AC power and set to a high-performance power plan to disable CPU core parking features.

---

## Understanding the Output Artifacts

Once execution completes, BenchmarkDotNet generates highly detailed statistical tables directly inside your terminal console. Additionally, it dumps permanent files in the following path:
