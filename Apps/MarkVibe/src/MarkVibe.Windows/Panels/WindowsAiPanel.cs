/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native Markdown AI composer, context and review view
 * Human guidance: use WinUI controls and keep assistant workflow state in the managed model
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

/// <summary>Displays the managed assistant projection and forwards user commands to its owning model.</summary>
internal sealed class WindowsAiPanel : UserControl
{
    private readonly WindowsAiSession _session;
    private readonly TextBlock _target = Label("", 13);
    private readonly TextBlock _status = Label("", 12);
    private readonly TextBlock _usage = Label("", 12);
    private readonly ProgressRing _waiting = new() { Width = 20, Height = 20, Visibility = Visibility.Collapsed };
    private readonly TextBox _prompt = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxHeight = 200,
        MaxLength = MarkdownAi.PromptCharacters, Header = T("Editing request")
    };
    private readonly TextBox _response = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240,
        Header = T("AI response"), Visibility = Visibility.Collapsed
    };
    private readonly ComboBox _model = new() { Header = T("Model"), DisplayMemberPath = nameof(MarkdownAiModel.Label), SelectedValuePath = nameof(MarkdownAiModel.Id) };
    private readonly ComboBox _effort = new() { Header = T("Reasoning effort"), ItemsSource = MarkdownAi.Efforts };
    private readonly Button _check = ButtonFor("Check connection");
    private readonly Button _settings = ButtonFor("Settings");
    private readonly Button _send = ButtonFor("Send");
    private readonly Button _cancel = ButtonFor("Cancel");
    private readonly Button _preview = ButtonFor("Preview Markdown");
    private readonly Button _undo = ButtonFor("Undo");
    private readonly Button _redo = ButtonFor("Redo");
    private readonly Button _save = ButtonFor("Save a copy");
    private readonly Button _restore = ButtonFor("Restore original Markdown");
    private readonly Button _close = ButtonFor("Close assistant");
    private readonly Button _addCurrentReference = ButtonFor("Add current document");
    private readonly Button _wholeDocument = ButtonFor("Use whole document");
    private readonly Button _clearReferences = ButtonFor("Clear references");
    private readonly StackPanel _references = new() { Spacing = 3 };
    private readonly ContentControl _referenceItems = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private IReadOnlyList<WindowsAiReferenceRow> _presentedReferences = [];
    private bool _rendering;

    /// <summary>Routes the connection settings action to the shell.</summary>
    public event Action? SettingsRequested;

    /// <summary>Routes document revision undo to the shell model.</summary>
    public event Action? UndoRequested;

    /// <summary>Routes document revision redo to the shell model.</summary>
    public event Action? RedoRequested;

    /// <summary>Routes saving the current source copy to native file commands.</summary>
    public event Action? SaveCopyRequested;

    /// <summary>Requests the original source from the current document's revision model.</summary>
    public event Action? RestoreRequested;

    /// <summary>Requests that the shell close the assistant pane.</summary>
    public event Action? CloseRequested;

    /// <summary>Requests adding the current Markdown file to the retained collection.</summary>
    public event Action? AddCurrentReferenceRequested;

    /// <summary>Requests a read-only rendered preview of the active AI response.</summary>
    public event Action<MarkdownModel>? PreviewRequested;

    /// <summary>Requests navigation to one reference in the current workspace.</summary>
    public event Action<string>? OpenReferenceRequested;

    /// <summary>Assembles keyboard-accessible native controls for one model-backed assistant pane.</summary>
    public WindowsAiPanel(WindowsAiSession session)
    {
        _session = session;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Content = Layout();
        Loaded += (_, _) =>
        {
            _session.Changed += Render;
            Render();
        };
        Unloaded += (_, _) => _session.Changed -= Render;

        _check.Click += async (_, _) => await _session.RefreshAvailabilityAsync();
        _settings.Click += (_, _) => SettingsRequested?.Invoke();
        _send.Click += async (_, _) => await _session.SendAsync();
        _cancel.Click += (_, _) => _session.Cancel();
        _preview.Click += (_, _) =>
        {
            if (_session.Snapshot().Preview is { } model)
            {
                PreviewRequested?.Invoke(model);
            }
        };
        _undo.Click += (_, _) => UndoRequested?.Invoke();
        _redo.Click += (_, _) => RedoRequested?.Invoke();
        _save.Click += (_, _) => SaveCopyRequested?.Invoke();
        _restore.Click += (_, _) => RestoreRequested?.Invoke();
        _close.Click += (_, _) => CloseRequested?.Invoke();
        _addCurrentReference.Click += (_, _) => AddCurrentReferenceRequested?.Invoke();
        _wholeDocument.Click += (_, _) => _session.ClearSelection();
        _clearReferences.Click += (_, _) => _session.RemoveReference(null);
        _prompt.TextChanged += (_, _) =>
        {
            if (!_rendering)
            {
                _session.SetPrompt(_prompt.Text);
            }
        };
        _model.SelectionChanged += (_, _) =>
        {
            if (!_rendering && _model.SelectedItem is MarkdownAiModel model)
            {
                _session.SetModel(model.Id);
            }
        };
        _effort.SelectionChanged += (_, _) =>
        {
            if (!_rendering && _effort.SelectedItem is string effort)
            {
                _session.SetEffort(effort);
            }
        };
    }

    /// <summary>Places response, editing controls, references and usage in one resizable native panel.</summary>
    private ScrollViewer Layout()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
        panel.Children.Add(Actions(Label("AI Assistant", 18), _close));
        panel.Children.Add(_target);
        panel.Children.Add(Actions(_check, _settings, _wholeDocument));
        panel.Children.Add(_response);
        panel.Children.Add(Actions(_preview));
        panel.Children.Add(_prompt);
        panel.Children.Add(Actions(_model, _effort));
        panel.Children.Add(Actions(_send, _cancel, _waiting));
        panel.Children.Add(_status);
        panel.Children.Add(Actions(_undo, _redo, _save, _restore));

        var collection = new StackPanel { Spacing = 4 };
        collection.Children.Add(Label("Add Markdown files from the workspace or map.", 12));
        _referenceItems.Content = _references;
        collection.Children.Add(_referenceItems);
        collection.Children.Add(Actions(_addCurrentReference, _clearReferences));
        panel.Children.Add(new Expander { Header = T("Reference documents"), Content = collection, HorizontalAlignment = HorizontalAlignment.Stretch });
        panel.Children.Add(new Expander { Header = T("Token usage"), Content = _usage, HorizontalAlignment = HorizontalAlignment.Stretch });
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>Applies an immutable snapshot while suppressing callbacks from programmatic control updates.</summary>
    private void Render()
    {
        var state = _session.Snapshot();
        _rendering = true;
        try
        {
            _target.Text = T(state.Target);
            _status.Text = T(state.Status);
            _prompt.IsEnabled = state.HasTarget && !state.Busy;
            _model.IsEnabled = state.Models.Count > 0 && !state.Busy && !state.Checking;
            _effort.IsEnabled = state.HasTarget && !state.Busy;
            _check.IsEnabled = !state.Busy && !state.Checking;
            _settings.IsEnabled = !state.Busy;
            _send.IsEnabled = state.CanSend && !state.Checking;
            _cancel.Visibility = state.Busy ? Visibility.Visible : Visibility.Collapsed;
            _waiting.Visibility = state.Busy || state.Checking ? Visibility.Visible : Visibility.Collapsed;
            _waiting.IsActive = state.Busy || state.Checking;
            _undo.IsEnabled = state.CanUndo;
            _redo.IsEnabled = state.CanRedo;
            _save.IsEnabled = state.CanSave;
            _restore.IsEnabled = state.CanSave;
            _addCurrentReference.IsEnabled = state.HasTarget && !state.Busy && !state.References.Any(item => item.IsTarget);
            _wholeDocument.Visibility = state.HasSelection ? Visibility.Visible : Visibility.Collapsed;
            _wholeDocument.IsEnabled = !state.Busy;
            _clearReferences.IsEnabled = !state.Busy && state.References.Count > 0;
            _referenceItems.IsEnabled = !state.Busy;
            _preview.Visibility = state.Preview != null ? Visibility.Visible : Visibility.Collapsed;
            _preview.IsEnabled = !state.Busy;

            if (_prompt.Text != state.Prompt)
            {
                _prompt.Text = state.Prompt;
            }
            if (!ReferenceEquals(_model.ItemsSource, state.Models))
            {
                _model.ItemsSource = state.Models;
            }
            _model.SelectedValue = state.Model;
            _effort.SelectedItem = state.Effort;
            var review = state.Result is { Kind: "review" } result ? result.Markdown : state.Result?.Summary ?? "";
            if (_response.Text != review)
            {
                _response.Text = review;
            }
            _response.Visibility = review.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            DrawReferences(state.References);
            _usage.Text = Usage(state);
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>Reuses rendered reference rows during prompt and request-state changes that do not alter membership.</summary>
    private void DrawReferences(IReadOnlyList<WindowsAiReferenceRow> rows)
    {
        if (_presentedReferences.SequenceEqual(rows))
        {
            return;
        }
        _presentedReferences = rows;
        _references.Children.Clear();
        foreach (var row in rows)
        {
            var open = new HyperlinkButton { Content = row.Title, IsEnabled = !row.Missing, HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTipService.SetToolTip(open, row.Path);
            open.Click += (_, _) => OpenReferenceRequested?.Invoke(row.Path);
            var remove = ButtonFor("Remove");
            AutomationProperties.SetName(remove, T("Remove") + " " + row.Path);
            remove.Click += (_, _) => _session.RemoveReference(row.Path);
            var details = row.IsTarget ? T("Current document") : row.Missing ? T("Unavailable") : row.Tokens is int count ? T("{0} estimated tokens", count) : "";
            _references.Children.Add(Actions(open, Label(details, 11), remove));
        }
    }

    /// <summary>Distinguishes estimates, provider-reported counts and cumulative session totals.</summary>
    private static string Usage(WindowsAiSnapshot state)
    {
        var usage = state.Usage;
        var input = T(usage.InputCounted || usage.Reported ? "Counted input" : "Estimated input");
        var summary = $"{input}: {usage.Input:N0}\n{T("Document")}: {usage.Document:N0}  ·  {T("Prompt")}: {usage.Prompt:N0}  ·  {T("System")}: {usage.System:N0}";
        if (state.Capacity is { } capacity)
        {
            summary += $"\n{T("Context window")}: {capacity.ContextWindow:N0}  ·  {T("Available")}: {capacity.FreeTokens:N0}";
            if (state.Provider == AiProviders.ApiKey)
            {
                summary += $"  ·  {T(capacity.CostTier)}";
            }
        }
        if (usage.Reported)
        {
            summary += $"\n{T("Output")}: {usage.Output:N0}  ·  {T("Reasoning")}: {usage.Reasoning:N0}  ·  {T("Cached")}: {usage.Cached:N0}";
            summary += $"\n{T("Session total")}: {state.TotalTokens:N0}  ·  {T("Requests")}: {state.RequestCount:N0}";
        }
        return summary;
    }

    /// <summary>Creates an action with a translated native accessibility name.</summary>
    private static Button ButtonFor(string label) => new() { Content = T(label) };

    /// <summary>Creates a readable label whose text can wrap at the current panel width.</summary>
    private static TextBlock Label(string value, double size) => new() { Text = T(value), FontSize = size, TextWrapping = TextWrapping.Wrap };

    /// <summary>Wraps related actions without replacing native input and focus behavior.</summary>
    private static NativeWrapPanel Actions(params UIElement[] controls)
    {
        var panel = new NativeWrapPanel();
        foreach (var control in controls)
        {
            panel.Children.Add(control);
        }
        return panel;
    }
}
