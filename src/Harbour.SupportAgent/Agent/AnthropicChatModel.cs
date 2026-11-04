using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harbour.SupportAgent.Agent;

/// <summary>Claude Messages API with tool use over plain HttpClient. Only used when HARBOUR_MODEL_PROVIDER=anthropic.</summary>
public sealed class AnthropicChatModel(HttpClient http, string apiKey, string model) : IChatModel
{
    public const string DefaultModel = "claude-opus-5-5";
    public const string ApiVersion = "2026-10-21";

    public string Name => $"anthropic ({model})";

    public async Task<ModelResponse> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(BuildBody(model, system, messages).ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);

        using var response = await http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ModelException($"Anthropic API returned {(int)response.StatusCode}.");
        return ParseResponse(payload);
    }

    public static JsonObject BuildBody(string model, string system, IReadOnlyList<ChatMessage> messages)
    {
        var wireMessages = new JsonArray();
        foreach (var message in messages)
        {
            var content = new JsonArray();
            foreach (var block in message.Content)
            {
                content.Add(block switch
                {
                    TextBlock text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
                    ToolUseBlock use => new JsonObject { ["type"] = "tool_use", ["id"] = use.Id, ["name"] = use.Name, ["input"] = JsonNode.Parse(use.Input.GetRawText()) },
                    ToolResultBlock result => new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = result.ToolUseId, ["content"] = result.Content, ["is_error"] = result.IsError },
                    OpaqueBlock opaque => JsonNode.Parse(opaque.Raw.GetRawText()),
                    _ => throw new InvalidOperationException($"Unknown block {block.GetType().Name}."),
                });
            }
            wireMessages.Add(new JsonObject { ["role"] = message.Role, ["content"] = content });
        }

        return new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 16000,
            ["system"] = system,
            ["tools"] = JsonNode.Parse(ToolDefinitions),
            ["messages"] = wireMessages,
        };
    }

    public static ModelResponse ParseResponse(string payload)
    {
        using var parsed = JsonDocument.Parse(payload);
        var root = parsed.RootElement;
        var blocks = new List<ContentBlock>();
        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            switch (block.GetProperty("type").GetString())
            {
                case "text":
                    blocks.Add(new TextBlock(block.GetProperty("text").GetString() ?? ""));
                    break;
                case "tool_use":
                    blocks.Add(new ToolUseBlock(block.GetProperty("id").GetString()!, block.GetProperty("name").GetString()!, block.GetProperty("input").Clone()));
                    break;
                default:
                    blocks.Add(new OpaqueBlock(block.Clone()));
                    break;
            }
        }
        return new ModelResponse(blocks, root.GetProperty("stop_reason").GetString() ?? "");
    }

    public const string ToolDefinitions = """
        [
          {
            "name": "verify_identity",
            "description": "Check a consignment number and delivery postcode given by the customer. Required before any other consignment tool.",
            "input_schema": { "type": "object", "additionalProperties": false, "required": ["consignment", "postcode"],
              "properties": { "consignment": { "type": "string" }, "postcode": { "type": "string" } } }
          },
          {
            "name": "track_consignment",
            "description": "Get status, latest scan, estimated delivery and available delivery windows for the verified consignment.",
            "input_schema": { "type": "object", "additionalProperties": false, "required": ["consignment"],
              "properties": { "consignment": { "type": "string" } } }
          },
          {
            "name": "reschedule_delivery",
            "description": "Propose moving the verified consignment to one of its available windows. The customer is asked to confirm before anything changes.",
            "input_schema": { "type": "object", "additionalProperties": false, "required": ["consignment", "window"],
              "properties": { "consignment": { "type": "string" }, "window": { "type": "string", "description": "Exactly as listed in availableWindows." } } }
          },
          {
            "name": "open_damage_claim",
            "description": "Propose opening a damage claim on the verified consignment. The customer is asked to confirm before anything changes.",
            "input_schema": { "type": "object", "additionalProperties": false, "required": ["consignment", "description"],
              "properties": { "consignment": { "type": "string" }, "description": { "type": "string" } } }
          },
          {
            "name": "handoff_to_human",
            "description": "Pass the conversation to a member of staff. Use when the customer is upset, asks for a person, or you are unsure.",
            "input_schema": { "type": "object", "additionalProperties": false, "required": ["reason"],
              "properties": { "reason": { "type": "string" } } }
          }
        ]
        """;
}

public static class ChatModelFactory
{
    /// <summary>Offline unless the provider is asked for by name. A key that merely exists is not enough.</summary>
    public static IChatModel Create(Func<string, string?> env, HttpClient http)
    {
        var provider = env("HARBOUR_MODEL_PROVIDER");
        if (string.IsNullOrEmpty(provider) || provider.Equals("scripted", StringComparison.OrdinalIgnoreCase)) return new RuleBasedFakeModel();
        if (!provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unknown HARBOUR_MODEL_PROVIDER '{provider}'. Use 'scripted' or 'anthropic'.");
        }
        var key = env("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("HARBOUR_MODEL_PROVIDER=anthropic needs ANTHROPIC_API_KEY.");
        var model = env("ANTHROPIC_MODEL");
        return new AnthropicChatModel(http, key, string.IsNullOrEmpty(model) ? AnthropicChatModel.DefaultModel : model);
    }
}
