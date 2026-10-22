using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acp;
using ContentBlock = Acp.ContentBlock; // disambiguate from Rhino.AI.Server.ContentBlock

namespace Rhino.AI;

// The Pi stream-json strategy: turns one `pi --mode json` stdout line into ACP session/update
// events and frames one user turn for stdin. Owns no process/threads/turn gating (the runner does);
// stays RhinoApp-free so it can be Compile Include'd into a test project. Model and the system
// prompt come from AISettings at spawn time; the MCP server set is resolved by the runner and
// handed in per spawn.
//
// Pi's print mode (`-p`) is one-shot: it merges piped stdin into the initial prompt, runs the
// agent to completion, streams JSON events, and exits. Multi-turn continuity therefore comes from
// pi's session files, not a long-lived process, so IsOneTurnPerProcess is true and the runner
// closes stdin after each turn. Pi ignores --session-id in print mode and mints its own UUIDv7,
// reporting it in the session event; the runner adopts that id (AdoptMintedSessionId) and every
// later spawn passes --session <adopted id> to load the file from disk. The terminal event is
// agent_end (turn_end fires once per LLM turn, i.e. many times across a tool loop); it carries
// willRetry while a compaction retry is pending, so only willRetry:false completes the turn.
// Usage is summed over this run's assistant messages in agent_end.messages.
//
// Pi has no CLI sign-in flow: credentials are per-provider config (env vars, auth.json, or an
// apiKey in models.json), and `pi auth` only knows print-api-key / print-bearer-token / check.
// AuthStatusArguments and LoginArguments are therefore empty - the runner stays permanently
// Unknown and off the sign-in path, and a missing credential surfaces as pi's own top-level
// {"error":...} line, which Parse turns into a failed turn with the real message in the transcript.
internal sealed class PiStreamJsonParser : IStreamJsonParser
{
    private AgentDefinition Definition { get; }
    private string? McpConfigPath { get; set; }

    // net48 has no ArgumentList to inspect or rewrite, so the CLI args are tracked here and the
    // cmd-shim bypass rebuilds psi.Arguments from them.
    private List<string> SpawnArgs { get; } = [];
    private string? CompactionCallId { get; set; }
    private int CompactionCalls;

    public PiStreamJsonParser(AgentDefinition definition)
    {
        Definition = definition;
    }

    public string DisplayName => Definition.Name;

    public string NotFoundMessage => "Pi CLI not found. Install pi (https://github.com/earendil-works/pi-coding-agent).";

    // One `pi -p` process per turn; the session file on disk carries continuity between them.
    public bool IsOneTurnPerProcess => true;

    // Pi cannot be asked for a single sign-in state (auth is per-provider, and there is no CLI
    // command that performs one), so leave it permanently Unknown and off the sign-in path.
    public IReadOnlyList<string> AuthStatusArguments => [];

    public IReadOnlyList<string> LoginArguments => [];

    public CliLogin.State ReadAuthState(string output, int exitCode) => CliLogin.State.Unknown;

    public void ConfigureArguments(ProcessStartInfo psi, string mcpUrl, string agentSessionId, IReadOnlyList<string> mcpServers, bool resume, IReadOnlyList<ContentBlock> prompt)
    {
        // The turn still travels over stdin (FormatTurn); pi -p merges piped stdin into the initial
        // prompt, so the spawn-time prompt is not needed here.
        SpawnArgs.Clear();

        // Pi takes --mcp-config as a FILE PATH (unlike Claude's inline JSON), so the server set is
        // written to a temp file. The previous spawn's file is replaced, not leaked.
        if (McpConfigPath is not null)
        {
            try { File.Delete(McpConfigPath); } catch { /* best effort */ }
        }

        // Pi's MCP adapter picks the transport from which fields are present: "url" means
        // StreamableHTTP (SSE fallback), "command" means stdio. There is no "type" field - that is
        // Claude Code's shape - so it is stripped from the runner-resolved entries, which may be
        // authored for Claude.
        JsonObject servers = new()
        {
            ["rhino"] = new JsonObject { ["url"] = mcpUrl },
        };
        foreach (string entry in mcpServers)
            if (JsonNode.Parse(entry) is JsonObject obj)
                foreach (KeyValuePair<string, JsonNode?> kvp in obj)
                    if (kvp.Key != "rhino" && kvp.Value is JsonObject server)
                    {
                        JsonObject copy = new();
                        foreach (KeyValuePair<string, JsonNode?> field in server)
                            if (field.Key != "type")
                                copy[field.Key] = field.Value?.DeepClone();
                        servers[kvp.Key] = copy;
                    }

        McpConfigPath = Path.Combine(Path.GetTempPath(), $"rhino-pi-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(McpConfigPath, new JsonObject { ["mcpServers"] = servers }.ToJsonString(McpSerializer.Options));

        Arg(psi, "-p"); // print mode: process the prompt and exit (one turn per process)
        Arg(psi, "--mode");
        Arg(psi, "json");
        Arg(psi, "--mcp-config");
        Arg(psi, McpConfigPath);

        string systemPrompt = AgentPrompts.Compose(AISettings.EffectivePrompt(Definition));
        if (systemPrompt.Length > 0)
        {
            Arg(psi, "--append-system-prompt");
            Arg(psi, systemPrompt);
        }

        // Empty means "use pi's own configured default" (its settings.json defaultProvider/
        // defaultModel), so no flag at all - not an empty --model.
        if (AISettings.EffectiveModel(Definition) is { Length: > 0 } model)
        {
            Arg(psi, "--model");
            Arg(psi, model);
        }

        // Fresh: ask pi to create the session under our id (it may mint its own instead - we adopt
        // whatever the session event reports). Respawn after cancel/crash: load it from disk.
        // (NOT --resume/-r: that flag takes no value and opens the interactive session picker.)
        Arg(psi, resume ? "--session" : "--session-id");
        Arg(psi, agentSessionId);

        // Last: on net48 the bypass rebuilds psi.Arguments from SpawnArgs, which must be complete.
        BypassCmdShim(psi);
    }

    // AddArgument is the net48-safe append (escaped Arguments string); SpawnArgs mirrors it so the
    // shim bypass can rebuild the command line where ArgumentList does not exist.
    private void Arg(ProcessStartInfo psi, string argument)
    {
        psi.AddArgument(argument);
        SpawnArgs.Add(argument);
    }

    // On Windows CliProcess launches an npm .cmd shim through `cmd.exe /c`, and cmd re-parses the
    // command line: a multi-line quoted argument (our steered system prompt) breaks the parse and
    // drops everything after it - including --session, which silently starts a fresh session. The
    // shim is nothing but a node launcher for the bundled CLI, so when its layout resolves, point
    // straight at node.exe + cli.js: a real .exe receives CreateProcess's argument list verbatim.
    private void BypassCmdShim(ProcessStartInfo psi)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!string.Equals(Path.GetFileName(psi.FileName), "cmd.exe", StringComparison.OrdinalIgnoreCase))
            return;

        // CliProcess.ConfigureFileName's batch-shim shape: FileName=cmd.exe, /c <shim> leading the
        // args. On net8.0 the shim is ArgumentList[1]; on net48 it is the quoted token after /c in
        // the raw Arguments string (ConfigureFileName ran before us, so SpawnArgs holds only the
        // CLI args that follow it).
        string shim;
#if NETFRAMEWORK
        shim = ShimFromArguments(psi.Arguments) ?? string.Empty;
#else
        if (psi.ArgumentList.Count < 2 || !string.Equals(psi.ArgumentList[0], "/c", StringComparison.OrdinalIgnoreCase))
            return;
        shim = psi.ArgumentList[1];
#endif
        if (!shim.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && !shim.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            return;

        string dir = Path.GetDirectoryName(shim) ?? string.Empty;
        string cliJs = Path.Combine(dir, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js");
        if (!File.Exists(cliJs))
            return; // unknown layout: keep the cmd launch (single-line args survive it)

        string node = Path.Combine(dir, "node.exe");
#if NETFRAMEWORK
        // No ArgumentList to rewrite: rebuild the raw command line from the tracked args.
        System.Text.StringBuilder line = new();
        AppendRebuiltArg(line, cliJs);
        foreach (string arg in SpawnArgs)
            AppendRebuiltArg(line, arg);
        psi.Arguments = line.ToString();
#else
        List<string> args = [cliJs];
        foreach (string arg in psi.ArgumentList.Skip(2))
            args.Add(arg);
        psi.ArgumentList.Clear();
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);
#endif
        psi.FileName = File.Exists(node) ? node : "node";
    }

#if NETFRAMEWORK
    private static void AppendRebuiltArg(System.Text.StringBuilder line, string argument)
    {
        if (line.Length != 0)
            line.Append(' ');
        line.Append(ProcessArguments.EscapeArgument(argument));
    }

    // First quoted token after the leading /c: "/c" "<shim>" ... (shim paths here never embed quotes).
    private static string? ShimFromArguments(string arguments)
    {
        int start = arguments.IndexOf('"');
        if (start < 0) return null;
        // Skip the "/c" token, then read the next quoted token.
        int firstClose = arguments.IndexOf('"', start + 1);
        if (firstClose < 0) return null;
        int secondOpen = arguments.IndexOf('"', firstClose + 1);
        if (secondOpen < 0) return null;
        int secondClose = arguments.IndexOf('"', secondOpen + 1);
        if (secondClose < 0) return null;
        return arguments.Substring(secondOpen, secondClose - secondOpen);
    }
#endif

    // The turn is written to stdin as plain text; pi -p merges piped stdin into the initial prompt
    // and the runner closes stdin (EOF) right after, which is what starts processing.
    public string FormatTurn(IReadOnlyList<ContentBlock> prompt)
    {
        List<string> pieces = [];
        foreach (ContentBlock block in prompt)
            if (block is TextContentBlock text && text.Text.Length > 0)
                pieces.Add(text.Text);
        return string.Join("\n", pieces);
    }

    public ParsedLine Parse(string line)
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return ParsedLine.None;

        // Pi's top-level failure shape: {"error":{"message":"...","code":404,...}} - a turn that
        // dies before it can stream (bad model id, missing credentials, HTTP error).
        if (root.TryGetProperty("error", out JsonElement error))
            return ParsedLine.Failed(StopReason.Refusal, Chunk($"Pi failed: {ErrorMessage(error)}"));

        if (!TryStr(root, "type", out string type))
            return ParsedLine.None;

        switch (type)
        {
            case "session":
                // Pi echoes the session id it adopted (ours, since we mint it). Lets the runner
                // adopt a different one if pi ever mints its own.
                return TryStr(root, "id", out string sessionId) ? ParsedLine.Session(sessionId) : ParsedLine.None;

            case "message_update":
                // Delta-only events off the in-flight assistant message: text_delta is live answer
                // text, thinking_delta is the model's reasoning (panel shows it in a collapsible
                // block), toolcall_start/end bracket one tool call's streamed arguments.
                if (!root.TryGetProperty("assistantMessageEvent", out JsonElement evt) ||
                    !TryStr(evt, "type", out string evtType))
                    return ParsedLine.None;
                switch (evtType)
                {
                    case "text_delta" when TryStr(evt, "delta", out string text):
                        return ParsedLine.Emit(Chunk(text));

                    case "thinking_delta" when TryStr(evt, "delta", out string thought):
                        return ParsedLine.Emit(Thought(thought));

                    // The call exists before its arguments finish streaming; the UI gets the title
                    // now and the args via the toolcall_end update.
                    case "toolcall_start" when TryStr(evt, "id", out string callId) &&
                        TryStr(evt, "toolName", out string callName):
                        return ParsedLine.Emit(new ToolCallSessionUpdate
                        {
                            ToolCallId = callId,
                            Title = callName,
                        });

                    case "toolcall_end" when TryStr(evt, "id", out string endId) &&
                        evt.TryGetProperty("toolCall", out JsonElement toolCall) &&
                        toolCall.TryGetProperty("arguments", out JsonElement args):
                        return ParsedLine.Emit(new ToolCallUpdateSessionUpdate
                        {
                            ToolCallId = endId,
                            RawInput = args.Clone(),
                        });

                    default:
                        return ParsedLine.None;
                }

            case "compaction_start":
                // Pi compacts the context mid-run (manually or after an overflow) and stays silent
                // for the whole summary call, so surface it as a tool chip: open here, close on
                // compaction_end. One compaction per one-shot process, so a plain counter id works.
                CompactionCallId ??= $"compaction-{++CompactionCalls}";
                string reason = TryStr(root, "reason", out string r) ? r : string.Empty;
                return ParsedLine.Emit(new ToolCallSessionUpdate
                {
                    ToolCallId = CompactionCallId,
                    Title = reason.Length > 0 ? $"Compacting context ({reason})" : "Compacting context",
                    Status = ToolCallStatus.InProgress,
                });

            case "compaction_end":
                if (CompactionCallId is not { } compactionId)
                    return ParsedLine.None; // end without a start: nothing to close
                CompactionCallId = null;
                JsonElement compactionResult = default;
                bool compactionFailed = root.TryGetProperty("aborted", out JsonElement compactionAborted) && compactionAborted.ValueKind == JsonValueKind.True
                    || !root.TryGetProperty("result", out compactionResult) || compactionResult.ValueKind != JsonValueKind.Object;
                string? compactionNote = TryStr(root, "errorMessage", out string compactionError) ? compactionError : null;
                return ParsedLine.Emit(new ToolCallUpdateSessionUpdate
                {
                    ToolCallId = compactionId,
                    Status = compactionFailed ? ToolCallStatus.Failed : ToolCallStatus.Completed,
                    RawOutput = compactionFailed ? null : compactionResult.Clone(),
                    Content = compactionNote is not null
                        ? [new ContentToolCallContent { Content = new TextContentBlock { Text = compactionNote } }]
                        : null,
                });

            case "tool_execution_start":
                // Pi announces execution separately from the streamed tool call; the gap between
                // toolcall_end and this event is argument validation.
                return TryStr(root, "toolCallId", out string runId)
                    ? ParsedLine.Emit(new ToolCallUpdateSessionUpdate
                    {
                        ToolCallId = runId,
                        Status = ToolCallStatus.InProgress,
                    })
                    : ParsedLine.None;

            case "tool_execution_end":
                return TryStr(root, "toolCallId", out string doneId)
                    ? ParsedLine.Emit(ToolExecutionEnd(root, doneId))
                    : ParsedLine.None;

            case "agent_end":
                // The run's true terminal event (turn_end fires per LLM turn inside the tool loop).
                // willRetry:true means a compaction overflow retry is about to continue the run.
                if (root.TryGetProperty("willRetry", out JsonElement retry) && retry.ValueKind == JsonValueKind.True)
                    return ParsedLine.None;
                return FinishRun(root);

            default:
                return ParsedLine.None;
        }
    }

    // Sum this run's usage over its assistant messages and resolve the turn. A final message that
    // stopped on error/abort fails the turn with whatever text pi recorded, so the transcript
    // shows why instead of just stopping.
    private static ParsedLine FinishRun(JsonElement root)
    {
        int input = 0, output = 0;
        decimal cost = 0m;
        bool haveCost = false;
        string? stopReason = null;
        string? errorMessage = null;

        if (root.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
            foreach (JsonElement message in messages.EnumerateArray())
                if (message.ValueKind == JsonValueKind.Object && TryStr(message, "role", out string role) && role == "assistant")
                {
                    input += ReadInt(message, "usage", "input");
                    output += ReadInt(message, "usage", "output");
                    if (ReadCost(message, out decimal c))
                    {
                        cost += c;
                        haveCost = true;
                    }
                    stopReason = TryStr(message, "stopReason", out string sr) ? sr : stopReason;
                    errorMessage = TryStr(message, "errorMessage", out string em) ? em : errorMessage;
                }

        if (stopReason is "error" or "abort")
            return ParsedLine.Failed(StopReason.Refusal, Chunk($"Pi {stopReason}: {errorMessage ?? "no error message recorded"}"));

        return ParsedLine.Complete(StopReason.EndTurn, haveCost ? new TokenUsage(input, output, cost) : new TokenUsage(input, output));
    }

    private static string ErrorMessage(JsonElement error) =>
        error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } s ? s
        : error.ValueKind == JsonValueKind.Object && TryStr(error, "message", out string m) ? m
        : "unknown error";

    private static AgentMessageChunkSessionUpdate Chunk(string text) => new()
    {
        Content = new TextContentBlock { Text = text },
    };

    private static AgentThoughtChunkSessionUpdate Thought(string text) => new()
    {
        Content = new TextContentBlock { Text = text },
    };

    // The terminal tool event: status from isError (pi reports a failed execution, not a crash),
    // the raw result for the transcript, and any text content as chip content. All fields are
    // best-effort - a missing piece degrades to a bare status update, never a faulted turn.
    private static SessionUpdate ToolExecutionEnd(JsonElement root, string toolCallId)
    {
        bool failed = root.TryGetProperty("isError", out JsonElement isError) && isError.ValueKind == JsonValueKind.True;

        JsonElement? rawOutput = null;
        if (root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object)
            rawOutput = result.Clone();

        ToolCallContent[]? content = null;
        if (rawOutput is { } raw && raw.TryGetProperty("content", out JsonElement blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            List<ToolCallContent> pieces = [];
            foreach (JsonElement block in blocks.EnumerateArray())
                if (TryStr(block, "type", out string blockType) && blockType == "text" &&
                    TryStr(block, "text", out string blockText) && blockText.Length > 0)
                    pieces.Add(new ContentToolCallContent
                    {
                        Content = new TextContentBlock { Text = blockText },
                    });
            if (pieces.Count > 0)
                content = [.. pieces];
        }

        return new ToolCallUpdateSessionUpdate
        {
            ToolCallId = toolCallId,
            Status = failed ? ToolCallStatus.Failed : ToolCallStatus.Completed,
            RawOutput = rawOutput,
            Content = content,
        };
    }

    // Two-level read: obj[name][inner].
    private static int ReadInt(JsonElement obj, string name, string inner) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Object ? ReadInt(el, inner) : 0;

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v) ? v : 0;

    private static bool ReadCost(JsonElement message, out decimal cost)
    {
        if (message.TryGetProperty("usage", out JsonElement usage) &&
            usage.TryGetProperty("cost", out JsonElement costEl) &&
            costEl.ValueKind == JsonValueKind.Object &&
            costEl.TryGetProperty("total", out JsonElement total) &&
            total.ValueKind == JsonValueKind.Number &&
            total.TryGetDecimal(out decimal d))
        {
            cost = d;
            return true;
        }
        cost = 0m;
        return false;
    }

    // JSON-boundary string read.
    private static bool TryStr(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }
}
