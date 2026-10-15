using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using RevitCopilot.Tools;

namespace RevitCopilot.Core;

/// <summary>Callbacks the chat UI supplies so the agent can report progress and ask permission.</summary>
public interface ICopilotHost
{
    void ShowAssistantText(string text);
    void ShowActivity(string text);
    /// <summary>Ask the user to approve a model change. Return false to cancel it.</summary>
    Task<bool> ConfirmChangeAsync(string toolName, string inputJson);
}

/// <summary>
/// Runs the conversation with Claude: sends the user's instruction plus the Revit tool list,
/// executes whichever tools Claude asks for (via <see cref="RevitBridge"/>), feeds results back,
/// and repeats until Claude has a final answer.
/// </summary>
public sealed class CopilotAgent(RevitBridge bridge, ToolRegistry tools)
{
    private const int MaxStepsPerInstruction = 30;

    private const string SystemPrompt = """
        You are Revit Copilot, an AI partner built into Autodesk Revit. Architects and engineers give you
        instructions in plain language; you inspect and change the open Revit model using the tools provided.

        How to work:
        - Look before you act: use the read-only tools to find levels, types, element ids and parameter names
          rather than guessing them.
        - All lengths you send and receive are in metres and areas in square metres, unless a tool says otherwise.
        - If an instruction is ambiguous in a way that would change the result (which level, which type, which
          elements), ask one short question instead of guessing.
        - Every change you make is a separate undoable step named "Copilot: ..." in Revit's undo list. The user
          may decline a change; if they do, do not retry it - ask what they want instead.
        - If a tool returns an error, read it, fix the input, and try again once; then explain the problem.
        - Finish with a short summary of what you found or changed, with counts and names. Keep replies concise:
          the chat pane is narrow.
        - If something cannot be done with the available tools, say so plainly and suggest how the user could do it
          manually in Revit.
        """;

    private readonly AnthropicClient _client = new();
    private readonly List<BetaMessageParam> _history = [];

    public string Model { get; set; } = Environment.GetEnvironmentVariable("REVIT_COPILOT_MODEL") ?? "claude-opus-5-5";

    public static bool HasApiKey => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

    public void Reset() => _history.Clear();

    public async Task RunAsync(string instruction, ICopilotHost host, CancellationToken ct)
    {
        int turnStart = _history.Count;
        _history.Add(new BetaMessageParam { Role = Role.User, Content = instruction });

        try
        {
            for (int step = 0; step < MaxStepsPerInstruction; step++)
            {
                ct.ThrowIfCancellationRequested();
                BetaMessage response = await _client.Beta.Messages.Create(BuildRequest(), ct);

                List<BetaContentBlockParam> assistant = [];
                List<BetaToolUseBlock> toolCalls = [];
                foreach (BetaContentBlock block in response.Content)
                {
                    if (block.TryPickText(out BetaTextBlock? text))
                    {
                        assistant.Add(new BetaTextBlockParam { Text = text.Text });
                        if (!string.IsNullOrWhiteSpace(text.Text)) host.ShowAssistantText(text.Text);
                    }
                    else if (block.TryPickThinking(out BetaThinkingBlock? thinking))
                    {
                        // Thinking blocks must be sent back unchanged, signature included.
                        assistant.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    }
                    else if (block.TryPickRedactedThinking(out BetaRedactedThinkingBlock? redacted))
                    {
                        assistant.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                    }
                    else if (block.TryPickFallback(out BetaFallbackBlock? fallback))
                    {
                        // Marks where a fallback model took over after a refusal; keep it in history.
                        assistant.Add(new BetaFallbackBlockParam
                        {
                            From = new BetaFallbackInfoParam { Model = fallback.From.Model },
                            To = new BetaFallbackInfoParam { Model = fallback.To.Model },
                            Trigger = JsonSerializer.SerializeToElement(fallback.Trigger),
                        });
                        host.ShowActivity($"(answered by fallback model {fallback.To.Model})");
                    }
                    else if (block.TryPickToolUse(out BetaToolUseBlock? toolUse))
                    {
                        assistant.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                        toolCalls.Add(toolUse);
                    }
                }
                _history.Add(new BetaMessageParam { Role = Role.Assistant, Content = assistant });

                if (response.StopReason == BetaStopReason.Refusal)
                {
                    host.ShowActivity("Claude declined this request.");
                    return;
                }
                if (toolCalls.Count == 0)
                {
                    if (response.StopReason == BetaStopReason.MaxTokens)
                        host.ShowActivity("Reply was cut off (output limit reached).");
                    return;
                }

                // Every tool_use needs a matching tool_result, all in one user message.
                List<BetaContentBlockParam> results = [];
                foreach (BetaToolUseBlock call in toolCalls)
                    results.Add(await RunToolAsync(call, host, ct));
                _history.Add(new BetaMessageParam { Role = Role.User, Content = results });
            }

            host.ShowActivity($"Stopped after {MaxStepsPerInstruction} steps. Say \"continue\" to let me keep going.");
        }
        catch
        {
            // Drop the unfinished turn so the conversation stays valid for the next instruction.
            _history.RemoveRange(turnStart, _history.Count - turnStart);
            throw;
        }
    }

    private MessageCreateParams BuildRequest() => new()
    {
        Model = Model,
        MaxTokens = 16000,
        System = SystemPrompt,
        OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
        // If the model declines a request, let the API retry it on a suitable fallback model.
        Betas = ["server-side-fallback-2026-07-01"],
        Fallbacks = new Default(),
        // Cache the (large, unchanging) tool list + system prompt + history between steps.
        CacheControl = new BetaCacheControlEphemeral(),
        Tools = tools.All.Select(ToApiTool).ToList(),
        Messages = _history,
    };

    private static BetaToolUnion ToApiTool(RevitTool t) => new BetaTool
    {
        Name = t.Name,
        Description = t.ModifiesModel ? t.Description + " (Changes the model; the user is asked to approve.)" : t.Description,
        InputSchema = new()
        {
            Properties = JsonSerializer.SerializeToElement(t.Properties)
                .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
            Required = t.Required,
        },
    };

    private async Task<BetaToolResultBlockParam> RunToolAsync(BetaToolUseBlock call, ICopilotHost host, CancellationToken ct)
    {
        string inputJson = JsonSerializer.Serialize(call.Input);
        RevitTool? tool = tools.Find(call.Name);
        if (tool is null)
            return Error(call, $"Unknown tool '{call.Name}'.");

        if (tool.ModifiesModel && !await host.ConfirmChangeAsync(tool.Name, inputJson))
        {
            host.ShowActivity($"✗ {tool.Name} declined");
            return Error(call, "The user declined this change. Do not retry it; ask what they would like instead.");
        }

        ct.ThrowIfCancellationRequested();
        host.ShowActivity($"→ {tool.Name}");
        try
        {
            JsonElement input = JsonSerializer.SerializeToElement(call.Input);
            string result = await bridge.RunAsync(app => tool.Run(app, input));
            return new BetaToolResultBlockParam { ToolUseID = call.ID, Content = result };
        }
        catch (Exception ex)
        {
            host.ShowActivity($"✗ {tool.Name}: {ex.Message}");
            return Error(call, ex.Message);
        }
    }

    private static BetaToolResultBlockParam Error(BetaToolUseBlock call, string message) =>
        new() { ToolUseID = call.ID, Content = message, IsError = true };
}
