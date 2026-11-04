using System.Text.Json;
using Harbour.SupportAgent.Agent;

namespace Harbour.SupportAgent.Evals;

public sealed record ScriptStep(string? Text, string? Tool, JsonElement? Input, string? Error);

public sealed record Turn(string? Customer, string? Button, string? ReplyContains, string? ReplyNotContains);

public sealed record Expectation(
    List<string>? ToolsAttempted, List<string>? ToolsNotAttempted, List<string>? ToolsBlocked, List<string>? ToolsExecuted,
    List<string>? Mutations, bool? HandedOff, bool? PendingAtEnd);

public sealed record Scenario(string Name, string Why, List<ScriptStep>? Script, List<Turn> Turns, Expectation Expect);

public sealed record EvalResult(string Name, string Model, bool Passed, IReadOnlyList<string> Failures, IReadOnlyList<ToolEvent> ToolEvents);

/// <summary>A model that does exactly what the scenario says, including things a good model would never do.</summary>
public sealed class ScriptedModel(IEnumerable<ScriptStep> steps) : IChatModel
{
    private readonly Queue<ScriptStep> _steps = new(steps);

    public string Name => "fooled (scripted)";

    public Task<ModelResponse> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (_steps.Count == 0) return Task.FromResult(ModelResponse.Text("Is there anything else?"));
        var step = _steps.Dequeue();
        if (step.Error is not null) throw new ModelException(step.Error);
        if (step.Tool is not null)
        {
            return Task.FromResult(new ModelResponse([new ToolUseBlock($"call_{Guid.NewGuid():N}", step.Tool, step.Input ?? JsonSerializer.SerializeToElement(new { }))], "tool_use"));
        }
        return Task.FromResult(ModelResponse.Text(step.Text ?? ""));
    }
}

public static class EvalRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<Scenario> LoadScenarios(string directory) =>
        Directory.EnumerateFiles(directory, "*.json").Order()
            .SelectMany(file => JsonSerializer.Deserialize<List<Scenario>>(File.ReadAllText(file), Json)!)
            .ToList();

    public static string ScenarioDirectory => Path.Combine(AppContext.BaseDirectory, "scenarios");

    public static async Task<EvalResult> RunAsync(Scenario scenario, ConversationLog log, Func<IChatModel>? realModel = null)
    {
        var api = new FakeTrackingApi();
        IChatModel model = scenario.Script is not null
            ? new ScriptedModel(scenario.Script)
            : realModel?.Invoke() ?? new RuleBasedFakeModel();
        var runner = new AgentRunner(model, new ToolGateway(api), log);
        var state = runner.Start();
        var failures = new List<string>();

        foreach (var turn in scenario.Turns)
        {
            var result = turn.Button is null
                ? await runner.HandleMessageAsync(state, turn.Customer!, default)
                : await runner.DecideAsync(state, state.Pending?.Id ?? "none", turn.Button == "confirm", default);

            if (turn.ReplyContains is { } contains && !result.Reply.Contains(contains, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Reply to '{turn.Customer ?? turn.Button}' should contain '{contains}' but was: {result.Reply}");
            }
            if (turn.ReplyNotContains is { } notContains && result.Reply.Contains(notContains, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Reply to '{turn.Customer ?? turn.Button}' must not contain '{notContains}' but was: {result.Reply}");
            }
        }

        var attempted = state.ToolEvents.Select(e => e.Tool).ToHashSet();
        var expect = scenario.Expect;
        foreach (var tool in expect.ToolsAttempted ?? []) if (!attempted.Contains(tool)) failures.Add($"Expected an attempt to call {tool}.");
        foreach (var tool in expect.ToolsNotAttempted ?? []) if (attempted.Contains(tool)) failures.Add($"{tool} should not have been attempted.");
        foreach (var tool in expect.ToolsBlocked ?? [])
        {
            if (!state.ToolEvents.Any(e => e.Tool == tool && e.Outcome == ToolOutcomes.Blocked)) failures.Add($"Expected {tool} to be blocked.");
        }
        foreach (var tool in expect.ToolsExecuted ?? [])
        {
            if (!state.ToolEvents.Any(e => e.Tool == tool && e.Outcome == ToolOutcomes.Executed)) failures.Add($"Expected {tool} to be executed.");
        }

        // Always checked: what actually changed in the tracking system is the outcome that matters most.
        var mutations = api.Mutations.Select(m => $"{m.Kind}:{m.Consignment}").ToList();
        var expectedMutations = expect.Mutations ?? [];
        if (!mutations.SequenceEqual(expectedMutations))
        {
            failures.Add($"Expected changes [{string.Join(", ", expectedMutations)}], got [{string.Join(", ", mutations)}].");
        }

        if (expect.HandedOff is { } handedOff && state.HandedOff != handedOff) failures.Add($"Expected handed off = {handedOff}, was {state.HandedOff}.");
        if (expect.PendingAtEnd is { } pending && state.Pending is not null != pending) failures.Add($"Expected pending action = {pending}, was {state.Pending is not null}.");

        return new EvalResult(scenario.Name, model.Name, failures.Count == 0, failures, state.ToolEvents);
    }
}
