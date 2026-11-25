/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native Markdown AI composer, context and review view
 * Human guidance: use WinUI controls and keep assistant workflow state in the managed model
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using Microsoft.UI.Text;
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
    private readonly Button _close = new()
    {
        Content = new SymbolIcon(Symbol.Cancel), MinWidth = 36, MinHeight = 36, Padding = new Thickness(6),
        HorizontalAlignment = HorizontalAlignment.Right
    };
    private readonly Button _addCurrentReference = ButtonFor("Add current document");
    private readonly Button _wholeDocument = ButtonFor("Use whole document");
    private readonly Button _clearReferences = ButtonFor("Clear references");
    private readonly StackPanel _references = new() { Spacing = 3 };
    private readonly ContentControl _referenceItems = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly NativeWrapPanel _modelControls;
    private readonly NativeWrapPanel _revisionActions;
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
        _modelControls = Actions(_model, _effort);
        _revisionActions = Actions(_undo, _redo, _save, _restore);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(_close, "AssistantCloseButton");
        AutomationProperties.SetName(_close, T("Close assistant"));
        WindowsDialogs.SetButtonDescription(_close, T("Close assistant"));
        Content = Layout();
        Loaded += (_, _) =>
        {
            _session.Changed += Render;
            Render();
            WindowsUiFont.ApplyTo(this);
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

    /// <summary>Reserves the title and Close action while the assistant controls scroll.</summary>
    private Grid Layout()
    {
        var root = new Grid { Padding = new Thickness(12), RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Label("AI Assistant", 20);
        title.FontWeight = FontWeights.Light;
        title.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        header.Children.Add(title);
        Grid.SetColumn(_close, 1);
        header.Children.Add(_close);
        root.Children.Add(header);

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 10, 0) };
        panel.Children.Add(_target);
        panel.Children.Add(Actions(_check, _settings, _wholeDocument));
        panel.Children.Add(_response);
        panel.Children.Add(_preview);
        panel.Children.Add(_prompt);
        panel.Children.Add(_modelControls);
        panel.Children.Add(Actions(_send, _cancel, _waiting));
        panel.Children.Add(_status);
        panel.Children.Add(_revisionActions);

        var collection = new StackPanel { Spacing = 4 };
        collection.Children.Add(Label("Add Markdown files from the workspace or map.", 12));
        _referenceItems.Content = _references;
        collection.Children.Add(_referenceItems);
        collection.Children.Add(Actions(_addCurrentReference, _clearReferences));
        panel.Children.Add(new Expander { Header = T("Reference documents"), Content = collection, HorizontalAlignment = HorizontalAlignment.Stretch });
        panel.Children.Add(new Expander { Header = T("Token usage"), Content = _usage, HorizontalAlignment = HorizontalAlignment.Stretch });
        var scroll = WindowsDialogs.Scroll(panel);
        scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    /// <summary>Provides a visible keyboard target after the full-width compact pane opens.</summary>
    public void FocusClose() => _close.Focus(FocusState.Programmatic);

    /// <summary>Applies an immutable snapshot while suppressing callbacks from programmatic control updates.</summary>
    private void Render()
    {
        var state = _session.Snapshot();
        _rendering = true;
        try
        {
            _target.Text = T(state.Target);
            _status.Text = T(state.Status);
            _status.Visibility = _status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _prompt.IsEnabled = state.HasTarget && !state.Busy;
            _modelControls.Visibility = state.Models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
            if (!state.Busy)
            {
                _undo.Visibility = state.CanUndo ? Visibility.Visible : Visibility.Collapsed;
                _redo.Visibility = state.CanRedo ? Visibility.Visible : Visibility.Collapsed;
                _save.Visibility = _restore.Visibility = state.CanSave ? Visibility.Visible : Visibility.Collapsed;
                _revisionActions.Visibility = state.CanUndo || state.CanRedo || state.CanSave ? Visibility.Visible : Visibility.Collapsed;
            }
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
            var actions = Actions(open, Label(details, 11), remove);
            WindowsUiFont.ApplyTo(actions);
            _references.Children.Add(actions);
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
    private static Button ButtonFor(string label)
    {
        var text = T(label);
        var button = new Button { Content = text };
        ToolTipService.SetToolTip(button, text);
        return button;
    }

    /// <summary>Creates a readable label whose text can wrap at the current panel width.</summary>
    private static TextBlock Label(string value, double size) => new()
    {
        Text = T(value), FontSize = size, FontWeight = FontWeights.Normal, TextWrapping = TextWrapping.Wrap
    };

    /// <summary>Wraps related actions without replacing native input and focus behavior.</summary>
    private static NativeWrapPanel Actions(params UIElement[] controls)
    {
        var panel = new NativeWrapPanel { Spacing = 8 };
        foreach (var control in controls)
        {
            panel.Children.Add(control);
        }
        return panel;
    }
}
