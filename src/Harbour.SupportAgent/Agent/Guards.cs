using System.Text.RegularExpressions;

namespace Harbour.SupportAgent.Agent;

/// <summary>Reads the customer's reply to a confirmation request. Only a plain yes counts, not a sentence containing one.</summary>
public static partial class Confirmation
{
    public static bool IsYes(string text) => Yes().IsMatch(text.Trim());
    public static bool IsNo(string text) => No().IsMatch(text.Trim());

    [GeneratedRegex(@"^(yes|yes please|yes confirm|confirm|confirmed|go ahead|yes go ahead)[\s.!]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Yes();

    [GeneratedRegex(@"^(no|nope|no thanks|no thank you|cancel|cancel it|don't|do not|stop)[\s.!]*$", RegexOptions.IgnoreCase)]
    private static partial Regex No();
}

/// <summary>
/// A last check on what the model is about to say: consignment numbers, dates, times and delivery statuses must have
/// come from a tool result or the customer. It is a heuristic that catches invented specifics, not a proof of truth.
/// </summary>
public static partial class GroundingCheck
{
    public static string? FindUnsupportedClaim(string reply, IEnumerable<ChatMessage> history)
    {
        var known = new System.Text.StringBuilder();
        var tracked = false;
        foreach (var block in history.SelectMany(m => m.Role == "user" ? m.Content : []))
        {
            if (block is TextBlock text) known.AppendLine(text.Text);
            if (block is ToolResultBlock result && !result.IsError)
            {
                known.AppendLine(result.Content);
                tracked |= result.Content.Contains("\"status\"") && result.Content.Contains("\"eta\"");
            }
        }
        var facts = known.ToString();

        foreach (var regex in new[] { ConsignmentNumber(), IsoDate(), ClockTime(), Weekday(), MonthDay(), RelativeDay() })
        {
            foreach (Match match in regex.Matches(reply))
            {
                if (!facts.Contains(match.Value, StringComparison.OrdinalIgnoreCase)) return match.Value;
            }
        }

        var status = StatusClaim().Match(reply);
        return status.Success && !tracked ? status.Value : null;
    }

    [GeneratedRegex(@"\bHF\d{7}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConsignmentNumber();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b\d{1,2}(:\d{2})?\s?(am|pm)\b|\b\d{1,2}:\d{2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"\b(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Weekday();

    [GeneratedRegex(@"\b\d{1,2}(st|nd|rd|th)?\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthDay();

    [GeneratedRegex(@"\b(today|tomorrow|tonight)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeDay();

    [GeneratedRegex(@"\b(is|was|been|currently|now)\s+(now\s+)?(out for delivery|delivered|in transit|at the depot|on its way|arriving)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StatusClaim();
}
