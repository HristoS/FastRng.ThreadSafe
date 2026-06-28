```

BenchmarkDotNet v0.14.0, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core i7-10510U CPU 1.80GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2


```
| Method                 | Mean          | Ratio     | RatioSD | Allocated | Alloc Ratio |
|----------------------- |--------------:|----------:|--------:|----------:|------------:|
| SystemRandom_Next      |      1.585 ns |      1.00 |    0.02 |         - |          NA |
| FastRng_NextByte       |     68.756 ns |     43.40 |    1.43 |         - |          NA |
| CryptoRandom_NextByte  |     76.163 ns |     48.07 |    1.22 |         - |          NA |
| SystemRandom_NextBytes |  9,066.840 ns |  5,722.62 |  140.34 |         - |          NA |
| FastRng_NextBytes      | 23,332.922 ns | 14,726.79 |  380.85 |         - |          NA |
| CryptoRandom_NextBytes | 20,273.207 ns | 12,795.63 |  248.26 |         - |          NA |
