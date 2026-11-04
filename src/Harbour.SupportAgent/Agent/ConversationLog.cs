using Microsoft.Data.Sqlite;

namespace Harbour.SupportAgent.Agent;

public sealed record LogEntry(long Seq, DateTimeOffset At, string Kind, string Detail);

public sealed record ConversationSummary(string Id, DateTimeOffset StartedAt, int Events, int Blocked, bool HandedOff);

/// <summary>What staff read: every customer message, reply, tool call and guardrail decision, in order.</summary>
public sealed class ConversationLog
{
    private readonly string _connectionString;

    public ConversationLog(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS conversations (id TEXT PRIMARY KEY, started_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT NOT NULL REFERENCES conversations (id),
                at TEXT NOT NULL,
                kind TEXT NOT NULL,
                detail TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_conversation ON events (conversation_id, seq);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void StartConversation(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO conversations (id, started_at) VALUES ($id, $at)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Add(string conversationId, string kind, string detail)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO events (conversation_id, at, kind, detail) VALUES ($id, $at, $kind, $detail)";
        command.Parameters.AddWithValue("$id", conversationId);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$detail", detail);
        command.ExecuteNonQuery();
    }

    public List<ConversationSummary> ListConversations()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.started_at, COUNT(e.seq),
                   COALESCE(SUM(e.kind = 'tool_blocked' OR e.kind = 'guard'), 0),
                   COALESCE(SUM(e.kind = 'handoff'), 0)
            FROM conversations c LEFT JOIN events e ON e.conversation_id = c.id
            GROUP BY c.id, c.started_at ORDER BY c.started_at DESC LIMIT 200
            """;
        using var reader = command.ExecuteReader();
        var list = new List<ConversationSummary>();
        while (reader.Read())
        {
            list.Add(new ConversationSummary(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4) > 0));
        }
        return list;
    }

    public List<LogEntry> Read(string conversationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq, at, kind, detail FROM events WHERE conversation_id = $id ORDER BY seq";
        command.Parameters.AddWithValue("$id", conversationId);
        using var reader = command.ExecuteReader();
        var list = new List<LogEntry>();
        while (reader.Read())
        {
            list.Add(new LogEntry(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3)));
        }
        return list;
    }
}
