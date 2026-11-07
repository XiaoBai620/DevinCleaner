using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DevinCleaner.Engine;

public sealed partial class Cleaner
{
    // Safety caps only; the largest values seen in real sessions are about 22 KB of tool output.
    internal const int ToolResultLimit = 200_000;
    internal const int ToolArgumentsLimit = 200_000;
    private const int SummaryLimit = 160;

    // The argument that best identifies what a call did, in order of preference.
    private static readonly string[] SummaryKeys = ["command", "file_path", "pattern", "query", "url", "search_term", "title", "task", "path", "shell_id", "agent_id", "server_name", "skill"];
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // message_nodes is a forest; sessions.main_chain_id is the leaf of the branch Devin shows.
    // Devin rebuilds the tree when a session is resumed and replays the whole history into it,
    // so the main chain alone holds every turn; other roots are drafts, sub-agents and summaries.
    public IReadOnlyList<ChatMessage> Conversation(string sessionId)
    {
        using var db = OpenRead();
        var session = Db.Rows(db, "SELECT main_chain_id FROM sessions WHERE id=$p0", [sessionId]);
        if (session.Count == 0) throw new CleanerException($"会话 {sessionId} 已不存在，请刷新。");
        if (session[0][0] is not long leaf) throw new CleanerException("这个会话没有记录当前对话主线，无法显示。");
        var parents = Db.Rows(db, "SELECT node_id, parent_node_id FROM message_nodes WHERE session_id=$p0", [sessionId])
            .ToDictionary(r => (long)r[0]!, r => r[1] as long?);
        var chain = new List<long>();
        var seen = new HashSet<long>();
        for (long? node = leaf; node is { } n; node = parents.GetValueOrDefault(n))
        {
            if (!parents.ContainsKey(n) || !seen.Add(n)) throw new CleanerException($"会话主线在节点 {n} 处断开，无法显示。");
            chain.Add(n);
        }
        chain.Reverse();

        var rows = Db.Rows(db, $"""
            SELECT node_id, created_at,
                   json_extract(chat_message,'$.role'),
                   CASE json_extract(chat_message,'$.role') WHEN 'tool' THEN substr(json_extract(chat_message,'$.content'),1,{ToolResultLimit + 1})
                        ELSE json_extract(chat_message,'$.content') END,
                   json_extract(chat_message,'$.thinking.thinking'),
                   json_extract(chat_message,'$.tool_calls'),
                   json_extract(chat_message,'$.tool_call_id'),
                   json_extract(chat_message,'$.images')
            FROM message_nodes WHERE session_id=$p0 AND node_id IN (SELECT value FROM json_each($p1))
            """, [sessionId, JsonSerializer.Serialize(chain)]).ToDictionary(r => (long)r[0]!);

        var messages = new List<ChatMessage>();
        var pending = new Dictionary<string, (int Message, int Call)>(StringComparer.Ordinal);
        foreach (var node in chain)
        {
            var r = rows[node];
            var created = DateTimeOffset.FromUnixTimeSeconds((long)r[1]!);
            var text = r[3] as string ?? "";
            var images = ParseImages(r[7] as string);
            switch (r[2] as string)
            {
                case "tool" when r[6] is string callId && pending.Remove(callId, out var at):
                    var calls = messages[at.Message].ToolCalls.ToList();
                    calls[at.Call] = calls[at.Call] with { Result = Clip(text, ToolResultLimit), Images = images };
                    messages[at.Message] = messages[at.Message] with { ToolCalls = calls };
                    break;
                case "tool":
                    messages.Add(new ChatMessage(ChatRole.Tool, Clip(text, ToolResultLimit), null, [], images, created));
                    break;
                case "user":
                    messages.Add(new ChatMessage(ChatRole.User, text, null, [], images, created));
                    break;
                case "assistant":
                    var toolCalls = ParseToolCalls(r[5] as string);
                    for (var i = 0; i < toolCalls.Count; i++) pending[toolCalls[i].Id] = (messages.Count, i);
                    messages.Add(new ChatMessage(ChatRole.Assistant, text, r[4] as string is { Length: > 0 } t ? t.Trim() : null, toolCalls, images, created));
                    break;
                default:
                    messages.Add(new ChatMessage(ChatRole.System, text, null, [], images, created));
                    break;
            }
        }
        return messages;
    }

    private static List<ChatImage> ParseImages(string? json)
    {
        if (json is null) return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Where(i => i.TryGetProperty("base64_data", out var data) && data.ValueKind == JsonValueKind.String)
            .Select(i => new ChatImage(Convert.FromBase64String(i.GetProperty("base64_data").GetString()!),
                i.TryGetProperty("width", out var w) && w.TryGetInt32(out var width) ? width : 0,
                i.TryGetProperty("height", out var h) && h.TryGetInt32(out var height) ? height : 0))
            .ToList();
    }

    private static List<ChatToolCall> ParseToolCalls(string? json)
    {
        if (json is null) return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(c =>
        {
            var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var (summary, arguments) = c.TryGetProperty("arguments", out var args) ? DescribeArguments(name, args) : ("", "");
            return new ChatToolCall(c.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "", name, summary, Clip(arguments, ToolArgumentsLimit), null, []);
        }).ToList();
    }

    // Arguments are shown as "key: value" with string values unescaped, so file contents
    // and edits read as written instead of as one escaped JSON line.
    internal static (string Summary, string Text) DescribeArguments(string name, JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.String) return (OneLine(args.GetString()!), args.GetString()!);
        if (args.ValueKind != JsonValueKind.Object) return (OneLine(args.GetRawText()), Pretty(args));
        var text = new StringBuilder();
        foreach (var p in args.EnumerateObject())
        {
            if (text.Length > 0) text.Append('\n');
            var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : Pretty(p.Value);
            text.Append(p.Name).Append(value.Contains('\n') ? ":\n" : ": ").Append(value);
            if (value.Contains('\n')) text.Append('\n');
        }
        string? Get(string key) => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
        var summary = name == "mcp_call_tool" && Get("server_name") is { } server
            ? $"{server} · {Get("tool_name")}"
            : SummaryKeys.Select(Get).FirstOrDefault(v => v is not null)
              ?? args.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()).FirstOrDefault()
              ?? "";
        return (OneLine(summary), text.ToString().TrimEnd());
    }

    private static string Pretty(JsonElement value) =>
        value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? JsonSerializer.Serialize(value, Indented) : value.GetRawText();

    private static string OneLine(string text)
    {
        var line = string.Join(' ', text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= SummaryLimit ? line : line[..SummaryLimit] + "…";
    }

    private static string Clip(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + $"\n…（超过 {limit:N0} 字符的部分未显示）";
}
