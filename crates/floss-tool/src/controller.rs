//! Tool controller — manages the active tool and dispatches pointer events.
//!
//! This is the central dispatcher for pointer input. It also manages
//! alternate tools (activated by modifier keys) and **queues tool-switch
//! requests during active strokes** — the fix for the Alt-cancels-stroke bug.
//!
//! ## Modifier key queuing
//!
//! When a `ChangeToolTemporarily` modifier is pressed during an active stroke,
//! the switch request is recorded but NOT applied until the stroke ends.
//! This prevents the tool from being deactivated (and the stroke cancelled)
//! mid-draw, matching CSP behavior.

use floss_input::{ModifierAction, ModifierKeyAssignment, ModifierKeySettings, ToolAuxOperationType};

use crate::tool::{ITool, InputSample, ToolContext};

/// A queued tool-switch request from a modifier key press during a stroke.
#[derive(Debug, Clone)]
pub struct QueuedToolSwitch {
    pub preset_id: Option<String>,
    pub alternate_active: bool,
}

/// Manages the active tool, alternate tool, and event dispatch.
pub struct ToolController {
    active_tool: Box<dyn ITool>,
    is_alternate_active: bool,
    has_pending_active: bool,
    /// Tool-switch queued during an active stroke — applied on pointer-up.
    queued_switch: Option<QueuedToolSwitch>,
    /// Saved alternate state before a brush-size gesture.
    had_alternate_before_brush_size: bool,
}

impl ToolController {
    pub fn new(initial_tool: Box<dyn ITool>) -> Self {
        Self {
            active_tool: initial_tool,
            is_alternate_active: false,
            has_pending_active: false,
            queued_switch: None,
            had_alternate_before_brush_size: false,
        }
    }

    pub fn active_tool(&self) -> &dyn ITool {
        self.active_tool.as_ref()
    }

    pub fn active_tool_mut(&mut self) -> &mut dyn ITool {
        self.active_tool.as_mut()
    }

    pub fn is_alternate_active(&self) -> bool {
        self.is_alternate_active && self.active_tool.alternate().is_some()
    }

    pub fn has_pending_operation(&self) -> bool {
        self.has_pending_active
    }

    /// Set whether the alternate tool is active.
    /// Returns true if the alternate state changed.
    pub fn set_alternate_active(&mut self, active: bool) -> bool {
        if self.is_alternate_active == active {
            return false;
        }
        let has_alternate = self.active_tool.alternate().is_some();
        self.is_alternate_active = active && has_alternate;
        true
    }

    /// Set the active tool, deactivating the old one and activating the new one.
    pub fn set_active_tool(&mut self, ctx: &mut ToolContext, tool: Box<dyn ITool>) {
        // Deactivate old tool
        self.active_tool.deactivate(ctx);
        self.is_alternate_active = false;
        self.queued_switch = None;

        self.active_tool = tool;
        self.active_tool.activate(ctx);
    }

    /// Queue a tool switch to be applied when the current stroke ends.
    /// Called when a modifier key requests `ChangeToolTemporarily` during
    /// an active stroke — the switch is deferred to prevent cancellation.
    pub fn queue_tool_switch(&mut self, preset_id: Option<String>) {
        let alternate_active = preset_id.is_none();
        self.queued_switch = Some(QueuedToolSwitch {
            preset_id,
            alternate_active,
        });
    }

    /// Apply any queued tool switch. Call this when a stroke ends (pointer-up).
    /// Returns true if a switch was applied.
    pub fn apply_queued_switch(&mut self, ctx: &mut ToolContext) -> bool {
        if let Some(queued) = self.queued_switch.take() {
            if queued.preset_id.is_none() {
                self.is_alternate_active = queued.alternate_active && self.active_tool.alternate().is_some();
            }
            return true;
        }
        false
    }

    /// Returns true if a tool switch is queued.
    pub fn has_queued_switch(&self) -> bool {
        self.queued_switch.is_some()
    }

    /// Get the queued switch info (for the app to act on).
    pub fn take_queued_switch(&mut self) -> Option<QueuedToolSwitch> {
        self.queued_switch.take()
    }

    /// Whether the alternate was active before a brush-size gesture started.
    pub fn had_alternate_before_brush_size(&self) -> bool {
        self.had_alternate_before_brush_size
    }

    /// Save the alternate state before starting a brush-size gesture.
    pub fn save_alternate_for_brush_size(&mut self) {
        self.had_alternate_before_brush_size = self.is_alternate_active;
    }

    // ── Event dispatch ──────────────────────────────────────────────────

    /// Dispatch a pointer-down event to the current (or alternate) tool.
    pub fn dispatch_down(&mut self, ctx: &mut ToolContext, sample: &InputSample) {
        self.has_pending_active = true;
        let tool = self.current_tool_mut();
        tool.pointer_down(ctx, sample);
    }

    /// Dispatch a pointer-move event.
    pub fn dispatch_move(&mut self, ctx: &mut ToolContext, sample: &InputSample) {
        let tool = self.current_tool_mut();
        tool.pointer_move(ctx, sample);
    }

    /// Dispatch a pointer-up event and apply any queued tool switch.
    pub fn dispatch_up(&mut self, ctx: &mut ToolContext, sample: &InputSample) {
        let tool = self.current_tool_mut();
        tool.pointer_up(ctx, sample);
        self.has_pending_active = false;
        self.apply_queued_switch(ctx);
    }

    /// Cancel the current tool operation.
    pub fn cancel(&mut self, ctx: &mut ToolContext) -> bool {
        let had_pending = self.has_pending_active;
        let tool = self.current_tool_mut();
        tool.cancel(ctx);
        self.has_pending_active = false;
        self.queued_switch = None;
        had_pending
    }

    /// Commit the current tool's pending operation.
    pub fn commit(&mut self, ctx: &mut ToolContext) {
        let tool = self.current_tool_mut();
        tool.commit(ctx);
        self.has_pending_active = false;
        self.queued_switch = None;
    }

    /// Get the currently effective tool (alternate if active, otherwise main).
    fn current_tool_mut(&mut self) -> &mut dyn ITool {
        if self.is_alternate_active {
            let active: *mut dyn ITool = self.active_tool.as_mut();
            // SAFETY: `active` points to `self.active_tool`. We only produce one
            // mutable reference here and return immediately if the alternate exists.
            unsafe {
                if let Some(alternate) = (&mut *active).alternate_mut() {
                    return alternate;
                }
            }
        }
        self.active_tool.as_mut()
    }
}
