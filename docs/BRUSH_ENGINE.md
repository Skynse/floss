# Brush Engine

Floss implements a highly customizable stamp-based brush engine designed to feel natural and responsive to tablet pressure/speed.

## Architecture Overview

The brush engine operates by tracking pointer movement, forming a curve, and "stamping" a brush tip along that curve at specific intervals.

1. **`StrokeState` & `BrushEngine`**: The core driver. The engine receives a stream of `CanvasInputSample` items. It smooths the path using `CubicCurve` interpolation to create fluid strokes.
2. **`StrokePoint`**: Represents an evaluated point along the curve, containing position, pressure, tilt, and speed.
3. **`BrushAsset` & `BrushPreset`**: Define the brush configuration. An asset represents the underlying definition, while a preset configures things like current size, opacity, and color.

## Dynamics & Sensors

A key part of the engine is `BrushDynamics`. This maps hardware sensors (like pressure) to visual brush parameters (like size and opacity).

- **`SensorType`**: Can be Pressure, Speed, Tilt, etc.
- **`ParameterDynamics`**: Configures how a sensor modifies a parameter.
- **`ToneCurve` / `CurveOption`**: A graphical curve that defines the exact response profile. For example, a curve might cause size to increase slowly at low pressure but ramp up quickly at high pressure.

## Brush Tips

The actual shape that gets stamped along the curve is determined by an `IBrushTip`.

- **`ProceduralBrushTip`**: Generates a tip mathematically (e.g., a soft circle or hard ellipse) using hardness, anti-aliasing, and ratio calculations.
- **`ImageBrushTip`**: Uses a bitmap image (like a scatter or bristle texture) to act as the stamp.
- **`CompoundBrushTip`**: Allows combining multiple tips, often used for adding a texture mask over a primary tip.

## Rendering a Stroke

When a tool calls the `BrushEngine` to apply a stroke:
1. `BrushEngine` receives a new pointer coordinate.
2. It generates points interpolated by the `CubicCurve` to ensure the spacing constraint (e.g., 10% of brush size) is met exactly, preventing "stepped" artifacts.
3. For each `StrokePoint`, it evaluates the `BrushDynamics` to determine the final size, opacity, and rotation of the stamp.
4. It calls `IBrushTip.Stamp()` or blends the tip onto the `TiledPixelBuffer` using the active blend mode and color.
