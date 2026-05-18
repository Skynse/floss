//! Brush engine — tip generation, dynamics, and stamp compositing.
//!
//! Ported from `Floss.App.Brushes.*`.
//!
//! Provides:
//! - `BrushTip` trait + `ProceduralBrushTip`, `ImageBrushTip`
//! - `CubicCurve` — Catmull-Rom spline with LUT evaluation
//! - `SensorConfig`, `CurveOption`, `BrushDynamics` — dynamics pipeline
//! - `BrushPreset` — complete brush configuration
//! - `BrushEngine` — stroke management and stamp rendering

pub mod types;
pub mod stroke;
pub mod curve;
pub mod sensor;
pub mod curve_option;
pub mod dynamics;
pub mod tip;
pub mod preset;
pub mod engine;

pub use curve::CubicCurve;
pub use dynamics::BrushDynamics;
pub use engine::BrushEngine;
pub use preset::BrushPreset;
pub use sensor::{SensorConfig, SensorType};
pub use stroke::{StrokePoint, StrokeState};
pub use tip::{BrushTip, ProceduralBrushTip, StampMask};
pub use types::{AngleSource, BrushTipDirection, BrushTipShape};
