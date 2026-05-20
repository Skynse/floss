using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Tests;

internal static class CanvasInputRouterTests
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

    public static void ModifierReleaseDoesNotEndRunningStroke()
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

        AssertEx.Equal(RouterState.Running, router.State, "Should be running after pointer press");

        // Release Space modifier while still running
        router.HandleKeyUp(Key.Space, KeyModifiers.None);

        // Verify: still running, no cancel
        AssertEx.Equal(RouterState.Running, router.State, "Modifier release must not end running stroke");
        AssertEx.False(host.Operations.Any(o => o == "CancelTool"), "No cancel during running stroke");
    }

    public static void ModifierPressDuringStrokeDoesNotChangeActiveTool()
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

        AssertEx.Equal(RouterState.Running, router.State);
        var beforeAction = router.RunningAction;

        // Press Space while running — should be deferred
        router.HandleKeyDown(Key.Space, KeyModifiers.None);

        // Verify: still running same action
        AssertEx.Equal(RouterState.Running, router.State);
        AssertEx.Equal(beforeAction, router.RunningAction, "Running action must not change on modifier press");
        AssertEx.False(host.Operations.Any(o => o.StartsWith("PushPreset")), "No tool push during running stroke");
    }

    public static void CompletedStrokeIsNotCancelledByLaterTempToolActivation()
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
        AssertEx.Equal(RouterState.Running, router.State);

        // Release — stroke completes
        router.HandlePointerRelease(null!);
        AssertEx.Equal(RouterState.Idle, router.State, "Stroke completed, should be Idle");

        // Verify no cancel occurred during the stroke cycle
        AssertEx.False(host.Operations.Contains("CancelTool"), "No cancel during or after stroke");

        // Clear operations — now only check what happens with modifier
        host.Operations.Clear();

        // Press Alt — activates Eyedropper temporary tool
        router.HandleKeyDown(Key.LeftAlt, KeyModifiers.Alt);
        AssertEx.True(host.Operations.Any(o => o.StartsWith("PushPreset")), "Alt should push temp eyedropper preset");
        AssertEx.False(host.Operations.Any(o => o == "CancelTool"), "Alt press must not cancel completed stroke");

        host.Operations.Clear();

        // Release Alt — pops temp preset, restores brush
        router.HandleKeyUp(Key.LeftAlt, KeyModifiers.None);
        AssertEx.True(host.Operations.Any(o => o.StartsWith("PopPreset")), "Alt release should pop temp preset");
        AssertEx.False(host.Operations.Any(o => o == "CancelTool"), "Alt release must not cancel completed stroke");
    }

    public static void AfterStrokeReleaseHeldSpaceBecomesReadyPan()
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

        AssertEx.Equal(RouterState.Running, router.State);

        // Release pointer while Space still held
        router.HandlePointerRelease(null!);

        AssertEx.Equal(RouterState.Ready, router.State, "Held Space should become a ready pan action after release");
        AssertEx.Equal(CanvasAction.PanCanvas, router.ReadyAction, "Held Space should resolve to pan.");
    }

    public static void CtrlShiftFallsThroughStaleSpecificNone()
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

        AssertEx.True(resolved != null, "Stale tool-specific None should not shadow a real general Ctrl+Shift assignment.");
        AssertEx.Equal("custom-select-layer", resolved!.TemporaryToolPresetId);
    }

    public static void CaptureLostCancelsOnlyActiveTransaction()
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

        AssertEx.Equal(RouterState.Running, router.State);

        // Capture lost
        router.HandleCaptureLost();

        AssertEx.True(host.Operations.Contains("CommitTool"), "CaptureLost should force-end/commit active tool");
        AssertEx.False(host.Operations.Contains("CancelTool"), "CaptureLost must not destructively cancel active paint");
        AssertEx.Equal(RouterState.Idle, router.State, "Should be idle after capture lost");
    }
}
