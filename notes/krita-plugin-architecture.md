# Krita feature / plugin architecture

Reference: `/home/neckles/projects/krita`

## Short answer

**Both.** Krita is a hybrid:

1. **Core engine + UI shell** live in `libs/` and are compiled into the main binary. These are hard-coded (document model, strokes, compositing, view manager, main window).
2. **Most user-facing features** are shipped as **in-tree C++ plugins** under `plugins/` — separate `.so` modules discovered at runtime via KDE's plugin system, but built and installed with Krita itself.
3. **Optional Python extensions** (`plugins/python/`, user-installed `.desktop` + `.py`) are true runtime plugins for dockers, scripts, and utilities.

There is no single "everything is a plugin" model. There is also no "everything is hard-coded in main.cpp" model.

---

## Directory layout

| Path | Role |
|------|------|
| `libs/` | Core libraries linked into `krita` executable |
| `libs/image/` | Document, layers, tiles, filters registry, paintop registry, strokes |
| `libs/ui/` | Main window, view manager, canvas, actions, tool manager |
| `libs/flake/` | Tool registry, dock registry, shape registry |
| `libs/koplugin/` | `KoPluginLoader` — loads `.json` + `.so` plugins |
| `plugins/` | Feature modules built as `MODULE` shared libs |
| `plugins/python/` | PyQt dockers/scripts installed to `share/krita/pykrita` |
| `qmlmodules/` | Shared QML UI components (not feature plugins) |

---

## How C++ "plugins" work

### Discovery

`KoPluginLoader` (`libs/koplugin/KoPluginLoader.cpp`) queries plugins by **service type** string (e.g. `"Krita/Tool"`) using `KoJsonTrader`. Each plugin ships a JSON manifest:

```json
// plugins/paintops/defaultpaintops/kritadefaultpaintops.json
{
    "Id": "Default Paint Operations",
    "Type": "Service",
    "X-KDE-Library": "kritadefaultpaintops",
    "X-KDE-ServiceTypes": [ "Krita/Paintop" ],
    "X-Krita-Version": "28"
}
```

Built as `kis_add_library(... MODULE ...)` and installed to `KRITA_PLUGIN_INSTALL_DIR`.

### Registration pattern

Plugin constructor runs once at load time, registers factories into a singleton registry, then the plugin object is often destroyed:

From `KoPluginLoader.h` docs:
> Inside the default constructor you can create whatever object you want and add it to whatever registry you prefer. After having been constructed, your plugin will be deleted.

**Paintops** — `plugins/paintops/defaultpaintops/defaultpaintops_plugin.cc`:

```cpp
K_PLUGIN_FACTORY_WITH_JSON(..., registerPlugin<DefaultPaintOpsPlugin>());

DefaultPaintOpsPlugin::DefaultPaintOpsPlugin(...) {
    KisPaintOpRegistry *r = KisPaintOpRegistry::instance();
    r->add(new KisSimplePaintOpFactory<KisBrushOp, ...>("paintbrush", ...));
    r->add(new KisSimplePaintOpFactory<KisDuplicateOp, ...>("duplicate", ...));
}
```

Registry init triggers load: `libs/image/brushengine/kis_paintop_registry.cc`:

```cpp
void KisPaintOpRegistry::initRegistry() {
    KoPluginLoader::instance()->load("Krita/Paintop");
}
```

**Tools** — `plugins/tools/basictools/default_tools.cc`:

```cpp
DefaultTools::DefaultTools(...) {
    KoToolRegistry::instance()->add(new KisToolBrushFactory());
    KoToolRegistry::instance()->add(new KisToolMoveFactory());
    // ...
}
```

Loaded from `libs/flake/KoToolRegistry.cpp`:

```cpp
KoPluginLoader::instance()->load("Krita/Tool", config);
// Some tools still registered directly in core:
add(new KoPathToolFactory());
add(new KoZoomToolFactory());
```

**Dockers** — `plugins/dockers/layerdocker/LayerDocker.cpp`:

```cpp
KritaLayerDockerPlugin::KritaLayerDockerPlugin(...) {
    KoDockRegistry::instance()->add(new LayerBoxFactory());
}
```

Service type: `Krita/Dock` (see `kritalayerdocker.json`).

**Filters** — `Krita/Filter` via `KisFilterRegistry`

**Generators** — `Krita/Generator` via `KisGeneratorRegistry`

**Import/export** — `plugins/impex/*` (PSD, PNG, KRA, etc.)

**View extensions** (menu actions per view) — `Krita/ViewPlugin`, loaded in `KisMainWindow` ctor:

```cpp
// libs/ui/KisMainWindow.cpp
KoPluginLoader::instance()->load("Krita/ViewPlugin", ..., d->viewManager, false);
```

Example: `plugins/extensions/waveletdecompose/` extends `KisActionPlugin`, adds menu action.

**Application plugins** — `Krita/ApplicationPlugin`, loaded once:

```cpp
KoPluginLoader::instance()->load("Krita/ApplicationPlugin", ..., qApp, true);
```

The Python manager (`kritapykrita.json`) uses this service type.

### Service types (partial list)

| Service type | Registry / use |
|--------------|----------------|
| `Krita/Paintop` | Brush engines (pixel, smudge, mypaint, …) |
| `Krita/Tool` | Canvas tools |
| `Krita/Dock` | Dock panels |
| `Krita/Filter` | Filter effects |
| `Krita/Generator` | Fill generators |
| `Krita/Flake` / `Krita/Shape` | Vector shapes |
| `Krita/ColorSpace` | Color space backends |
| `Krita/Metadata` | Metadata backends |
| `Krita/ViewPlugin` | Per-view menu extensions |
| `Krita/ApplicationPlugin` | App-wide (Python host) |
| `Krita/PythonPlugin` | Individual Python scripts (`.desktop`) |

Plugins can be disabled via KConfig blacklists (e.g. `ToolPluginsDisabled`, `DockerPluginsDisabled` in `[krita]` group).

---

## What is hard-coded in core

These are in `libs/` and not loaded via plugin manifests:

- **Document / image model** — `KisImage`, layer tree, undo, projections (`libs/image/`)
- **Stroke / dab pipeline** — `KisStroke`, `KisPaintOp` base, compositing (`libs/image/brushengine/`)
- **Canvas / view** — `KisCanvas2`, `KisView`, input routing (`libs/ui/`)
- **Main window shell** — menus, dock layout, action collection (`KisMainWindow`)
- **Resource system** — brushes, presets, bundles (`libs/resources/`)
- **Some tools** — path tool, zoom tool registered directly in `KoToolRegistry::init()`

Adding a new **brush engine** or **tool** does not require editing core registries — add a plugin under `plugins/` with JSON + factory. Adding a new **compositing primitive** or changing **tile storage** requires core changes.

---

## Python plugins (true optional extensions)

Built-in scripts: `plugins/python/` → installed to `${KDE_INSTALL_DATADIR}/krita/pykrita`.

Each has a `.desktop` file with `ServiceTypes=Krita/PythonPlugin`.

The C++ host `kritapykrita` (`plugins/extensions/pykrita/plugin/plugin.cpp`) initializes embedded Python, imports `pykrita`/`krita`, scans and loads enabled scripts.

Python dockers subclass `DockWidget` from the `krita` module (see `plugins/python/palette_docker/palette_docker.py`).

Users can add plugins without recompiling Krita by dropping files into the pykrita directory.

---

## Adding a new feature — typical paths

| Feature kind | Where to add | Recompile Krita? |
|--------------|--------------|------------------|
| New brush engine | `plugins/paintops/myengine/` + JSON | Yes (in-tree plugin) |
| New tool | `plugins/tools/mytool/` + JSON | Yes |
| New docker | `plugins/dockers/mydocker/` + JSON | Yes |
| New filter | `plugins/filters/myfilter/` + JSON | Yes |
| New file format | `plugins/impex/myformat/` + JSON | Yes |
| Menu extension / script action | `plugins/extensions/` (`Krita/ViewPlugin`) | Yes |
| User script / docker | Python in pykrita folder | No (Python only) |
| Core behavior (layers, strokes, tiles) | `libs/image/` or `libs/ui/` | Yes (core change) |

---

## Relevance to Floss

Floss Rust rewrite already mirrors part of this split:

- **Core crates** (`floss-document`, `floss-brush`, `floss-compositor`, …) ≈ Krita `libs/`
- **Tool factory / presets** ≈ registries + plugin factories
- No dynamic plugin loading yet — everything is statically linked (like Krita if you merged all `plugins/` into the binary)

A Krita-like path for Floss later would be: stable trait registries in core + optional dynamically loaded modules (or WASM/scripting) registering into those registries at startup — not scattering feature logic in `main.rs`.

---

## Key file paths

```
libs/koplugin/KoPluginLoader.h          — plugin loading docs + API
libs/koplugin/KoPluginLoader.cpp        — load loop, blacklist, version dedup
libs/image/brushengine/kis_paintop_registry.cc
libs/flake/KoToolRegistry.cpp
libs/flake/KoDockRegistry.cpp
libs/ui/KisMainWindow.cpp               — ViewPlugin + ApplicationPlugin load
plugins/CMakeLists.txt                  — all in-tree plugin categories
plugins/tools/basictools/default_tools.cc — tool registration example
plugins/paintops/defaultpaintops/defaultpaintops_plugin.cc
plugins/dockers/layerdocker/LayerDocker.cpp
plugins/extensions/waveletdecompose/waveletdecompose.cpp
plugins/extensions/pykrita/plugin/plugin.cpp
plugins/python/CMakeLists.txt
```
