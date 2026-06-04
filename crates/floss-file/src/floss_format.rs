//! Floss native file format (`.floss`) — a ZIP archive with per-layer tile data.
//!
//! Ported from `Floss.App.FlossFiles.FlossFileFormat.cs`.
//!
//! ## Format
//!
//! A `.floss` file is a standard ZIP archive containing:
//!
//! ```text
//! mimetype           — "application/x-floss" (uncompressed)
//! document.json      — manifest (canvas size, paper color, layer metadata)
//! layers/layer0.bgra — per-layer tile data: [tx: i32][ty: i32][tile: 16384 bytes]...
//! mergedimage.png    — full canvas composite
//! preview.png        — thumbnail (max 512px)
//! ```

use std::collections::HashMap;
use std::io::{self, Cursor, Read, Write};

use floss_core::{BlendMode, Color};
use floss_document::{DrawingDocument, DrawingLayer, ExpressionColorMode};
use serde::{Deserialize, Serialize};

/// File extension for Floss documents.
pub const EXTENSION: &str = ".floss";
const MIME_TYPE: &str = "application/x-floss";
const TILE_BYTES: usize = 64 * 64 * 4; // 16384

// ── Manifest JSON ────────────────────────────────────────────────────────

#[derive(Serialize, Deserialize)]
struct FlossManifest {
    #[serde(rename = "mimetype")]
    mime_type: String,
    #[serde(rename = "formatVersion", default)]
    format_version: i32,
    #[serde(default)]
    app: String,
    width: i32,
    height: i32,
    #[serde(rename = "activeLayerIndex", default)]
    active_layer_index: usize,
    #[serde(rename = "paperColor", default, skip_serializing_if = "Option::is_none")]
    paper_color: Option<ManifestColor>,
    layers: Vec<LayerInfo>,
}

#[derive(Clone, Copy, Serialize, Deserialize)]
struct ManifestColor {
    r: u8,
    g: u8,
    b: u8,
    a: u8,
}

#[derive(Serialize, Deserialize)]
struct LayerInfo {
    name: String,
    #[serde(rename = "isVisible", default = "default_true")]
    visible: bool,
    #[serde(rename = "isLocked", default)]
    locked: bool,
    #[serde(rename = "blendMode")]
    blend_mode: String,
    opacity: f64,
    #[serde(rename = "isGroup")]
    is_group: bool,
    #[serde(rename = "offsetX", default)]
    offset_x: i32,
    #[serde(rename = "offsetY", default)]
    offset_y: i32,
    #[serde(default)]
    width: i32,
    #[serde(default)]
    height: i32,
    #[serde(rename = "isOpen", default = "default_true")]
    is_open: bool,
    #[serde(rename = "isClipping", default)]
    is_clipping: bool,
    #[serde(rename = "isReference")]
    is_reference: bool,
    #[serde(rename = "isPaper", default)]
    is_paper: bool,
    #[serde(rename = "indentLevel", default)]
    indent_level: i32,
    #[serde(rename = "parentIndex", default)]
    parent_index: Option<i32>,
    #[serde(rename = "parentGroup", default)]
    parent_group: Option<i32>,
    #[serde(rename = "pixelPath", default)]
    pixel_path: Option<String>,
    #[serde(rename = "layerColor", default)]
    layer_color: Option<ManifestColor>,
    #[serde(rename = "expressionColor", default)]
    expression_color: Option<String>,
}

fn default_true() -> bool { true }

fn blend_mode_to_string(bm: BlendMode) -> &'static str {
    match bm {
        BlendMode::Normal => "Normal",
        BlendMode::PassThrough => "PassThrough",
        BlendMode::Dissolve => "Dissolve",
        BlendMode::Multiply => "Multiply",
        BlendMode::Screen => "Screen",
        BlendMode::Overlay => "Overlay",
        BlendMode::SoftLight => "SoftLight",
        BlendMode::HardLight => "HardLight",
        BlendMode::ColorDodge => "ColorDodge",
        BlendMode::ColorBurn => "ColorBurn",
        BlendMode::EasyDodge => "EasyDodge",
        BlendMode::Darken => "Darken",
        BlendMode::Lighten => "Lighten",
        BlendMode::Difference => "Difference",
        BlendMode::Exclusion => "Exclusion",
        BlendMode::LinearBurn => "LinearBurn",
        BlendMode::LinearDodge => "LinearDodge",
        BlendMode::VividLight => "VividLight",
        BlendMode::LinearLight => "LinearLight",
        BlendMode::PinLight => "PinLight",
        BlendMode::HardMix => "HardMix",
        BlendMode::Subtract => "Subtract",
        BlendMode::Divide => "Divide",
        BlendMode::DarkerColor => "DarkerColor",
        BlendMode::LighterColor => "LighterColor",
        BlendMode::Hue => "Hue",
        BlendMode::Saturation => "Saturation",
        BlendMode::Color => "Color",
        BlendMode::Luminosity => "Luminosity",
        BlendMode::NormalAlphaPreserving => "NormalAlphaPreserving",
        BlendMode::Erase => "Erase",
        BlendMode::SrcOver => "SrcOver",
        BlendMode::DstOut => "DstOut",
        BlendMode::Clear => "Clear",
        BlendMode::Pigment => "Pigment",
        BlendMode::PigmentAlpha => "PigmentAlpha",
        BlendMode::PigmentAndEraser => "PigmentAndEraser",
        BlendMode::OklabNormal => "OklabNormal",
        BlendMode::OklabNormalAndEraser => "OklabNormalAndEraser",
        BlendMode::OklabRecolor => "OklabRecolor",
    }
}

fn blend_mode_from_string(s: &str) -> BlendMode {
    match s {
        "PassThrough" => BlendMode::PassThrough,
        "Dissolve" => BlendMode::Dissolve,
        "Multiply" => BlendMode::Multiply,
        "Screen" => BlendMode::Screen,
        "Overlay" => BlendMode::Overlay,
        "SoftLight" => BlendMode::SoftLight,
        "HardLight" => BlendMode::HardLight,
        "ColorDodge" => BlendMode::ColorDodge,
        "ColorBurn" => BlendMode::ColorBurn,
        "EasyDodge" => BlendMode::EasyDodge,
        "Darken" => BlendMode::Darken,
        "Lighten" => BlendMode::Lighten,
        "Difference" => BlendMode::Difference,
        "Exclusion" => BlendMode::Exclusion,
        "LinearBurn" => BlendMode::LinearBurn,
        "LinearDodge" => BlendMode::LinearDodge,
        "VividLight" => BlendMode::VividLight,
        "LinearLight" => BlendMode::LinearLight,
        "PinLight" => BlendMode::PinLight,
        "HardMix" => BlendMode::HardMix,
        "Subtract" => BlendMode::Subtract,
        "Divide" => BlendMode::Divide,
        "DarkerColor" => BlendMode::DarkerColor,
        "LighterColor" => BlendMode::LighterColor,
        "Hue" => BlendMode::Hue,
        "Saturation" => BlendMode::Saturation,
        "Color" => BlendMode::Color,
        "Luminosity" => BlendMode::Luminosity,
        "NormalAlphaPreserving" => BlendMode::NormalAlphaPreserving,
        "Erase" => BlendMode::Erase,
        "SrcOver" => BlendMode::SrcOver,
        "DstOut" => BlendMode::DstOut,
        "Clear" => BlendMode::Clear,
        "Pigment" => BlendMode::Pigment,
        "PigmentAlpha" => BlendMode::PigmentAlpha,
        "PigmentAndEraser" => BlendMode::PigmentAndEraser,
        "OklabNormal" => BlendMode::OklabNormal,
        "OklabNormalAndEraser" => BlendMode::OklabNormalAndEraser,
        "OklabRecolor" => BlendMode::OklabRecolor,
        _ => BlendMode::Normal,
    }
}

// ── Save ─────────────────────────────────────────────────────────────────

/// Save a document to a `.floss` file.
pub fn save(writer: impl Write + io::Seek, doc: &mut DrawingDocument) -> Result<(), io::Error> {
    let mut archive = zip::ZipWriter::new(writer);

    let options = zip::write::SimpleFileOptions::default();

    // mimetype (uncompressed, must be first)
    archive.start_file("mimetype", options.compression_method(zip::CompressionMethod::Stored))?;
    archive.write_all(MIME_TYPE.as_bytes())?;

    // document.json
    let manifest = build_manifest(doc);
    let manifest_json = serde_json::to_string_pretty(&manifest)
        .map_err(|e| io::Error::new(io::ErrorKind::Other, e))?;
    archive.start_file(
        "document.json",
        options.compression_method(zip::CompressionMethod::Deflated),
    )?;
    archive.write_all(manifest_json.as_bytes())?;

    // Per-layer tile data
    for i in 0..doc.layer_count() {
        let layer = doc.layer(i);
        if layer.is_group {
            continue;
        }
        let tiles = doc.layer_mut(i).pixels.capture_tiles();
        if tiles.is_empty() {
            continue;
        }
        archive.start_file(
            format!("layers/layer{}.bgra", i),
            options.compression_method(zip::CompressionMethod::Deflated),
        )?;

        let mut tx_buf = [0u8; 4];
        let mut ty_buf = [0u8; 4];
        for ((tx, ty), tile) in &tiles {
            tx_buf.copy_from_slice(&tx.to_le_bytes());
            ty_buf.copy_from_slice(&ty.to_le_bytes());
            archive.write_all(&tx_buf)?;
            archive.write_all(&ty_buf)?;
            archive.write_all(tile.as_ref())?;
        }
    }

    // Merged image (full canvas composite)
    if let Some(png) = render_merged_image(doc, None) {
        archive.start_file(
            "mergedimage.png",
            options.compression_method(zip::CompressionMethod::Stored),
        )?;
        archive.write_all(&png)?;
    }

    // Preview thumbnail (max 512px)
    if let Some(png) = render_merged_image(doc, Some(512)) {
        archive.start_file(
            "preview.png",
            options.compression_method(zip::CompressionMethod::Stored),
        )?;
        archive.write_all(&png)?;
    }

    archive.finish()?;
    Ok(())
}

fn build_manifest(doc: &DrawingDocument) -> FlossManifest {
    FlossManifest {
        mime_type: MIME_TYPE.to_string(),
        format_version: 1,
        app: "Floss".to_string(),
        width: doc.width(),
        height: doc.height(),
        active_layer_index: doc.active_layer_index(),
        paper_color: Some(ManifestColor::from_color(doc.paper_color())),
        layers: (0..doc.layer_count())
            .map(|i| {
                let layer = doc.layer(i);
                LayerInfo {
                    name: layer.name.clone(),
                    visible: layer.visible,
                    locked: layer.locked,
                    blend_mode: blend_mode_to_string(layer.blend_mode).to_string(),
                    opacity: layer.opacity,
                    is_group: layer.is_group,
                    offset_x: layer.offset_x,
                    offset_y: layer.offset_y,
                    width: layer.pixels.width(),
                    height: layer.pixels.height(),
                    is_open: layer.is_open,
                    is_clipping: layer.is_clipping,
                    is_reference: layer.is_reference,
                    is_paper: layer.is_paper,
                    indent_level: layer.indent_level,
                    parent_index: (layer.parent_group >= 0).then_some(layer.parent_group),
                    parent_group: Some(layer.parent_group),
                    pixel_path: (!layer.is_group).then(|| format!("layers/layer{}.bgra", i)),
                    layer_color: layer.layer_color.map(ManifestColor::from_rgba),
                    expression_color: Some(expression_color_to_string(layer.expression_color).to_string()),
                }
            })
            .collect(),
    }
}

fn render_merged_image(doc: &mut DrawingDocument, max_side: Option<u32>) -> Option<Vec<u8>> {
    let w = doc.width() as u32;
    let h = doc.height() as u32;

    let (out_w, out_h) = if let Some(max) = max_side {
        let scale = (max as f64 / w.max(h) as f64).min(1.0);
        ((w as f64 * scale) as u32, (h as f64 * scale) as u32)
    } else {
        (w, h)
    };

    let mut rgba = vec![0u8; w as usize * h as usize * 4];

    // Composite all visible layers bottom-to-top
    for i in 0..doc.layer_count() {
        let opacity = {
            let layer = doc.layer(i);
            if !layer.visible || layer.is_group {
                continue;
            }
            layer.opacity
        };
        doc.layer_mut(i)
            .pixels
            .blend_onto(&mut rgba, w as i32, h as i32, opacity);
    }

    // Convert BGRA → RGBA for PNG encoding
    for px in rgba.chunks_exact_mut(4) {
        let b = px[0];
        let r = px[2];
        px[0] = r;
        px[2] = b;
    }

    let img = image::RgbaImage::from_raw(w, h, rgba)?;
    let mut out = Cursor::new(Vec::new());

    if out_w != w || out_h != h {
        let resized = image::imageops::resize(
            &img,
            out_w,
            out_h,
            image::imageops::FilterType::Lanczos3,
        );
        resized
            .write_to(&mut out, image::ImageFormat::Png)
            .ok()?;
    } else {
        img.write_to(&mut out, image::ImageFormat::Png).ok()?;
    }

    Some(out.into_inner())
}

// ── Load ─────────────────────────────────────────────────────────────────

/// Load a document from a `.floss` file.
pub fn load(reader: impl Read + io::Seek) -> Result<DrawingDocument, io::Error> {
    let mut archive = zip::ZipArchive::new(reader)?;

    // Read manifest
    let manifest_entry = archive
        .by_name("document.json")
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "Missing document.json"))?;
    let manifest: FlossManifest = serde_json::from_reader(manifest_entry)
        .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;

    if manifest.mime_type != MIME_TYPE {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "File is not a Floss document",
        ));
    }

    let paper_color = manifest
        .paper_color
        .map(ManifestColor::to_color)
        .unwrap_or_else(|| Color::from_bytes(255, 255, 255, 255));
    let mut layers = Vec::with_capacity(manifest.layers.len());

    for (i, info) in manifest.layers.iter().enumerate() {
        let mut layer = DrawingLayer::new(
            if info.name.trim().is_empty() {
                format!("Layer {}", i + 1)
            } else {
                info.name.clone()
            },
            info.width.max(1).max(manifest.width.max(1)),
            info.height.max(1).max(manifest.height.max(1)),
        );
        layer.name = info.name.clone();
        layer.visible = info.visible;
        layer.locked = info.locked;
        layer.blend_mode = blend_mode_from_string(&info.blend_mode);
        layer.opacity = info.opacity;
        layer.is_group = info.is_group;
        layer.offset_x = info.offset_x;
        layer.offset_y = info.offset_y;
        layer.is_open = info.is_open;
        layer.is_clipping = info.is_clipping;
        layer.is_reference = info.is_reference;
        layer.is_paper = info.is_paper;
        layer.indent_level = info.indent_level.max(0);
        layer.parent_group = info.parent_index.or(info.parent_group).unwrap_or(-1);
        layer.layer_color = info.layer_color.as_ref().map(|c| [c.r, c.g, c.b, c.a]);
        layer.expression_color = info.expression_color.as_deref().map(expression_color_from_string).unwrap_or(ExpressionColorMode::Color);

        if info.is_group {
            layers.push(layer);
            continue;
        }

        // Load tile data
        let entry_name = info.pixel_path.clone().unwrap_or_else(|| format!("layers/layer{}.bgra", i));
        if let Ok(entry) = archive.by_name(&entry_name) {
            let mut data = Vec::new();
            let mut reader = std::io::BufReader::new(entry);
            reader.read_to_end(&mut data)?;

            let mut cursor = Cursor::new(&data);
            let mut tiles: HashMap<(i32, i32), Box<[u8; TILE_BYTES]>> = HashMap::new();

            loop {
                let mut tx_buf = [0u8; 4];
                let mut ty_buf = [0u8; 4];
                if cursor.read_exact(&mut tx_buf).is_err()
                    || cursor.read_exact(&mut ty_buf).is_err()
                {
                    break;
                }
                let tx = i32::from_le_bytes(tx_buf);
                let ty = i32::from_le_bytes(ty_buf);

                let mut tile = Box::new([0u8; TILE_BYTES]);
                if cursor.read_exact(tile.as_mut()).is_err() {
                    break;
                }
                tiles.insert((tx, ty), tile);
            }

            layer.pixels.restore_tiles(tiles);
        }

        layers.push(layer);
    }

    let mut doc = DrawingDocument::new(manifest.width.max(1), manifest.height.max(1));
    doc.replace_for_import(
        manifest.width.max(1),
        manifest.height.max(1),
        paper_color,
        layers,
        manifest.active_layer_index,
    );
    Ok(doc)
}

impl ManifestColor {
    fn from_color(color: Color) -> Self {
        Self {
            r: (color.r() * 255.0).round() as u8,
            g: (color.g() * 255.0).round() as u8,
            b: (color.b() * 255.0).round() as u8,
            a: (color.a() * 255.0).round() as u8,
        }
    }

    fn from_rgba(rgba: [u8; 4]) -> Self {
        Self { r: rgba[0], g: rgba[1], b: rgba[2], a: rgba[3] }
    }

    fn to_color(self) -> Color {
        Color::from_bytes(self.r, self.g, self.b, self.a)
    }
}

fn expression_color_to_string(mode: ExpressionColorMode) -> &'static str {
    match mode {
        ExpressionColorMode::Color => "Color",
        ExpressionColorMode::Gray => "Gray",
        ExpressionColorMode::Monochrome => "Monochrome",
    }
}

fn expression_color_from_string(value: &str) -> ExpressionColorMode {
    match value {
        "Gray" => ExpressionColorMode::Gray,
        "Monochrome" => ExpressionColorMode::Monochrome,
        _ => ExpressionColorMode::Color,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trip_basic_document() {
        let mut doc = DrawingDocument::new(256, 256);
        doc.active_layer_mut()
            .pixels
            .set_pixel(10, 20, 255, 0, 0, 128);
        doc.active_layer_mut()
            .pixels
            .set_pixel(50, 60, 0, 255, 0, 200);

        let mut buf = Cursor::new(Vec::new());
        save(&mut buf, &mut doc).unwrap();

        buf.set_position(0);
        let loaded = load(&mut buf).unwrap();

        assert_eq!(loaded.width(), 256);
        assert_eq!(loaded.height(), 256);
        assert_eq!(loaded.layer_count(), 1);

        let px = loaded.layer(0).pixels.try_read_pixel(10, 20);
        // BGRA: we set B=255, G=0, R=0, A=128
        assert_eq!(px[0], 255); // B
        assert_eq!(px[1], 0); // G
        assert_eq!(px[2], 0); // R
        assert_eq!(px[3], 128); // A
    }

    #[test]
    fn round_trip_multiple_layers() {
        let mut doc = DrawingDocument::new(128, 128);
        doc.add_layer();
        doc.add_layer();
        assert_eq!(doc.layer_count(), 3);

        doc.layer_mut(0)
            .pixels
            .set_pixel(5, 5, 0, 0, 255, 255);
        doc.layer_mut(2)
            .pixels
            .set_pixel(10, 10, 255, 0, 0, 255);

        let mut buf = Cursor::new(Vec::new());
        save(&mut buf, &mut doc).unwrap();

        buf.set_position(0);
        let loaded = load(&mut buf).unwrap();

        assert_eq!(loaded.layer_count(), 3);
        assert_eq!(loaded.layer(0).name, "Background");
    }

    #[test]
    fn round_trip_document_metadata() {
        let mut doc = DrawingDocument::new(64, 64);
        doc.set_paper_color(Color::from_bytes(12, 34, 56, 255));
        doc.add_background_layer();
        doc.set_active_layer(0);
        doc.layer_mut(0).offset_x = 9;
        doc.layer_mut(0).offset_y = -4;
        doc.layer_mut(0).is_clipping = true;
        doc.layer_mut(0).layer_color = Some([1, 2, 3, 255]);
        doc.layer_mut(0).expression_color = ExpressionColorMode::Gray;

        let mut buf = Cursor::new(Vec::new());
        save(&mut buf, &mut doc).unwrap();

        buf.set_position(0);
        let loaded = load(&mut buf).unwrap();

        assert_eq!(loaded.active_layer_index(), 0);
        assert_eq!(loaded.paper_color(), Color::from_bytes(12, 34, 56, 255));
        assert!(loaded.layer(0).is_paper);
        assert_eq!(loaded.layer(0).offset_x, 9);
        assert_eq!(loaded.layer(0).offset_y, -4);
        assert!(loaded.layer(0).is_clipping);
        assert_eq!(loaded.layer(0).layer_color, Some([1, 2, 3, 255]));
        assert_eq!(loaded.layer(0).expression_color, ExpressionColorMode::Gray);
    }
}
