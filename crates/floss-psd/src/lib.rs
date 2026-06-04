//! Convert parsed PSD document into a Floss DrawingDocument.

pub mod reader;

use floss_core::BlendMode;
use floss_document::DrawingDocument;

use crate::reader::{PsdDocument, PsdNode};

/// Import a parsed PSD into a DrawingDocument.
pub fn import_psd(psd: PsdDocument) -> DrawingDocument {
    let mut doc = DrawingDocument::new(psd.width, psd.height);

    // Build import layers: we need to collect them in order, then replace
    let mut layers = Vec::new();
    for node in &psd.layers {
        import_node(&mut layers, node, -1, 0);
    }

    let layer_count = layers.len();
    let active = if layer_count > 0 { layer_count - 1 } else { 0 };

    doc.replace_for_import(
        psd.width,
        psd.height,
        floss_core::Color::from_bytes(255, 255, 255, 255),
        layers,
        active,
    );

    doc
}

fn import_node(
    layers: &mut Vec<floss_document::DrawingLayer>,
    node: &PsdNode,
    parent_group: i32,
    depth: i32,
) {
    match node {
        PsdNode::Group(group) => {
            let group_idx = layers.len() as i32;
            let mut gl = floss_document::DrawingLayer::new_group(&group.name, 1, 1);
            gl.opacity = group.opacity as f64 / 255.0;
            gl.visible = group.visible;
            gl.blend_mode = map_blend_mode(&group.blend_mode);
            gl.is_clipping = group.clipping;
            gl.indent_level = depth;
            gl.is_open = group.is_open;
            gl.parent_group = parent_group;
            layers.push(gl);

            for child in &group.children {
                import_node(layers, child, group_idx, depth + 1);
            }
        }
        PsdNode::Layer(psd_layer) => {
            let w = (psd_layer.right - psd_layer.left).max(1);
            let h = (psd_layer.bottom - psd_layer.top).max(1);
            let mut layer = floss_document::DrawingLayer::new(
                &psd_layer.name,
                w,
                h,
            );
            layer.opacity = psd_layer.opacity as f64 / 255.0;
            layer.visible = psd_layer.visible;
            layer.blend_mode = map_blend_mode(&psd_layer.blend_mode);
            layer.is_clipping = psd_layer.clipping;
            layer.indent_level = depth;
            layer.offset_x = psd_layer.left;
            layer.offset_y = psd_layer.top;
            layer.parent_group = parent_group;

            if !psd_layer.bgra.is_empty() {
                let w = psd_layer.right - psd_layer.left;
                let h = psd_layer.bottom - psd_layer.top;
                if w > 0 && h > 0 {
                    layer.pixels.copy_from_bgra(&psd_layer.bgra, w, h);
                }
            }

            layers.push(layer);
        }
    }
}

fn map_blend_mode(key: &str) -> BlendMode {
    match key.trim_end() {
        "norm" => BlendMode::Normal,
        "pass" => BlendMode::PassThrough,
        "diss" => BlendMode::Dissolve,
        "dark" => BlendMode::Darken,
        "mul " => BlendMode::Multiply,
        "idiv" => BlendMode::ColorBurn,
        "lbrn" => BlendMode::LinearBurn,
        "dkCl" => BlendMode::DarkerColor,
        "lite" => BlendMode::Lighten,
        "scrn" => BlendMode::Screen,
        "div " => BlendMode::ColorDodge,
        "lddg" => BlendMode::LinearDodge,
        "lgCl" => BlendMode::LighterColor,
        "over" => BlendMode::Overlay,
        "sLit" => BlendMode::SoftLight,
        "hLit" => BlendMode::HardLight,
        "vLit" => BlendMode::VividLight,
        "lLit" => BlendMode::LinearLight,
        "pLit" => BlendMode::PinLight,
        "hMix" => BlendMode::HardMix,
        "diff" => BlendMode::Difference,
        "smud" => BlendMode::Exclusion,
        "fsub" => BlendMode::Subtract,
        "fdiv" => BlendMode::Divide,
        "hue " => BlendMode::Hue,
        "sat " => BlendMode::Saturation,
        "colr" => BlendMode::Color,
        "lum " => BlendMode::Luminosity,
        _ => BlendMode::Normal,
    }
}
