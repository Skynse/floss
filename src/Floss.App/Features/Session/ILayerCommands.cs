using System.Threading.Tasks;

namespace Floss.App.Features.Session;

/// <summary>Layer panel actions: filters and layer-properties refresh.</summary>
public interface ILayerCommands
{
    void RefreshLayerProperties();

    void ShowPaperColorPicker();

    Task ApplyBlurFilter();

    Task ApplySharpenFilter();

    Task ApplyNoiseFilter();

    Task ApplyChromaticAberrationFilter();

    Task ApplyRemoveDustFilter();

    Task ApplyLevelsFilter();

    Task ApplyColorCurvesFilter();

    void ApplyInvertFilter();

    void ApplyDesaturateFilter();

    Task ApplyBrightnessContrastFilter();

    Task ApplyExposureGammaFilter();

    Task ApplyHueSaturationFilter();

    Task ApplySepiaFilter();

    Task ApplyThresholdFilter();

    Task ApplyPosterizeFilter();

    Task ApplyPixelateFilter();

    Task ApplyVignetteFilter();

    Task ApplyBloomFilter();

    Task ApplyMotionBlurFilter();

    Task ApplyEmbossFilter();

    Task ApplyEdgeDetectFilter();
}
