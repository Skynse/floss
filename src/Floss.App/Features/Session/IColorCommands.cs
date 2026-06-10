using System;
using Avalonia.Media;

namespace Floss.App.Features.Session;

/// <summary>Foreground color orchestration across color dock, tools well, and brush preview.</summary>
public interface IColorCommands
{
    ReadOnlySpan<Color> Swatches { get; }

    void SetColor(Color color, bool syncPicker = true);

    void SyncPickerFromColor(Color color);

    void RefreshColorSliders();

    void CycleColor();
}
