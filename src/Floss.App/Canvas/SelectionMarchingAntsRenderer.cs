using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Canvas;

/// <summary>
/// GPU marching-ants via an alpha-mask texture + SkSL edge shader.
/// Falls back to vector overlay when runtime shaders are unavailable.
/// </summary>
internal static class SelectionMarchingAntsRenderer
{
    private const float AntPeriod = 8f;
    private const float AntPhaseStep = 2f;

    private static readonly object EffectLock = new();
    private static SKRuntimeEffect? _effect;
    private static bool _effectUnavailable;

    private const string EffectSource = """
        uniform shader mask;
        uniform float phase;
        uniform float period;

        half4 main(float2 coord) {
            half m  = mask.eval(coord).a;
            half mx = mask.eval(coord + float2(1, 0)).a;
            half my = mask.eval(coord + float2(0, 1)).a;
            half edge = max(max(abs(m - mx), abs(m - my)),
                             max(abs(m - mask.eval(coord + float2(-1, 0)).a),
                                 abs(m - mask.eval(coord + float2(0, -1)).a)));
            if (edge < 0.08h) {
                return half4(0);
            }
            half stripe = half(floor((coord.x + coord.y + phase) / period));
            return (stripe - 2.0h * half(floor(stripe * 0.5h))) < 0.5h
                ? half4(0, 0, 0, 1)
                : half4(1, 1, 1, 1);
        }
        """;

    public static float AntPhaseStepPx => AntPhaseStep;

    public static bool TryDraw(DrawingContext context, SelectionMask selection, double zoom, float phase)
    {
        if (!selection.HasSelection || GetEffect() == null)
            return false;

        if (!selection.TryGetAlphaTexture(out var image, out var bounds, out var texScale) || image == null)
            return false;

        var dest = new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        context.Custom(new MarchingAntsDrawOp(dest, image, bounds, texScale, (float)Math.Max(1.0, 1.0 / zoom), phase));
        return true;
    }

    private static SKRuntimeEffect? GetEffect()
    {
        if (_effectUnavailable)
            return null;

        lock (EffectLock)
        {
            if (_effectUnavailable)
                return null;
            if (_effect != null)
                return _effect;

            _effect = SKRuntimeEffect.CreateShader(EffectSource, out var error);
            if (_effect != null)
                return _effect;

            _effectUnavailable = true;
            CrashLog.Write(new InvalidOperationException(error ?? "SKRuntimeEffect compile failed"),
                "SelectionMarchingAntsRenderer.GetEffect");
            return null;
        }
    }

    private sealed class MarchingAntsDrawOp : ICustomDrawOperation
    {
        private readonly Rect _dest;
        private readonly SKImage _image;
        private readonly SKRectI _bounds;
        private readonly int _texScale;
        private readonly float _period;
        private readonly float _phase;

        public MarchingAntsDrawOp(Rect dest, SKImage image, SKRectI bounds, int texScale, float period, float phase)
        {
            _dest = dest;
            _image = image;
            _bounds = bounds;
            _texScale = texScale;
            _period = period;
            _phase = phase;
            Bounds = dest;
        }

        public Rect Bounds { get; }

        public void Dispose() { }

        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var effect = GetEffect();
            if (effect == null)
                return;

            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>()?.Lease();
            if (lease == null)
                return;

            using (lease)
            {
                using var baseShader = SKShader.CreateImage(
                    _image,
                    SKShaderTileMode.Decal,
                    SKShaderTileMode.Decal,
                    SKMatrix.CreateScale(1f / _texScale, 1f / _texScale));

                using var uniforms = new SKRuntimeEffectUniforms(effect);
                uniforms["phase"] = _phase;
                uniforms["period"] = Math.Max(AntPeriod, _period * 2f);

                using var children = new SKRuntimeEffectChildren(effect);
                children["mask"] = baseShader;

                using var shader = effect.ToShader(uniforms, children);
                using var paint = new SKPaint
                {
                    Shader = shader,
                    IsAntialias = false,
                    Style = SKPaintStyle.Fill
                };

                lease.SkCanvas.DrawRect(
                    SKRect.Create((float)_dest.X, (float)_dest.Y, (float)_dest.Width, (float)_dest.Height),
                    paint);
            }
        }
    }
}
