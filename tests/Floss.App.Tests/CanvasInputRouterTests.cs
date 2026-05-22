using Avalonia;

namespace Floss.App.Tests;

public class CanvasInputRouterTests
{
    // ── Mock host ─────────────────────────────────────────────────────────────

    private sealed class MockHost : ICanvasInputHost
    {
        public bool IsAlternateActive { get; set; }
        public bool PaintInputSuspended { get; set; }
        public ITool? ActiveTool { get; set; }
        public bool IsLayerPickDrag { get; set; }
        public bool IsResizeDragging { get; set; }
        public double Zoom { get; set; } = 1.0;
        public IViewportController? ViewportController { get; set; }
        public (int Input, int Output) ToolTypes { get; set; }
        public bool TemporaryPresetActive { get; set; }
        public bool HasViewportNavOverlay { get; set; }

        public List<string> Operations { get; } = [];

        // ICanvasInputHost coordinate helpers (return dummy values)
        PointerPoint ICanvasInputHost.GetViewportPointerPoint(PointerEventArgs e) => default;
        PointerPoint ICanvasInputHost.GetCanvasPointerPoint(PointerEventArgs e) => default;
        Point ICanvasInputHost.GetViewportPosition(PointerEventArgs e) => new();
        Point ICanvasInputHost.GetCanvasPosition(PointerEventArgs e) => new();

        void ICanvasInputHost.DispatchViewportPointerInput(ToolInputEventKind kind, Point viewportPos, PointerPoint point)
            => Operations.Add($"ViewportDispatch:{kind}");

        void ICanvasInputHost.DispatchPointerInput(ToolInputEventKind kind, PointerPoint canvasPoint)
            => Operations.Add($"Dispatch:{kind}");

        bool ICanvasInputHost.PushTemporaryPreset(string presetId)
        {
            TemporaryPresetActive = true;
            Operations.Add($"PushPreset:{presetId}");
            return true;
        }

        void ICanvasInputHost.PopTemporaryPreset()
        {
            TemporaryPresetActive = false;
            Operations.Add("PopPreset");
        }

        bool ICanvasInputHost.HasViewportNavOverlay => HasViewportNavOverlay;

        void ICanvasInputHost.SetAlternateActive(bool active)
            => Operations.Add($"SetAlternate:{active}");

        void ICanvasInputHost.SetCanvasModifiers(KeyModifiers mods) { }
        void ICanvasInputHost.SetToolAuxMode(ToolAuxOperationType mode)
            => Operations.Add($"SetAuxMode:{mode}");

        void ICanvasInputHost.CancelActiveTool()
            => Operations.Add("CancelTool");

        void ICanvasInputHost.CommitActiveTool()
            => Operations.Add("CommitTool");

        void ICanvasInputHost.StartLayerPickDrag(Point pos)
            => Operations.Add("StartLayerPick");

        void ICanvasInputHost.UpdateLayerPickDrag(Point pos)
            => Operations.Add("UpdateLayerPick");

        void ICanvasInputHost.EndLayerPickDrag(Point pos)
            => Operations.Add("EndLayerPick");

        void ICanvasInputHost.LockCursorPreview(Point center, bool forceBrushOutline)
            => Operations.Add("LockCursor");

        void ICanvasInputHost.UnlockCursorPreview()
            => Operations.Add("UnlockCursor");

        double ICanvasInputHost.GetActiveToolSize() => 10;
        double ICanvasInputHost.GetActiveToolSizeMin() => 1;
        double ICanvasInputHost.GetActiveToolSizeMax() => 500;

        void ICanvasInputHost.SetActiveToolSize(double size)
            => Operations.Add($"SetToolSize:{size}");

        void ICanvasInputHost.FinishActiveToolSizeEdit()
            => Operations.Add("FinishToolSizeEdit");

        void ICanvasInputHost.InvalidateViewport() { }

        bool ICanvasInputHost.TryBeginResizeDrag(Point pos, bool isPrimary) => false;
        void ICanvasInputHost.EndResizeDrag() { }
        void ICanvasInputHost.UpdateResizeDrag(Point pt) { }

        void ICanvasInputHost.CapturePointer(IPointer pointer)
            => Operations.Add("CapturePointer");

        void ICanvasInputHost.ReleasePointerCapture()
            => Operations.Add("ReleaseCapture");

        bool ICanvasInputHost.IsOverCanvasUi(Point viewportPos) => false;

        void ICanvasInputHost.SetCursorNone()
            => Operations.Add("CursorNone");
        void ICanvasInputHost.ResetCursor()
            => Operations.Add("ResetCursor");

        (int, int) ICanvasInputHost.GetActiveToolTypes() => ToolTypes;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ModifierReleaseDoesNotEndRunningStroke()
    {
        var host = new MockHost();
        var router = new CanvasInputRouter(host);

        // Press Space (modifier) — this should set modifier state
        router.HandleKeyDown(Key.Space, KeyModifiers.None);

        // Start a pointer transaction via HandlePointerPress
        router.HandlePointerPress(
            action: CanvasAction.PrimaryTool,
            isPrimaryDown: true,
            pointerId: 1,
            viewportPos: new Point(100, 100),
            eventArgs: null,
            ctrlHeld: false,
            shiftHeld: false);

        TestAssertions.Equal(RouterState.Running, router.State, "Should be running after pointer press");

        // Release Space modifier while still running
        router.HandleKeyUp(Key.Space, KeyModifiers.None);

        // Verify: still running, no cancel
        TestAssertions.Equal(RouterState.Running, router.State, "Modifier release must not end running stroke");
        TestAssertions.False(host.Operations.Any(o => o == "CancelTool"), "No cancel during running stroke");
    }

    [Fact]
    public void ModifierPressDuringStrokeDoesNotChangeActiveTool()
    {
        var host = new MockHost();
        var router = new CanvasInputRouter(host);

        // Start a pointer transaction
        router.HandlePointerPress(
            action: CanvasAction.PrimaryTool,
            isPrimaryDown: true,
            pointerId: 1,
            viewportPos: new Point(100, 100),
            eventArgs: null,
            ctrlHeld: false,
            shiftHeld: false);

        TestAssertions.Equal(RouterState.Running, router.State);
        var beforeAction = router.RunningAction;

        // Press Space while running — should be deferred
        router.HandleKeyDown(Key.Space, KeyModifiers.None);

        // Verify: still running same action
        TestAssertions.Equal(RouterState.Running, router.State);
        TestAssertions.Equal(beforeAction, router.RunningAction, "Running action must not change on modifier press");
        TestAssertions.False(host.Operations.Any(o => o.StartsWith("PushPreset")), "No tool push during running stroke");
    }

    [Fact]
    public void CompletedStrokeIsNotCancelledByLaterTempToolActivation()
    {
        var host = new MockHost { ToolTypes = (1, 1) };
        var router = new CanvasInputRouter(host);

        // Start a stroke (Down)
        router.HandlePointerPress(
            action: CanvasAction.PrimaryTool,
            isPrimaryDown: true,
            pointerId: 1,
            viewportPos: new Point(100, 100),
            eventArgs: null,
            ctrlHeld: false,
            shiftHeld: false);
        TestAssertions.Equal(RouterState.Running, router.State);

        // Release — stroke completes
        router.HandlePointerRelease(null!);
        TestAssertions.Equal(RouterState.Idle, router.State, "Stroke completed, should be Idle");

        // Verify no cancel occurred during the stroke cycle
        TestAssertions.False(host.Operations.Contains("CancelTool"), "No cancel during or after stroke");

        // Clear operations — now only check what happens with modifier
        host.Operations.Clear();

        // Press Alt — activates Eyedropper temporary tool
        router.HandleKeyDown(Key.LeftAlt, KeyModifiers.Alt);
        TestAssertions.True(host.Operations.Any(o => o.StartsWith("PushPreset")), "Alt should push temp eyedropper preset");
        TestAssertions.False(host.Operations.Any(o => o == "CancelTool"), "Alt press must not cancel completed stroke");

        host.Operations.Clear();

        // Release Alt — pops temp preset, restores brush
        router.HandleKeyUp(Key.LeftAlt, KeyModifiers.None);
        TestAssertions.True(host.Operations.Any(o => o.StartsWith("PopPreset")), "Alt release should pop temp preset");
        TestAssertions.False(host.Operations.Any(o => o == "CancelTool"), "Alt release must not cancel completed stroke");
    }

    [Fact]
    public void AfterStrokeReleaseHeldSpaceBecomesReadyPan()
    {
        var host = new MockHost { ToolTypes = (1, 1) };
        var router = new CanvasInputRouter(host);

        // Press and hold Space
        router.HandleKeyDown(Key.Space, KeyModifiers.None);

        // Start pointer transaction while Space still held
        router.HandlePointerPress(
            action: CanvasAction.PrimaryTool,
            isPrimaryDown: true,
            pointerId: 1,
            viewportPos: new Point(100, 100),
            eventArgs: null,
            ctrlHeld: false,
            shiftHeld: false);

        TestAssertions.Equal(RouterState.Running, router.State);

        // Release pointer while Space still held
        router.HandlePointerRelease(null!);

        TestAssertions.Equal(RouterState.Ready, router.State, "Held Space should become a ready pan action after release");
        TestAssertions.Equal(CanvasAction.PanCanvas, router.ReadyAction, "Held Space should resolve to pan.");
    }

    [Fact]
    public void CtrlShiftFallsThroughStaleSpecificNone()
    {
        var settings = new ModifierKeySettings
        {
            GeneralAssignments =
            [
                new ModifierKeyAssignment
                {
                    Modifiers = KeyModifiers.Control | KeyModifiers.Shift,
                    Action = ModifierAction.ChangeToolTemporarily,
                    TemporaryToolPresetId = "custom-select-layer"
                }
            ],
            ToolSpecificAssignments =
            {
                ["1:1"] =
                [
                    new ModifierKeyAssignment
                    {
                        Modifiers = KeyModifiers.Control | KeyModifiers.Shift,
                        Action = ModifierAction.None
                    }
                ]
            }
        };

        var resolved = settings.Resolve(1, 1, null, KeyModifiers.Control | KeyModifiers.Shift);

        TestAssertions.True(resolved != null, "Stale tool-specific None should not shadow a real general Ctrl+Shift assignment.");
        TestAssertions.Equal("custom-select-layer", resolved!.TemporaryToolPresetId);
    }

    [Fact]
    public void CaptureLostCancelsOnlyActiveTransaction()
    {
        var host = new MockHost();
        var router = new CanvasInputRouter(host);

        // Start a transaction
        router.HandlePointerPress(
            action: CanvasAction.PrimaryTool,
            isPrimaryDown: true,
            pointerId: 1,
            viewportPos: new Point(100, 100),
            eventArgs: null,
            ctrlHeld: false,
            shiftHeld: false);

        TestAssertions.Equal(RouterState.Running, router.State);

        // Capture lost
        router.HandleCaptureLost();

        TestAssertions.True(host.Operations.Contains("CommitTool"), "CaptureLost should force-end/commit active tool");
        TestAssertions.False(host.Operations.Contains("CancelTool"), "CaptureLost must not destructively cancel active paint");
        TestAssertions.Equal(RouterState.Idle, router.State, "Should be idle after capture lost");
    }
}
