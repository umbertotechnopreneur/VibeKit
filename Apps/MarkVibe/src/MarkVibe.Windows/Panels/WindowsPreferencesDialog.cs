/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native window, reader and AI connection preferences
 * Human guidance: apply preferences through the application model and keep credentials in native services
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

/// <summary>Presents global preferences and folder-specific reader appearance without owning persistence.</summary>
internal static class WindowsPreferencesDialog
{
    /// <summary>Represents one localized display choice with a fixed value from the managed preference contract.</summary>
    private sealed record Choice(string Id, string Label);

    private static readonly Choice[] Languages =
    [
        new("system", "System language"), new("en", "English"), new("it", "Italiano"), new("fr", "Français"),
        new("vi", "Tiếng Việt"), new("zh-CN", "简体中文"), new("yue-HK", "廣東話"), new("id", "Bahasa Indonesia"),
        new("ko", "한국어"), new("ja", "日本語")
    ];

    /// <summary>Shows the native settings dialog and invokes only managed preference and connection callbacks.</summary>
    public static async Task ShowAsync(XamlRoot root, Preferences preferences, Func<Preferences, Task> apply,
        AiConnectionService connections, DesktopOptions options, CancellationToken lifetime)
    {
        using var dialogLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var form = new Form(preferences, apply, dialogLifetime.Token);
        var panel = form.Panel;
        panel.Children.Add(Text("Make MarkVibe yours", 20));
        panel.Children.Add(form.Status);
        form.Choice("Application appearance", [new("system", "System"), new("light", "Light"), new("dark", "Dark")],
            value => value.Appearance, (value, next) => value with { Appearance = next });
        form.Check("Acrylic window background", value => value.Acrylic, (value, next) => value with { Acrylic = next });
        form.Choice("Application language", Languages, value => value.Language, (value, next) => value with { Language = next });
        var fonts = InstalledFonts();
        form.Choice("UI font", fonts, value => value.UiFont, (value, next) => value with { UiFont = next });
        form.Check("Use lightweight source view", value => value.SimpleSource, (value, next) => value with { SimpleSource = next });
        form.Check("Remember last workspace", value => value.RememberLastDocument, (value, next) => value with { RememberLastDocument = next });
        form.Choice("Document presentation", [new("technical", "Technical documentation"), new("editorial", "Editorial reading"), new("compact", "Compact reading")],
            value => value.DocumentStyle, (value, next) => value with { DocumentStyle = next });
        form.Choice("Reading zoom", Enumerable.Range(8, 8).Select(step => new Choice((step * 10).ToString(System.Globalization.CultureInfo.InvariantCulture), (step * 10) + "%"))
            .Append(new Choice(preferences.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture), preferences.Zoom + "%")).DistinctBy(choice => choice.Id),
            value => value.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (value, next) => value with { Zoom = int.Parse(next, System.Globalization.CultureInfo.InvariantCulture) });
        AddReaderChoices(form, fonts);
        AddFileAssociations(panel, options);
        AddProviderChoices(form, connections, options.SmokeTest, dialogLifetime.Token);
        var reset = new Button { Content = T("Reset preferences") };
        reset.Click += (_, _) => form.Reset();
        panel.Children.Add(reset);

        var dialog = Dialog(root, "Settings", panel);
        dialog.Closing += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            await form.Saved;
            dialogLifetime.Cancel();
            deferral.Complete();
        };
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            dialogLifetime.Cancel();
        }
    }

    /// <summary>Edits reader appearance for one folder and offers an explicit return to the global values.</summary>
    public static async Task ShowReaderAsync(XamlRoot root, Preferences preferences, bool hasFolderAppearance,
        Func<ReaderAppearance?, Task> apply, CancellationToken lifetime)
    {
        using var dialogLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var form = new Form(preferences, value => apply(ReaderAppearance.From(value)), dialogLifetime.Token);
        form.Panel.Children.Add(form.Status);
        AddReaderChoices(form, InstalledFonts());
        var dialog = Dialog(root, "Folder appearance", form.Panel);
        dialog.SecondaryButtonText = T("Use global settings");
        dialog.IsSecondaryButtonEnabled = hasFolderAppearance;
        form.Changed += () => dialog.IsSecondaryButtonEnabled = true;
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                await form.Saved;
                await apply(null);
            }
            catch (Exception)
            {
                form.Status.Text = T("Preferences could not be saved. Try again.");
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };
        dialog.Closing += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            await form.Saved;
            dialogLifetime.Cancel();
            deferral.Complete();
        };
        await dialog.ShowAsync();
    }

    /// <summary>Shares the independent code and document theme choices between global and folder settings.</summary>
    private static void AddReaderChoices(Form form, Choice[] fonts)
    {
        form.Choice("Light document theme", ThemeChoices(ReaderThemes.Documents("light")), value => value.LightDocumentTheme,
            (value, next) => value with { LightDocumentTheme = next });
        form.Choice("Dark document theme", ThemeChoices(ReaderThemes.Documents("dark")), value => value.DarkDocumentTheme,
            (value, next) => value with { DarkDocumentTheme = next });
        form.Choice("Light code theme", ThemeChoices(ReaderThemes.Code("light")), value => value.LightCodeTheme,
            (value, next) => value with { LightCodeTheme = next });
        form.Choice("Dark code theme", ThemeChoices(ReaderThemes.Code("dark")), value => value.DarkCodeTheme,
            (value, next) => value with { DarkCodeTheme = next });
        form.Choice("Document font", fonts, value => value.DocumentFont, (value, next) => value with { DocumentFont = next });
        form.Check("Underline hyperlinks", value => value.UnderlineLinks, (value, next) => value with { UnderlineLinks = next });
    }

    /// <summary>Shows native file-association status and opens Windows Default apps on explicit activation.</summary>
    private static void AddFileAssociations(Panel panel, DesktopOptions options)
    {
        var initial = MarkdownFileAssociations.GetStatus(options);
        if (!initial.CanConfigure)
        {
            return;
        }

        var status = Text(Associations(initial), 12);
        var action = new Button { Content = T("Use MarkVibe for Markdown files") };
        action.Click += (_, _) =>
        {
            try
            {
                MarkdownFileAssociations.OpenSettings(options);
            }
            catch (Exception)
            {
                status.Text = T("Default-app setup could not be opened. Open Windows Settings > Apps > Default apps and select MarkVibe.");
            }
        };
        var refresh = new Button { Content = T("Refresh") };
        refresh.Click += (_, _) => status.Text = T(Associations(MarkdownFileAssociations.GetStatus(options)));
        var actions = new NativeWrapPanel();
        actions.Children.Add(action);
        actions.Children.Add(refresh);
        panel.Children.Add(Text("Markdown files", 16));
        panel.Children.Add(status);
        panel.Children.Add(actions);
    }

    /// <summary>Formats Windows-reported defaults for the two Markdown extensions.</summary>
    private static string Associations(FileAssociationStatus status) =>
        $".md  {AssociationValue(status.MdDefault)}     .markdown  {AssociationValue(status.MarkdownDefault)}";

    /// <summary>Distinguishes a different default app from an unavailable Windows query.</summary>
    private static string AssociationValue(bool? value) => value == true ? T("MarkVibe") : value == false ? T("Another app") : T("Unavailable");

    /// <summary>Supports both native AI connections with local status and user-initiated tests.</summary>
    private static void AddProviderChoices(Form form, AiConnectionService connections, bool smoke, CancellationToken lifetime)
    {
        form.Panel.Children.Add(Text("AI connection", 16));
        var users = form.Choice("AI provider", [new(AiProviders.ApiKey, "OpenAI API key"), new(AiProviders.CodexCli, "Codex CLI")],
            value => value.AiProvider, (value, next) => value with { AiProvider = next });
        var key = new PasswordBox { Header = T("OpenAI API key"), MaxLength = 4096, PasswordRevealMode = PasswordRevealMode.Hidden };
        var status = Text("", 12);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var test = new Button { Content = T("Test connection"), IsEnabled = !smoke };
        var generation = 0;
        CancellationTokenSource? pending = null;

        /// <summary>Shows only local configuration, cancelling obsolete requests when the provider changes.</summary>
        async Task RefreshAsync()
        {
            var id = ++generation;
            pending?.Cancel();
            var provider = form.Value.AiProvider;
            key.Password = "";
            key.Visibility = provider == AiProviders.ApiKey ? Visibility.Visible : Visibility.Collapsed;
            test.IsEnabled = !smoke;
            if (smoke)
            {
                status.Text = T("AI connections are unavailable in this application mode.");
                return;
            }
            try
            {
                var result = await connections.StatusAsync(provider, lifetime);
                if (id == generation && !lifetime.IsCancellationRequested)
                {
                    status.Text = T(result.Message);
                    test.IsEnabled = result.Available;
                    key.IsEnabled = result.Available;
                }
            }
            catch (OperationCanceledException)
            {
                // Status lookups do not outlive the dialog or provider choice.
            }
            catch (Exception)
            {
                if (id == generation && !lifetime.IsCancellationRequested)
                {
                    status.Text = T("AI connection status is unavailable.");
                }
            }
        }

        users.SelectionChanged += async (_, _) => await RefreshAsync();
        test.Click += async (_, _) =>
        {
            var id = ++generation;
            var provider = form.Value.AiProvider;
            var candidate = provider == AiProviders.ApiKey ? key.Password : null;
            key.Password = "";
            test.IsEnabled = false;
            key.IsEnabled = false;
            status.Text = T("Testing connection…");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            pending = cancellation;
            try
            {
                var result = await connections.TestAsync(provider, candidate, cancellation.Token);
                if (id == generation && !cancellation.IsCancellationRequested)
                {
                    status.Text = T(result.Message);
                }
            }
            catch (OperationCanceledException)
            {
                // Closing the dialog or changing provider cancels credential validation.
            }
            catch (Exception)
            {
                if (id == generation && !cancellation.IsCancellationRequested)
                {
                    status.Text = T("The connection test could not be completed.");
                }
            }
            finally
            {
                if (ReferenceEquals(pending, cancellation))
                {
                    pending = null;
                }
                if (id == generation && !cancellation.IsCancellationRequested)
                {
                    test.IsEnabled = true;
                    key.IsEnabled = true;
                }
            }
        };
        form.Panel.Children.Add(key);
        form.Panel.Children.Add(test);
        form.Panel.Children.Add(status);
        form.Panel.Loaded += async (_, _) => await RefreshAsync();
    }

    /// <summary>Creates a native scrollable dialog that fits the current content viewport.</summary>
    private static ContentDialog Dialog(XamlRoot root, string title, StackPanel panel) => new()
    {
        XamlRoot = root,
        Title = T(title),
        Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(150, root.Size.Height - 200), VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        CloseButtonText = T("Close"),
        DefaultButton = ContentDialogButton.Close
    };

    /// <summary>Lists the application font catalog entries actually present on this Windows system.</summary>
    private static Choice[] InstalledFonts()
    {
        try
        {
            return UiFontCatalog.Installed(CanvasTextFormat.GetSystemFontFamilies()).Select(font => new Choice(font.Id, font.Label)).ToArray();
        }
        catch (Exception)
        {
            return [new("system", "System default")];
        }
    }

    /// <summary>Maps authoritative theme catalog choices to translated native controls.</summary>
    private static IEnumerable<Choice> ThemeChoices(IEnumerable<ReaderThemeChoice> themes) => themes.Select(theme => new Choice(theme.Id, theme.Label));

    /// <summary>Builds a translated caption or live status with visible wrapping.</summary>
    private static TextBlock Text(string value, double size) => new() { Text = T(value), FontSize = size, TextWrapping = TextWrapping.Wrap };

    /// <summary>Projects one ephemeral dialog draft and delegates serialized persistence to the owning model.</summary>
    private sealed class Form(Preferences current, Func<Preferences, Task> apply, CancellationToken lifetime)
    {
        private readonly List<Action<Preferences>> _setters = [];
        private bool _updating;

        public Preferences Value { get; private set; } = current;
        public StackPanel Panel { get; } = new() { Spacing = 10, Margin = new Thickness(0, 0, 12, 0) };
        public TextBlock Status { get; } = Text("Changes are saved automatically.", 12);
        public Task Saved { get; private set; } = Task.CompletedTask;
        public event Action? Changed;

        /// <summary>Builds a labelled selector bound to a managed preference value.</summary>
        public ComboBox Choice(string label, IEnumerable<Choice> choices, Func<Preferences, string> read, Func<Preferences, string, Preferences> replace)
        {
            var options = choices.ToArray();
            var control = new ComboBox { Header = T(label), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var option in options)
            {
                control.Items.Add(new ComboBoxItem { Content = T(option.Label), Tag = option.Id });
            }
            /// <summary>Reads the current selected identifier without firing a write during initialization or reset.</summary>
            void Set(Preferences value)
            {
                var id = read(value);
                control.SelectedIndex = Math.Max(0, Array.FindIndex(options, option => option.Id == id));
            }
            Set(Value);
            control.SelectionChanged += (_, _) =>
            {
                if (control.SelectedItem is ComboBoxItem { Tag: string selected })
                {
                    Change(replace(Value, selected));
                }
            };
            _setters.Add(Set);
            Panel.Children.Add(control);
            return control;
        }

        /// <summary>Builds a native checkbox for a boolean model setting.</summary>
        public void Check(string label, Func<Preferences, bool> read, Func<Preferences, bool, Preferences> replace)
        {
            var control = new CheckBox { Content = T(label), IsChecked = read(Value) };
            control.Checked += (_, _) => Change(replace(Value, true));
            control.Unchecked += (_, _) => Change(replace(Value, false));
            _setters.Add(value => control.IsChecked = read(value));
            Panel.Children.Add(control);
        }

        /// <summary>Restores the single managed default snapshot and refreshes its control projections.</summary>
        public void Reset()
        {
            Change(new Preferences());
            _updating = true;
            try
            {
                foreach (var setter in _setters)
                {
                    setter(Value);
                }
            }
            finally
            {
                _updating = false;
            }
        }

        /// <summary>Queues the latest value without letting a slower save restore earlier selections.</summary>
        private void Change(Preferences next)
        {
            next = next.Validated();
            if (_updating || lifetime.IsCancellationRequested || next == Value)
            {
                return;
            }
            Value = next;
            Saved = SaveAsync(Saved, next);
            Changed?.Invoke();
        }

        /// <summary>Serializes writes and skips snapshots superseded before their save begins.</summary>
        private async Task SaveAsync(Task previous, Preferences next)
        {
            await previous;
            if (lifetime.IsCancellationRequested || next != Value)
            {
                return;
            }
            try
            {
                await apply(next);
                if (next == Value && !lifetime.IsCancellationRequested)
                {
                    Status.Text = T("Changes are saved automatically.");
                }
            }
            catch (Exception)
            {
                if (next == Value && !lifetime.IsCancellationRequested)
                {
                    Status.Text = T("Preferences could not be saved. Try again.");
                }
            }
        }
    }
}
