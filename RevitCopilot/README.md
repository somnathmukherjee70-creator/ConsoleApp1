# Revit Copilot

An AI partner that lives inside Autodesk Revit. Open the **AI Copilot → Copilot** button, type an
instruction in plain language, and the copilot inspects or changes the open model for you.

```
You:      Colour the columns in this view by type
Copilot:  → find_elements → color_elements   Coloured 42 columns: 300x300 red (18), 400x400 blue (16), 450x600 green (8).
You:      Make a filter for concrete beams deeper than 600 mm and hide them
Copilot:  → create_view_filter   Filter "Deep Concrete Beams" applied to Level 2 plan — 11 beams hidden.
You:      Add a "Concrete Grade" parameter to columns and beams, set M30 on Level 1 columns
Copilot:  → create_parameter → find_elements → set_parameter   Done: parameter added, 18 columns set to M30.
You:      How many doors are on Level 1, by type?
Copilot:  → find_elements …  Level 1 has 24 doors: 16 × Single-Flush 900x2100, 8 × Double-Glass 1800x2100.
You:      Set Comments to "Fire check" on the double-glass ones
Copilot:  [asks you to approve]  → set_parameter …  Done: 8 doors updated (Ctrl+Z to undo).
```

## How it works

```
 Chat pane (WPF, docked in Revit)
        │ your instruction
        ▼
 CopilotAgent ──► Claude (Anthropic API) ── decides which tools to call
        │ tool calls
        ▼
 RevitBridge (ExternalEvent) ──► ToolRegistry ──► Revit API, on Revit's main thread
```

* **Claude plans, Revit tools act.** Claude never runs arbitrary code; it can only call the tools in
  `Tools/ToolRegistry.cs`.
* **You stay in control.** Any tool that changes the model asks for approval first. You can turn that
  off with the checkbox at the top of the pane.
* **Everything is undoable.** Each change is one `Copilot: …` step in Revit's undo list.
* **Revit warnings don't block it.** They are collected and reported back to the AI.

### Built-in tools

| What | Tools |
|---|---|
| Read the model | `get_model_info`, `get_selection`, `list_levels`, `count_elements`, `get_element_parameters`, `list_rooms`, `list_views`, `list_sheets`, `list_types` |
| Find & select | `find_elements`: several categories at once, active view only, by level, with rules such as *Structural Material contains Concrete* or *Length greater than 6 m*. `select_elements` selects and zooms to elements. |
| Parameters ✱ | `create_parameter` adds a new text/number/length/area/yes-no parameter to categories. `set_parameter` gives many elements one value; `set_parameter_values` gives each element its own value (numbering, copying). |
| Colour & filter the view ✱ | `color_elements` colours elements with one colour, or by parameter value with a legend. `create_view_filter` makes a reusable Revit view filter with a rule, colour or hide. `clear_colors` removes colours. `isolate_in_view` does temporary isolate or hide. |
| Create & edit ✱ | `create_walls`, `create_level`, `create_sheets`, `rename_views`, `delete_elements` |

✱ = asks for your approval first (except temporary isolate/hide).

`create_parameter` stores its definitions in `%AppData%\RevitCopilot\SharedParameters.txt`, so your
own shared parameter file is never touched.

Lengths are in metres and areas in m² between the AI and the tools.

## Setup

**Requirements:** Windows, Revit **2027** (default) or **2026**, the .NET 10 SDK, and an Anthropic API key
from <https://console.anthropic.com>.

1. Set your API key once, then restart Revit:
   ```bat
   setx ANTHROPIC_API_KEY "sk-ant-..."
   ```
2. Build. On Windows the build installs the add-in into
   `%AppData%\Autodesk\Revit\Addins\<version>` automatically:
   ```bat
   dotnet build RevitCopilot -c Release                         :: Revit 2027
   dotnet build RevitCopilot -c Release -p:RevitVersion=2026    :: Revit 2026
   ```
   You can also open `ConsoleApp1.slnx` in Visual Studio and build there.
3. Start Revit, allow the add-in when asked, open a model, and click **AI Copilot → Copilot**.

Optional: set `REVIT_COPILOT_MODEL` to use a different Claude model (default `claude-opus-5-5`).

### Why Revit 2026+ only

The Anthropic SDK depends on System.Text.Json 10. Revit 2027 runs on .NET 10, so it loads natively.
Revit 2026 runs on .NET 8, so the manifest turns on 2026's add-in isolation (`UseRevitContext=False`)
to load the newer libraries without clashing with Revit's own copies. Revit 2025 has no isolation
option, so it isn't supported.

## Teaching it new skills

Add one entry to `ToolRegistry.CreateDefault()`:

```csharp
new("tag_untagged_doors",
    "Place a door tag on every untagged door in the active view.",   // Claude reads this to decide when to use it
    new { },                     // JSON-schema properties for the inputs
    [],                          // required inputs
    true,                        // modifies the model → user is asked to approve
    (app, input) => { /* Revit API code, wrapped in InTransaction(...) */ return Result(new { tagged = 12 }); }),
```

Good next tools: tagging, room renumbering, placing families, view/sheet setup from templates,
schedule export, model health checks (warnings, unplaced rooms, CAD imports).

## Files

| File | Purpose |
|---|---|
| `App.cs` | Revit startup: ribbon button and dockable pane registration |
| `Core/RevitBridge.cs` | Runs work on Revit's API thread via `ExternalEvent` and awaits the result |
| `Core/CopilotAgent.cs` | Conversation with Claude: tool-use loop, approvals, history |
| `Tools/ToolRegistry.cs` | Every action the copilot can take |
| `UI/CopilotPane.cs` | The chat pane |
| `RevitCopilot.addin` | Revit add-in manifest |

## Notes on the AI configuration

* Model `claude-opus-5-5` at `medium` effort. Raise it to `high` in `CopilotAgent.BuildRequest` for harder
  multi-step jobs; this costs more and takes longer.
* Server-side fallback is enabled (`fallbacks: "default"`). If the model ever declines a request,
  the API retries it on a fallback model automatically.
* Prompt caching is on, so the tool list and conversation aren't re-billed in full on every step.
