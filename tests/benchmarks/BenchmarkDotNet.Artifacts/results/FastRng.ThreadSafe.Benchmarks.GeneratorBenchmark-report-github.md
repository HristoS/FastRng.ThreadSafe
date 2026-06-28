```

BenchmarkDotNet v0.14.0, Windows 10 (10.0.19045.6456/22H2/2022Update)
Intel Core i7-10510U CPU 1.80GHz, 1 CPU, 8 logical and 4 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.5 (10.0.526.15411), X64 RyuJIT AVX2


```
| Method                 | Mean             | Ratio        | RatioSD    | Allocated | Alloc Ratio |
|----------------------- |-----------------:|-------------:|-----------:|----------:|------------:|
| SystemRandom_Next      |         1.829 ns |         1.01 |       0.17 |         - |          NA |
| FastRng_NextByte       |        48.939 ns |        27.11 |       2.94 |         - |          NA |
| SystemRandom_NextBytes |     9,047.131 ns |     5,012.53 |     543.84 |         - |          NA |
| FastRng_NextBytes      | 2,672,586.458 ns | 1,480,737.22 | 160,136.04 |         - |          NA |
