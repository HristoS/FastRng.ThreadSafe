```

BenchmarkDotNet v0.14.0, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core i7-10510U CPU 1.80GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2


```
| Method                 | Mean             | Ratio      | RatioSD   | Allocated | Alloc Ratio |
|----------------------- |-----------------:|-----------:|----------:|----------:|------------:|
| SystemRandom_Next      |         2.073 ns |       1.01 |      0.15 |         - |          NA |
| FastRng_NextByte       |        53.758 ns |      26.20 |      2.87 |         - |          NA |
| SystemRandom_NextBytes |     9,893.751 ns |   4,822.22 |    657.80 |         - |          NA |
| FastRng_NextBytes      | 1,165,691.927 ns | 568,158.45 | 56,063.63 |         - |          NA |
