# Floss

Digital painting and image editing for desktop.

<img src="assets/hero-workspace.png" alt="Floss workspace with artwork on canvas" width="800">

Floss is a painting app built for people who think in strokes, not menus. It supports pressure, speed, and tilt on every brush, a full layer system with folders and blend modes, and it opens PSD and KRA files with layers intact.

The brush engine is built around a node graph. You wire up shapes, noise, textures, and bristles, and the preview shows you exactly what hits the canvas. Save your favorites as presets.

Pan, zoom, rotate, flip. Work big, work close, work however you like.

## Platforms

- **Linux** - AppImage, Flatpak
- **Windows** - portable zip
- **macOS** - Apple Silicon and Intel

## Build

Requires the .NET 10 SDK.

```sh
dotnet restore
dotnet build
dotnet run --project src/Floss.App
```

## Tech

Floss is built with [Avalonia](https://avaloniaui.net/) and C# on .NET 10. It includes a plugin system if you want to extend it.

## License

Floss is source-available. The code is here to read, learn from, and fork, but commercial use and redistribution are restricted. See the [LICENSE](LICENSE) file for details.
