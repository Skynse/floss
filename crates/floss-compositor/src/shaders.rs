//! WGSL shaders for GPU compositing.
//!
//! Two shaders:
//! 1. `composite` — composites all layer tiles onto the viewport with blend modes
//! 2. `brush_stamp` — stamps a brush mask onto a target tile with color and opacity

/// WGSL vertex/fragment shader for compositing layer tiles onto the viewport.
///
/// Takes an array of layer metadata (visibility, opacity, blend mode, tile transform)
/// and composites them from bottom to top onto the output.
pub const COMPOSITE_SHADER: &str = r#"
// ── Composite vertex shader ────────────────────────────────────────────────
@vertex
fn vs_main(
    @location(0) pos: vec2<f32>,
    @location(1) uv: vec2<f32>,
) -> VertexOutput {
    var out: VertexOutput;
    out.position = vec4<f32>(pos, 0.0, 1.0);
    out.uv = uv;
    return out;
}

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
}

// ── Layer descriptor (one per layer, passed as uniform) ────────────────────
struct LayerInfo {
    visible: u32,       // bool
    opacity: f32,       // 0.0–1.0
    blend_mode: u32,    // 0=Normal, 1=Multiply, 2=Screen, 3=Overlay, etc.
    tile_offset: vec2<f32>,
}

// ── Composite fragment shader ──────────────────────────────────────────────
@group(0) @binding(0) var layer_texture: texture_2d<f32>;
@group(0) @binding(1) var layer_sampler: sampler;
@group(0) @binding(2) var<uniform> layer_info: LayerInfo;
@group(0) @binding(3) var background_texture: texture_2d<f32>;
@group(0) @binding(4) var background_sampler: sampler;

// Blend mode functions — operate on premultiplied alpha

fn blend_normal(base: vec4<f32>, src: vec4<f32>) -> vec4<f32> {
    return src + base * (1.0 - src.a);
}

fn blend_multiply(base: vec4<f32>, src: vec4<f32>) -> vec4<f32> {
    let result = base * src + base * (1.0 - src.a) + src * (1.0 - base.a);
    return vec4<f32>(result.rgb, base.a + src.a * (1.0 - base.a));
}

fn blend_screen(base: vec4<f32>, src: vec4<f32>) -> vec4<f32> {
    let inv = (1.0 - base) * (1.0 - src);
    let result = 1.0 - inv;
    return vec4<f32>(result.rgb, base.a + src.a * (1.0 - base.a));
}

fn blend_overlay(base: vec4<f32>, src: vec4<f32>) -> vec4<f32> {
    var result: vec3<f32>;
    for (var i = 0u; i < 3u; i++) {
        if (base[i] < 0.5) {
            result[i] = 2.0 * base[i] * src[i];
        } else {
            result[i] = 1.0 - 2.0 * (1.0 - base[i]) * (1.0 - src[i]);
        }
    }
    return vec4<f32>(result, base.a + src.a * (1.0 - base.a));
}

fn blend_erase(base: vec4<f32>, src: vec4<f32>) -> vec4<f32> {
    return vec4<f32>(base.rgb, base.a * (1.0 - src.a));
}

fn apply_blend(base: vec4<f32>, src: vec4<f32>, mode: u32) -> vec4<f32> {
    switch mode {
        case 0u: { return blend_normal(base, src); }
        case 1u: { return blend_multiply(base, src); }
        case 2u: { return blend_screen(base, src); }
        case 3u: { return blend_overlay(base, src); }
        case 11u: { return blend_erase(base, src); }
        default: { return blend_normal(base, src); }
    }
}

@fragment
fn fs_main(in: VertexOutput) -> @location(0) vec4<f32> {
    // Sample the layer tile
    let layer_uv = in.uv + layer_info.tile_offset;
    let src = textureSample(layer_texture, layer_sampler, layer_uv);

    // Sample the background (accumulated composite so far)
    let bg = textureSample(background_texture, background_sampler, in.uv);

    // Apply layer opacity
    let src_opaque = vec4<f32>(src.rgb, src.a * layer_info.opacity);

    // Apply blend mode
    return apply_blend(bg, src_opaque, layer_info.blend_mode);
}
"#;

/// WGSL compute shader for stamping a brush mask onto a tile.
///
/// Reads the brush mask texture and composites the brush color
/// onto the target tile using src-over blending.
pub const BRUSH_STAMP_SHADER: &str = r#"
// ── Brush stamp compute shader ──────────────────────────────────────────────

struct StampParams {
    offset_x: i32,      // offset within the tile where the stamp starts
    offset_y: i32,
    stamp_width: u32,
    stamp_height: u32,
    color: vec4<f32>,   // premultiplied brush color
    opacity: f32,
    blend_mode: u32,
}

@group(0) @binding(0) var stamp_mask: texture_2d<f32>;
@group(0) @binding(1) var target_tile: texture_storage_2d<rgba8unorm, read_write>;
@group(0) @binding(2) var<uniform> params: StampParams;

@compute @workgroup_size(8, 8)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let x = gid.x;
    let y = gid.y;

    // Bounds check
    if (x >= params.stamp_width || y >= params.stamp_height) {
        return;
    }

    // Read stamp mask alpha
    let mask_uv = vec2<f32>(
        f32(x) / f32(params.stamp_width),
        f32(y) / f32(params.stamp_height)
    );
    let mask = textureLoad(stamp_mask, vec2<i32>(i32(x), i32(y)), 0);

    let stamp_alpha = mask.a * params.opacity;
    if (stamp_alpha <= 0.0) {
        return;
    }

    // Read existing pixel from target tile
    let tx = i32(x) + params.offset_x;
    let ty = i32(y) + params.offset_y;
    let existing = textureLoad(target_tile, vec2<i32>(tx, ty));

    // Src-over composite
    let src = vec4<f32>(params.color.rgb, params.color.a * stamp_alpha);
    let out = src + existing * (1.0 - src.a);

    textureStore(target_tile, vec2<i32>(tx, ty), out);
}
"#;

/// Simple checkerboard background pattern shader.
pub const CHECKERBOARD_SHADER: &str = r#"
@vertex
fn vs_main(
    @location(0) pos: vec2<f32>,
    @location(1) uv: vec2<f32>,
) -> VertexOutput {
    var out: VertexOutput;
    out.position = vec4<f32>(pos, 0.0, 1.0);
    out.uv = uv;
    return out;
}

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
}

struct CheckerParams {
    zoom: f32,
    pan_x: f32,
    pan_y: f32,
    light_color: vec4<f32>,
    dark_color: vec4<f32>,
    checker_size: f32,  // pixels at zoom=1
}

@group(0) @binding(0) var<uniform> params: CheckerParams;

@fragment
fn fs_main(in: VertexOutput) -> @location(0) vec4<f32> {
    let scaled_x = (in.uv.x * params.zoom + params.pan_x) / params.checker_size;
    let scaled_y = (in.uv.y * params.zoom + params.pan_y) / params.checker_size;
    let cx = i32(floor(scaled_x));
    let cy = i32(floor(scaled_y));
    if ((cx + cy) & 1) == 0 {
        return params.light_color;
    } else {
        return params.dark_color;
    }
}
"#;
