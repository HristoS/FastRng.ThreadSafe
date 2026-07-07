```

BenchmarkDotNet v0.14.0, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core i7-10510U CPU 1.80GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2


```
| Method                 | Mean          | Ratio    | RatioSD | Allocated | Alloc Ratio |
|----------------------- |--------------:|---------:|--------:|----------:|------------:|
| SystemRandom_Next      |      1.993 ns |     1.00 |    0.08 |         - |          NA |
| FastRng_NextByte       |      2.576 ns |     1.30 |    0.08 |         - |          NA |
| CryptoRandom_NextByte  |     74.378 ns |    37.44 |    2.35 |         - |          NA |
| SystemRandom_NextBytes |  8,584.201 ns | 4,320.79 |  258.21 |         - |          NA |
| FastRng_NextBytes      | 14,323.777 ns | 7,209.75 |  513.53 |         - |          NA |
| CryptoRandom_NextBytes | 19,375.017 ns | 9,752.25 |  575.91 |         - |          NA |
