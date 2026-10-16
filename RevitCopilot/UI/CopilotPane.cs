using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RevitCopilot.Core;

namespace RevitCopilot.UI;

/// <summary>
/// The chat pane docked inside Revit. Built in code (no XAML) to keep the project small.
/// </summary>
public sealed class CopilotPane : UserControl, ICopilotHost
{
    private static readonly string[] Examples =
    [
        "Give me an overview of this model",
        "Colour the columns in this view by type",
        "Create a filter for concrete columns and beams and show them orange",
        "Select all beams longer than 6 m on Level 2",
        "Add a text parameter 'Concrete Grade' to structural columns and beams",
        "Set Concrete Grade to M30 on all columns on Level 1",
        "Number the selected columns C1, C2, C3… by Mark",
    ];

    private readonly CopilotAgent _agent;
    private readonly StackPanel _messages = new() { Margin = new Thickness(8) };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly CheckBox _confirm = new() { Content = "Ask before changing the model", IsChecked = true, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private CancellationTokenSource? _running;

    public CopilotPane(CopilotAgent agent)
    {
        _agent = agent;
        Background = Brushes.White;

        var newChat = new Button { Content = "New chat", Padding = new Thickness(8, 2, 8, 2) };
        newChat.Click += (_, _) => { _running?.Cancel(); _agent.Reset(); _messages.Children.Clear(); ShowWelcome(); };
        var header = new DockPanel { Margin = new Thickness(8, 6, 8, 6), LastChildFill = false };
        header.Children.Add(_confirm);
        DockPanel.SetDock(newChat, Dock.Right);
        header.Children.Add(newChat);

        _input = new TextBox
        {
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MinHeight = 48, MaxHeight = 140,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4),
            ToolTip = "Type an instruction. Enter sends, Shift+Enter adds a new line.",
        };
        _input.PreviewKeyDown += OnInputKeyDown;
        _send = new Button { Content = "Send", Width = 64, Margin = new Thickness(6, 0, 0, 0) };
        _send.Click += async (_, _) => await SendOrStopAsync();
        var inputRow = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(_send, Dock.Right);
        inputRow.Children.Add(_send);
        inputRow.Children.Add(_input);

        _scroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(inputRow, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(inputRow);
        root.Children.Add(_scroll);
        Content = root;

        ShowWelcome();
    }

    private void ShowWelcome()
    {
        AddBubble(CopilotAgent.HasApiKey
            ? "Hi! I'm your Revit Copilot. Tell me what to find or change in this model. Every change I make can be undone with Ctrl+Z."
            : "Set the ANTHROPIC_API_KEY environment variable (then restart Revit) to start using the copilot.",
            Brushes.Black, Color.FromRgb(0xF1, 0xF3, 0xF5));
        foreach (string example in Examples)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = example, TextWrapping = TextWrapping.Wrap },
                HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(6, 3, 6, 3), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            };
            b.Click += (_, _) => { _input.Text = example; _input.Focus(); _input.CaretIndex = example.Length; };
            _messages.Children.Add(b);
        }
    }

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            int caret = _input.CaretIndex;
            _input.Text = _input.Text.Insert(caret, Environment.NewLine);
            _input.CaretIndex = caret + Environment.NewLine.Length;
        }
        else if (_running is null)
        {
            await SendOrStopAsync();
        }
        e.Handled = true;
    }

    private async Task SendOrStopAsync()
    {
        if (_running is not null)
        {
            _running.Cancel();
            return;
        }

        string text = _input.Text.Trim();
        if (text.Length == 0) return;
        if (!CopilotAgent.HasApiKey)
        {
            ShowActivity("ANTHROPIC_API_KEY is not set.");
            return;
        }

        _input.Clear();
        AddBubble(text, Brushes.White, Color.FromRgb(0x25, 0x63, 0xEB), alignRight: true);

        _running = new CancellationTokenSource();
        _send.Content = "Stop";
        var thinking = AddActivity("Working…");
        try
        {
            await _agent.RunAsync(text, this, _running.Token);
        }
        catch (OperationCanceledException)
        {
            ShowActivity("Stopped.");
        }
        catch (Exception ex)
        {
            ShowActivity("Error: " + ex.Message);
        }
        finally
        {
            _messages.Children.Remove(thinking);
            _running.Dispose();
            _running = null;
            _send.Content = "Send";
            _input.Focus();
        }
    }

    // ---------- ICopilotHost ----------

    public void ShowAssistantText(string text) => AddBubble(text, Brushes.Black, Color.FromRgb(0xF1, 0xF3, 0xF5));

    public void ShowActivity(string text) => AddActivity(text);

    public Task<bool> ConfirmChangeAsync(string toolName, string inputJson)
    {
        if (_confirm.IsChecked != true) return Task.FromResult(true);
        string details = JsonSerializer.Serialize(JsonDocument.Parse(inputJson), new JsonSerializerOptions { WriteIndented = true });
        if (details.Length > 1500) details = details[..1500] + "\n…";
        MessageBoxResult answer = MessageBox.Show(
            Window.GetWindow(this)!,
            $"The copilot wants to run '{toolName}':\n\n{details}\n\nAllow this change? (You can undo it later with Ctrl+Z.)",
            "Revit Copilot - approve change", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return Task.FromResult(answer == MessageBoxResult.Yes);
    }

    // ---------- rendering ----------

    private void AddBubble(string text, Brush foreground, Color background, bool alignRight = false)
    {
        var bubble = new Border
        {
            Background = new SolidColorBrush(background), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(alignRight ? 24 : 0, 4, alignRight ? 0 : 24, 4),
            HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Child = new TextBox
            {
                // Read-only TextBox rather than TextBlock so users can copy answers.
                Text = text, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Foreground = foreground,
            },
        };
        _messages.Children.Add(bubble);
        _scroll.ScrollToEnd();
    }

    private TextBlock AddActivity(string text)
    {
        var line = new TextBlock
        {
            Text = text, Foreground = Brushes.Gray, FontStyle = FontStyles.Italic, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 1, 4, 1),
        };
        _messages.Children.Add(line);
        _scroll.ScrollToEnd();
        return line;
    }
}
