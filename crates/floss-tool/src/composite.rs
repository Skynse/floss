//! Composite tool — wires an input process to an output process.
//!
//! Ported from `Floss.App.Processes.CompositeTool`.
//!
//! This is the standard tool implementation used for all process-based tools.
//!
//! ## Alternate tool
//!
//! The `alternate` field provides the tool that's used when a modifier key
//! (e.g., Alt) activates `ChangeToolTemporarily` without a specific preset ID.
//! For brush-family tools, the alternate is an eyedropper.

use crate::process::{IInputProcess, IOutputProcess};
use crate::tool::{ITool, InputSample, ToolContext};

/// A tool composed of an input process (shape capture) and
/// an output process (what to do with the captured shape).
pub struct CompositeTool {
    pub input: Box<dyn IInputProcess>,
    pub output: Box<dyn IOutputProcess>,
    /// Optional alternate tool (activated by modifier keys).
    pub alternate: Option<Box<dyn ITool>>,
}

impl CompositeTool {
    pub fn new(
        input: Box<dyn IInputProcess>,
        output: Box<dyn IOutputProcess>,
        alternate: Option<Box<dyn ITool>>,
    ) -> Self {
        Self {
            input,
            output,
            alternate,
        }
    }
}

impl ITool for CompositeTool {
    fn activate(&mut self, _ctx: &mut ToolContext) {}

    fn deactivate(&mut self, ctx: &mut ToolContext) {
        self.cancel(ctx);
    }

    fn pointer_down(&mut self, ctx: &mut ToolContext, sample: &InputSample) {
        self.input.set_tool_aux_mode(ctx.tool_aux_mode);
        self.input.pointer_down(sample);
        if let Some(immediate) = self.input.get_immediate_result() {
            self.output.execute(ctx, &immediate);
        }
        if self.input.is_active() {
            if let Some(preview) = self.input.get_preview() {
                self.output.preview(ctx, &preview);
            }
        }
    }

    fn pointer_move(&mut self, ctx: &mut ToolContext, sample: &InputSample) {
        self.input.set_tool_aux_mode(ctx.tool_aux_mode);
        self.input.pointer_move(sample);
        if self.input.is_active() {
            if let Some(preview) = self.input.get_preview() {
                self.output.preview(ctx, &preview);
            }
        }
    }

    fn pointer_up(&mut self, ctx: &mut ToolContext, sample: &InputSample) {
        self.input.set_tool_aux_mode(ctx.tool_aux_mode);
        self.input.pointer_up(sample);
        if let Some(result) = self.input.get_result() {
            self.output.execute(ctx, &result);
        }
    }

    fn cancel(&mut self, ctx: &mut ToolContext) {
        self.input.cancel();
        self.output.cancel(ctx);
    }

    fn has_pending_operation(&self) -> bool {
        self.input.is_active()
    }

    fn can_commit_from_click(&self) -> bool {
        // Polyline input can be committed on Enter
        false
    }

    fn commit(&mut self, ctx: &mut ToolContext) {
        // For modal tools (polyline, etc.)
        self.input.commit();
        if let Some(result) = self.input.get_result() {
            self.output.execute(ctx, &result);
        }
    }

    fn alternate(&self) -> Option<&dyn ITool> {
        self.alternate.as_ref().map(|a| a.as_ref())
    }

    fn alternate_mut(&mut self) -> Option<&mut (dyn ITool + '_)> {
        match self.alternate.as_mut() {
            Some(alternate) => Some(alternate.as_mut()),
            None => None,
        }
    }
}
