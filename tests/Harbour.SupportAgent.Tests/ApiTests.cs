using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harbour.SupportAgent.Agent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Harbour.SupportAgent.Tests;

public sealed class AppFactory : WebApplicationFactory<AgentRunner>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"support-api-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Database:Path", _db);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }
}

/// <summary>The real app over HTTP with the offline model, plus what staff can and cannot see.</summary>
public class ApiTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<string> Start() =>
        (await (await _client.PostAsync("/api/conversations", null)).Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();

    private async Task<JsonObject> Say(string id, string text) =>
        (await (await _client.PostAsJsonAsync($"/api/conversations/{id}/messages", new { text })).Content.ReadFromJsonAsync<JsonObject>())!;

    private HttpRequestMessage Staff(string path, string? key = "demo-staff-key")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null) request.Headers.Add("X-Staff-Key", key);
        return request;
    }

    [Fact]
    public async Task Confirm_button_executes_only_with_the_current_action_id()
    {
        var id = await Start();
        await Say(id, "Please reschedule HF1000004 postcode 2998");
        var proposal = await Say(id, "2026-11-06 morning");
        var actionId = proposal["pending"]!["id"]!.GetValue<string>();

        var stale = await _client.PostAsJsonAsync($"/api/conversations/{id}/decision", new { actionId = "stale", confirm = true });
        var staleBody = await stale.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Contains("no longer waiting", staleBody!["reply"]!.GetValue<string>());

        var real = await _client.PostAsJsonAsync($"/api/conversations/{id}/decision", new { actionId, confirm = true });
        var realBody = await real.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Contains("Your reference is", realBody!["reply"]!.GetValue<string>());
        Assert.Null(realBody["pending"]);
    }

    [Fact]
    public async Task Staff_log_needs_the_key()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Staff("/api/staff/conversations", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Staff("/api/staff/conversations", "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Staff("/api/staff/conversations"))).StatusCode);
    }

    [Fact]
    public async Task Staff_log_shows_the_tool_calls_and_blocks_but_not_the_postcode_in_tool_calls()
    {
        var id = await Start();
        await Say(id, "Where is HF1000001 postcode 2998?");

        var response = await _client.SendAsync(Staff($"/api/staff/conversations/{id}"));
        var entries = (await response.Content.ReadFromJsonAsync<List<LogEntry>>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

        Assert.Equal(["customer", "tool_call", "tool_result", "tool_call", "tool_result", "agent"], entries.Select(e => e.Kind));
        Assert.Contains("****", entries[1].Detail);
        Assert.DoesNotContain("2998", entries[1].Detail);
    }

    [Fact]
    public async Task Unknown_conversation_and_oversized_message_are_refused()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/api/conversations/nope/messages", new { text = "hi" })).StatusCode);
        var id = await Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/conversations/{id}/messages", new { text = new string('x', 2001) })).StatusCode);
    }
}
