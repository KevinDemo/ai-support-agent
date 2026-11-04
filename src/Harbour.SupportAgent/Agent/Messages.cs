using System.Text.Json;

namespace Harbour.SupportAgent.Agent;

public abstract record ContentBlock;
public sealed record TextBlock(string Text) : ContentBlock;
public sealed record ToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock;
public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError) : ContentBlock;

/// <summary>A block we do not interpret, such as thinking. The API needs it passed back unchanged.</summary>
public sealed record OpaqueBlock(JsonElement Raw) : ContentBlock;

public sealed record ChatMessage(string Role, IReadOnlyList<ContentBlock> Content)
{
    public static ChatMessage User(string text) => new("user", [new TextBlock(text)]);
    public static ChatMessage Assistant(string text) => new("assistant", [new TextBlock(text)]);
}

public sealed record ModelResponse(IReadOnlyList<ContentBlock> Content, string StopReason)
{
    public static ModelResponse Text(string text) => new([new TextBlock(text)], "end_turn");
    public IEnumerable<ToolUseBlock> ToolCalls => Content.OfType<ToolUseBlock>();
    public string JoinedText => string.Join("\n", Content.OfType<TextBlock>().Select(b => b.Text));
}

public sealed class ModelException(string message) : Exception(message);

/// <summary>Whatever produces the next assistant turn. Nothing it returns is trusted by the gateway.</summary>
public interface IChatModel
{
    string Name { get; }
    Task<ModelResponse> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);
}
