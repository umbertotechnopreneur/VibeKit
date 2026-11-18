using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace RetroStudio;
internal static class ScrollBarBehavior
{
    // Keep the native scroll range and hit testing; only the bar itself reveals on hover.
    internal static Action HoverScrollBar(ScrollViewer scroll, FrameworkElement region, UIElement eventRoot)
    {
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        ScrollBar? vertical = null;
        uint? draggingPointer = null;
        bool attached = false;

        ScrollBar? FindBar(DependencyObject node)
        {
            // Do not accidentally select a scrollbar belonging to nested content.
            if (node is ScrollViewer nested && !ReferenceEquals(nested, scroll)) return null;
            if (node is ScrollBar bar && bar.Name == "VerticalScrollBar" && bar.Orientation == Orientation.Vertical) return bar;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var found = FindBar(VisualTreeHelper.GetChild(node, i));
                if (found is not null) return found;
            }
            return null;
        }
        bool IsOverBar(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            if (vertical is null || scroll.ScrollableHeight <= 0) return false;
            var point = args.GetCurrentPoint(vertical).Position;
            return point.X >= 0 && point.Y >= 0 && point.X < vertical.ActualWidth && point.Y < vertical.ActualHeight;
        }
        void Update(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            if (vertical is not null) vertical.Opacity = draggingPointer.HasValue || IsOverBar(args) ? 1 : 0;
        }
        void Pressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            if (IsOverBar(args)) draggingPointer = args.Pointer.PointerId;
            Update(sender, args);
        }
        void Released(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            if (draggingPointer == args.Pointer.PointerId) draggingPointer = null;
            Update(sender, args);
        }
        void Loaded(object sender, RoutedEventArgs args)
        {
            if (attached) return;
            attached = true;
            scroll.ApplyTemplate();
            scroll.UpdateLayout();
            vertical = FindBar(scroll);
            // Opacity does not remove the hit target or alter the page width.
            if (vertical is not null) vertical.Opacity = 0;
            eventRoot.AddHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update), true);
            eventRoot.AddHandler(UIElement.PointerEnteredEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update), true);
            eventRoot.AddHandler(UIElement.PointerExitedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update), true);
            eventRoot.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Pressed), true);
            eventRoot.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Released), true);
            eventRoot.AddHandler(UIElement.PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Released), true);
            eventRoot.AddHandler(UIElement.PointerCanceledEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Released), true);
        }
        scroll.Loaded += Loaded;
        region.Unloaded += (_, _) =>
        {
            if (!attached) return;
            attached = false;
            vertical = null;
            draggingPointer = null;
            eventRoot.RemoveHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update));
            eventRoot.RemoveHandler(UIElement.PointerEnteredEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update));
            eventRoot.RemoveHandler(UIElement.PointerExitedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update));
            eventRoot.RemoveHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Pressed));
            eventRoot.RemoveHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Released));
            eventRoot.RemoveHandler(UIElement.PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Released));
            eventRoot.RemoveHandler(UIElement.PointerCanceledEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Released));
        };
        return () => { draggingPointer = null; if (vertical is not null) vertical.Opacity = 0; };
    }

}
