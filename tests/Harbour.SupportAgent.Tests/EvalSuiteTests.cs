using Harbour.SupportAgent.Agent;
using Harbour.SupportAgent.Evals;

namespace Harbour.SupportAgent.Tests;

/// <summary>Runs the eval suite on every test run, so a guardrail regression fails the build.</summary>
public class EvalSuiteTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"support-test-{Guid.NewGuid():N}.db");

    public static TheoryData<string> ScenarioNames()
    {
        var data = new TheoryData<string>();
        foreach (var scenario in EvalRunner.LoadScenarios(EvalRunner.ScenarioDirectory)) data.Add(scenario.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Scenario_passes(string name)
    {
        var scenario = EvalRunner.LoadScenarios(EvalRunner.ScenarioDirectory).Single(s => s.Name == name);
        var result = await EvalRunner.RunAsync(scenario, new ConversationLog(_db));
        Assert.True(result.Passed, string.Join("\n", result.Failures));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }
}
