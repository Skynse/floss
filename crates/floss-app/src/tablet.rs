use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Arc;

/// Spawn a background thread that reads tablet pressure from evdev.
/// Returns an `Arc<AtomicU32>` containing the latest normalized pressure (0.0–1.0 as f32 bits).
pub fn spawn() -> Arc<AtomicU32> {
    let pressure = Arc::new(AtomicU32::new(f32::to_bits(1.0)));
    let pressure_bg = Arc::clone(&pressure);
    std::thread::spawn(move || {
        use evdev::{AbsoluteAxisType, InputEventKind};
        let Some((_path, mut device)) = evdev::enumerate().find(|(_, d)| {
            d.supported_absolute_axes()
                .map_or(false, |axes| axes.contains(AbsoluteAxisType::ABS_PRESSURE))
        }) else {
            eprintln!("[tablet] no pressure-capable evdev device found");
            return;
        };
        let (abs_min, abs_max) = device
            .get_abs_state().ok()
            .and_then(|s| {
                let info = s.get(AbsoluteAxisType::ABS_PRESSURE.0 as usize)?;
                Some((info.minimum as f32, info.maximum as f32))
            })
            .unwrap_or((0.0, 4095.0));
        let range = (abs_max - abs_min).max(1.0);
        loop {
            match device.fetch_events() {
                Ok(evs) => {
                    for ev in evs {
                        if let InputEventKind::AbsAxis(AbsoluteAxisType::ABS_PRESSURE) = ev.kind() {
                            let norm = ((ev.value() as f32 - abs_min) / range).clamp(0.0, 1.0);
                            pressure_bg.store(f32::to_bits(norm), Ordering::Relaxed);
                        }
                    }
                }
                Err(_) => std::thread::sleep(std::time::Duration::from_secs(1)),
            }
        }
    });
    pressure
}
