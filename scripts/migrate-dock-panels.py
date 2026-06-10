#!/usr/bin/env python3
"""One-shot migration: MainWindow dock partials -> Features/Dock/Panels/*DockPanel.cs"""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MW = ROOT / "src/Floss.App/MainWindow"
OUT = ROOT / "src/Floss.App/Features/Dock/Panels"

MIGRATIONS = [
    {
        "src": "MainWindow.NodeGraphDock.cs",
        "dest": "NodeGraphDockPanel.cs",
        "class_name": "NodeGraphDockPanel",
        "host": "INodeGraphDockHost",
        "host_field": "_host",
        "extra_usings": [
            "using Floss.App.Brushes;",
            "using Floss.App.Input;",
        ],
        "replacements": [
            (r"\b_canvas\b", "_host.Canvas"),
            (r"\b_activePreset\b", "_host.ActivePreset"),
            (r"\b_activeBrushAsset\b", "_host.ActiveBrushAsset"),
            (r"\b_keyboardInputScope\b", "_host.KeyboardInputScope"),
            (r"UpdateCurrentBrush\(", "_host.UpdateCurrentBrush("),
            (r"SaveNodeGraphAsNewBrushPreset\(", "_host.SaveNodeGraphAsNewBrushPreset("),
            (r"GraphForBrushTip\(", "_host.GraphForBrushTip("),
            (r"IsDockerVisible\(", "_host.IsDockerVisible("),
            (r"ToggleDockerVisibility\(", "_host.ToggleDockerVisibility("),
            (r"RebuildDockers\(\)", "_host.RebuildDockers()"),
            (r"SyncBottomDockVisibility\(\)", "_host.SyncBottomDockVisibility()"),
            (r"PersistWorkspaceLayout\(\)", "_host.PersistWorkspaceLayout()"),
            (r"App\.Config", "_host.Config"),
        ],
        "header": '''public sealed partial class NodeGraphDockPanel : Control
{
    private readonly INodeGraphDockHost _host;

    public NodeGraphDockPanel(INodeGraphDockHost host)
    {
        _host = host;
        Content = BuildNodeGraphDockerContent();
    }

    public void OnVisibilityChanged(bool visible) => HandleNodeGraphDockVisibility(visible);

    public void InvalidateState() => InvalidateNodeGraphDockState();

    public void SyncToActiveBrush(bool force = false) => SyncNodeGraphDockToActiveBrush(force);

    public void RefreshImageOptions() => RefreshNodeGraphImageOptions();

    public void WireKeyboardSurface() => WireNodeGraphKeyboardSurface();

    public NodeGraphEditorPanel? Editor => _nodeGraphEditor;
''',
        "footer": "}",
    },
    {
        "src": "MainWindow.LayerProperties.cs",
        "dest": "LayerPropertiesDockPanel.cs",
        "class_name": "LayerPropertiesDockPanel",
        "host": "ILayerPropertiesDockHost",
        "replacements": [
            (r"\b_canvas\b", "_host.Canvas"),
            (r"\b_syncingToolPropertyPanel\b", "_host.Sync.SyncingToolPropertyPanel"),
            (r"RefreshLayerProperties\(\)", "Refresh()"),
            (r"picker\.Show\(this\)", "picker.Show(_host.Owner)"),
        ],
        "header": '''public sealed partial class LayerPropertiesDockPanel : Control
{
    private readonly ILayerPropertiesDockHost _host;

    public LayerPropertiesDockPanel(ILayerPropertiesDockHost host)
    {
        _host = host;
        Content = BuildLayerPropertiesSection();
    }

    public void Refresh() => RefreshLayerProperties();

    public void ToggleActiveLayerColor() => ToggleActiveLayerColorInternal();
''',
        "rename_methods": {
            "ToggleActiveLayerColor": "ToggleActiveLayerColorInternal",
        },
    },
]


def apply_replacements(text: str, replacements: list[tuple[str, str]]) -> str:
    for pat, repl in replacements:
        text = re.sub(pat, repl, text)
    return text


def migrate(entry: dict) -> None:
    src_path = MW / entry["src"]
    text = src_path.read_text()
    text = text.replace("namespace Floss.App;", "namespace Floss.App.Features.Dock.Panels;")
    text = re.sub(
        r"public partial class MainWindow( : Window)?",
        f"public sealed partial class {entry['class_name']} : Control",
        text,
        count=1,
    )
    for old, new in entry.get("rename_methods", {}).items():
        text = re.sub(rf"\bvoid {old}\(", f"void {new}(", text)
        text = re.sub(rf"\(\) => {old}\(\)", f"() => {new}()", text)
    text = apply_replacements(text, entry.get("replacements", []))
    usings = entry.get("extra_usings", [])
    if usings:
        ns_idx = text.index("namespace")
        text = text[:ns_idx] + "\n".join(usings) + "\n\n" + text[ns_idx:]
    # strip first class opening line and inject header
    text = re.sub(
        rf"public sealed partial class {entry['class_name']} : Control\n\{{\n",
        entry["header"],
        text,
        count=1,
    )
    dest = OUT / entry["dest"]
    dest.write_text(text)
    print(f"Wrote {dest}")


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    for m in MIGRATIONS:
        migrate(m)
