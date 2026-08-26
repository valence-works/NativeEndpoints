using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

// No explicit job in the config, deliberately: with none, BenchmarkDotNet runs its default job,
// and a command-line `--job dry` (or `--job short`) replaces it instead of running alongside it.
// `--filter` narrows to specific benchmarks; see benchmarks/README.md for the invocations.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, DefaultConfig.Instance);
