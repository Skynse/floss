mod app;
mod tablet;
mod widgets;

use std::path::PathBuf;

fn main() -> eframe::Result<()> {
    let args: Vec<String> = std::env::args().collect();
    let psd_path = args.get(1).map(PathBuf::from);

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1280.0, 800.0])
            .with_min_inner_size([1000.0, 650.0]),
        ..Default::default()
    };

    eframe::run_native(
        "Floss",
        options,
        Box::new(move |cc| {
            widgets::install_fonts(&cc.egui_ctx);
            Ok(Box::new(app::FlossApp::new(psd_path)))
        }),
    )
}
