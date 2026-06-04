use floss_brush::{BrushPreset, BrushTipDirection};
use floss_brush::preset::{BrushQuality, MixingMode, SmudgeMode};
use floss_core::{InputProcessType, OutputProcessType};
use floss_input::{Key, KeyBinding, KeyModifiers};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum SelectMode {
    Rect,
    Lasso,
    PolylineLasso,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum SelectOp {
    Replace,
    Add,
    Subtract,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum GradientType {
    Linear,
    Radial,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ShapeKind {
    Rectangle,
    Ellipse,
    Line,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ShapeDrawMode {
    Fill,
    Stroke,
    FillAndStroke,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum FillReferenceMode {
    CurrentLayer,
    ReferenceLayers,
    AllLayers,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum LiquifyMode {
    Push,
    Expand,
    Pinch,
    PushLeft,
    PushRight,
    TwirlCw,
    TwirlCcw,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum EyedropperSampleMode {
    Image,
    CurrentLayer,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum AntialiasingQuality {
    None,
    Low,
    Medium,
    High,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ToolPresetEngine {
    Brush,
    Eraser,
    Smudge,
    Select,
    MagicWand,
    Fill,
    LassoFill,
    Eyedropper,
    Move,
    MoveLayer,
    Gradient,
    Shape,
    Polyline,
    Liquify,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ToolPreset {
    pub id: String,
    pub name: String,
    pub input_process: InputProcessType,
    pub output_process: OutputProcessType,
    pub engine: ToolPresetEngine,
    pub stabilization: f64,
    pub antialiasing: bool,
    pub antialiasing_quality: AntialiasingQuality,
    pub brush_size: Option<f64>,
    pub brush_opacity: Option<f64>,
    pub brush_flow: Option<f64>,
    pub brush_hardness: Option<f64>,
    pub brush_spacing: Option<f64>,
    pub brush_smoothing: Option<f64>,
    pub brush_grain: Option<f64>,
    pub brush_color_mix: Option<bool>,
    pub brush_color_load: Option<f64>,
    pub brush_color_stretch: Option<f64>,
    pub brush_blur_amount: Option<f64>,
    pub brush_smudge_mode: Option<SmudgeMode>,
    pub brush_mixing_mode: Option<MixingMode>,
    pub brush_amount_of_paint: Option<f64>,
    pub brush_density_of_paint: Option<f64>,
    pub brush_tip_density: Option<f64>,
    pub brush_tip_thickness: Option<f64>,
    pub brush_tip_direction: Option<BrushTipDirection>,
    pub brush_quality: Option<BrushQuality>,
    pub brush_id: Option<String>,
    pub preset_icon: Option<String>,
    pub select_mode: SelectMode,
    pub select_op: SelectOp,
    pub tolerance: f64,
    pub fill_reference: FillReferenceMode,
    pub gradient_type: GradientType,
    pub shape_kind: ShapeKind,
    pub shape_draw_mode: ShapeDrawMode,
    pub polyline_close_path: bool,
    pub polyline_stroke_width: f32,
    pub liquify_mode: LiquifyMode,
    pub liquify_size: f64,
    pub liquify_strength: f64,
    pub zoom_direction: f64,
    pub eyedropper_sample_mode: EyedropperSampleMode,
    pub eyedropper_exclude_locked_layers: bool,
    pub eyedropper_exclude_reference_layers: bool,
}

impl ToolPreset {
    pub fn new(name: impl Into<String>, engine: ToolPresetEngine) -> Self {
        let name = name.into();
        Self {
            id: name.to_lowercase().replace(' ', "-"),
            name,
            input_process: InputProcessType::Brush,
            output_process: OutputProcessType::DirectDraw,
            engine,
            stabilization: 0.0,
            antialiasing: true,
            antialiasing_quality: AntialiasingQuality::High,
            brush_size: None,
            brush_opacity: None,
            brush_flow: None,
            brush_hardness: None,
            brush_spacing: None,
            brush_smoothing: None,
            brush_grain: None,
            brush_color_mix: None,
            brush_color_load: None,
            brush_color_stretch: None,
            brush_blur_amount: None,
            brush_smudge_mode: None,
            brush_mixing_mode: None,
            brush_amount_of_paint: None,
            brush_density_of_paint: None,
            brush_tip_density: None,
            brush_tip_thickness: None,
            brush_tip_direction: None,
            brush_quality: None,
            brush_id: None,
            preset_icon: None,
            select_mode: SelectMode::Rect,
            select_op: SelectOp::Replace,
            tolerance: 0.1,
            fill_reference: FillReferenceMode::CurrentLayer,
            gradient_type: GradientType::Linear,
            shape_kind: ShapeKind::Rectangle,
            shape_draw_mode: ShapeDrawMode::Fill,
            polyline_close_path: false,
            polyline_stroke_width: 4.0,
            liquify_mode: LiquifyMode::Push,
            liquify_size: 80.0,
            liquify_strength: 0.3,
            zoom_direction: 1.0,
            eyedropper_sample_mode: EyedropperSampleMode::Image,
            eyedropper_exclude_locked_layers: false,
            eyedropper_exclude_reference_layers: false,
        }
    }

    pub fn apply_to_brush_preset(&self, preset: &BrushPreset) -> BrushPreset {
        let mut result = preset.clone_preset();
        if let Some(v) = self.brush_size { result.size = v; }
        if let Some(v) = self.brush_opacity { result.opacity = v; }
        if let Some(v) = self.brush_hardness { result.hardness = v; }
        if let Some(v) = self.brush_spacing { result.spacing = v; }
        if let Some(v) = self.brush_flow { result.flow = v; }
        if let Some(v) = self.brush_smoothing { result.stabilization = v; }
        if let Some(v) = self.brush_grain { result.grain = v; }
        if let Some(v) = self.brush_color_mix { result.color_mix = v; }
        if let Some(v) = self.brush_color_load { result.color_load = v; }
        if let Some(v) = self.brush_color_stretch { result.color_stretch = v; }
        if let Some(v) = self.brush_blur_amount { result.blur_amount = v; }
        if let Some(v) = self.brush_smudge_mode { result.smudge_mode = v; }
        if let Some(v) = self.brush_mixing_mode { result.mixing_mode = v; }
        if let Some(v) = self.brush_amount_of_paint { result.amount_of_paint = v; }
        if let Some(v) = self.brush_density_of_paint { result.density_of_paint = v; }
        if let Some(v) = self.brush_tip_density { result.tip_density = v; }
        if let Some(v) = self.brush_tip_thickness { result.tip_thickness = v; }
        if let Some(v) = self.brush_tip_direction { result.tip_direction = v; }
        if let Some(v) = self.brush_quality { result.quality = v; }
        result
    }

    pub fn capture_from_brush_preset(&mut self, preset: &BrushPreset) {
        self.brush_size = Some(preset.size);
        self.brush_opacity = Some(preset.opacity);
        self.brush_hardness = Some(preset.hardness);
        self.brush_spacing = Some(preset.spacing);
        self.brush_flow = Some(preset.flow);
        self.brush_smoothing = Some(preset.stabilization);
        self.brush_grain = Some(preset.grain);
        self.brush_color_mix = Some(preset.color_mix);
        self.brush_color_load = Some(preset.color_load);
        self.brush_color_stretch = Some(preset.color_stretch);
        self.brush_blur_amount = Some(preset.blur_amount);
        self.brush_smudge_mode = Some(preset.smudge_mode);
        self.brush_mixing_mode = Some(preset.mixing_mode);
        self.brush_amount_of_paint = Some(preset.amount_of_paint);
        self.brush_density_of_paint = Some(preset.density_of_paint);
        self.brush_tip_density = Some(preset.tip_density);
        self.brush_tip_thickness = Some(preset.tip_thickness);
        self.brush_tip_direction = Some(preset.tip_direction);
        self.brush_quality = Some(preset.quality);
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ToolCategory {
    pub name: String,
    pub preset_ids: Vec<String>,
    pub last_active_preset_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ToolGroup {
    pub id: String,
    pub name: String,
    pub shortcut: KeyBinding,
    pub default_engine: ToolPresetEngine,
    pub custom_icon: Option<String>,
    pub presets: Vec<ToolPreset>,
    pub categories: Vec<ToolCategory>,
    pub last_active_preset_id: Option<String>,
    pub last_active_category_name: Option<String>,
}

impl ToolGroup {
    pub fn active_preset(&self) -> Option<&ToolPreset> {
        self.last_active_preset_id
            .as_ref()
            .and_then(|id| self.presets.iter().find(|preset| &preset.id == id))
            .or_else(|| self.presets.first())
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ToolGroupConfig {
    pub groups: Vec<ToolGroup>,
}

impl Default for ToolGroupConfig {
    fn default() -> Self {
        Self { groups: defaults() }
    }
}

pub const VIEW_HAND_PRESET_ID: &str = "builtin-hand";
pub const VIEW_ROTATE_PRESET_ID: &str = "builtin-rotate";
pub const VIEW_ZOOM_IN_PRESET_ID: &str = "builtin-zoomin";
pub const VIEW_ZOOM_OUT_PRESET_ID: &str = "builtin-zoomout";
pub const EYEDROPPER_PRESET_ID: &str = "builtin-eyedropper";
pub const MOVE_LAYER_PRESET_ID: &str = "builtin-movelayer";

fn defaults() -> Vec<ToolGroup> {
    vec![
        with_default_category(ToolGroup {
            id: "brush".into(),
            name: "Brush".into(),
            shortcut: KeyBinding::new(Key::B, KeyModifiers::NONE),
            default_engine: ToolPresetEngine::Brush,
            custom_icon: None,
            presets: vec![ToolPreset {
                input_process: InputProcessType::Brush,
                output_process: OutputProcessType::DirectDraw,
                ..ToolPreset::new("Brush", ToolPresetEngine::Brush)
            }],
            categories: vec![],
            last_active_preset_id: None,
            last_active_category_name: None,
        }),
        with_default_category(ToolGroup {
            id: "eraser".into(),
            name: "Eraser".into(),
            shortcut: KeyBinding::new(Key::E, KeyModifiers::NONE),
            default_engine: ToolPresetEngine::Eraser,
            custom_icon: None,
            presets: vec![ToolPreset {
                input_process: InputProcessType::Eraser,
                output_process: OutputProcessType::DirectDraw,
                ..ToolPreset::new("Eraser", ToolPresetEngine::Eraser)
            }],
            categories: vec![],
            last_active_preset_id: None,
            last_active_category_name: None,
        }),
        with_default_category(ToolGroup {
            id: "smudge".into(),
            name: "Smudge".into(),
            shortcut: KeyBinding::new(Key::U, KeyModifiers::NONE),
            default_engine: ToolPresetEngine::Smudge,
            custom_icon: None,
            presets: vec![ToolPreset {
                input_process: InputProcessType::Smudge,
                output_process: OutputProcessType::DirectDraw,
                brush_color_mix: Some(true),
                brush_smudge_mode: Some(SmudgeMode::Smudge),
                brush_amount_of_paint: Some(0.0),
                brush_density_of_paint: Some(0.0),
                ..ToolPreset::new("Smudge", ToolPresetEngine::Smudge)
            }],
            categories: vec![],
            last_active_preset_id: None,
            last_active_category_name: None,
        }),
        with_default_category(ToolGroup {
            id: "select".into(),
            name: "Select".into(),
            shortcut: KeyBinding::new(Key::S, KeyModifiers::NONE),
            default_engine: ToolPresetEngine::Select,
            custom_icon: None,
            presets: vec![
                ToolPreset {
                    input_process: InputProcessType::Rect,
                    output_process: OutputProcessType::SelectionArea,
                    ..ToolPreset::new("Rectangle", ToolPresetEngine::Select)
                },
                ToolPreset {
                    input_process: InputProcessType::Lasso,
                    output_process: OutputProcessType::SelectionArea,
                    select_mode: SelectMode::Lasso,
                    stabilization: 0.3,
                    ..ToolPreset::new("Lasso", ToolPresetEngine::Select)
                },
                ToolPreset {
                    input_process: InputProcessType::Polyline,
                    output_process: OutputProcessType::SelectionArea,
                    select_mode: SelectMode::PolylineLasso,
                    ..ToolPreset::new("Polygon", ToolPresetEngine::Select)
                },
            ],
            categories: vec![],
            last_active_preset_id: None,
            last_active_category_name: None,
        }),
        with_default_category(ToolGroup {
            id: "fill".into(),
            name: "Fill".into(),
            shortcut: KeyBinding::new(Key::G, KeyModifiers::NONE),
            default_engine: ToolPresetEngine::Fill,
            custom_icon: None,
            presets: vec![
                ToolPreset {
                    input_process: InputProcessType::Click,
                    output_process: OutputProcessType::FloodFill,
                    ..ToolPreset::new("Fill", ToolPresetEngine::Fill)
                },
                ToolPreset {
                    input_process: InputProcessType::Lasso,
                    output_process: OutputProcessType::ClosedAreaFill,
                    stabilization: 0.3,
                    ..ToolPreset::new("Lasso Fill", ToolPresetEngine::LassoFill)
                },
            ],
            categories: vec![],
            last_active_preset_id: None,
            last_active_category_name: None,
        }),
        with_default_category(ToolGroup {
            id: "operation".into(),
            name: "Operation".into(),
            shortcut: KeyBinding::new(Key::V, KeyModifiers::NONE),
            default_engine: ToolPresetEngine::MoveLayer,
            custom_icon: None,
            presets: vec![
                ToolPreset {
                    id: MOVE_LAYER_PRESET_ID.into(),
                    input_process: InputProcessType::MoveLayer,
                    output_process: OutputProcessType::MoveLayer,
                    ..ToolPreset::new("Move Layer", ToolPresetEngine::MoveLayer)
                },
                ToolPreset {
                    input_process: InputProcessType::Rect,
                    output_process: OutputProcessType::SelectLayer,
                    ..ToolPreset::new("Select Layer", ToolPresetEngine::MoveLayer)
                },
            ],
            categories: vec![],
            last_active_preset_id: None,
            last_active_category_name: None,
        }),
        with_default_category(ToolGroup {
            id: "view".into(),
            name: "View".into(),
            shortcut: KeyBinding::EMPTY,
            default_engine: ToolPresetEngine::Move,
            custom_icon: None,
            presets: vec![
                ToolPreset { id: VIEW_HAND_PRESET_ID.into(), input_process: InputProcessType::Hand, output_process: OutputProcessType::Hand, ..ToolPreset::new("Hand", ToolPresetEngine::Move) },
                ToolPreset { id: VIEW_ROTATE_PRESET_ID.into(), input_process: InputProcessType::Rotate, output_process: OutputProcessType::Rotate, ..ToolPreset::new("Rotate", ToolPresetEngine::Move) },
                ToolPreset { id: VIEW_ZOOM_IN_PRESET_ID.into(), input_process: InputProcessType::Zoom, output_process: OutputProcessType::Zoom, zoom_direction: 1.0, ..ToolPreset::new("Zoom In", ToolPresetEngine::Move) },
                ToolPreset { id: VIEW_ZOOM_OUT_PRESET_ID.into(), input_process: InputProcessType::Zoom, output_process: OutputProcessType::Zoom, zoom_direction: -1.0, ..ToolPreset::new("Zoom Out", ToolPresetEngine::Move) },
            ],
            categories: vec![],
            last_active_preset_id: None,
            last_active_category_name: None,
        }),
        with_default_category(ToolGroup {
            id: "eyedropper".into(),
            name: "Eyedropper".into(),
            shortcut: KeyBinding::new(Key::I, KeyModifiers::NONE),
            default_engine: ToolPresetEngine::Eyedropper,
            custom_icon: None,
            presets: vec![ToolPreset { id: EYEDROPPER_PRESET_ID.into(), input_process: InputProcessType::Click, output_process: OutputProcessType::Eyedropper, ..ToolPreset::new("Eyedropper", ToolPresetEngine::Eyedropper) }],
            categories: vec![],
            last_active_preset_id: None,
            last_active_category_name: None,
        }),
    ]
}

fn with_default_category(mut group: ToolGroup) -> ToolGroup {
    ensure_fallback_category(&mut group);
    group
}

fn ensure_fallback_category(group: &mut ToolGroup) {
    let uncategorized: Vec<String> = group
        .presets
        .iter()
        .filter(|preset| !group.categories.iter().any(|category| category.preset_ids.contains(&preset.id)))
        .map(|preset| preset.id.clone())
        .collect();

    if uncategorized.is_empty() {
        return;
    }

    if let Some(category) = group.categories.iter_mut().find(|category| category.name == group.name) {
        for preset_id in uncategorized {
            if !category.preset_ids.contains(&preset_id) {
                category.preset_ids.push(preset_id);
            }
        }
        return;
    }

    group.categories.insert(0, ToolCategory {
        name: group.name.clone(),
        preset_ids: uncategorized,
        last_active_preset_id: None,
    });
}
