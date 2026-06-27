```

BenchmarkDotNet v0.14.0, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core i7-10510U CPU 1.80GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2


```
| Method                 | Mean             | Ratio        | RatioSD    | Allocated | Alloc Ratio |
|----------------------- |-----------------:|-------------:|-----------:|----------:|------------:|
| SystemRandom_Next      |         1.924 ns |         1.00 |       0.04 |         - |          NA |
| FastRng_NextByte       |        79.774 ns |        41.49 |       1.41 |         - |          NA |
| SystemRandom_NextBytes |     9,232.516 ns |     4,802.27 |     153.39 |         - |          NA |
| FastRng_NextBytes      | 3,843,189.980 ns | 1,999,025.30 | 113,474.84 |         - |          NA |
