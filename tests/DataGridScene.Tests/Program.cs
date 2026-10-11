using DataGridScene.Validation;
using System.Text.Json;

if (args.Contains("--benchmark"))
{
    Directory.CreateDirectory("artifacts");
    var benchmark = new { runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        samples = PerformanceBenchmarks.Run() };
    var json = JsonSerializer.Serialize(benchmark, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json); await File.WriteAllTextAsync("artifacts/performance-benchmarks.json", json); return 0;
}

var report = await FilterPresentationValidationSuite.RunAsync();
foreach (var result in report.Results) Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {result.Name} ({result.Milliseconds:F2} ms){(result.Error is null ? "" : "\n" + result.Error)}");
Console.WriteLine($"{report.PassedCount}/{report.Results.Count} passed in {report.Milliseconds:F2} ms");
Directory.CreateDirectory("artifacts");
await File.WriteAllTextAsync("artifacts/core-validation.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return report.Passed ? 0 : 1;
