using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace oshituner;

internal enum DockLocation { Center, Top, Bottom, Left, Right }

internal sealed record DockGroupState(DockLocation? Dock, string[] Panes, string? Selected, bool Pinned,
    double Extent = 300, WindowBounds? Bounds = null);

internal sealed record WorkspaceState(DockGroupState[] Groups, Dictionary<string, DockLocation> LastDocks,
    Dictionary<string, WindowBounds> WindowPositions, Dictionary<DockLocation, bool>? DockPins = null);

internal sealed partial class DockWorkspace : Grid
{
    private readonly Window _shell;
    private readonly TunerSession _session;
    private readonly Action<string?> _reportError;
    private readonly List<Group> _groups = [];
    private readonly Dictionary<string, Pane> _panes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DockLocation> _lastDocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WindowBounds> _windowPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<DockLocation, bool> _dockPins = new();
    private readonly DispatcherTimer _saveDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _stopping;
    private bool _restoring;
    private bool _trayHidden;

    public DockWorkspace(Window shell, TunerSession session, Action<string?> reportError)
    {
        _shell = shell;
        _session = session;
        _reportError = reportError;
        InitializeLayout();
        _saveDelay.Tick += (_, _) => { _saveDelay.Stop(); Save(); };
        _shell.LocationChanged += (_, _) => QueueSave();
        _shell.SizeChanged += (_, _) => QueueSave();
        _shell.Deactivated += (_, _) => CheckDockFocus();
        _shell.Activated += (_, _) => CheckDockFocus();
    }

    public void Register(string id, string title, FrameworkElement view, Action focus) =>
        _panes.Add(id, new Pane(id, title, view, focus));

    public void Restore()
    {
        _restoring = true;
        try
        {
            var state = _session.Workspace;
            if (state is null)
            {
                if (_panes.ContainsKey("twitch-status")) Open("twitch-status");
            }
            else
            {
                foreach (var (id, dock) in state.LastDocks ?? [])
                    if (_panes.ContainsKey(id) && Enum.IsDefined(dock)) _lastDocks[id] = dock;
                foreach (var (dock, pinned) in state.DockPins ?? [])
                    if (Enum.IsDefined(dock)) _dockPins[dock] = pinned;
                foreach (var (id, bounds) in state.WindowPositions ?? [])
                    _windowPositions[id] = bounds;
                WindowPlacement.Apply(_shell, _windowPositions.GetValueOrDefault("main"), _shell);
                foreach (var saved in state.Groups ?? [])
                {
                    if (saved is null || saved.Dock is { } dock && !Enum.IsDefined(dock)) continue;
                    var panes = (saved.Panes ?? []).Where(id => id is not null && _panes.TryGetValue(id, out var p) && p.Group is null)
                        .Distinct(StringComparer.Ordinal).Select(id => _panes[id]).ToArray();
                    if (panes.Length == 0) continue;
                    var group = saved.Dock is { } edge ? GetDock(edge) ?? NewGroup(edge) : NewGroup(null);
                    group.Pinned = saved.Pinned && group.Dock is not null;
                    if (group.Dock is { } groupDock) _dockPins[groupDock] = group.Pinned;
                    group.Expanded = group.Pinned || group.Dock is null;
                    if (double.IsFinite(saved.Extent)) group.Extent = Math.Clamp(saved.Extent, 100, 1000);
                    group.Bounds = saved.Bounds;
                    foreach (var pane in panes) Attach(group, pane);
                    Select(group, group.Panes.FirstOrDefault(p => p.Id == saved.Selected) ?? group.Panes[0], false);
                }
            }

            UpdateLayoutSlots();
            ShowPendingFloatingWindows();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.UserPreferenceChanged += OnDisplayPreferenceChanged;
        }
        finally { _restoring = false; }
    }

    public void SetTrayHidden(bool hidden)
    {
        _trayHidden = hidden;
        if (!hidden) ShowPendingFloatingWindows();
    }

    private void ShowPendingFloatingWindows()
    {
        foreach (var group in _groups.Where(group => group.Dock is null && group.Window is null))
            ShowFloating(group, activate: false);
    }

    public void Stop()
    {
        _stopping = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.UserPreferenceChanged -= OnDisplayPreferenceChanged;
        _saveDelay.Stop();
        Save();
    }

    public IEnumerable<Window> FloatingWindows => _groups.Select(group => group.Window).OfType<Window>();

    public void TrackDialog(Window dialog, string id)
    {
        dialog.SourceInitialized += (_, _) => WindowPlacement.Apply(dialog, _windowPositions.GetValueOrDefault(id), _shell);
        dialog.Closing += (_, _) =>
        {
            if (WindowPlacement.Read(dialog) is { } bounds) _windowPositions[id] = bounds;
            QueueSave();
        };
    }

    public WorkspaceState Snapshot()
    {
        if (WindowPlacement.Read(_shell) is { } bounds) _windowPositions["main"] = bounds;
        return new(_groups.Select(group =>
        {
            if (group.Window is { } window && WindowPlacement.Read(window) is { } current) group.Bounds = current;
            return new DockGroupState(group.Dock, group.Panes.Select(pane => pane.Id).ToArray(),
                group.Selected?.Id, group.Pinned, group.Extent, group.Bounds);
        }).ToArray(), new(_lastDocks), new(_windowPositions), new(_dockPins));
    }

    public void Open(string id)
    {
        var pane = GetPane(id);
        var group = pane.Group ?? AddToDock(pane, _lastDocks.GetValueOrDefault(id, DockLocation.Center));
        if (group.Window is { } window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
        Select(group, pane, true);
    }

    public void Close(string id)
    {
        var pane = GetPane(id);
        if (pane.Group is not { } group) return;
        Remove(group, pane);
        if (group.Selected is { } selected) Select(group, selected, true);
        else if (!_stopping) _shell.Focus();
        QueueSave();
    }

    public void Move(string id, DockLocation? location)
    {
        if (location is { } edge && !Enum.IsDefined(edge)) throw new ArgumentException("Choose a valid dock location.");
        var pane = GetPane(id);
        if (pane.Group is { } group && group.Dock == location && (location is not null || group.Panes.Count == 1))
        {
            Select(group, pane, true);
            return;
        }
        if (pane.Group is { } origin) Remove(origin, pane);
        var target = location is { } dock ? AddToDock(pane, dock) : Float(pane, null);
        Select(target, pane, true);
    }

    public void JoinFloatingGroup(string id, string peerId)
    {
        var pane = GetPane(id);
        if (GetPane(peerId).Group is not { Dock: null } target)
            throw new InvalidOperationException("The destination pane must be in a floating group.");
        if (pane.Group != target)
        {
            if (pane.Group is { } origin) Remove(origin, pane);
            Attach(target, pane);
        }
        target.Window?.Activate();
        Select(target, pane, true);
    }

    public void RedockFloatingGroup(string id, DockLocation location)
    {
        if (!Enum.IsDefined(location)) throw new ArgumentException("Choose a valid dock location.");
        var pane = GetPane(id);
        if (pane.Group is not { Dock: null } floating)
            throw new InvalidOperationException("Choose a pane in a floating group.");

        foreach (var item in floating.Panes.ToArray())
        {
            Remove(floating, item);
            AddToDock(item, location);
        }
        if (pane.Group is { } target) Select(target, pane, true);
    }

    public void SetPinned(string id, bool pinned)
    {
        if (GetPane(id).Group is not { Dock: { } dock } group)
            throw new InvalidOperationException("Only a docked pane can be pinned.");
        group.Pinned = pinned;
        _dockPins[dock] = pinned;
        group.Expanded = true;
        if (group.Selected is { } selected) Select(group, selected, true);
    }

    private Pane GetPane(string id) => _panes.TryGetValue(id, out var pane) ? pane
        : throw new ArgumentException("Unknown status pane.");

    private Group? GetDock(DockLocation dock) => _groups.FirstOrDefault(group => group.Dock == dock);

    private Group AddToDock(Pane pane, DockLocation dock)
    {
        var group = GetDock(dock) ?? NewGroup(dock);
        Attach(group, pane);
        _lastDocks[pane.Id] = dock;
        return group;
    }

    private Group Float(Pane pane, WindowBounds? bounds)
    {
        var group = NewGroup(null);
        group.Bounds = bounds;
        Attach(group, pane);
        Select(group, pane, false);
        ShowFloating(group, activate: true);
        return group;
    }

    private Group NewGroup(DockLocation? dock)
    {
        var group = new Group(dock);
        if (dock is { } location) group.Pinned = _dockPins.GetValueOrDefault(location, true);
        InitializeGroup(group);
        _groups.Add(group);
        if (dock is not null) Children.Add(group.Chrome);
        return group;
    }

    private void Attach(Group group, Pane pane)
    {
        group.Panes.Add(pane);
        pane.Group = group;
        if (group.Dock is { } dock) _lastDocks[pane.Id] = dock;
    }

    private void Remove(Group group, Pane pane)
    {
        group.Body.Content = null;
        var index = group.Panes.IndexOf(pane);
        if (index < 0) return;
        group.Panes.RemoveAt(index);
        pane.Group = null;
        if (group.Panes.Count == 0)
        {
            group.Selected = null;
            _groups.Remove(group);
            Children.Remove(group.Chrome);
            if (group.Window is { } window)
            {
                group.Window = null;
                window.Content = null;
                window.Close();
            }
            UpdateLayoutSlots();
            return;
        }
        var remaining = group.Selected == pane ? group.Panes[Math.Min(index, group.Panes.Count - 1)] : group.Selected!;
        Select(group, remaining, false);
    }

    private void Select(Group group, Pane pane, bool focus)
    {
        group.Selected = pane;
        if (focus) group.Expanded = true;
        RenderGroup(group);
        UpdateLayoutSlots();
        if (focus && !_stopping) Dispatcher.BeginInvoke(pane.Focus, DispatcherPriority.Input);
        QueueSave();
    }

    private void CheckDockFocus()
    {
        foreach (var group in _groups) CheckFocus(group);
    }

    private void CheckFocus(Group group)
    {
        if (group.Pinned || group.Dock is null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_dragging || _stopping || group.Pinned || !_groups.Contains(group)) return;
            var focused = _shell.IsActive && (group.Chrome.IsKeyboardFocusWithin || Keyboard.FocusedElement is MenuItem)
                || _shell.OwnedWindows.Cast<Window>().Any(window => window.IsActive && window.Content is not DockPanel);
            if (group.Expanded == focused) return;
            group.Expanded = focused;
            RenderGroup(group);
            UpdateLayoutSlots();
        }, DispatcherPriority.Background);
    }

    private Window ActiveWindow() =>
        _groups.Select(group => group.Window).FirstOrDefault(window => window?.IsActive == true) ?? _shell;

    private void OnDisplayPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (args.Category == UserPreferenceCategory.Desktop) OnDisplayChanged(sender, args);
    }

    private void OnDisplayChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(() =>
    {
        if (_stopping) return;
        var active = ActiveWindow();
        foreach (var group in _groups)
            if (group.Window is { WindowState: WindowState.Normal } window)
                WindowPlacement.Apply(window, group.Bounds ?? WindowPlacement.Read(window), active);
    });

    private void QueueSave()
    {
        if (_restoring || _stopping) return;
        _saveDelay.Stop();
        _saveDelay.Start();
    }

    private void Save()
    {
        try
        {
            _session.SetWorkspace(Snapshot());
            _reportError(null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { _reportError(exception.Message); }
    }

    private sealed class Pane(string id, string title, FrameworkElement view, Action focus)
    {
        public string Id { get; } = id;
        public string Title { get; } = title;
        public FrameworkElement View { get; } = view;
        public Action Focus { get; } = focus;
        public Group? Group { get; set; }
    }

    private sealed class Group(DockLocation? dock)
    {
        public DockLocation? Dock { get; } = dock;
        public Window? Window { get; set; }
        public WindowBounds? Bounds { get; set; }
        public DockPanel Chrome { get; } = new();
        public DockPanel Header { get; } = new();
        public ContentControl Body { get; } = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        public List<Pane> Panes { get; } = [];
        public Pane? Selected { get; set; }
        public bool Pinned { get; set; } = dock is not null;
        public bool Expanded { get; set; } = true;
        public double Extent { get; set; } = 300;
    }
}
