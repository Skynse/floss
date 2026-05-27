# Floss

Floss is now an Avalonia/C# desktop drawing app scaffold.

## Current Groundwork

- Avalonia desktop app targeting `net10.0`.
- Custom `DrawingCanvas` backed by an `Avalonia.Media.Imaging.WriteableBitmap`.
- Pressure-aware brush samples from Avalonia pointer events.
- Brush presets, color swatches, brush sliders, eraser mode, undo/redo, clear.
- Keyboard shortcuts: `B`, `E`, `[`, `]`, `Ctrl+Z`, `Ctrl+Y`, `Ctrl+Shift+Z`, `Ctrl+0`.
- Workspace pan with hold-space drag, zoom with `Ctrl+MouseWheel`.

## Build

```sh
dotnet restore
dotnet build
dotnet run --project src/Floss.App
```

## Performance Direction

The first canvas backend is CPU raster into one `WriteableBitmap`. That is not the final architecture. The next pass should split the document into dirty tiles, update only changed tile regions, and introduce a real tool/brush pipeline before adding layers and transforms.

# Builds (Windows)

Windows builds require the **Visual C++ Redistributable for Visual Studio 2015–2022**. If you get "side-by-side configuration is incorrect", install [vc_redist.x64.exe](https://aka.ms/vs/17/release/vc_redist.x64.exe).

dotnet publish src/Floss.App/Floss.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o artifacts/floss-win-x64-compact

# Builds (linux)

dotnet publish src/Floss.App/Floss.App.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o artifacts/floss-linux-x64-compact
