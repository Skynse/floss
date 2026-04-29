use crate::brush::BrushPreset;
use crate::geometry::{CanvasPoint, CanvasRect};

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct StrokeSample {
    pub x: f32,
    pub y: f32,
    pub pressure: f32,
    pub velocity: f32,
    pub time_micros: i64,
    pub pointer: i64,
}

impl StrokeSample {
    pub fn position(self) -> CanvasPoint {
        CanvasPoint {
            x: self.x,
            y: self.y,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct Stroke {
    pub id: u64,
    pub brush: BrushPreset,
    pub raw_samples: Vec<StrokeSample>,
    pub render_samples: Vec<StrokeSample>,
    pub bounds: CanvasRect,
}

impl Stroke {
    pub fn new(id: u64, brush: BrushPreset, raw_samples: &[StrokeSample]) -> Self {
        let spacing = (brush.size * brush.spacing).max(1.0);
        let render_samples = resample_catmull_rom(raw_samples, spacing);
        let bounds = bounds_for_samples(&render_samples, brush.size);
        Self {
            id,
            brush,
            raw_samples: raw_samples.to_vec(),
            render_samples,
            bounds,
        }
    }
}

pub fn bounds_for_samples(samples: &[StrokeSample], brush_size: f32) -> CanvasRect {
    let Some(first) = samples.first() else {
        return CanvasRect::empty();
    };

    let mut bounds = CanvasRect::from_point(first.position());
    for sample in samples.iter().skip(1) {
        bounds.include_point(sample.position());
    }
    bounds.inflate(brush_size * 0.75)
}

pub fn resample_catmull_rom(samples: &[StrokeSample], spacing: f32) -> Vec<StrokeSample> {
    if samples.len() < 2 {
        return samples.to_vec();
    }

    let mut output = Vec::with_capacity(samples.len() * 2);
    output.push(samples[0]);

    for i in 0..samples.len() - 1 {
        let p0 = samples[clamped_index(i as isize - 1, samples.len())];
        let p1 = samples[i];
        let p2 = samples[i + 1];
        let p3 = samples[clamped_index(i as isize + 2, samples.len())];
        let distance = p1.position().distance_to(p2.position());
        let steps = ((distance / spacing.max(1.0)).ceil() as usize).clamp(1, 96);

        for step in 1..=steps {
            let t = step as f32 / steps as f32;
            let position = catmull_rom(
                p0.position(),
                p1.position(),
                p2.position(),
                p3.position(),
                t,
            );
            let time_micros = lerp_i64(p1.time_micros, p2.time_micros, t);
            let pressure = lerp(p1.pressure, p2.pressure, t);

            // Compute velocity: px/sec from previous sample
            let prev = *output.last().unwrap();
            let dist = prev.position().distance_to(position);
            let time_delta_micros = (time_micros - prev.time_micros).max(1);
            let velocity = (dist * 1_000_000.0) / time_delta_micros as f32;

            output.push(StrokeSample {
                x: position.x,
                y: position.y,
                pressure,
                velocity,
                time_micros,
                pointer: p2.pointer,
            });
        }
    }

    output
}

fn clamped_index(index: isize, len: usize) -> usize {
    index.clamp(0, len.saturating_sub(1) as isize) as usize
}

fn catmull_rom(
    p0: CanvasPoint,
    p1: CanvasPoint,
    p2: CanvasPoint,
    p3: CanvasPoint,
    t: f32,
) -> CanvasPoint {
    let t2 = t * t;
    let t3 = t2 * t;
    CanvasPoint {
        x: 0.5
            * ((2.0 * p1.x)
                + (-p0.x + p2.x) * t
                + (2.0 * p0.x - 5.0 * p1.x + 4.0 * p2.x - p3.x) * t2
                + (-p0.x + 3.0 * p1.x - 3.0 * p2.x + p3.x) * t3),
        y: 0.5
            * ((2.0 * p1.y)
                + (-p0.y + p2.y) * t
                + (2.0 * p0.y - 5.0 * p1.y + 4.0 * p2.y - p3.y) * t2
                + (-p0.y + 3.0 * p1.y - 3.0 * p2.y + p3.y) * t3),
    }
}

fn lerp(a: f32, b: f32, t: f32) -> f32 {
    a + (b - a) * t
}

fn lerp_i64(a: i64, b: i64, t: f32) -> i64 {
    a + ((b - a) as f32 * t).round() as i64
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resamples_curved_strokes() {
        let samples = vec![
            sample(0.0, 0.0),
            sample(80.0, 4.0),
            sample(160.0, 40.0),
            sample(220.0, 120.0),
        ];

        let resampled = resample_catmull_rom(&samples, 4.0);

        assert!(resampled.len() > samples.len());
    }

    fn sample(x: f32, y: f32) -> StrokeSample {
        StrokeSample {
            x,
            y,
            pressure: 1.0,
            velocity: 0.0,
            time_micros: 0,
            pointer: 1,
        }
    }
}
