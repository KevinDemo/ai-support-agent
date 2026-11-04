// Runs every scripted conversation and writes evals/results/latest.md. Exit code 1 if any fail.
// Usage: dotnet run --project evals/Harbour.SupportAgent.Evals -- [output.md]
// With HARBOUR_MODEL_PROVIDER=anthropic, scenarios without a script use the real model. Those calls cost money.
using System.Text;
using Harbour.SupportAgent.Agent;
using Harbour.SupportAgent.Evals;

var dbPath = Path.Combine(Path.GetTempPath(), $"support-evals-{Guid.NewGuid():N}.db");
var log = new ConversationLog(dbPath);
var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

var results = new List<EvalResult>();
foreach (var scenario in EvalRunner.LoadScenarios(EvalRunner.ScenarioDirectory))
{
    var result = await EvalRunner.RunAsync(scenario, log, () => ChatModelFactory.Create(Environment.GetEnvironmentVariable, http));
    results.Add(result);
    Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")}  {result.Name}");
    foreach (var failure in result.Failures) Console.WriteLine($"      {failure}");
}

var passed = results.Count(r => r.Passed);
Console.WriteLine($"\n{passed} passed, {results.Count - passed} failed, {results.Count} total");

var report = new StringBuilder();
report.AppendLine("# Evaluation results").AppendLine();
report.AppendLine($"{passed} passed, {results.Count - passed} failed, {results.Count} total.").AppendLine();
report.AppendLine("| Scenario | Model | Result | Tool calls (outcome) |").AppendLine("|---|---|---|---|");
foreach (var r in results)
{
    var tools = r.ToolEvents.Count == 0 ? "none" : string.Join(", ", r.ToolEvents.Select(e => $"`{e.Tool}` ({e.Outcome})"));
    report.AppendLine($"| {r.Name} | {r.Model} | {(r.Passed ? "pass" : "FAIL")} | {tools} |");
}
if (args.Length > 0)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
    File.WriteAllText(args[0], report.ToString());
}

Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
File.Delete(dbPath);
return results.All(r => r.Passed) ? 0 : 1;
