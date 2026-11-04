using System.Text.Json;
using Harbour.SupportAgent.Agent;

namespace Harbour.SupportAgent.Tests;

/// <summary>No real API is called. These check our side of the wire format against the documented shape.</summary>
public class ProviderTests
{
    [Fact]
    public void Default_is_offline_even_when_an_api_key_happens_to_exist()
    {
        var model = ChatModelFactory.Create(name => name == "ANTHROPIC_API_KEY" ? "not-a-real-key" : null, new HttpClient());
        Assert.IsType<RuleBasedFakeModel>(model);
    }

    [Fact]
    public void Anthropic_mode_without_a_key_fails_at_startup() =>
        Assert.Throws<InvalidOperationException>(() => ChatModelFactory.Create(name => name == "HARBOUR_MODEL_PROVIDER" ? "anthropic" : null, new HttpClient()));

    [Fact]
    public void Thinking_blocks_are_sent_back_unchanged_with_tool_results()
    {
        var response = AnthropicChatModel.ParseResponse("""
            {"stop_reason":"tool_use","content":[
              {"type":"thinking","thinking":"","signature":"sig-123"},
              {"type":"tool_use","id":"t1","name":"track_consignment","input":{"consignment":"HF1000001"}}
            ]}
            """);
        Assert.Equal("t1", Assert.Single(response.ToolCalls).Id);

        List<ChatMessage> history =
        [
            ChatMessage.User("Where is it?"),
            new("assistant", response.Content),
            new("user", [new ToolResultBlock("t1", "{\"error\":\"x\"}", true)]),
        ];
        var body = AnthropicChatModel.BuildBody("claude-test", "system", history);
        using var parsed = JsonDocument.Parse(body.ToJsonString());
        var messages = parsed.RootElement.GetProperty("messages");

        var assistant = messages[1].GetProperty("content");
        Assert.Equal("sig-123", assistant[0].GetProperty("signature").GetString());
        Assert.Equal("HF1000001", assistant[1].GetProperty("input").GetProperty("consignment").GetString());

        var result = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("t1", result.GetProperty("tool_use_id").GetString());
        Assert.True(result.GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public void Request_offers_all_five_tools_and_never_a_forced_choice()
    {
        var body = AnthropicChatModel.BuildBody("claude-test", "system", [ChatMessage.User("hi")]);
        Assert.Equal(5, body["tools"]!.AsArray().Count);
        Assert.False(body.ContainsKey("tool_choice"));
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("max_tokens")]
    public async Task Refusal_or_truncation_hands_off(string stopReason)
    {
        var db = Path.Combine(Path.GetTempPath(), $"support-prov-{Guid.NewGuid():N}.db");
        var runner = new AgentRunner(new FixedModel(new ModelResponse([], stopReason)), new ToolGateway(new FakeTrackingApi()), new ConversationLog(db));
        var state = runner.Start();
        var turn = await runner.HandleMessageAsync(state, "hello", default);
        Assert.True(turn.HandedOff);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(db);
    }

    private sealed class FixedModel(ModelResponse response) : IChatModel
    {
        public string Name => "fixed";
        public Task<ModelResponse> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
