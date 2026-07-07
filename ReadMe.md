# FastRng.ThreadSafe 🚀

[![NuGet Version](https://img.shields.io/nuget/v/FastRng.ThreadSafe.svg)](https://www.nuget.org/packages/FastRng.ThreadSafe/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/FastRng.ThreadSafe.svg)](https://www.nuget.org/packages/FastRng.ThreadSafe/)
[![Build](https://github.com/HristoS/FastRng.ThreadSafe/actions/workflows/build.yml/badge.svg)](https://github.com/HristoS/FastRng.ThreadSafe/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A thread-safe, lock-free Pseudo-Random Number Generator for **.NET 10**, built on a reduced-round **AES-NI counter-mode** core. It's a drop-in replacement for `System.Random` that is measurably faster than `RandomNumberGenerator` (the framework's cryptographic RNG) on both single-value and bulk generation, while still being built on a real, published cryptographic primitive rather than an ad-hoc mixing function.

| | `FastRng` | `RandomNumberGenerator` (Crypto RNG) | `System.Random` |
|---|---|---|---|
| Single value | **2.6 ns** | 74.4 ns | 2.0 ns |
| 64 KB fill | **14.3 µs** | 19.4 µs | 8.6 µs |
| Core primitive | AES-NI (hardware) | OS CSPRNG | xoshiro256** |
| Thread-safe without locking | ✅ | ✅ (syscall-based) | ❌ (needs `Random.Shared`) |
| Designed to be unpredictable | ✅ (reduced-round AES) | ✅ | ❌ (explicitly not) |
| Casino-style helpers (weighted pick, unbiased shuffle, rejection-sampled uniform range) | ✅ | ❌ | partial (`Shuffle` only, .NET 8+) |

*(Benchmarks: Intel i7-10510U, .NET 10.0.5, Release, BenchmarkDotNet — see [`tests/benchmarks`](tests/benchmarks) for the full report and how to reproduce it on your own hardware.)*

`FastRng` inherits directly from `System.Random`, so existing code that takes a `Random` keeps working unchanged.

---

## Architecture

The generator is a counter-mode construction in the style of Random123's **ARS** generator (Salmon et al., *"Parallel Random Numbers: As Easy as 1, 2, 3"*, SC'11) — the same family of design as NIST SP 800-90A's `CTR_DRBG using AES`, but with a deliberately reduced round count for speed:

- Each 16-byte output block is `AES-Encrypt(counter XOR nonce, roundKeys)`, run for **5 AES rounds** instead of the full 10 used by standards-track AES-256. Five rounds is where published statistical batteries (and our own, see below) stop finding structure — it buys most of the diffusion quality of full AES at a fraction of the cost.
- **8 independent counter lanes** are encrypted per 128-byte chunk. AES-NI's `AESENC` has ~4-7 cycle latency but 1-cycle throughput, so running independent lanes back-to-back keeps the pipeline full instead of stalling on each block's latency.
- `NextByte()` pops from a small pool refilled one 128-byte chunk at a time by the exact same routine that backs `NextBytes()` — there's a single code path for both APIs, not a fast/slow pair that drifts apart.
- Round keys and the nonce are periodically remixed with fresh OS entropy (`RandomNumberGenerator`) every 64 KB of output, so key material doesn't stay static forever.

**Hardware requirement:** this design needs a CPU with **AES-NI** (and AVX2, used elsewhere in the library). That's every x86-64 CPU since roughly 2013 (Intel Haswell / AMD Excavator onward), but it will not run on ARM or on AES-NI-less hardware. There's currently no software fallback — if that matters for your deployment targets, open an issue.

---

## Statistical & Regulatory-Style Validation

35 automated tests run on every change, split across two independent axes people often conflate:

- **Both public APIs, tested separately.** `NextByte()` and `NextBytes()` share one implementation, but they're validated independently anyway — the test suite runs the full statistical battery against `NextBytes()`'s raw output stream as well as the byte-at-a-time path, rather than assuming one implies the other.
- **NIST SP 800-22 subset**: Frequency (Monobit), Runs, Approximate Entropy, Non-overlapping Template Matching.
- **Distributional tests**: 256-bucket uniformity, a 256×256 pairwise-transition Chi-Squared matrix over 10,000,000 samples, dead-path coverage.
- **FIPS 140-3 / SP 800-90A style health checks**: continuous RNG test (no stuck-output faults), sub-cycle loop detection, and a state-evolution check that the AES key material actually gets replaced across a reseed boundary.
- **Casino-specific correctness**: unbiased Fisher-Yates shuffle (deck conservation), weighted-index convergence to exact target probabilities, zero-probability exclusion, roulette-wheel-style uniformity at a 2% regulatory-style tolerance, modulo-bias elimination, cross-thread independence under concurrent load (20 threads × 5,000 draws, Pearson correlation), and a sliding-window predictability check.

Run them yourself:
```bash
dotnet test tests/reliability/FastRng.ThreadSafe.tests.csproj -c Release
```

---

## Is this certified for real-money casino use?

**Not yet — and be skeptical of any RNG library that claims otherwise without naming the lab.** Here's an honest breakdown of where this sits:

**What's true today:** the core design (AES counter mode) is architecturally the same family NIST standardized as an approved DRBG mechanism, and it passes a real, independently-checked statistical battery — not just "looks random," but the specific pass/fail criteria NIST SP 800-22 and comparable regulatory tests define, run against both public APIs.

**What real certification (GLI-19, iTech Labs, BMM Testlabs, eCOGRA, etc.) actually requires, that this project doesn't have yet:**
- Independent accredited-lab source code review and testing — self-testing, however rigorous, isn't a substitute.
- The full NIST SP 800-22 battery (15 tests) at regulatory sample sizes, typically supplemented with TestU01 Crush/BigCrush or DIEHARDER — this suite covers a useful subset, not the whole thing.
- A formally justified entropy source and reseed policy against SP 800-90B, if the security-strength claim depends on it.
- A documented decision on the reduced-round AES choice: either move to full-round AES for a "standards-literal" mode, or carry a citable cryptanalytic justification for 5 rounds being sufficient against the threat model regulators care about (not just statistical randomness — actual unpredictability under adversarial play).
- Change-control and audit trail: regulated RNGs generally can't be silently modified post-certification.

If you're evaluating this for a licensed product, treat it as a strong, fast, well-tested *building block* and budget for the accredited-lab pass — not as a finished, certified component.

---

## Where this sits versus other approaches

| Approach | Speed | Unpredictable? | Typical use |
|---|---|---|---|
| xoshiro/xoroshiro, PCG | Fastest (sub-ns) | ❌ No — designed to be fast, not secure; short output windows can reveal state | Simulations, games, non-adversarial contexts |
| `System.Random` (.NET) | Fast | ❌ No (uses xoshiro256** internally) | General-purpose app code |
| `RandomNumberGenerator` / OS CSPRNG | Slow (syscall overhead) | ✅ Yes | Keys, tokens, anything security-critical |
| ChaCha20-based fast CSPRNGs (e.g. `arc4random` on BSD/macOS) | Fast | ✅ Yes | OS-level userspace RNG |
| Full AES-CTR DRBG (NIST SP 800-90A) | Moderate | ✅ Yes, formally | FIPS-validated modules, certified gaming RNGs |
| **FastRng (this project)** | **Fast** (beats OS CSPRNG both ways) | Believed yes (reduced-round AES, not yet independently cryptanalyzed) | Game servers, simulations, RNG-heavy pipelines wanting crypto-grade design without OS-syscall cost |

The gap between "fast but not secure" (xoshiro/PCG) and "secure but slow" (OS CSPRNG) is exactly the niche this fills — using a real block cipher instead of a syscall is what makes both sides of that trade-off possible at once.

---

## ✨ Features

- **🛡️ Thread-Safe Without Locking**: One instance per thread via `[ThreadStatic]`, so there's no lock contention across concurrent pipelines.
- **🎰 Casino-Grade Uniformity**: Rejection-sampling based uniform ranges eliminate modulo and floating-point truncation bias.
- **🔀 Unbiased Fisher-Yates Shuffle**: For card decks, reel sets, and anything needing exact permutation probabilities.
- **⚖️ Weighted Index Selection**: For slot machine RTP tables, loot tables, and probability-weighted outcomes.
- **🔄 Drop-in `System.Random` Replacement**: Code that accepts a `Random` works unchanged, including third-party frameworks that upcast it.

---

## 💻 Installation

```bash
dotnet add package FastRng.ThreadSafe
```

or via the NuGet Package Manager Console:

```powershell
Install-Package FastRng.ThreadSafe
```

---

## 🚀 Quick Start

### 1. Basic Generation & Drop-In Replacement

```csharp
using FastRng.ThreadSafe;

// Safe to share across multiple tasks and threads simultaneously
Random rng = FastRng.Instance;

// Generates a perfectly uniform integer: 0 to 36 inclusive (e.g., European Roulette Wheel)
int rouletteSpin = rng.Next(0, 37);

double probability = rng.NextDouble();
byte randomByte = FastRng.Instance.NextByte();
```

### 2. Unbiased Card / Array Shuffling

```csharp
var rng = FastRng.Instance;

int[] playingCards = Enumerable.Range(0, 52).ToArray();
rng.Shuffle(playingCards); // exactly uniform permutation probabilities
```

### 3. Weighted Selection (Slot Machine Reel Strip)

```csharp
var rng = FastRng.Instance;

// Index 0 (Jackpot) = 1% chance, Index 1 = 19%, Index 2 = 80%
int[] prizeWeights = { 10, 190, 800 };
int winningPrizeIndex = rng.NextWeightedIndex(prizeWeights);
```

---

## ☕ Support This Project

`FastRng` is free, MIT-licensed, and maintained in my spare time. If it saved you the trouble of rolling your own thread-safe RNG (or just made your benchmarks look better), consider buying me a coffee — it directly funds the time spent on things like the AES-NI redesign and the statistical validation suite above:

**[ko-fi.com/hristostoev](https://ko-fi.com/hristostoev)**

Issues, PRs, and star-throwing are just as welcome.

---

## 📄 License

This project is licensed under the terms of the **MIT License**. The text of the license is included in full inside the root `LICENSE` file.
