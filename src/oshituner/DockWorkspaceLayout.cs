using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace oshituner;

internal sealed partial class DockWorkspace
{
    private const string TabDragFormat = "OshiTuner.DockTab";
    private const string GroupDragFormat = "OshiTuner.DockGroup";
    private readonly List<GridSplitter> _splitters = [];
    private readonly Border _dropHint = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false, Opacity = .55 };
    private bool _dragging;
    private bool _dragCanceled;

    internal void AddBackground(ThemeBackground background)
    {
        SetZIndex(background, -10);
        SetRowSpan(background, 5);
        SetColumnSpan(background, 5);
        Children.Insert(0, background);
    }

    private void InitializeLayout()
    {
        AllowDrop = true;
        SetResourceReference(BackgroundProperty, "WorkspaceBackground");
        _dropHint.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        SetZIndex(_dropHint, 100);
        Children.Add(_dropHint);
        for (var index = 0; index < 5; index++)
        {
            RowDefinitions.Add(new RowDefinition());
            ColumnDefinitions.Add(new ColumnDefinition());
        }
        DragOver += OnDragOver;
        DragLeave += (_, _) => _dropHint.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
    }

    private void InitializeGroup(Group group)
    {
        group.Chrome.SetResourceReference(Panel.BackgroundProperty, group.Dock is null ? "FloatingPanelBackground" : "DockPanelBackground");
        group.Header.SetResourceReference(Panel.BackgroundProperty, "DockHeaderBackground");
        group.Header.Focusable = true;
        DockPanel.SetDock(group.Header, System.Windows.Controls.Dock.Top);
        group.Chrome.Children.Add(group.Header);
        group.Chrome.Children.Add(group.Body);
        group.Chrome.IsKeyboardFocusWithinChanged += (_, _) => CheckFocus(group);
        group.Header.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (args.OriginalSource is DependencyObject element && IsUnderButton(element)) return;
            group.Header.Focus();
            if (group.Dock is null)
                ObserveDrag(group.Header, args.GetPosition(group.Header), () => DragFloatingGroup(group));
        };
        group.Header.GotKeyboardFocus += (_, _) => CheckFocus(group);
    }

    private static bool IsUnderButton(DependencyObject source)
    {
        for (var current = source; current is Visual; current = VisualTreeHelper.GetParent(current))
            if (current is ButtonBase) return true;
        return false;
    }

    private void RenderGroup(Group group)
    {
        group.Header.Children.Clear();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Right);
        group.Header.Children.Add(buttons);
        if (group.Dock is not null)
        {
            var pin = new ToggleButton { IsChecked = group.Pinned, Width = 30, Height = 27, ToolTip = "Pin dock group" };
            AutomationProperties.SetName(pin, "Pin dock group");
            var icon = new Path
            {
                Width = 14, Height = 14, Stretch = Stretch.Uniform,
                Data = Geometry.Parse("M16,12 V4 H17 V2 H7 V4 H8 V12 L6,14 V16 H11 V22 L12,23 L13,22 V16 H18 V14 Z")
            };
            icon.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = pin });
            if (!group.Pinned) icon.LayoutTransform = new RotateTransform(35);
            pin.Content = icon;
            pin.Click += (_, _) => { if (group.Selected is { } pane) SetPinned(pane.Id, pin.IsChecked == true); };
            buttons.Children.Add(pin);
        }
        var close = new Button { Content = "×", Padding = new Thickness(6, 0, 6, 0), Width = 30, Height = 27, ToolTip = "Close selected pane" };
        AutomationProperties.SetName(close, "Close selected pane");
        close.Click += (_, _) => { if (group.Selected is { } selected) Close(selected.Id); };
        buttons.Children.Add(close);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        group.Header.Children.Add(tabs);
        foreach (var pane in group.Panes)
        {
            var tab = new ToggleButton { Content = pane.Title, IsChecked = pane == group.Selected, Padding = new Thickness(8, 3, 8, 3) };
            AutomationProperties.SetName(tab, pane.Title);
            tab.ToolTip = PaneTabHelp;
            if (pane.Id == "oshi")
            {
                pane.Tab = tab;
                tab.SetResourceReference(Control.ForegroundProperty, "OshiWatchTabForeground");
                tab.SetResourceReference(Control.BorderBrushProperty, tab.IsChecked == true ? "OshiWatchTabForeground" : "BorderBrush");
                RenderOshiWatchTab(tab, pane.Title);
            }
            else if (pane.Id == "twitch-status")
            {
                pane.Tab = tab;
                RenderTwitchStatusTab(tab, pane.Title);
            }
            tab.Click += (_, _) => Select(group, pane, true);
            tab.PreviewMouseLeftButtonDown += (_, args) =>
                ObserveDrag(tab, args.GetPosition(tab), () => DragPane(tab, pane));
            tab.PreviewKeyDown += (_, args) => MoveFromKeyboard(pane, args);
            tabs.Children.Add(tab);
        }

        if (group.Window is { } window && group.Selected is { } selectedPane) window.Title = selectedPane.Title;
        group.Body.Visibility = group.Expanded ? Visibility.Visible : Visibility.Collapsed;
        var content = group.Expanded ? group.Selected?.View : null;
        if (!ReferenceEquals(group.Body.Content, content)) group.Body.Content = content;
    }

    private const string PaneTabHelp =
        "Drag to move this pane. Ctrl+Shift+F floats it. Ctrl+Shift plus an arrow docks it; Home returns it to the center.";
    private static readonly Geometry PlayIcon = Geometry.Parse("M0,0 L8,5 L0,10 Z");
    private static readonly Geometry PauseIcon = Geometry.Parse("M0,0 H3 V10 H0 Z M5,0 H8 V10 H5 Z");

    private void RenderOshiWatchTab(ToggleButton tab, string title)
    {
        var status = _oshiWatchRunning ? "Oshi Watch running" : "Oshi Watch paused";
        tab.SetResourceReference(Control.BackgroundProperty,
            _oshiWatchRunning ? "OshiWatchTabRunningBackground" : "OshiWatchTabPausedBackground");
        SetTabStatus(tab, status);
        var icon = new Path
        {
            Data = _oshiWatchRunning ? PlayIcon : PauseIcon,
            Width = 9, Height = 11, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(Shape.FillProperty, "OshiWatchTabForeground");
        tab.Content = TabContentWithIcon(title, icon);
    }

    private void RenderTwitchStatusTab(ToggleButton tab, string title)
    {
        SetTabStatus(tab, _twitchConnected ? "Twitch connected" : "Twitch disconnected");
        tab.Content = _twitchConnected ? title : TabContentWithIcon(title, new TextBlock
        {
            Text = "\U0001F4A4",
            FontFamily = new FontFamily("Segoe UI Emoji")
        });
    }

    private static void SetTabStatus(ToggleButton tab, string status)
    {
        AutomationProperties.SetItemStatus(tab, status);
        tab.ToolTip = $"{status}. {PaneTabHelp}";
    }

    private static StackPanel TabContentWithIcon(string title, FrameworkElement icon)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = title });
        icon.Margin = new Thickness(6, 0, 0, 0);
        content.Children.Add(icon);
        return content;
    }

    private void MoveFromKeyboard(Pane pane, KeyEventArgs args)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != (ModifierKeys.Control | ModifierKeys.Shift)) return;
        if (args.Key == Key.F) { Move(pane.Id, null); args.Handled = true; }
        else if (args.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home)
        {
            Move(pane.Id, args.Key switch
            {
                Key.Up => DockLocation.Top, Key.Down => DockLocation.Bottom, Key.Left => DockLocation.Left,
                Key.Right => DockLocation.Right, _ => DockLocation.Center
            });
            args.Handled = true;
        }
    }

    private void ShowFloating(Group group, bool activate)
    {
        if (_trayHidden) return;
        var window = new Window
        {
            Owner = _shell, Content = group.Chrome, Width = 680, Height = 420, MinWidth = 260, MinHeight = 170,
            ShowActivated = activate, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResizeWithGrip,
            WindowStartupLocation = group.Bounds is null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.Manual,
            Title = group.Selected?.Title ?? "Oshi Tuner"
        };
        group.Window = window;
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6) });
        window.SetResourceReference(Window.BackgroundProperty, "FloatingPanelBackground");
        group.Header.AllowDrop = true;
        group.Header.DragOver += (_, args) =>
        {
            if (args.Data.GetData(TabDragFormat) is not Pane pane || pane.Group == group) return;
            args.Effects = DragDropEffects.Move;
            args.Handled = true;
        };
        group.Header.Drop += (_, args) =>
        {
            if (args.Data.GetData(TabDragFormat) is not Pane pane || pane.Group is not { } original || original == group) return;
            if (group.Selected is not { } peer) return;
            JoinFloatingGroup(pane.Id, peer.Id);
            args.Effects = DragDropEffects.Move;
            args.Handled = true;
        };
        window.SourceInitialized += (_, _) => WindowPlacement.Apply(window, group.Bounds, ActiveWindow());
        window.LocationChanged += (_, _) => QueueSave();
        window.SizeChanged += (_, _) => QueueSave();
        window.Closing += (_, args) =>
        {
            if (_stopping || group.Window != window || group.Selected is null) return;
            args.Cancel = true;
            Dispatcher.BeginInvoke(() => { if (group.Selected is { } selected) Close(selected.Id); });
        };
        window.Show();
    }

    private static void ObserveDrag(FrameworkElement handle, Point original, Action drag)
    {
        MouseEventHandler? moving = null;
        MouseButtonEventHandler? released = null;
        void Detach()
        {
            handle.PreviewMouseMove -= moving;
            handle.PreviewMouseLeftButtonUp -= released;
            handle.MouseLeave -= Left;
        }

        void Left(object sender, MouseEventArgs args)
        {
            if (args.LeftButton != MouseButtonState.Pressed) Detach();
        }

        moving = (_, args) =>
        {
            if (args.LeftButton != MouseButtonState.Pressed) { Detach(); return; }
            var point = args.GetPosition(handle);
            if (Math.Abs(point.X - original.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(point.Y - original.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            Detach();
            drag();
        };
        released = (_, _) => Detach();
        handle.PreviewMouseMove += moving;
        handle.PreviewMouseLeftButtonUp += released;
        handle.MouseLeave += Left;
    }

    private bool DroppedOutside(FrameworkElement handle, string format, object data)
    {
        _dragging = true;
        _dragCanceled = false;
        QueryContinueDragEventHandler observeCancel = (_, args) => _dragCanceled |= args.EscapePressed;
        handle.QueryContinueDrag += observeCancel;
        try { return DragDrop.DoDragDrop(handle, new DataObject(format, data), DragDropEffects.Move) == DragDropEffects.None && !_dragCanceled; }
        finally
        {
            handle.QueryContinueDrag -= observeCancel;
            _dragging = false;
            _dropHint.Visibility = Visibility.Collapsed;
            foreach (var group in _groups) CheckFocus(group);
        }
    }

    private void DragPane(FrameworkElement handle, Pane pane)
    {
        if (!DroppedOutside(handle, TabDragFormat, pane)) return;
        var bounds = WindowPlacement.NearPointer(new WindowBounds(0, 0, 680, 420, null), new(36, 18));
        if (pane.Group is { } source) Remove(source, pane);
        Select(Float(pane, bounds), pane, true);
    }

    private void DragFloatingGroup(Group group)
    {
        if (group.Window is not { } window || WindowPlacement.Read(window) is not { } before) return;
        var offset = WindowPlacement.PointerOffset(before);
        if (DroppedOutside(group.Header, GroupDragFormat, group))
            WindowPlacement.Apply(window, WindowPlacement.NearPointer(before, offset), ActiveWindow());
        QueueSave();
    }

    private DockLocation DropLocation(Point point)
    {
        var horizontal = Math.Min(ActualWidth * .24, 170);
        var vertical = Math.Min(ActualHeight * .24, 140);
        if (point.Y < vertical) return DockLocation.Top;
        if (point.Y > ActualHeight - vertical) return DockLocation.Bottom;
        if (point.X < horizontal) return DockLocation.Left;
        return point.X > ActualWidth - horizontal ? DockLocation.Right : DockLocation.Center;
    }

    private void OnDragOver(object sender, DragEventArgs args)
    {
        if (!args.Data.GetDataPresent(TabDragFormat) && !args.Data.GetDataPresent(GroupDragFormat)) return;
        var location = DropLocation(args.GetPosition(this));
        SetRowSpan(_dropHint, 5);
        SetColumnSpan(_dropHint, 5);
        _dropHint.HorizontalAlignment = location == DockLocation.Left ? HorizontalAlignment.Left
            : location == DockLocation.Right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        _dropHint.VerticalAlignment = location == DockLocation.Top ? VerticalAlignment.Top
            : location == DockLocation.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        _dropHint.Width = location is DockLocation.Left or DockLocation.Right ? Math.Min(ActualWidth * .24, 170) : double.NaN;
        _dropHint.Height = location is DockLocation.Top or DockLocation.Bottom ? Math.Min(ActualHeight * .24, 140) : double.NaN;
        _dropHint.Margin = location == DockLocation.Center ? new Thickness(115, 90, 115, 90) : new Thickness(0);
        _dropHint.Visibility = Visibility.Visible;
        args.Effects = DragDropEffects.Move;
        args.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs args)
    {
        var location = DropLocation(args.GetPosition(this));
        if (args.Data.GetData(TabDragFormat) is Pane pane) Move(pane.Id, location);
        else if (args.Data.GetData(GroupDragFormat) is Group { Selected: { } selected })
            RedockFloatingGroup(selected.Id, location);
        else return;
        args.Effects = DragDropEffects.Move;
        args.Handled = true;
        _dropHint.Visibility = Visibility.Collapsed;
    }

    private void UpdateLayoutSlots()
    {
        for (var index = 0; index < 5; index++)
        {
            RowDefinitions[index].Height = index == 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            ColumnDefinitions[index].Width = index == 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
        foreach (var splitter in _splitters) Children.Remove(splitter);
        _splitters.Clear();

        foreach (var group in _groups)
        {
            if (group.Dock is not { } dock) continue;
            SetRow(group.Chrome, dock switch { DockLocation.Top => 0, DockLocation.Bottom => 4, _ => 2 });
            SetColumn(group.Chrome, dock switch { DockLocation.Left => 0, DockLocation.Right => 4, DockLocation.Center => 2, _ => 0 });
            SetColumnSpan(group.Chrome, dock is DockLocation.Top or DockLocation.Bottom ? 5 : 1);
            if (dock == DockLocation.Center)
            {
                group.Chrome.VerticalAlignment = group.Expanded ? VerticalAlignment.Stretch : VerticalAlignment.Top;
                continue;
            }
            var vertical = dock is DockLocation.Top or DockLocation.Bottom;
            var index = dock is DockLocation.Top or DockLocation.Left ? 0 : 4;
            var splitterIndex = index == 0 ? 1 : 3;
            var available = vertical ? ActualHeight : ActualWidth;
            var maxExtent = available > 0 ? Math.Max(100, available * .55) : 1000;
            var extent = group.Expanded ? new GridLength(Math.Min(group.Extent, maxExtent)) : GridLength.Auto;
            if (vertical) RowDefinitions[index].Height = extent;
            else ColumnDefinitions[index].Width = extent;
            if (!group.Expanded) continue;

            var splitter = new GridSplitter
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = vertical ? GridResizeDirection.Rows : GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext
            };
            splitter.SetResourceReference(BackgroundProperty, "DockHeaderBackground");
            if (vertical)
            {
                RowDefinitions[splitterIndex].Height = new GridLength(5);
                SetRow(splitter, splitterIndex);
                SetColumnSpan(splitter, 5);
            }
            else
            {
                ColumnDefinitions[splitterIndex].Width = new GridLength(5);
                SetRow(splitter, 2);
                SetColumn(splitter, splitterIndex);
            }
            splitter.DragCompleted += (_, _) =>
            {
                group.Extent = Math.Clamp(vertical ? RowDefinitions[index].ActualHeight : ColumnDefinitions[index].ActualWidth, 100, 1000);
                QueueSave();
            };
            Children.Add(splitter);
            _splitters.Add(splitter);
        }
    }
}
