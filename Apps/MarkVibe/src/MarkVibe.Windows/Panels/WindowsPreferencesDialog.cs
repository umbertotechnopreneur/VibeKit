/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native window, reader and AI connection preferences
 * Human guidance: apply preferences through the application model and keep credentials in native services
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

/// <summary>Supplies the name and typeface to both presenters of a native font picker.</summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial class FontPreview(string label, FontFamily family)
{
    public string Label { get; } = label;
    public FontFamily Family { get; } = family;

    /// <summary>Exposes the readable label to controls that request the item's text.</summary>
    public override string ToString() => Label;
}

/// <summary>Presents global preferences and folder-specific reader appearance without owning persistence.</summary>
internal static class WindowsPreferencesDialog
{
    private const double SettingsWidth = 760;
    private const double TwoColumnWidth = 600;
    private const double ColumnGap = 28;
    private const double DialogHorizontalPadding = 48;
    private const double WindowGutter = 32;
    private const double SettingsChromeHeight = 216;

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
        Func<AiConnectionService> connections, DesktopOptions options, CancellationToken lifetime)
    {
        using var dialogLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var form = new Form(preferences, apply, dialogLifetime.Token);
        var appearance = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Top };
        form.Choice(appearance, "Application appearance", [new("system", "System"), new("light", "Light"), new("dark", "Dark")],
            value => value.Appearance, (value, next) => value with { Appearance = next });
        form.Choice(appearance, "Map colors", GraphThemeCatalog.Options.Select(theme => new Choice(theme.Id, theme.Label)),
            value => value.GraphPalette, (value, next) => value with { GraphPalette = next });
        form.Check(appearance, "Acrylic map surfaces", value => value.Acrylic, (value, next) => value with { Acrylic = next });
        form.Choice(appearance, "Application language", Languages, value => value.Language, (value, next) => value with { Language = next });

        var fonts = InstalledFonts();
        var typography = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Top };
        typography.Children.Add(Heading("Fonts"));
        form.Choice(typography, "UI font", fonts, value => value.UiFont, (value, next) => value with { UiFont = next }, previewFont: true);
        form.Choice(typography, "Graph font", fonts, value => value.GraphFont, (value, next) => value with { GraphFont = next }, previewFont: true);
        AddDocumentFont(typography, form, fonts);
        var appearancePage = SettingsColumns(appearance, typography);

        var workspaceDetails = new StackPanel { Spacing = 20, VerticalAlignment = VerticalAlignment.Top };
        var workspace = Section(workspaceDetails, "Workspace");
        form.Check(workspace, "Show source as plain text", value => value.SimpleSource, (value, next) => value with { SimpleSource = next },
            "Read and copy Markdown without syntax highlighting. Turn this off to see headings and other Markdown elements in color.");
        form.Check(workspace, "Reopen the last document", value => value.RememberLastDocument, (value, next) => value with { RememberLastDocument = next },
            "When you start MarkVibe, it opens the last folder and document and returns to your reading position.");
        AddFileAssociations(workspaceDetails, options);

        var document = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Top };
        form.Choice(document, "Document presentation", [new("technical", "Technical documentation"), new("editorial", "Editorial reading"), new("compact", "Compact reading")],
            value => value.DocumentStyle, (value, next) => value with { DocumentStyle = next });
        form.Choice(document, "Reading zoom", Enumerable.Range(8, 8).Select(step => new Choice((step * 10).ToString(System.Globalization.CultureInfo.InvariantCulture), (step * 10) + "%"))
            .Append(new Choice(preferences.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture), preferences.Zoom + "%")).DistinctBy(choice => choice.Id),
            value => value.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (value, next) => value with { Zoom = int.Parse(next, System.Globalization.CultureInfo.InvariantCulture) });
        AddReaderChoices(document, form);
        var documentPage = SettingsColumns(document, workspaceDetails);
        var providers = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };

        var appearanceTab = SettingsCategory("Appearance", "SettingsAppearanceTab");
        var documentTab = SettingsCategory("Document", "SettingsDocumentTab");
        var aiTab = SettingsCategory("AI", "SettingsAiTab");
        var categories = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(categories, "SettingsCategorySelector");
        AutomationProperties.SetName(categories, T("Settings"));
        categories.Items.Add(appearanceTab);
        categories.Items.Add(documentTab);
        categories.Items.Add(aiTab);
        categories.SelectedItem = appearanceTab;

        form.Status.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(form.Status, AutomationLiveSetting.Polite);

        var scroll = WindowsDialogs.Scroll(appearancePage);
        scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        scroll.Padding = new Thickness(0, 0, 12, 0);
        var body = new Grid();
        categories.Margin = new Thickness(0, 0, 0, 12);
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(categories);
        Grid.SetRow(scroll, 1);
        body.Children.Add(scroll);
        Grid.SetRow(form.Status, 2);
        body.Children.Add(form.Status);
        var dialog = Dialog(root, "Settings", body);
        dialog.Resources["ContentDialogMaxWidth"] = SettingsWidth;
        dialog.PrimaryButtonText = T("Reset preferences");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            form.Reset();
        };
        Func<Task>? openAiTest = null;
        double? reopenOffset = null;
        var refreshProviderStatus = AddProviderChoices(providers, form, connections, options.SmokeTest, dialogLifetime.Token, candidate =>
        {
            reopenOffset = scroll.VerticalOffset;
            openAiTest = () => WindowsOpenAiTestDialog.ShowAsync(root, candidate, lifetime);
            dialog.Hide();
        });
        categories.SelectionChanged += async (_, _) =>
        {
            var next = categories.SelectedItem;
            FrameworkElement page = next == aiTab ? providers : next == documentTab ? documentPage : appearancePage;
            if (ReferenceEquals(scroll.Content, page))
            {
                return;
            }

            scroll.ChangeView(null, 0, null, disableAnimation: true);
            scroll.Content = page;
            WindowsUiFont.ApplyTo(page);
            if (next == aiTab)
            {
                await refreshProviderStatus();
            }
        };
        dialog.Opened += (_, _) =>
        {
            if (reopenOffset is double offset)
            {
                scroll.ChangeView(null, offset, null, disableAnimation: true);
                reopenOffset = null;
            }
        };

        /// <summary>Sizes only the settings dialog content and preserves control instances when the layout changes.</summary>
        void Arrange(XamlRoot sender, XamlRootChangedEventArgs? args)
        {
            body.Width = Math.Clamp(sender.Size.Width - DialogHorizontalPadding - WindowGutter, 0, SettingsWidth - DialogHorizontalPadding);
            // The middle row scrolls independently of the category strip, save status and dialog buttons.
            body.Height = Math.Clamp(sender.Size.Height - SettingsChromeHeight, 0, 468);
            providers.Width = Math.Clamp(body.Width - scroll.Padding.Right, 0, 500);
            var wide = body.Width >= TwoColumnWidth;
            ArrangeSettingsColumns(appearancePage, wide);
            ArrangeSettingsColumns(documentPage, wide);
        }

        dialog.Closing += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            await form.Saved;
            deferral.Complete();
        };
        Arrange(root, null);
        root.Changed += Arrange;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await WindowsDialogs.ShowAsync(dialog);
                if (openAiTest == null)
                {
                    break;
                }

                var test = openAiTest;
                openAiTest = null;
                await test();
                await refreshProviderStatus();
            }
        }
        finally
        {
            root.Changed -= Arrange;
            dialogLifetime.Cancel();
        }
    }

    /// <summary>Creates a named native category item with a matching pointer hint.</summary>
    private static SelectorBarItem SettingsCategory(string label, string automationId)
    {
        var item = new SelectorBarItem { Text = T(label) };
        AutomationProperties.SetAutomationId(item, automationId);
        ToolTipService.SetToolTip(item, item.Text);
        return item;
    }

    /// <summary>Builds a settings page whose controls retain identity during vertical or side-by-side layout.</summary>
    private static Grid SettingsColumns(FrameworkElement first, FrameworkElement second)
    {
        var columns = new Grid { RowSpacing = 20 };
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.Children.Add(first);
        columns.Children.Add(second);
        return columns;
    }

    /// <summary>Moves the same controls between one column and two based on the dialog width.</summary>
    private static void ArrangeSettingsColumns(Grid columns, bool wide)
    {
        columns.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        columns.ColumnSpacing = wide ? ColumnGap : 0;
        var second = (FrameworkElement)columns.Children[1];
        Grid.SetRow(second, wide ? 0 : 1);
        Grid.SetColumn(second, wide ? 1 : 0);
    }

    /// <summary>Edits reader appearance for one folder and offers an explicit return to the global values.</summary>
    public static async Task ShowReaderAsync(XamlRoot root, Preferences preferences, bool hasFolderAppearance,
        Func<ReaderAppearance?, Task> apply, CancellationToken lifetime)
    {
        using var dialogLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var form = new Form(preferences, value => apply(ReaderAppearance.From(value)), dialogLifetime.Token);
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(4, 0, 12, 0) };
        panel.Children.Add(Text("These reading settings apply only to this folder.", 14));
        panel.Children.Add(form.Status);
        AutomationProperties.SetLiveSetting(form.Status, AutomationLiveSetting.Polite);
        AddReaderChoices(panel, form);
        AddDocumentFont(panel, form, InstalledFonts());
        var scroll = WindowsDialogs.Scroll(panel);
        var dialog = Dialog(root, "Folder appearance", scroll);
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
                form.ShowSaveError();
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
        await WindowsDialogs.ShowScrollableAsync(dialog, scroll, 548);
    }

    /// <summary>Shares the independent code and document theme choices between global and folder settings.</summary>
    private static void AddReaderChoices(Panel panel, Form form)
    {
        form.Choice(panel, "Light document theme", ThemeChoices(ReaderThemes.Documents("light")), value => value.LightDocumentTheme,
            (value, next) => value with { LightDocumentTheme = next });
        form.Choice(panel, "Dark document theme", ThemeChoices(ReaderThemes.Documents("dark")), value => value.DarkDocumentTheme,
            (value, next) => value with { DarkDocumentTheme = next });
        form.Choice(panel, "Light code theme", ThemeChoices(ReaderThemes.Code("light")), value => value.LightCodeTheme,
            (value, next) => value with { LightCodeTheme = next });
        form.Choice(panel, "Dark code theme", ThemeChoices(ReaderThemes.Code("dark")), value => value.DarkCodeTheme,
            (value, next) => value with { DarkCodeTheme = next });
        form.Check(panel, "Underline hyperlinks", value => value.UnderlineLinks, (value, next) => value with { UnderlineLinks = next });
    }

    /// <summary>Exposes installed document typefaces and an explicit return to the active theme typography.</summary>
    private static void AddDocumentFont(Panel panel, Form form, Choice[] fonts)
    {
        var documentFonts = fonts.Select(font => font.Id == "system" ? font with { Label = "Theme font" } : font);
        form.Choice(panel, "Document font", documentFonts, value => value.DocumentFont, (value, next) => value with { DocumentFont = next }, previewFont: true);
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
        WindowsDialogs.SetButtonDescription(action, T("Choose MarkVibe in Windows Default apps"));
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
        WindowsDialogs.SetButtonDescription(refresh, T("Refresh the Markdown file association status."));
        refresh.Click += (_, _) => status.Text = T(Associations(MarkdownFileAssociations.GetStatus(options)));
        var actions = new NativeWrapPanel();
        actions.Children.Add(action);
        actions.Children.Add(refresh);
        var section = Section(panel, "MARKDOWN FILES");
        section.Children.Add(status);
        section.Children.Add(actions);
    }

    /// <summary>Formats Windows-reported defaults for the two Markdown extensions.</summary>
    private static string Associations(FileAssociationStatus status) =>
        $".md  {AssociationValue(status.MdDefault)}     .markdown  {AssociationValue(status.MarkdownDefault)}";

    /// <summary>Distinguishes a different default app from an unavailable Windows query.</summary>
    private static string AssociationValue(bool? value) => value == true ? T("MarkVibe") : value == false ? T("Another app") : T("Unavailable");

    /// <summary>Supports both native AI connections with local status and user-initiated tests.</summary>
    private static Func<Task> AddProviderChoices(Panel panel, Form form, Func<AiConnectionService> connections, bool smoke,
        CancellationToken lifetime, Action<string?> openAiTest)
    {
        var users = form.Choice(panel, "AI provider", [new(AiProviders.ApiKey, "OpenAI API key"), new(AiProviders.CodexCli, "Codex CLI")],
            value => value.AiProvider, (value, next) => value with { AiProvider = next });
        var key = new PasswordBox { Header = T("OpenAI API key"), MaxLength = 4096, PasswordRevealMode = PasswordRevealMode.Hidden };
        var status = Text("", 12);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var testOpenAi = new Button { Content = T("Test OpenAI key"), IsEnabled = !smoke };
        AutomationProperties.SetAutomationId(testOpenAi, "OpenAiTestButton");
        WindowsDialogs.SetButtonDescription(testOpenAi, T("Send a short test message to OpenAI."));
        testOpenAi.Click += (_, _) =>
        {
            var candidate = key.Password;
            key.Password = "";
            openAiTest(string.IsNullOrWhiteSpace(candidate) ? null : candidate);
        };
        var testCodex = new Button { Content = T("Test connection"), IsEnabled = !smoke };
        WindowsDialogs.SetButtonDescription(testCodex, T("Test the local Codex CLI connection."));
        var generation = 0;
        var activeProvider = form.Value.AiProvider;
        CancellationTokenSource? pending = null;

        /// <summary>Refreshes provider status and discards a candidate only when changing providers.</summary>
        async Task RefreshAsync()
        {
            var id = ++generation;
            pending?.Cancel();
            var provider = form.Value.AiProvider;
            var usingOpenAi = provider == AiProviders.ApiKey;
            if (provider != activeProvider)
            {
                activeProvider = provider;
                key.Password = "";
            }
            key.Visibility = testOpenAi.Visibility = usingOpenAi ? Visibility.Visible : Visibility.Collapsed;
            testCodex.Visibility = usingOpenAi ? Visibility.Collapsed : Visibility.Visible;
            testCodex.IsEnabled = !smoke;
            if (smoke)
            {
                status.Text = T("AI connections are unavailable in this application mode.");
                return;
            }
            try
            {
                lifetime.ThrowIfCancellationRequested();
                var result = usingOpenAi
                    ? AiConnectionService.OpenAiStatus()
                    : await connections().StatusAsync(provider, lifetime);
                if (id == generation && !lifetime.IsCancellationRequested)
                {
                    status.Text = T(result.Message);
                    testCodex.IsEnabled = result.Available;
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
        testCodex.Click += async (_, _) =>
        {
            var id = ++generation;
            testCodex.IsEnabled = false;
            status.Text = T("Testing connection…");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            pending = cancellation;
            try
            {
                var result = await connections().TestAsync(AiProviders.CodexCli, null, cancellation.Token);
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
                    testCodex.IsEnabled = true;
                }
            }
        };
        panel.Children.Add(key);
        panel.Children.Add(testOpenAi);
        panel.Children.Add(testCodex);
        panel.Children.Add(status);
        return RefreshAsync;
    }

    /// <summary>Creates the native dialog with its standard close action.</summary>
    private static ContentDialog Dialog(XamlRoot root, string title, FrameworkElement content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Content = content,
            CloseButtonText = T("Close"),
            DefaultButton = ContentDialogButton.Close
        };
        WindowsDialogs.SetTitle(dialog, T(title));
        return dialog;
    }

    /// <summary>Adds a flat settings section without a nested scroll surface.</summary>
    private static StackPanel Section(Panel parent, string title)
    {
        var section = new StackPanel { Spacing = 10 };
        section.Children.Add(Heading(title));
        parent.Children.Add(section);
        return section;
    }

    /// <summary>Marks the section label as a heading for assistive technology.</summary>
    private static TextBlock Heading(string title)
    {
        var heading = Text(title, 16);
        heading.FontWeight = FontWeights.Medium;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        return heading;
    }

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
    private static TextBlock Text(string value, double size) => new()
    {
        Text = T(value), FontSize = size, FontWeight = FontWeights.Normal, TextWrapping = TextWrapping.Wrap
    };

    /// <summary>Projects one ephemeral dialog draft and delegates serialized persistence to the owning model.</summary>
    private sealed class Form(Preferences current, Func<Preferences, Task> apply, CancellationToken lifetime)
    {
        private readonly List<Action<Preferences>> _setters = [];
        private bool _updating;

        public Preferences Value { get; private set; } = current;
        public TextBlock Status { get; } = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        public Task Saved { get; private set; } = Task.CompletedTask;
        public event Action? Changed;

        /// <summary>Builds a labelled selector bound to a managed preference value.</summary>
        public ComboBox Choice(Panel panel, string label, IEnumerable<Choice> choices, Func<Preferences, string> read,
            Func<Preferences, string, Preferences> replace, bool previewFont = false)
        {
            var options = choices.ToArray();
            var control = new ComboBox { Header = Text(label, 14), HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(control, T(label));
            if (previewFont)
            {
                control.ItemTemplate = (DataTemplate)Application.Current.Resources["FontPreviewTemplate"];
                control.ItemsSource = options.Select(option => new FontPreview(T(option.Label), option.Id == "system"
                    ? new FontFamily("XamlAutoFontFamily") : new FontFamily(option.Id))).ToArray();
            }
            else
            {
                control.ItemsSource = options.Select(option => T(option.Label)).ToArray();
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
                var index = control.SelectedIndex;
                if (index >= 0 && index < options.Length)
                {
                    Change(replace(Value, options[index].Id));
                }
            };
            _setters.Add(Set);
            panel.Children.Add(control);
            return control;
        }

        /// <summary>Builds a native checkbox for a boolean model setting.</summary>
        public void Check(Panel panel, string label, Func<Preferences, bool> read, Func<Preferences, bool, Preferences> replace,
            string? description = null)
        {
            var control = new CheckBox { Content = T(label), IsChecked = read(Value) };
            if (description != null)
            {
                var caption = new StackPanel { Spacing = 3 };
                caption.Children.Add(Text(label, 14));
                var detail = Text(description, 12);
                detail.Style = (Style)Application.Current.Resources["SettingsDescriptionStyle"];
                caption.Children.Add(detail);
                control.Content = caption;
                control.HorizontalAlignment = HorizontalAlignment.Stretch;
                AutomationProperties.SetName(control, T(label));
                AutomationProperties.SetHelpText(control, detail.Text);
            }
            control.Checked += (_, _) => Change(replace(Value, true));
            control.Unchecked += (_, _) => Change(replace(Value, false));
            _setters.Add(value => control.IsChecked = read(value));
            panel.Children.Add(control);
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
                    Status.Visibility = Visibility.Collapsed;
                    Status.Text = string.Empty;
                }
            }
            catch (Exception)
            {
                if (next == Value && !lifetime.IsCancellationRequested)
                {
                    ShowSaveError();
                }
            }
        }

        /// <summary>Exposes the live status only when a preference write needs attention.</summary>
        public void ShowSaveError()
        {
            Status.Visibility = Visibility.Visible;
            Status.Text = T("Preferences could not be saved. Try again.");
        }
    }
}
