using BenchmarkDotNet.Running;
using SvelteSharp.Benchmarks;

BenchmarkSwitcher.FromTypes([typeof(SsrRenderingBenchmarks)]).Run(args);
