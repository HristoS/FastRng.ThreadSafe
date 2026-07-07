# Changelog

All notable changes to this project are documented in this file.

## [1.0.7]

### Changed — Core generator rebuilt on AES-NI

The internal engine was replaced with a reduced-round AES-NI counter-mode construction (in the style of Random123's ARS generator — the same design family NIST standardized as `CTR_DRBG using AES`), running 5 AES rounds across 8 parallel counter lanes per 128-byte chunk. This replaces the previous 16-layer RC4-style cascade matrix.

`NextByte()` and `NextBytes()` now share a single implementation (`NextByte()` pops from a pool refilled one AES chunk at a time), instead of two separate algorithms of very different strength.

### Fixed — `NextBytes()` output was never actually validated for randomness quality

The full statistical/regulatory-style test suite always drove `NextByte()`, which used a different (and much stronger) algorithm than the vectorized `NextBytes()` bulk path. Testing `NextBytes()`'s raw output directly surfaced real failures in the previous design (pairwise-independence clustering, chi-squared over threshold, approximate-entropy pass rate below the NIST minimum) — independent of anything else in this release, this was a pre-existing latent defect in the bulk-fill API. The AES-NI redesign resolves it: both APIs now pass the full suite, including 7 new tests that exercise `NextBytes()` directly (`NextBytesStreamTests.cs`).

### Performance

| | Before | After | vs. `RandomNumberGenerator` |
|---|---|---|---|
| Single value (`NextByte`) | 69.5 ns | **2.6 ns** | ~29x faster (was ~1.1x) |
| Bulk 64 KB fill (`NextBytes`) | 25.5 µs | **14.3 µs** | ~26% faster (was ~20% *slower*) |

`NextBytes()` bulk throughput now beats the OS cryptographic RNG, which it previously did not.

### Fixed — packaging metadata

- `FastRng.ThreadSafe.csproj` had two `<Description>` elements; MSBuild silently used the last one, so the description actually published to NuGet was just the Ko-fi blurb, not a description of the package. Consolidated into one.
- `PackageTags` updated to match the current architecture (`aes-ni`, `ctr-drbg`); removed stale tags (`rc4`, `avalanche-effect`, `l1-cache`) describing the old design.

### Documentation

- `ReadMe.md` rewritten to describe the current AES-NI architecture (the previous version described a "6-layer" cascade, which was already inconsistent with the shipped 16-layer implementation before this release). Added a comparison table against `System.Random`/`RandomNumberGenerator`/other PRNG families, an honest "certification status" section (what's validated today vs. what accredited-lab casino certification — GLI, iTech Labs, BMM — actually requires), and fixed non-functional badge placeholders.
- `tests/benchmarks/readme.md` updated to list `RandomNumberGenerator` among the benchmarked engines and correct a stale architecture description.

### Tests

- Added `tests/reliability/NextBytesStreamTests.cs`: Chi-Squared matrix, pairwise independence, uniform distribution, NIST Monobit/Runs/Approximate Entropy/Template Matching — all run directly against `NextBytes()` output rather than `NextByte()`.
- Rewrote `FipsRegulatoryTests.NistSP80090A_StateBacktrackingResistanceTest` to reflect the new internal state (`_roundKeys`, `_nonce`) instead of the removed `_flatMatrix`, and to verify key material is actually replaced across a reseed boundary.

### Known limitations

- Requires AES-NI and AVX2 hardware support (effectively any x86-64 CPU since ~2013). There is no software fallback; running on unsupported hardware will throw. This was already true of the bulk path before this release, but now applies to `NextByte()` as well.
- The 5-round AES choice is a statistically-validated engineering judgment call (Random123's own ARS-5 precedent, plus this project's own test suite), not a formally proven cryptanalytic result. See the README's certification-status section before using this in a regulated real-money context.
