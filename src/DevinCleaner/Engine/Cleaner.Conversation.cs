using System.Text.Json;

namespace DevinCleaner.Engine;

public sealed partial class Cleaner
{
    // Tool output can reach megabytes per call; the viewer only needs a readable prefix.
    internal const int ToolResultLimit = 20_000;
    internal const int ToolArgumentsLimit = 4_000;

    // message_nodes is a forest; sessions.main_chain_id is the leaf of the branch Devin shows.
    public IReadOnlyList<ChatMessage> Conversation(string sessionId)
    {
        using var db = OpenRead();
        var session = Db.Rows(db, "SELECT main_chain_id FROM sessions WHERE id=$p0", [sessionId]);
        if (session.Count == 0) throw new CleanerException($"会话 {sessionId} 已不存在，请刷新。");
        if (session[0][0] is not long leaf) throw new CleanerException("这个会话没有记录当前对话主线，无法显示。");
        var parents = Db.Rows(db, "SELECT node_id, parent_node_id FROM message_nodes WHERE session_id=$p0", [sessionId])
            .ToDictionary(r => (long)r[0]!, r => r[1] as long?);
        var chain = new List<long>();
        for (long? node = leaf; node is { } n; node = parents.GetValueOrDefault(n))
        {
            if (!parents.ContainsKey(n) || chain.Contains(n)) throw new CleanerException($"会话主线在节点 {n} 处断开，无法显示。");
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
                   json_extract(chat_message,'$.tool_call_id')
            FROM message_nodes WHERE session_id=$p0 AND node_id IN (SELECT value FROM json_each($p1))
            """, [sessionId, JsonSerializer.Serialize(chain)]).ToDictionary(r => (long)r[0]!);

        var messages = new List<ChatMessage>();
        var pending = new Dictionary<string, (int Message, int Call)>(StringComparer.Ordinal);
        foreach (var node in chain)
        {
            var r = rows[node];
            var created = DateTimeOffset.FromUnixTimeSeconds((long)r[1]!);
            var text = r[3] as string ?? "";
            switch (r[2] as string)
            {
                case "tool" when r[6] is string callId && pending.Remove(callId, out var at):
                    var calls = messages[at.Message].ToolCalls.ToList();
                    calls[at.Call] = calls[at.Call] with { Result = Clip(text, ToolResultLimit) };
                    messages[at.Message] = messages[at.Message] with { ToolCalls = calls };
                    break;
                case "tool":
                    messages.Add(new ChatMessage(ChatRole.Tool, Clip(text, ToolResultLimit), null, [], created));
                    break;
                case "user":
                    messages.Add(new ChatMessage(ChatRole.User, text, null, [], created));
                    break;
                case "assistant":
                    var toolCalls = ParseToolCalls(r[5] as string);
                    for (var i = 0; i < toolCalls.Count; i++) pending[toolCalls[i].Id] = (messages.Count, i);
                    messages.Add(new ChatMessage(ChatRole.Assistant, text, r[4] as string is { Length: > 0 } t ? t.Trim() : null, toolCalls, created));
                    break;
                default:
                    messages.Add(new ChatMessage(ChatRole.System, text, null, [], created));
                    break;
            }
        }
        return messages;
    }

    private static List<ChatToolCall> ParseToolCalls(string? json)
    {
        if (json is null) return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(c => new ChatToolCall(
            c.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            c.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            c.TryGetProperty("arguments", out var args) ? Clip(args.ValueKind == JsonValueKind.String ? args.GetString()! : args.GetRawText(), ToolArgumentsLimit) : "",
            null)).ToList();
    }

    private static string Clip(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + $"\n…（超过 {limit:N0} 字符的部分未显示）";
}
