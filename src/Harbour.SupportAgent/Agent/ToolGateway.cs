using System.Collections.Concurrent;
using System.Text.Json;

namespace Harbour.SupportAgent.Agent;

public static class ToolNames
{
    public const string VerifyIdentity = "verify_identity";
    public const string Track = "track_consignment";
    public const string Reschedule = "reschedule_delivery";
    public const string DamageClaim = "open_damage_claim";
    public const string Handoff = "handoff_to_human";
}

public sealed record PendingAction(string Id, string Tool, string Consignment, string Detail, string Summary);

public sealed record ToolEvent(string Tool, string Outcome, string? Reason);

public static class ToolOutcomes
{
    public const string Executed = "executed";
    public const string Blocked = "blocked";
    public const string Pending = "pending_confirmation";
    public const string Failed = "failed";
}

public sealed class ConversationState(string id)
{
    public string Id { get; } = id;
    public string? VerifiedConsignment { get; set; }
    public int FailedVerifications { get; set; }
    public PendingAction? Pending { get; set; }
    public string? HandoffTicket { get; set; }
    public bool HandedOff => HandoffTicket is not null;
    public List<ChatMessage> Messages { get; } = [];
    public List<ToolEvent> ToolEvents { get; } = [];
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

public sealed record ToolResult(string Json, bool IsError, string Outcome);

/// <summary>
/// The guardrails. Every tool call the model makes goes through here, and the rules are code, so they hold
/// even when the model has been talked into something. The model has no way to mark an action as confirmed.
/// </summary>
public sealed class ToolGateway(ITrackingApi api)
{
    public const int MaxFailuresPerConversation = 3;
    public const int MaxFailuresPerConsignment = 5;

    // A postcode has only 10,000 values, so failures count per consignment across every conversation as well.
    private readonly ConcurrentDictionary<string, int> _failuresByConsignment = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ToolResult Execute(ConversationState state, string tool, JsonElement input)
    {
        var result = tool switch
        {
            ToolNames.VerifyIdentity => Verify(state, input),
            ToolNames.Track => Track(state, input),
            ToolNames.Reschedule => ProposeReschedule(state, input),
            ToolNames.DamageClaim => ProposeClaim(state, input),
            ToolNames.Handoff => Handoff(state, Read(input, "reason") ?? "Model asked for a person."),
            _ => Blocked(state, tool, "unknown_tool", "No such tool."),
        };
        if (result.Outcome != ToolOutcomes.Blocked) state.ToolEvents.Add(new ToolEvent(tool, result.Outcome, null));
        return result;
    }

    public string ExecutePending(ConversationState state)
    {
        var pending = state.Pending ?? throw new InvalidOperationException("Nothing is waiting for confirmation.");
        state.Pending = null;
        if (pending.Tool == ToolNames.Reschedule)
        {
            var reference = api.Reschedule(pending.Consignment, pending.Detail);
            state.ToolEvents.Add(new ToolEvent(pending.Tool, ToolOutcomes.Executed, "customer confirmed"));
            return $"Done. {pending.Consignment} is now booked for {pending.Detail}. Your reference is {reference}.";
        }
        var claim = api.OpenDamageClaim(pending.Consignment, pending.Detail);
        state.ToolEvents.Add(new ToolEvent(pending.Tool, ToolOutcomes.Executed, "customer confirmed"));
        return $"Done. Your damage claim for {pending.Consignment} is open. Your claim number is {claim}.";
    }

    public string Handoff(ConversationState state, string reason, out string ticket)
    {
        ticket = state.HandoffTicket ??= api.CreateHandoff(state.Id, reason);
        state.Pending = null;
        return ticket;
    }

    private ToolResult Handoff(ConversationState state, string reason)
    {
        var ticket = Handoff(state, reason, out _);
        return Ok(new { status = "handed_off", ticket });
    }

    private ToolResult Verify(ConversationState state, JsonElement input)
    {
        var number = Read(input, "consignment")?.Trim().ToUpperInvariant() ?? "";
        var postcode = Read(input, "postcode")?.Trim() ?? "";

        var locked = _failuresByConsignment.GetValueOrDefault(number) >= MaxFailuresPerConsignment;
        var match = !locked && api.Find(number) is { } consignment && consignment.Postcode == postcode;
        if (match)
        {
            state.VerifiedConsignment = number;
            return Ok(new { verified = true, consignment = number });
        }

        state.VerifiedConsignment = null;
        state.FailedVerifications++;
        if (api.Find(number) is not null) _failuresByConsignment.AddOrUpdate(number, 1, (_, n) => n + 1);
        if (state.FailedVerifications >= MaxFailuresPerConversation)
        {
            Handoff(state, "Identity check failed repeatedly.", out _);
        }
        // The same answer for an unknown number and a wrong postcode, so numbers cannot be probed.
        return new ToolResult(JsonSerializer.Serialize(new { verified = false, message = "Those details do not match our records." }, Json), false, ToolOutcomes.Executed);
    }

    private ToolResult Track(ConversationState state, JsonElement input)
    {
        if (RequireVerified(state, ToolNames.Track, input, out var number) is { } refusal) return refusal;
        var c = api.Find(number)!;
        return Ok(new
        {
            number = c.Number,
            status = c.Status,
            lastScan = c.LastScan,
            lastScanAt = c.LastScanAt,
            eta = c.Eta,
            currentWindow = c.CurrentWindow,
            availableWindows = c.CanReschedule ? c.AvailableWindows : [],
            untrustedCustomerNote = c.CustomerNote,
        });
    }

    private ToolResult ProposeReschedule(ConversationState state, JsonElement input)
    {
        if (RequireVerified(state, ToolNames.Reschedule, input, out var number) is { } refusal) return refusal;
        if (state.Pending is not null) return Blocked(state, ToolNames.Reschedule, "one_change_at_a_time", "Another change is already waiting for the customer to confirm.");

        var c = api.Find(number)!;
        if (!c.CanReschedule) return Failed($"{number} cannot be rescheduled because its status is {c.Status}.");
        var window = Read(input, "window")?.Trim() ?? "";
        if (!c.AvailableWindows.Contains(window))
        {
            return Failed($"'{window}' is not available. The available windows are: {string.Join(", ", c.AvailableWindows)}.");
        }
        return Propose(state, ToolNames.Reschedule, number, window, $"move {number} from {c.CurrentWindow} to {window}");
    }

    private ToolResult ProposeClaim(ConversationState state, JsonElement input)
    {
        if (RequireVerified(state, ToolNames.DamageClaim, input, out var number) is { } refusal) return refusal;
        if (state.Pending is not null) return Blocked(state, ToolNames.DamageClaim, "one_change_at_a_time", "Another change is already waiting for the customer to confirm.");
        var description = Read(input, "description")?.Trim();
        if (string.IsNullOrEmpty(description)) return Failed("A description of the damage is needed.");
        return Propose(state, ToolNames.DamageClaim, number, description, $"open a damage claim on {number} saying: \"{description}\"");
    }

    private static ToolResult Propose(ConversationState state, string tool, string number, string detail, string summary)
    {
        state.Pending = new PendingAction(Guid.NewGuid().ToString("N"), tool, number, detail, summary);
        return new ToolResult(JsonSerializer.Serialize(new { status = "confirmation_required", summary }, Json), false, ToolOutcomes.Pending);
    }

    private static ToolResult? RequireVerified(ConversationState state, string tool, JsonElement input, out string number)
    {
        number = Read(input, "consignment")?.Trim().ToUpperInvariant() ?? "";
        if (state.VerifiedConsignment is null)
        {
            return Blocked(state, tool, "identity_not_verified", "Ask the customer for their consignment number and postcode first.");
        }
        return state.VerifiedConsignment == number
            ? null
            : Blocked(state, tool, "other_consignment", "This customer has only been verified for a different consignment.");
    }

    private static ToolResult Ok(object value) => new(JsonSerializer.Serialize(value, Json), false, ToolOutcomes.Executed);

    private static ToolResult Failed(string message) =>
        new(JsonSerializer.Serialize(new { error = "not_possible", message }, Json), true, ToolOutcomes.Failed);

    private static ToolResult Blocked(ConversationState state, string tool, string reason, string message)
    {
        state.ToolEvents.Add(new ToolEvent(tool, ToolOutcomes.Blocked, reason));
        return new ToolResult(JsonSerializer.Serialize(new { error = reason, message }, Json), true, ToolOutcomes.Blocked);
    }

    private static string? Read(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
