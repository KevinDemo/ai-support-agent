using System.Security.Cryptography;
using System.Text;
using Harbour.SupportAgent.Agent;

const int MaxMessageLength = 2000;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<FakeTrackingApi>();
builder.Services.AddSingleton<ITrackingApi>(sp => sp.GetRequiredService<FakeTrackingApi>());
builder.Services.AddSingleton<ToolGateway>();
builder.Services.AddSingleton(_ => new ConversationLog(Path.GetFullPath(builder.Configuration["Database:Path"] ?? "data/support.db", AppContext.BaseDirectory)));
builder.Services.AddSingleton(_ => ChatModelFactory.Create(Environment.GetEnvironmentVariable, new HttpClient { Timeout = TimeSpan.FromMinutes(5) }));
builder.Services.AddSingleton<AgentRunner>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var staffKey = Encoding.UTF8.GetBytes(app.Configuration["Staff:Key"] ?? throw new InvalidOperationException("Staff:Key is not set."));

app.MapGet("/api/info", (AgentRunner runner) => new { model = runner.ModelName });

app.MapPost("/api/conversations", (AgentRunner runner) => new { id = runner.Start().Id });

app.MapPost("/api/conversations/{id}/messages", async (string id, MessageRequest request, AgentRunner runner, CancellationToken ct) =>
{
    if (runner.Find(id) is not { } state) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > MaxMessageLength)
    {
        return Results.BadRequest(new { error = $"A message must be between 1 and {MaxMessageLength} characters." });
    }
    return Results.Ok(ToResponse(await runner.HandleMessageAsync(state, request.Text, ct)));
});

app.MapPost("/api/conversations/{id}/decision", async (string id, DecisionRequest request, AgentRunner runner, CancellationToken ct) =>
{
    if (runner.Find(id) is not { } state) return Results.NotFound();
    return Results.Ok(ToResponse(await runner.DecideAsync(state, request.ActionId ?? "", request.Confirm, ct)));
});

var staff = app.MapGroup("/api/staff").AddEndpointFilter(async (context, next) =>
{
    var supplied = Encoding.UTF8.GetBytes(context.HttpContext.Request.Headers["X-Staff-Key"].ToString());
    return CryptographicOperations.FixedTimeEquals(supplied, staffKey) ? await next(context) : Results.Unauthorized();
});
staff.MapGet("/conversations", (ConversationLog log) => log.ListConversations());
staff.MapGet("/conversations/{id}", (string id, ConversationLog log) => log.Read(id));

app.Run();

// The summary is safe to show: the pending action never carries anything the customer has not already been told.
static object ToResponse(TurnResult turn) => new
{
    reply = turn.Reply,
    pending = turn.Pending is { } p ? new { id = p.Id, summary = p.Summary } : null,
    handedOff = turn.HandedOff,
    ticket = turn.Ticket,
};

public sealed record MessageRequest(string? Text);
public sealed record DecisionRequest(string? ActionId, bool Confirm);

public partial class Program;
