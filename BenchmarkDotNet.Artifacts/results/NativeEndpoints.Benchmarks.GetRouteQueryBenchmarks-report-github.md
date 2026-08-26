```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.111
  [Host]     : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v4


```
| Method           | Mean     | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|----------------- |---------:|---------:|---------:|------:|--------:|----------:|------------:|
| RawMinimalApi    | 34.23 μs | 1.531 μs | 4.465 μs |  1.02 |    0.19 |  10.54 KB |        1.00 |
| NativeReflective | 36.15 μs | 1.878 μs | 5.537 μs |  1.07 |    0.22 |  10.97 KB |        1.04 |
| NativeGenerated  | 31.41 μs | 1.433 μs | 4.225 μs |  0.93 |    0.17 |   10.7 KB |        1.02 |
| FastEndpoints    | 50.41 μs | 1.966 μs | 5.767 μs |  1.50 |    0.26 |  11.55 KB |        1.10 |
