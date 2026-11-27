// SPDX-License-Identifier: MIT
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace Tools.Widgets;

// Shell supplies physical desktop coordinates. The menu has its own WinUI root so it
// can receive keyboard focus and render at the taskbar monitor's DPI while Studio is hidden.
public sealed class WidgetTrayMenu : IDisposable
{
    private readonly Window owner;
    private readonly Action<Items> populate;
    private Window? popup;
    private MenuFlyoutPresenter? presenter;
    private int generation;
    private bool visible, disposed;

    public WidgetTrayMenu(Window owner, Action open, Action preferences, Action about, Action quit,
        Func<(bool Active, bool Available)> effectState, Action<bool> setEffect, Action<Items>? configure = null)
        : this(owner, commands =>
        {
            commands.Add("Preferences", preferences);
            commands.Add("Open", open);
            commands.Separator();
            var state = effectState();
            commands.Toggle("Effect", state.Active, state.Available, () => setEffect(!effectState().Active));
            configure?.Invoke(commands);
            commands.Add("Disable all effects", DesktopEffectStop.Broadcast);
            commands.Separator();
            commands.Add("Quit", quit);
            commands.Separator();
            commands.Add("Mini Utilities", OpenMiniUtilities);
            commands.Add("About", about);
        })
    {
    }

    public WidgetTrayMenu(Window owner, Action<Items> populate)
    {
        this.owner = owner;
        this.populate = populate;
    }

    public sealed class Items
    {
        private readonly WidgetTrayMenu parent;
        internal readonly MenuFlyout Menu = new();
        internal Items(WidgetTrayMenu parent) => this.parent = parent;
        public void Add(string text, Action execute, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            Bind(item, execute);
        }

        public void Toggle(string text, bool active, bool enabled, Action execute) =>
            Bind(new ToggleMenuFlyoutItem { Text = text, IsChecked = active, IsEnabled = enabled }, execute);

        private void Bind(MenuFlyoutItem item, Action execute)
        {
            item.Click += (_, _) =>
            {
                parent.Hide();
                parent.owner.DispatcherQueue.TryEnqueue(() => { if (!parent.disposed) execute(); });
            };
            Menu.Items.Add(item);
        }

        public void Separator() => Menu.Items.Add(new MenuFlyoutSeparator());
    }

    public void Show(int x, int y)
    {
        if (disposed) return;
        if (popup is null)
        {
            presenter = new MenuFlyoutPresenter { MinWidth = 198, HorizontalAlignment = HorizontalAlignment.Stretch };
            popup = new Window { Content = presenter, Title = owner.Title };
            popup.AppWindow.IsShownInSwitchers = false;
            var frame = (OverlappedPresenter)popup.AppWindow.Presenter;
            frame.IsResizable = frame.IsMaximizable = frame.IsMinimizable = false;
            frame.SetBorderAndTitleBar(false, false);
            frame.IsAlwaysOnTop = true;
            popup.Activated += (_, change) =>
            {
                if (change.WindowActivationState == WindowActivationState.Deactivated) Hide();
            };
            popup.AppWindow.Closing += (_, args) => { if (!disposed) { args.Cancel = true; Hide(); } };
            presenter.KeyDown += (_, args) =>
            {
                if (args.Key == VirtualKey.Escape) { args.Handled = true; Hide(); }
            };
        }

        int opened = ++generation;
        var commands = new Items(this);
        populate(commands);
        var content = presenter!;
        content.RequestedTheme = (owner.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
        // MenuFlyoutPresenter expects the native vector of MenuFlyoutItemBase from MenuFlyout.
        content.ItemsSource = commands.Menu.Items;
        var point = new PointInt32(x, y);
        var area = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Nearest).WorkArea;
        // Place the entire HWND on the target monitor before querying its DPI. At the
        // taskbar edge, moving just the top left could leave most of it on another display.
        popup.AppWindow.MoveAndResize(new RectInt32(area.X + area.Width / 2, area.Y + area.Height / 2, 1, 1));
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(popup);
        void Fit()
        {
            double scale = Math.Max(96u, GetDpiForWindow(handle)) / 96d;
            content.Measure(new Size(area.Width / scale, area.Height / scale));
            // The first layout pass can precede template realization. Refine the initial estimate once loaded.
            int width = Math.Clamp((int)Math.Ceiling(Math.Max(content.MinWidth, content.DesiredSize.Width) * scale), 1, area.Width);
            double heightDip = content.DesiredSize.Height;
            if (heightDip <= 1) heightDip = commands.Menu.Items.Count * 32 + 12;
            int height = Math.Clamp((int)Math.Ceiling(heightDip * scale), 1, area.Height);
            int left = Math.Clamp(x + width <= area.X + area.Width ? x : x - width, area.X, area.X + area.Width - width);
            int top = Math.Clamp(y + height <= area.Y + area.Height ? y : y - height, area.Y, area.Y + area.Height - height);
            popup.AppWindow.MoveAndResize(new RectInt32(left, top, width, height));
        }
        Fit();
        visible = true;
        popup.AppWindow.Show();
        popup.Activate();
        SetForegroundWindow(handle);
        popup.DispatcherQueue.TryEnqueue(() =>
        {
            if (!visible || disposed || opened != generation) return;
            Fit();
            if (commands.Menu.Items.Count > 0) commands.Menu.Items[0].Focus(FocusState.Programmatic);
        });
    }

    private static void OpenMiniUtilities()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://umbertogiacobbi.biz/mini-utilities/") { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception error) { Debug.WriteLine(error.Message); }
    }

    private void Hide()
    {
        if (!visible) return;
        visible = false;
        popup?.AppWindow.Hide();
    }

    public void Dispose()
    {
        disposed = true;
        visible = false;
        popup?.Close();
        popup = null;
        presenter = null;
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
