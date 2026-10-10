using DataGridScene.Validation;
using System.Text.Json;

var report = await WorkspaceValidationSuite.RunAsync();
foreach (var result in report.Results) Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {result.Name} ({result.Milliseconds:F2} ms){(result.Error is null ? "" : "\n" + result.Error)}");
Console.WriteLine($"{report.PassedCount}/{report.Results.Count} passed in {report.Milliseconds:F2} ms");
Directory.CreateDirectory("artifacts");
await File.WriteAllTextAsync("artifacts/core-validation.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return report.Passed ? 0 : 1;
