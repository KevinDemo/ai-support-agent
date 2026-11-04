using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harbour.SupportAgent.Agent;

/// <summary>
/// The offline stand-in for a real model: keyword rules over the conversation, nothing more. It exists so the demo
/// and evals run with no key. It behaves well on purpose, so the evals also use a scripted model that misbehaves.
/// </summary>
public sealed partial class RuleBasedFakeModel : IChatModel
{
    private enum Intent { None, Track, Reschedule, Damage }

    public string Name => "scripted (offline)";

    public Task<ModelResponse> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var last = messages[^1];
        var response = last.Content.OfType<ToolResultBlock>().FirstOrDefault() is { } result
            ? AfterTool(result, messages)
            : AfterCustomer(((TextBlock)last.Content[0]).Text, messages);
        return Task.FromResult(response);
    }

    private static ModelResponse AfterCustomer(string text, IReadOnlyList<ChatMessage> messages)
    {
        if (Injection().IsMatch(text)) return ModelResponse.Text("I can't do that. I can track a consignment, reschedule a delivery, open a damage claim or connect you with our team.");
        if (Upset().IsMatch(text)) return Call(ToolNames.Handoff, new { reason = "Customer is upset." });
        if (WantsPerson().IsMatch(text)) return Call(ToolNames.Handoff, new { reason = "Customer asked for a person." });
        if (OutOfScope().IsMatch(text)) return ModelResponse.Text("Sorry, I can only help with tracking, rescheduling a delivery, damage claims, or connecting you with our team.");

        var number = Number().Match(text);
        var postcode = PostcodeOnly().Match(text);
        if (number.Success && postcode.Success)
        {
            return Call(ToolNames.VerifyIdentity, new { consignment = number.Value.ToUpperInvariant(), postcode = postcode.Value });
        }

        var verified = VerifiedConsignment(messages);
        var intent = ReadIntent(text);
        var window = Window().Match(text);
        if (window.Success) intent = Intent.Reschedule;

        if (intent == Intent.None && !number.Success)
        {
            if (Thanks().IsMatch(text)) return ModelResponse.Text("You're welcome. Is there anything else I can help with?");
            var alreadyAsked = messages.Any(m => m.Role == "assistant" && m.Content.OfType<TextBlock>().Any(b => b.Text.StartsWith(NotSure)));
            return alreadyAsked ? Call(ToolNames.Handoff, new { reason = "Assistant is unsure what the customer needs." }) : ModelResponse.Text(NotSure + " I can track a consignment, reschedule a delivery or open a damage claim.");
        }

        if (verified is null) return ModelResponse.Text("To protect your details, I need your consignment number (it starts with HF) and the delivery postcode.");
        return Act(intent, verified, text, window);
    }

    private const string NotSure = "I am not sure I understood that.";

    private static ModelResponse AfterTool(ToolResultBlock result, IReadOnlyList<ChatMessage> messages)
    {
        using var json = JsonDocument.Parse(result.Content);
        var root = json.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            return error.GetString() == "identity_not_verified"
                ? ModelResponse.Text("To protect your details, I need your consignment number (it starts with HF) and the delivery postcode.")
                : ModelResponse.Text($"I couldn't do that. {root.GetProperty("message").GetString()}");
        }

        if (root.TryGetProperty("verified", out var verified))
        {
            if (!verified.GetBoolean()) return ModelResponse.Text("Those details do not match our records. Please check the consignment number and postcode and try again.");
            var (intent, intentText) = LatestIntent(messages);
            var number = root.GetProperty("consignment").GetString()!;
            return intent == Intent.None
                ? ModelResponse.Text($"Thanks, I have found {number}. What would you like to do?")
                : Act(intent, number, intentText, Window().Match(intentText));
        }

        var status = root.GetProperty("status").GetString();
        var consignment = root.GetProperty("number").GetString();
        if (LatestIntent(messages).Intent == Intent.Reschedule)
        {
            var windows = root.GetProperty("availableWindows").EnumerateArray().Select(w => w.GetString()).ToList();
            return ModelResponse.Text(windows.Count == 0
                ? $"{consignment} is {status}, so it can't be rescheduled."
                : $"{consignment} is booked for {root.GetProperty("currentWindow").GetString()}. The available windows are: {string.Join(", ", windows)}. Which would you like?");
        }
        return ModelResponse.Text($"{consignment}: {status}. Latest scan: {root.GetProperty("lastScan").GetString()} at {root.GetProperty("lastScanAt").GetString()}. Estimated delivery: {root.GetProperty("eta").GetString()}.");
    }

    private static ModelResponse Act(Intent intent, string number, string text, Match window) => intent switch
    {
        Intent.Damage => Call(ToolNames.DamageClaim, new { consignment = number, description = text }),
        Intent.Reschedule when window.Success => Call(ToolNames.Reschedule, new { consignment = number, window = window.Value.ToLowerInvariant() }),
        _ => Call(ToolNames.Track, new { consignment = number }),
    };

    private static (Intent Intent, string Text) LatestIntent(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages.Reverse().Where(m => m.Role == "user"))
        {
            foreach (var text in message.Content.OfType<TextBlock>())
            {
                var intent = Window().IsMatch(text.Text) ? Intent.Reschedule : ReadIntent(text.Text);
                if (intent != Intent.None) return (intent, text.Text);
            }
        }
        return (Intent.None, "");
    }

    private static Intent ReadIntent(string text) =>
        Damage().IsMatch(text) ? Intent.Damage
        : Reschedule().IsMatch(text) ? Intent.Reschedule
        : Track().IsMatch(text) ? Intent.Track
        : Intent.None;

    private static string? VerifiedConsignment(IReadOnlyList<ChatMessage> messages)
    {
        string? number = null;
        foreach (var result in messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Where(r => r.Content.Contains("\"verified\"")))
        {
            using var json = JsonDocument.Parse(result.Content);
            number = json.RootElement.GetProperty("verified").GetBoolean() ? json.RootElement.GetProperty("consignment").GetString() : null;
        }
        return number;
    }

    private static ModelResponse Call(string tool, object input) =>
        new([new ToolUseBlock($"call_{Guid.NewGuid():N}", tool, JsonSerializer.SerializeToElement(input))], "tool_use");

    [GeneratedRegex(@"ignore (all |any |your |previous |the )+|disregard|system\s*:|you are now|developer mode|jailbreak|as an admin|reveal your|system prompt", RegexOptions.IgnoreCase)]
    private static partial Regex Injection();

    [GeneratedRegex(@"useless|ridiculous|furious|disgusting|angry|fed up|sick of|terrible|worst|lawyer|scam|complaint|!!!", RegexOptions.IgnoreCase)]
    private static partial Regex Upset();

    [GeneratedRegex(@"\b(human|real person|a person|agent|manager|someone real|call me)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WantsPerson();

    [GeneratedRegex(@"\b(weather|joke|poem|recipe|bitcoin|crypto|homework|stock|capital of|write me|competitor|lottery)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OutOfScope();

    [GeneratedRegex(@"\bHF\d{7}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Number();

    [GeneratedRegex(@"(?<![-\d])\d{4}(?![-\d])")]
    private static partial Regex PostcodeOnly();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2} (morning|afternoon)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Window();

    [GeneratedRegex(@"damag|broken|smashed|crushed|claim", RegexOptions.IgnoreCase)]
    private static partial Regex Damage();

    [GeneratedRegex(@"reschedul|change .*deliver|different (day|time)|redeliver|not home|won't be home", RegexOptions.IgnoreCase)]
    private static partial Regex Reschedule();

    [GeneratedRegex(@"where|track|status|parcel|arriv|when will|delivery", RegexOptions.IgnoreCase)]
    private static partial Regex Track();

    [GeneratedRegex(@"thank|cheers|that's all|bye", RegexOptions.IgnoreCase)]
    private static partial Regex Thanks();
}
