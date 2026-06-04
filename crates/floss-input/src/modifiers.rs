//! Modifier key settings — maps modifier combinations to actions.
//!
//! Ported from `Floss.App.Input.ModifierKeySettings.cs`.
//!
//! Supports:
//! - General assignments (any tool)
//! - Tool-specific assignments (per input+output process type)
//! - Exact key match (Ctrl+Z) vs any-key match (just Ctrl held)
//! - Priority: exact key > modifier-only; tool-specific > general

use std::collections::HashMap;

use serde::{Deserialize, Serialize};

use crate::keyboard::{Key, KeyModifiers};

// ── Actions ──────────────────────────────────────────────────────────────

/// What happens when a modifier combination is active.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ModifierAction {
    /// No action.
    None,
    /// Fall through to general assignments.
    Common,
    /// Temporarily switch to a different tool (eyedropper, hand, etc.).
    ChangeToolTemporarily,
    /// Auxiliary tool operation (e.g., Shift → straight line).
    ToolAux,
    /// Change brush size with Ctrl+Alt+drag.
    ChangeBrushSize,
}

/// Auxiliary tool mode.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ToolAuxOperationType {
    None = 0,
    StraightLine,
}

// ── Assignment ───────────────────────────────────────────────────────────

/// A single modifier key assignment.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ModifierKeyAssignment {
    /// If present, this assignment requires a specific key to be held.
    pub key: Option<Key>,
    /// Required modifier flags.
    pub modifiers: KeyModifiers,
    /// What action to perform.
    pub action: ModifierAction,
    /// For ChangeToolTemporarily: the ID of the preset to switch to.
    pub temporary_tool_preset_id: Option<String>,
    /// For ToolAux: the aux operation type.
    pub tool_aux_oper: ToolAuxOperationType,
}

// ── Settings ─────────────────────────────────────────────────────────────

/// The full modifier key configuration.
#[derive(Clone, Serialize, Deserialize)]
pub struct ModifierKeySettings {
    /// Assignments that apply to all tools.
    pub general_assignments: Vec<ModifierKeyAssignment>,
    /// Tool-specific assignments keyed by "input_type:output_type".
    pub tool_specific_assignments: HashMap<String, Vec<ModifierKeyAssignment>>,
}

impl ModifierKeySettings {
    /// Create the default modifier key configuration (matches C# defaults).
    pub fn create_defaults() -> Self {
        let general = vec![
            // Modifier-only combos
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers::SHIFT,
                action: ModifierAction::ToolAux,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::StraightLine,
            },
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers::CTRL,
                action: ModifierAction::None,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers::ALT,
                action: ModifierAction::ChangeToolTemporarily,
                temporary_tool_preset_id: Some("eyedropper".into()),
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers { ctrl: true, alt: true, shift: false },
                action: ModifierAction::ChangeBrushSize,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers { ctrl: true, alt: false, shift: true },
                action: ModifierAction::None,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers { ctrl: false, alt: true, shift: true },
                action: ModifierAction::None,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers { ctrl: true, alt: true, shift: true },
                action: ModifierAction::None,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            // Space combos — view tools
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers::NONE,
                action: ModifierAction::ChangeToolTemporarily,
                temporary_tool_preset_id: Some("hand".into()),
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers::CTRL,
                action: ModifierAction::ChangeToolTemporarily,
                temporary_tool_preset_id: Some("zoom_in".into()),
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers::ALT,
                action: ModifierAction::ChangeToolTemporarily,
                temporary_tool_preset_id: Some("zoom_out".into()),
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers::SHIFT,
                action: ModifierAction::ChangeToolTemporarily,
                temporary_tool_preset_id: Some("rotate".into()),
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers { ctrl: true, alt: true, shift: false },
                action: ModifierAction::None,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers { ctrl: true, alt: false, shift: true },
                action: ModifierAction::None,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers { ctrl: false, alt: true, shift: true },
                action: ModifierAction::None,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers { ctrl: true, alt: true, shift: true },
                action: ModifierAction::None,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
        ];

        // Tool-specific defaults for brush-family tools
        let brush_assignments = vec![
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers::CTRL,
                action: ModifierAction::ChangeToolTemporarily,
                temporary_tool_preset_id: Some("move_layer".into()),
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers::ALT,
                action: ModifierAction::ChangeToolTemporarily,
                temporary_tool_preset_id: Some("eyedropper".into()),
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers { ctrl: true, alt: true, shift: false },
                action: ModifierAction::ChangeBrushSize,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers::SHIFT,
                action: ModifierAction::ToolAux,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::StraightLine,
            },
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers { ctrl: true, alt: false, shift: true },
                action: ModifierAction::None,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers { ctrl: false, alt: true, shift: true },
                action: ModifierAction::None,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: None,
                modifiers: KeyModifiers { ctrl: true, alt: true, shift: true },
                action: ModifierAction::None,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers::NONE,
                action: ModifierAction::Common,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers::CTRL,
                action: ModifierAction::Common,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers::ALT,
                action: ModifierAction::Common,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers::SHIFT,
                action: ModifierAction::Common,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers { ctrl: true, alt: true, shift: false },
                action: ModifierAction::Common,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers { ctrl: true, alt: false, shift: true },
                action: ModifierAction::Common,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers { ctrl: false, alt: true, shift: true },
                action: ModifierAction::Common,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
            ModifierKeyAssignment {
                key: Some(Key::Space),
                modifiers: KeyModifiers { ctrl: true, alt: true, shift: true },
                action: ModifierAction::Common,
                temporary_tool_preset_id: None,
                tool_aux_oper: ToolAuxOperationType::None,
            },
        ];

        let mut tool_specific = HashMap::new();
        // Brush family tools all get the same assignments
        for input_type in &[1, 2, 3, 4] {
            // Pen, Brush, Eraser, Smudge
            let key = format!("{}:1", input_type); // :1 = DirectDraw
            tool_specific.insert(key, brush_assignments.clone());
        }

        Self {
            general_assignments: general,
            tool_specific_assignments: tool_specific,
        }
    }

    /// Resolve the modifier action for the given input state.
    ///
    /// Resolution priority:
    /// 1. Tool-specific exact key match
    /// 2. Tool-specific modifier-only match
    /// 3. General exact key match
    /// 4. General modifier-only match
    ///
    /// Returns `None` if no assignment matches or if the matched assignment is `None`.
    pub fn resolve(
        &self,
        input_process_type: i32,
        output_process_type: i32,
        key: Option<Key>,
        mods: KeyModifiers,
    ) -> Option<&ModifierKeyAssignment> {
        let specific_key = format!("{}:{}", input_process_type, output_process_type);

        let exact_match = |a: &&ModifierKeyAssignment| -> bool {
            key.is_some()
                && a.modifiers == mods
                && a.key.is_some()
                && a.key == key
        };

        let any_match = |a: &&ModifierKeyAssignment| -> bool {
            a.modifiers == mods && a.key.is_none()
        };

        // Tool-specific
        if let Some(specific) = self.tool_specific_assignments.get(&specific_key) {
            if let Some(m) = specific.iter().find(exact_match) {
                if m.action == ModifierAction::None {
                    return None;
                }
                if m.action == ModifierAction::Common {
                    return self.general_assignments.iter().find(exact_match)
                        .or_else(|| self.general_assignments.iter().find(any_match))
                        .filter(|a| a.action != ModifierAction::None);
                }
                return Some(m);
            }
            if let Some(m) = specific.iter().find(any_match) {
                if m.action == ModifierAction::None {
                    return None;
                }
                if m.action == ModifierAction::Common {
                    return self.general_assignments.iter().find(exact_match)
                        .or_else(|| self.general_assignments.iter().find(any_match))
                        .filter(|a| a.action != ModifierAction::None);
                }
                return Some(m);
            }
        }

        // General
        let gm = self.general_assignments.iter().find(exact_match)
            .or_else(|| self.general_assignments.iter().find(any_match));
        gm.filter(|a| a.action != ModifierAction::None)
    }
}
