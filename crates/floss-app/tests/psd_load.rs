//! Integration test: load a real PSD file and verify the compositor output.
//!
//! Uses `260426.psd` — a 2762×4743 RGB PSD with layers.
//! Run with: `cargo test -p floss-app --test psd_load -- --nocapture`

use std::path::PathBuf;

use floss_compositor::LayerCompositor;
use floss_core::Color;
use floss_document::DrawingDocument;

#[test]
fn test_load_and_composite_psd() {
    let path = PathBuf::from("/home/neckles/Downloads/260426.psd");
    if !path.exists() {
        eprintln!("SKIP: PSD file not found at {}", path.display());
        return;
    }

    let file = std::fs::File::open(&path).expect("open PSD");
    let mut reader = std::io::BufReader::new(file);
    let psd = floss_psd::reader::read_psd(&mut reader).expect("parse PSD");

    assert_eq!(psd.width, 2762);
    assert_eq!(psd.height, 4743);
    assert!(!psd.layers.is_empty(), "PSD should have layers");

    let mut doc = floss_psd::import_psd(psd);
    assert_eq!(doc.width(), 2762);
    assert_eq!(doc.height(), 4743);
    assert!(doc.layer_count() > 0, "Document should have layers");

    // Count groups, layers, and clipping layers
    let (mut groups, mut paint_layers, mut clips) = (0, 0, 0);
    for li in 0..doc.layer_count() {
        let l = doc.layer(li);
        if l.is_group { groups += 1; }
        if l.is_clipping { clips += 1; }
        if !l.is_group && !l.is_paper { paint_layers += 1; }
    }
    eprintln!("Loaded PSD: {} layers ({paint_layers} paint, {groups} groups, {clips} clips)", doc.layer_count());

    // Debug: dump layer info
    for li in 0..doc.layer_count() {
        let l = doc.layer(li);
        eprintln!(
            "  L{li}: '{}' visible={} group={} clip={} opacity={:.3} blend={:?} off=({},{}) parent={} tiles={}",
            l.name, l.visible, l.is_group, l.is_clipping,
            l.opacity, l.blend_mode,
            l.offset_x, l.offset_y, l.parent_group,
            l.pixels.tile_count()
        );
    }

    // Composite
    let mut comp = LayerCompositor::new(doc.width(), doc.height());
    let out = comp.composite(&mut doc);
    assert_eq!(out.len(), (2762 * 4743 * 4) as usize);

    // Check that output is not all paper/transparent
    let mut non_zero = 0;
    for chunk in out.chunks_exact(4).take(10000) {
        if chunk[3] != 0 { non_zero += 1; }
    }
    assert!(non_zero > 0, "Composite should have non-alpha pixels");

    // Sample a few known positions
    let sample = |x: usize, y: usize| -> (u8, u8, u8, u8) {
        let i = (y * 2762 + x) * 4;
        (out[i + 2], out[i + 1], out[i], out[i + 3])
    };

    // Just check that edges are not all the same color (has content)
    let (r, g, b, a) = sample(1381, 2371); // center
    eprintln!("Center pixel (1381,2371): R={r} G={g} B={b} A={a}");

    let (r2, g2, b2, a2) = sample(0, 0);
    eprintln!("Top-left (0,0): R={r2} G={g2} B={b2} A={a2}");

    // Verify the output buffer has content (not just solid paper color)
    let mut colors = std::collections::BTreeSet::new();
    for y in (0..4743).step_by(10) {
        for x in (0..2762).step_by(13) {
            let i = (y * 2762 + x) * 4;
            if i + 3 < out.len() {
                colors.insert((out[i], out[i + 1], out[i + 2], out[i + 3]));
            }
        }
    }
    assert!(colors.len() > 1000, "Composite output should have variation, not just one solid color. Got {} unique colors.", colors.len());

    eprintln!("PSD integration test PASSED");
}
