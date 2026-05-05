using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Input;
using KeyBinding = Floss.App.Input.KeyBinding;

namespace Floss.App;

public sealed class SettingsWindow : Window
{
    private const string Bg0 = "#0d0f14";
    private const string Bg1 = "#13151a";
    private const string Bg2 = "#1a1c22";
    private const string BgSidebar = "#0f1117";
    private const string Stroke = "#2b303b";
    private const string TextPrimary = "#d7dde8";
    private const string TextSecondary = "#A0AAB4";
    private const string TextMuted = "#6f7888";
    private const string Accent = "#3d6fd8";
    private const string AccentSoft = "#22355f";
    private const string AccentWarm = "#d87e3d";
    private const string AccentWarmSoft = "#3d2415";

    private static readonly string[] SidebarItems = ["General", "Keyboard & Gestures"];

    private readonly ShortcutsConfig _sc;
    private readonly AppConfig _cfg;

    private int _activePage;
    private readonly Button[] _sidebarBtns;
    private readonly Border _contentHost = new();

    // Key recorder state
    private Action<KeyBinding>? _recordingSetter;
    private TextBlock? _recordingDisplay;
    private Border? _recordingRow;
    private bool _isRecording;
    private bool _recordingGesture;
    private KeyModifiers _recordingPendingModifiers;
    private string? _recordingLabel;

    // All binding descriptors — rebuilt each time the keyboard page is shown.
    // Used for conflict detection when recording a new shortcut.
    private (string Label, Func<KeyBinding> Getter, Action<KeyBinding> Setter)[] _bindingDescriptors = [];

    public SettingsWindow()
    {
        _sc = App.Shortcuts;
        _cfg = App.Config;

        Width = 700;
        Height = 560;
        CanResize = true;
        MinWidth = 520;
        MinHeight = 400;
        Background = new SolidColorBrush(Color.Parse(Bg1));
        Title = "Settings";
        ShowInTaskbar = false;

        _sidebarBtns = new Button[SidebarItems.Length];
        for (var i = 0; i < SidebarItems.Length; i++)
            _sidebarBtns[i] = MakeSidebarBtn(i);

        Content = BuildShell();
        SelectPage(0);

        // Tunnel phase fires before any focused child (e.g. the Record button) can
        // consume the event, so combos like Ctrl+Space are captured correctly.
        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnWindowKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        App.Config.Save();
        App.Shortcuts.Save();
    }

    // ── Shell ─────────────────────────────────────────────────────────────────

    private Control BuildShell()
    {
        var sidebar = new Border
        {
            Width = 140,
            Background = new SolidColorBrush(Color.Parse(BgSidebar)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new StackPanel
            {
                Spacing = 1,
                Margin = new Thickness(0, 8, 0, 8),
                Children = { _sidebarBtns[0], _sidebarBtns[1] }
            }
        };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        Grid.SetColumn(sidebar, 0);
        Grid.SetColumn(_contentHost, 1);
        body.Children.Add(sidebar);
        body.Children.Add(_contentHost);

        return body;
    }

    private Button MakeSidebarBtn(int index)
    {
        var btn = new Button
        {
            Content = SidebarItems[index],
            Height = 34,
            Padding = new Thickness(14, 0),
            FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0)
        };
        btn.Click += (_, _) => SelectPage(index);
        return btn;
    }

    private void SelectPage(int index)
    {
        StopRecording();
        _activePage = index;
        for (var i = 0; i < _sidebarBtns.Length; i++)
        {
            var active = i == index;
            _sidebarBtns[i].Background = active
                ? new SolidColorBrush(Color.Parse(AccentSoft))
                : new SolidColorBrush(Colors.Transparent);
            _sidebarBtns[i].Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextMuted));
        }
        _contentHost.Child = index == 0 ? BuildGeneralPage() : BuildKeyboardPage();
    }

    // ── General page ──────────────────────────────────────────────────────────

    private Control BuildGeneralPage()
    {
        var content = new StackPanel { Spacing = 0, Margin = new Thickness(20, 16, 20, 20) };

        content.Children.Add(PageHeader("General"));

        content.Children.Add(GroupHeader("New Canvas"));
        content.Children.Add(RowNudger("Width (px)", _cfg.NewCanvasWidth, 1, 16384, v => { _cfg.NewCanvasWidth = (int)v; App.Config.Save(); }));
        content.Children.Add(RowNudger("Height (px)", _cfg.NewCanvasHeight, 1, 16384, v => { _cfg.NewCanvasHeight = (int)v; App.Config.Save(); }));

        content.Children.Add(GroupHeader("Zoom"));
        content.Children.Add(RowSlider("Scroll wheel factor", _sc.ZoomScrollFactor, 1.01, 1.5,
            v => { _sc.ZoomScrollFactor = v; App.Shortcuts.Save(); }, "f3"));
        content.Children.Add(RowSlider("Key zoom step", _sc.ZoomKeyFactor, 1.05, 2.0,
            v => { _sc.ZoomKeyFactor = v; App.Shortcuts.Save(); }, "f2"));

        content.Children.Add(GroupHeader("Rotation"));
        content.Children.Add(RowSlider("Key step (degrees)", _sc.RotateKeyStep, 1, 90,
            v => { _sc.RotateKeyStep = v; App.Shortcuts.Save(); }, "f1"));

        content.Children.Add(GroupHeader("Brush Nudge Keys"));
        content.Children.Add(RowSlider("Size step (small, px)", _sc.BrushSizeStep, 0.5, 20,
            v => { _sc.BrushSizeStep = v; App.Shortcuts.Save(); }, "f1"));
        content.Children.Add(RowSlider("Size step (large, px)", _sc.BrushSizeStepLarge, 1, 100,
            v => { _sc.BrushSizeStepLarge = v; App.Shortcuts.Save(); }, "f1"));
        content.Children.Add(RowSlider("Opacity step", _sc.BrushOpacityStep, 0.01, 0.2,
            v => { _sc.BrushOpacityStep = v; App.Shortcuts.Save(); }, "f2"));

        content.Children.Add(GroupHeader("Brush Cursor"));
        content.Children.Add(RowBrushCursorPicker("Cursor style",
            _cfg.BrushCursorMode, v => { _cfg.BrushCursorMode = v; App.Config.Save(); }));

        content.Children.Add(GroupHeader("Pen Gesture Sensitivity"));
        content.Children.Add(RowAxisPicker("Zoom axis",
            _sc.GestureZoomAxis, v => { _sc.GestureZoomAxis = v; App.Shortcuts.Save(); }));
        content.Children.Add(RowSlider("Zoom (factor/px)", _sc.GestureZoomSensitivity, 1.001, 1.05,
            v => { _sc.GestureZoomSensitivity = v; App.Shortcuts.Save(); }, "f3"));
        content.Children.Add(RowSlider("Rotate (°/px)", _sc.GestureRotateSensitivity, 0.05, 2.0,
            v => { _sc.GestureRotateSensitivity = v; App.Shortcuts.Save(); }, "f2"));
        content.Children.Add(RowSlider("Brush size (px/px)", _sc.GestureSizeSensitivity, 0.05, 3.0,
            v => { _sc.GestureSizeSensitivity = v; App.Shortcuts.Save(); }, "f2"));

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        };
    }

    // ── Keyboard page ─────────────────────────────────────────────────────────

    private Control BuildKeyboardPage()
    {
        var content = new StackPanel { Spacing = 0, Margin = new Thickness(20, 16, 20, 20) };

        content.Children.Add(PageHeader("Keyboard & Gestures"));
        content.Children.Add(RecordingHint());

        var sc = _sc;

        _bindingDescriptors =
        [
            ("New File",               () => sc.FileNew,                v => sc.FileNew = v),
            ("Open file",               () => sc.FileOpen,               v => sc.FileOpen = v),
            ("Save file",               () => sc.FileSave,               v => sc.FileSave = v),
            ("Save File As",            () => sc.FileSaveAs,             v => sc.FileSaveAs = v),
            ("Undo",                    () => sc.Undo,                   v => sc.Undo = v),
            ("Redo",                    () => sc.Redo,                   v => sc.Redo = v),
            ("Redo (alt)",              () => sc.RedoAlt,                v => sc.RedoAlt = v),
            ("Copy",                    () => sc.Copy,                   v => sc.Copy = v),
            ("Paste",                   () => sc.Paste,                  v => sc.Paste = v),
            ("Delete selection",        () => sc.DeleteSelection,         v => sc.DeleteSelection = v),
            ("Transform selection",     () => sc.Transform,               v => sc.Transform = v),
            ("Flip canvas horizontal",  () => sc.FlipHorizontal,         v => sc.FlipHorizontal = v),
            ("Flip canvas vertical",    () => sc.FlipVertical,           v => sc.FlipVertical = v),
            ("Mirror horizontal",      () => sc.MirrorHorizontal,      v => sc.MirrorHorizontal = v),
            ("Mirror vertical",        () => sc.MirrorVertical,        v => sc.MirrorVertical = v),
            ("Zoom in",                 () => sc.ZoomIn,                 v => sc.ZoomIn = v),
            ("Zoom in (alt)",           () => sc.ZoomInAlt,              v => sc.ZoomInAlt = v),
            ("Zoom out",                () => sc.ZoomOut,                v => sc.ZoomOut = v),
            ("Reset view",              () => sc.ZoomReset,              v => sc.ZoomReset = v),
            ("Fit to view",             () => sc.ZoomFit,                v => sc.ZoomFit = v),
            ("Rotate left",             () => sc.RotateLeft,             v => sc.RotateLeft = v),
            ("Rotate right",            () => sc.RotateRight,            v => sc.RotateRight = v),
            ("Reset rotation",          () => sc.RotateReset,            v => sc.RotateReset = v),
            ("Rotate canvas 90° CW",    () => sc.RotateCanvas90Cw,       v => sc.RotateCanvas90Cw = v),
            ("Rotate canvas 90° CCW",   () => sc.RotateCanvas90Ccw,      v => sc.RotateCanvas90Ccw = v),
            ("Rotate canvas 180°",      () => sc.RotateCanvas180,        v => sc.RotateCanvas180 = v),
            ("Select all",              () => sc.SelectAll,              v => sc.SelectAll = v),
            ("Deselect",                () => sc.Deselect,               v => sc.Deselect = v),
            ("Invert select",           () => sc.InvertSelect,           v => sc.InvertSelect = v),
            ("Size decrease (small)",   () => sc.BrushSizeDecrease,      v => sc.BrushSizeDecrease = v),
            ("Size increase (small)",   () => sc.BrushSizeIncrease,      v => sc.BrushSizeIncrease = v),
            ("Size decrease (large)",   () => sc.BrushSizeDecreaseLarge, v => sc.BrushSizeDecreaseLarge = v),
            ("Size increase (large)",   () => sc.BrushSizeIncreaseLarge, v => sc.BrushSizeIncreaseLarge = v),
            ("Opacity decrease",        () => sc.BrushOpacityDecrease,   v => sc.BrushOpacityDecrease = v),
            ("Opacity increase",        () => sc.BrushOpacityIncrease,   v => sc.BrushOpacityIncrease = v),
            ("Cycle swatch",            () => sc.ColorCycle,             v => sc.ColorCycle = v),
            ("Default black",           () => sc.ColorDefault,           v => sc.ColorDefault = v),
            ("New layer",               () => sc.LayerNew,               v => sc.LayerNew = v),
            ("Duplicate layer",         () => sc.LayerDuplicate,         v => sc.LayerDuplicate = v),
            ("Delete layer",            () => sc.LayerDelete,            v => sc.LayerDelete = v),
            ("Move up",                 () => sc.LayerMoveUp,            v => sc.LayerMoveUp = v),
            ("Move down",               () => sc.LayerMoveDown,          v => sc.LayerMoveDown = v),
            ("Merge / Flatten",         () => sc.LayerMerge,             v => sc.LayerMerge = v),
            ("Toggle layer color",       () => sc.LayerToggleColor,       v => sc.LayerToggleColor = v),
            ("Open settings",           () => sc.OpenSettings,           v => sc.OpenSettings = v),
            ("Open brush editor",       () => sc.OpenBrushEditor,        v => sc.OpenBrushEditor = v),
            ("Toggle canvas only",      () => sc.ToggleCanvasOnly,       v => sc.ToggleCanvasOnly = v),
            ("Toggle rulers",           () => sc.ToggleRulers,           v => sc.ToggleRulers = v),
            ("Alternate invocation",    () => sc.AlternateInvocation,    v => sc.AlternateInvocation = v),
            ("Pan canvas",              () => sc.GesturePan,             v => sc.GesturePan = v),
            ("Zoom  (drag ↑↓)",         () => sc.GestureZoom,            v => sc.GestureZoom = v),
            ("Rotate (drag ←→)",        () => sc.GestureRotate,          v => sc.GestureRotate = v),
            ("Brush size (←→)",         () => sc.GestureBrushSize,       v => sc.GestureBrushSize = v),
        ];

        content.Children.Add(GroupHeader("File"));
        content.Children.Add(BindingRow("New File", sc.FileNew, v => sc.FileNew = v));
        content.Children.Add(BindingRow("Open file", sc.FileOpen, v => sc.FileOpen = v));
        content.Children.Add(BindingRow("Save file", sc.FileSave, v => sc.FileSave = v));
        content.Children.Add(BindingRow("Save File As", sc.FileSaveAs, v => sc.FileSaveAs = v));

        content.Children.Add(GroupHeader("Edit"));
        content.Children.Add(BindingRow("Undo", sc.Undo, v => sc.Undo = v));
        content.Children.Add(BindingRow("Redo", sc.Redo, v => sc.Redo = v));
        content.Children.Add(BindingRow("Redo (alt)", sc.RedoAlt, v => sc.RedoAlt = v));
        content.Children.Add(BindingRow("Copy", sc.Copy, v => sc.Copy = v));
        content.Children.Add(BindingRow("Paste", sc.Paste, v => sc.Paste = v));
        content.Children.Add(BindingRow("Delete selection", sc.DeleteSelection, v => sc.DeleteSelection = v));
        content.Children.Add(BindingRow("Transform selection", sc.Transform, v => sc.Transform = v));

        content.Children.Add(GroupHeader("View — Zoom"));
        content.Children.Add(BindingRow("Zoom in", sc.ZoomIn, v => sc.ZoomIn = v));
        content.Children.Add(BindingRow("Zoom in (alt)", sc.ZoomInAlt, v => sc.ZoomInAlt = v));
        content.Children.Add(BindingRow("Zoom out", sc.ZoomOut, v => sc.ZoomOut = v));
        content.Children.Add(BindingRow("Reset view", sc.ZoomReset, v => sc.ZoomReset = v));
        content.Children.Add(BindingRow("Fit to view", sc.ZoomFit, v => sc.ZoomFit = v));

        content.Children.Add(GroupHeader("View — Rotation"));
        content.Children.Add(BindingRow("Rotate left", sc.RotateLeft, v => sc.RotateLeft = v));
        content.Children.Add(BindingRow("Rotate right", sc.RotateRight, v => sc.RotateRight = v));
        content.Children.Add(BindingRow("Reset rotation", sc.RotateReset, v => sc.RotateReset = v));

        content.Children.Add(GroupHeader("Image"));
        content.Children.Add(BindingRow("Flip canvas horizontal", sc.FlipHorizontal, v => sc.FlipHorizontal = v));
        content.Children.Add(BindingRow("Flip canvas vertical", sc.FlipVertical, v => sc.FlipVertical = v));

        content.Children.Add(GroupHeader("View — Mirror"));
        content.Children.Add(BindingRow("Mirror horizontal", sc.MirrorHorizontal, v => sc.MirrorHorizontal = v));
        content.Children.Add(BindingRow("Mirror vertical", sc.MirrorVertical, v => sc.MirrorVertical = v));

        content.Children.Add(GroupHeader("Selection"));
        content.Children.Add(BindingRow("Select all", sc.SelectAll, v => sc.SelectAll = v));
        content.Children.Add(BindingRow("Deselect", sc.Deselect, v => sc.Deselect = v));
        content.Children.Add(BindingRow("Invert select", sc.InvertSelect, v => sc.InvertSelect = v));

        content.Children.Add(GroupHeader("Brush — Size"));
        content.Children.Add(BindingRow("Size decrease (small)", sc.BrushSizeDecrease, v => sc.BrushSizeDecrease = v));
        content.Children.Add(BindingRow("Size increase (small)", sc.BrushSizeIncrease, v => sc.BrushSizeIncrease = v));
        content.Children.Add(BindingRow("Size decrease (large)", sc.BrushSizeDecreaseLarge, v => sc.BrushSizeDecreaseLarge = v));
        content.Children.Add(BindingRow("Size increase (large)", sc.BrushSizeIncreaseLarge, v => sc.BrushSizeIncreaseLarge = v));

        content.Children.Add(GroupHeader("Brush — Opacity"));
        content.Children.Add(BindingRow("Opacity decrease", sc.BrushOpacityDecrease, v => sc.BrushOpacityDecrease = v));
        content.Children.Add(BindingRow("Opacity increase", sc.BrushOpacityIncrease, v => sc.BrushOpacityIncrease = v));

        content.Children.Add(GroupHeader("Color"));
        content.Children.Add(BindingRow("Cycle swatch", sc.ColorCycle, v => sc.ColorCycle = v));
        content.Children.Add(BindingRow("Default black", sc.ColorDefault, v => sc.ColorDefault = v));

        content.Children.Add(GroupHeader("Layers"));
        content.Children.Add(BindingRow("New layer", sc.LayerNew, v => sc.LayerNew = v));
        content.Children.Add(BindingRow("Duplicate layer", sc.LayerDuplicate, v => sc.LayerDuplicate = v));
        content.Children.Add(BindingRow("Delete layer", sc.LayerDelete, v => sc.LayerDelete = v));
        content.Children.Add(BindingRow("Move up", sc.LayerMoveUp, v => sc.LayerMoveUp = v));
        content.Children.Add(BindingRow("Move down", sc.LayerMoveDown, v => sc.LayerMoveDown = v));
        content.Children.Add(BindingRow("Merge / Flatten", sc.LayerMerge, v => sc.LayerMerge = v));
        content.Children.Add(BindingRow("Toggle layer color", sc.LayerToggleColor, v => sc.LayerToggleColor = v));

        content.Children.Add(GroupHeader("Misc"));
        content.Children.Add(BindingRow("Open settings", sc.OpenSettings, v => sc.OpenSettings = v));
        content.Children.Add(BindingRow("Open brush editor", sc.OpenBrushEditor, v => sc.OpenBrushEditor = v));
        content.Children.Add(BindingRow("Toggle canvas only", sc.ToggleCanvasOnly, v => sc.ToggleCanvasOnly = v));
        content.Children.Add(BindingRow("Toggle rulers", sc.ToggleRulers, v => sc.ToggleRulers = v));
        content.Children.Add(BindingRow("Alternate invocation", sc.AlternateInvocation, v => sc.AlternateInvocation = v));

        content.Children.Add(GroupHeader("Pen Gestures  (hold key + drag pen)"));
        content.Children.Add(BindingRow("Pan canvas", sc.GesturePan, v => sc.GesturePan = v, gesture: true));
        content.Children.Add(BindingRow("Zoom  (drag ↑↓)", sc.GestureZoom, v => sc.GestureZoom = v, gesture: true));
        content.Children.Add(BindingRow("Rotate (drag ←→)", sc.GestureRotate, v => sc.GestureRotate = v, gesture: true));
        content.Children.Add(BindingRow("Brush size (←→)", sc.GestureBrushSize, v => sc.GestureBrushSize = v, gesture: true));

        // ── Tool-group alternate invocation ─────────────────────────────────
        var toolGroups = App.ToolGroups.Groups;
        if (toolGroups.Count > 0)
        {
            content.Children.Add(GroupHeader("Tool Groups  (alt invocation per preset)"));
            foreach (var group in toolGroups)
            {
                var groupLabel = string.IsNullOrEmpty(group.Shortcut.IsEmpty ? "" : group.Shortcut.Display())
                    ? group.Name
                    : $"{group.Name}  [{group.Shortcut.Display()}]";
                content.Children.Add(SubGroupHeader(groupLabel));
                foreach (var preset in group.Presets)
                {
                    var label = $"    {preset.Name}";
                    content.Children.Add(PresetBindingRow(
                        label,
                        preset.AlternateInvocation,
                        v =>
                        {
                            preset.AlternateInvocation = v;
                            App.ToolGroups.Save();
                        }));
                }
            }
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        };
    }

    // ── Row builders ──────────────────────────────────────────────────────────

    private Border BindingRow(string label, KeyBinding current, Action<KeyBinding> setter, bool gesture = false)
    {
        var keyDisplay = new TextBlock
        {
            Text = current.Display(),
            FontSize = 11,
            Width = 130,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Foreground = new SolidColorBrush(Color.Parse(current.IsEmpty ? TextMuted : TextPrimary)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var recordBtn = new Button
        {
            Content = "Record",
            Height = 22,
            Padding = new Thickness(8, 0),
            FontSize = 10,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };

        var clearBtn = new Button
        {
            Content = MaterialIcon(Icons.Close, 13),
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(gesture ? AccentWarm : TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 200
        };

        var row = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(labelText, Dock.Left);
        DockPanel.SetDock(keyDisplay, Dock.Left);
        DockPanel.SetDock(clearBtn, Dock.Right);
        DockPanel.SetDock(recordBtn, Dock.Right);
        row.Children.Add(labelText);
        row.Children.Add(keyDisplay);
        row.Children.Add(clearBtn);
        row.Children.Add(new Border { Width = 4 });
        row.Children.Add(recordBtn);

        var rowBorder = new Border
        {
            Padding = new Thickness(8, 5),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            Child = row
        };

        clearBtn.Click += (_, _) =>
        {
            StopRecording();
            setter(KeyBinding.Empty);
            keyDisplay.Text = "--";
            keyDisplay.Foreground = new SolidColorBrush(Color.Parse(TextMuted));
        };

        recordBtn.Click += (_, _) =>
        {
            if (_isRecording && _recordingSetter != null && ReferenceEquals(_recordingRow, rowBorder))
            {
                StopRecording();
                return;
            }
            StopRecording();
            StartRecording(rowBorder, keyDisplay, gesture, v =>
            {
                setter(v);
                keyDisplay.Text = v.Display();
                keyDisplay.Foreground = new SolidColorBrush(Color.Parse(v.IsEmpty ? TextMuted : TextPrimary));
            }, label);
        };

        return rowBorder;
    }

    private Border PresetBindingRow(string label, KeyBinding altInvocation, Action<KeyBinding> altSetter)
    {
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 200,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var altDisplay = new TextBlock
        {
            Text = altInvocation.IsEmpty ? "—" : altInvocation.Display(),
            FontSize = 11,
            Width = 130,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Foreground = new SolidColorBrush(Color.Parse(altInvocation.IsEmpty ? TextMuted : TextPrimary)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var recordBtn = new Button
        {
            Content = "Record",
            Height = 22,
            Padding = new Thickness(8, 0),
            FontSize = 10,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };

        var clearBtn = new Button
        {
            Content = MaterialIcon(Icons.Close, 13),
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };

        var row = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(labelText, Dock.Left);
        DockPanel.SetDock(altDisplay, Dock.Left);
        DockPanel.SetDock(clearBtn, Dock.Right);
        DockPanel.SetDock(recordBtn, Dock.Right);
        row.Children.Add(labelText);
        row.Children.Add(altDisplay);
        row.Children.Add(clearBtn);
        row.Children.Add(new Border { Width = 4 });
        row.Children.Add(recordBtn);

        var rowBorder = new Border
        {
            Padding = new Thickness(8, 5),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            Child = row
        };

        clearBtn.Click += (_, _) =>
        {
            StopRecording();
            altSetter(KeyBinding.Empty);
            altDisplay.Text = "—";
            altDisplay.Foreground = new SolidColorBrush(Color.Parse(TextMuted));
        };

        recordBtn.Click += (_, _) =>
        {
            if (_isRecording && _recordingSetter != null && ReferenceEquals(_recordingRow, rowBorder))
            {
                StopRecording();
                return;
            }
            StopRecording();
            StartRecording(rowBorder, altDisplay, false, v =>
            {
                altSetter(v);
                altDisplay.Text = v.IsEmpty ? "—" : v.Display();
                altDisplay.Foreground = new SolidColorBrush(Color.Parse(v.IsEmpty ? TextMuted : TextPrimary));
            }, label);
        };

        return rowBorder;
    }

    private static PathIcon MaterialIcon(string pathData, double size) =>
        Icons.Make(pathData, size, new SolidColorBrush(Color.Parse(TextMuted)));

    private static Control RowNudger(string label, int currentValue, int min, int max, Action<double> setter)
    {
        var nudger = new NumericUpDown
        {
            Value = currentValue,
            Minimum = min,
            Maximum = max,
            Increment = 1,
            Width = 100,
            Height = 26,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            FormatString = "0"
        };
        nudger.ValueChanged += (_, _) =>
        {
            if (nudger.Value.HasValue) setter((double)nudger.Value.Value);
        };
        return SettingRow(label, nudger);
    }

    private static Control RowSlider(string label, double currentValue, double min, double max,
        Action<double> setter, string fmt)
    {
        var valLabel = new TextBlock
        {
            Text = Format(currentValue, fmt),
            Width = 44,
            FontSize = 10,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Foreground = new SolidColorBrush(Color.Parse("#6f7888")),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Right
        };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = currentValue,
            Width = 180,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            setter(slider.Value);
            valLabel.Text = Format(slider.Value, fmt);
        };
        var inner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { slider, valLabel }
        };
        return SettingRow(label, inner);
    }

    private static Control SettingRow(string label, Control control)
    {
        var lbl = new TextBlock
        {
            Text = label,
            FontSize = 11,
            MinWidth = 200,
            Foreground = new SolidColorBrush(Color.Parse("#A0AAB4")),
            VerticalAlignment = VerticalAlignment.Center
        };
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(8, 5) };
        DockPanel.SetDock(lbl, Dock.Left);
        DockPanel.SetDock(control, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(control);
        return row;
    }

    private Control RowAxisPicker(string label, GestureAxis current, Action<GestureAxis> setter)
    {
        Button MkBtn(GestureAxis axis, string text)
        {
            var active = current == axis;
            var btn = new Button
            {
                Content = text,
                Height = 24,
                Padding = new Thickness(10, 0),
                FontSize = 10,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : Bg2)),
                BorderBrush = new SolidColorBrush(Color.Parse(active ? Accent : Stroke)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextMuted)),
                CornerRadius = new CornerRadius(3)
            };
            btn.Click += (_, _) =>
            {
                setter(axis);
                // Rebuild general page so the buttons reflect the new state
                SelectPage(0);
            };
            return btn;
        }

        var picker = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { MkBtn(GestureAxis.Vertical, "Vertical  ↑↓"), MkBtn(GestureAxis.Horizontal, "Horizontal  ←→") }
        };
        return SettingRow(label, picker);
    }

    private Control RowBrushCursorPicker(string label, BrushCursorMode current, Action<BrushCursorMode> setter)
    {
        Button MkBtn(BrushCursorMode mode, string text)
        {
            var active = current == mode;
            var btn = new Button
            {
                Content = text,
                Height = 24,
                Padding = new Thickness(10, 0),
                FontSize = 10,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse(active ? AccentSoft : Bg2)),
                BorderBrush = new SolidColorBrush(Color.Parse(active ? Accent : Stroke)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Color.Parse(active ? TextPrimary : TextMuted)),
                CornerRadius = new CornerRadius(3)
            };
            btn.Click += (_, _) =>
            {
                setter(mode);
                SelectPage(0);
            };
            return btn;
        }

        var picker = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                MkBtn(BrushCursorMode.Outline, "Size"),
                MkBtn(BrushCursorMode.Dot, "Dot"),
                MkBtn(BrushCursorMode.DotAndOutline, "Dot + size")
            }
        };
        return SettingRow(label, picker);
    }

    private static string Format(double v, string fmt) => fmt switch
    {
        "f1" => $"{v:0.0}",
        "f2" => $"{v:0.00}",
        "f3" => $"{v:0.000}",
        _ => $"{v:0.##}"
    };

    // ── Recording ─────────────────────────────────────────────────────────────

    private Task<bool> ConfirmConflictAsync(string conflictLabel)
    {
        var tcs = new TaskCompletionSource<bool>();

        var msg = new TextBlock
        {
            Text = $"\"{conflictLabel}\" already uses this shortcut.\nReassign it here and clear that binding?",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var yesBtn = new Button
        {
            Content = "Reassign",
            Height = 28,
            Padding = new Thickness(14, 0),
            FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(AccentSoft)),
            Foreground = new SolidColorBrush(Color.Parse(TextPrimary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Accent)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        var noBtn = new Button
        {
            Content = "Cancel",
            Height = 28,
            Padding = new Thickness(14, 0),
            FontSize = 11,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse(Bg2)),
            Foreground = new SolidColorBrush(Color.Parse(TextSecondary)),
            BorderBrush = new SolidColorBrush(Color.Parse(Stroke)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { noBtn, yesBtn }
        };

        var dlg = new Window
        {
            Title = "Shortcut conflict",
            Width = 320,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.Parse(Bg1)),
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children = { msg, buttons }
            }
        };

        yesBtn.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
        noBtn.Click += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(false);

        dlg.ShowDialog(this);
        return tcs.Task;
    }

    private void StartRecording(Border rowBorder, TextBlock display, bool gesture, Action<KeyBinding> setter, string label)
    {
        _isRecording = true;
        _recordingGesture = gesture;
        _recordingPendingModifiers = KeyModifiers.None;
        _recordingSetter = setter;
        _recordingDisplay = display;
        _recordingRow = rowBorder;
        _recordingLabel = label;

        display.Text = "Press keys...";
        display.Foreground = new SolidColorBrush(Color.Parse(Accent));
        rowBorder.Background = new SolidColorBrush(Color.Parse(AccentSoft));
        Cursor = new Cursor(StandardCursorType.Ibeam);
    }

    private void StopRecording()
    {
        if (!_isRecording) return;
        _isRecording = false;

        if (_recordingRow != null)
            _recordingRow.Background = null;

        _recordingSetter = null;
        _recordingDisplay = null;
        _recordingRow = null;
        _recordingLabel = null;
        _recordingGesture = false;
        _recordingPendingModifiers = KeyModifiers.None;
        Cursor = Cursor.Default;
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isRecording) return;
        var mods = KeyBinding.ModifiersWithKeyDown(e.Key, e.KeyModifiers);

        // Modifier keys are tracked as a pending chord and committed on key-up if
        // no regular key follows. This lets users record modifier-only bindings
        // like Alt or Ctrl+Shift for alternate-invocation shortcuts.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or
                     Key.LeftShift or Key.RightShift or
                     Key.LeftAlt or Key.RightAlt or
                     Key.LWin or Key.RWin)
        {
            if (mods != KeyModifiers.None)
            {
                _recordingPendingModifiers = mods;
                if (_recordingDisplay != null)
                    _recordingDisplay.Text = new KeyBinding(Key.None, _recordingPendingModifiers).Display();
                e.Handled = true;
            }
            return;
        }

        var setter = _recordingSetter;
        var currentLabel = _recordingLabel;

        // ESC cancels without changing the binding
        if (e.Key == Key.Escape)
        {
            StopRecording();
            SelectPage(_activePage);
            e.Handled = true;
            return;
        }

        KeyBinding newBinding = e.Key is Key.Back or Key.Delete
            ? KeyBinding.Empty
            : new KeyBinding(e.Key, mods);

        e.Handled = true;

        if (!newBinding.IsEmpty)
        {
            var conflict = _bindingDescriptors.FirstOrDefault(d =>
            {
                if (d.Label == currentLabel) return false;
                var b = d.Getter();
                return !b.IsEmpty && b.Key == newBinding.Key && b.Modifiers == newBinding.Modifiers;
            });

            if (conflict.Label != null)
            {
                StopRecording();
                var confirmed = await ConfirmConflictAsync(conflict.Label);
                if (confirmed)
                {
                    conflict.Setter(KeyBinding.Empty);
                    setter?.Invoke(newBinding);
                }
                SelectPage(_activePage);
                return;
            }
        }

        setter?.Invoke(newBinding);
        StopRecording();
        SelectPage(_activePage);
    }

    private async void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (!_isRecording || _recordingPendingModifiers == KeyModifiers.None)
            return;

        // Gesture bindings require a 2+ modifier chord (e.g. Ctrl+Alt).
        // Normal bindings accept single-modifier chords (e.g. Alt for
        // alternate invocation).
        if (_recordingGesture && CountModifiers(_recordingPendingModifiers) < 2)
        {
            if (_recordingDisplay != null)
                _recordingDisplay.Text = "Press chord...";
            e.Handled = true;
            return;
        }

        var newBinding = new KeyBinding(Key.None, _recordingPendingModifiers);
        var setter = _recordingSetter;
        var currentLabel = _recordingLabel;

        e.Handled = true;

        var conflict = _bindingDescriptors.FirstOrDefault(d =>
        {
            if (d.Label == currentLabel) return false;
            var b = d.Getter();
            return !b.IsEmpty && b.Key == newBinding.Key && b.Modifiers == newBinding.Modifiers;
        });

        if (conflict.Label != null)
        {
            StopRecording();
            var confirmed = await ConfirmConflictAsync(conflict.Label);
            if (confirmed)
            {
                conflict.Setter(KeyBinding.Empty);
                setter?.Invoke(newBinding);
            }
            SelectPage(_activePage);
            return;
        }

        setter?.Invoke(newBinding);
        StopRecording();
        SelectPage(_activePage);
    }

    private static int CountModifiers(KeyModifiers modifiers)
    {
        var count = 0;
        if (modifiers.HasFlag(KeyModifiers.Control)) count++;
        if (modifiers.HasFlag(KeyModifiers.Alt)) count++;
        if (modifiers.HasFlag(KeyModifiers.Shift)) count++;
        if (modifiers.HasFlag(KeyModifiers.Meta)) count++;
        return count;
    }

    // ── Section headers ───────────────────────────────────────────────────────

    private static TextBlock PageHeader(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(Color.Parse("#d7dde8")),
        Margin = new Thickness(0, 0, 0, 16)
    };

    private static Control GroupHeader(string text) => new Border
    {
        Margin = new Thickness(0, 12, 0, 0),
        Padding = new Thickness(8, 5),
        Background = new SolidColorBrush(Color.Parse("#0d0f14")),
        BorderBrush = new SolidColorBrush(Color.Parse("#2b303b")),
        BorderThickness = new Thickness(0, 1, 0, 1),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#6f7888")),
            LetterSpacing = 1.0
        }
    };

    private static Control SubGroupHeader(string text) => new Border
    {
        Margin = new Thickness(0, 6, 0, 0),
        Padding = new Thickness(12, 3),
        Background = new SolidColorBrush(Color.Parse("#12141a")),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.Normal,
            Foreground = new SolidColorBrush(Color.Parse("#8891a0")),
        }
    };

    private static Control RecordingHint() => new TextBlock
    {
        Text = "Click Record, then press the desired key combination. Backspace clears. Esc cancels.",
        FontSize = 10,
        Foreground = new SolidColorBrush(Color.Parse(TextMuted)),
        Margin = new Thickness(0, 0, 0, 4),
        TextWrapping = Avalonia.Media.TextWrapping.Wrap
    };
}
