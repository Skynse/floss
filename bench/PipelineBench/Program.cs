using System.Diagnostics;
using System.Reflection;
using Avalonia.Media;
using Floss.App;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Processes.Output;
using Floss.App.Tools;

// Long horizontal stroke on a large canvas — NOT a single dab.
const float StrokeStartX = 400f;
const float StrokeStartY = 2040f;
const float StrokeLengthPx = 960f;
const float StepPx = 8f;
const int CanvasW = 3000;
const int CanvasH = 4080;

static CanvasInputSample Sample(float x, float y, long t) =>
    new(x, y, 1, 0, 0, 0, t, 0, CanvasInputSource.Mouse, CanvasInputPhase.Move);

static List<CanvasInputSample> BuildLongStroke()
{
    var steps = (int)(StrokeLengthPx / StepPx);
    var samples = new List<CanvasInputSample>(steps + 1);
    for (var i = 0; i <= steps; i++)
        samples.Add(Sample(StrokeStartX + i * StepPx, StrokeStartY, i * 1_000L));
    return samples;
}

static (BrushPreset brush, string label)[] GetBrushes() =>
[
    (new BrushPreset("Circle dry", 48, 1, 0.75, 0.05, Colors.Black, 0)
    {
        Tip = new ProceduralBrushTip(BrushTipShape.Circle),
        ColorMix = false,
        Spacing = 0.04,
    }, "Basic circle 48px"),
    (new BrushPreset("CSP smear", 230, 1, 0.75, 0.05, Colors.Black, 0)
    {
        Tip = new ProceduralBrushTip(BrushTipShape.Circle),
        ColorMix = true,
        SmudgeMode = SmudgeMode.Smear,
        AmountOfPaint = 0,
        DensityOfPaint = 0,
        ColorStretch = 0.86,
        BlurAmount = 0.94,
        MixingMode = MixingMode.Perceptual,
        Spacing = 0.04,
    }, "Spatial smear 230px"),
    (new BrushPreset("Blend", 230, 1, 0.75, 0.05, Colors.Black, 0)
    {
        Tip = new ProceduralBrushTip(BrushTipShape.Circle),
        ColorMix = true,
        SmudgeMode = SmudgeMode.Blend,
        AmountOfPaint = 0.5,
        DensityOfPaint = 0.5,
        BlurAmount = 0.94,
        Spacing = 0.04,
    }, "Blend batch 230px"),
];

static void Prepaint(DrawingLayer layer)
{
    for (var y = 1800; y < 2280; y++)
    for (var x = 400; x < 2600; x++)
        layer.Pixels.SetPixel(x, y, 80, 120, 200, 255);
}

static (DrawingDocument doc, DrawingLayer layer, ToolContext ctx) NewDoc()
{
    var doc = new DrawingDocument();
    doc.AddLayer();
    var layer = doc.ActiveLayer!;
    return (doc, layer, new ToolContext(doc));
}

static BenchResult BenchEngineOnly(BrushPreset brush, bool prepaint)
{
    using var engine = new BrushEngine();
    var (_, layer, _) = NewDoc();
    if (prepaint) Prepaint(layer);

    var samples = BuildLongStroke();
    var sw = Stopwatch.StartNew();
    engine.BeginStroke(brush, samples[0]);
    var stamps = 0;
    for (var i = 1; i < samples.Count; i++)
    {
        engine.RasterizeSegment(layer, brush, samples[i - 1], samples[i]);
        stamps += engine.LastStats.StampCount;
    }
    engine.EndStroke();
    sw.Stop();

    return new BenchResult("Engine-only (RasterizeSegment)", sw.Elapsed.TotalMilliseconds, stamps,
        engine.LastStats.Path, samples.Count - 1, samples.Count);
}

static void SetPrivateField(object target, string name, object? value)
{
    var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"Missing field {name}");
    field.SetValue(target, value);
}

static BenchResult BenchDirectDrawFlush(BrushPreset brush, bool prepaint)
{
    using var engine = new BrushEngine();
    var (doc, layer, ctx) = NewDoc();
    if (prepaint) Prepaint(layer);

    var output = new DirectDrawOutput(engine, doc);
    var tx = CreateQueuedTransaction(output, doc, layer, ctx, brush, out var queued);

    var sw = Stopwatch.StartNew();
    SetPrivateField(output, "_active", tx);
    output.FlushPending();
    sw.Stop();

    return new BenchResult("DirectDraw FlushPending (sync slices + capture)", sw.Elapsed.TotalMilliseconds,
        engine.LastStats.StampCount, engine.LastStats.Path, -1, queued.Count);
}

static object CreateQueuedTransaction(
    DirectDrawOutput output,
    DrawingDocument doc,
    DrawingLayer layer,
    ToolContext ctx,
    BrushPreset brush,
    out List<CanvasInputSample> queued)
{
    var txType = typeof(DirectDrawOutput).GetNestedType("StrokeTransaction", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("StrokeTransaction missing");
    var queue = typeof(DirectDrawOutput).GetMethod("QueueNewSamples", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("QueueNewSamples missing");

    var samples = BuildLongStroke();
    var tx = Activator.CreateInstance(txType, ctx, layer, doc.ActiveLayerIndex, brush, samples[0])
        ?? throw new InvalidOperationException("tx create failed");
    queue.Invoke(null, [tx, layer, brush, samples]);

    queued = (List<CanvasInputSample>)(txType.GetProperty("QueuedSamples")?.GetValue(tx)
        ?? throw new InvalidOperationException("QueuedSamples missing"));
    txType.GetProperty("FinalizeRequested")?.SetValue(tx, true);
    return tx;
}

static BenchResult BenchDirectDrawAsync(BrushPreset brush, bool prepaint)
{
    using var engine = new BrushEngine();
    var (doc, layer, ctx) = NewDoc();
    if (prepaint) Prepaint(layer);
    ctx.Brush = brush;
    ctx.PaintColor = Colors.Black;

    var output = new DirectDrawOutput(engine, doc);
    var txType = typeof(DirectDrawOutput).GetNestedType("StrokeTransaction", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("StrokeTransaction missing");
    var snapshotSamples = typeof(DirectDrawOutput).GetMethod("SnapshotSamples", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SnapshotSamples missing");
    var processBatch = typeof(DirectDrawOutput).GetMethod("ProcessSegmentBatch", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("ProcessSegmentBatch missing");
    var recordTime = txType.GetMethod("SuggestedSegmentCount", BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("SuggestedSegmentCount missing");

    var tx = CreateQueuedTransaction(output, doc, layer, ctx, brush, out var queued);
    engine.BeginStroke(brush, queued[0]);
    txType.GetProperty("StrokeStarted")?.SetValue(tx, true);

    const double sliceBudgetMs = 3.0;
    const int initialSegments = 1;
    const int maxSegments = 8;

    var sw = Stopwatch.StartNew();
    var batches = 0;
    var stamps = 0;
    var nextIndex = 1;

    while (nextIndex < queued.Count)
    {
        var remaining = queued.Count - nextIndex;
        var segmentCount = (int)recordTime.Invoke(tx, [sliceBudgetMs, initialSegments, maxSegments])!;
        segmentCount = Math.Clamp(Math.Min(remaining, segmentCount), 1, maxSegments);

        var batchStarted = Stopwatch.GetTimestamp();
        var snapshot = snapshotSamples.Invoke(null, [queued, nextIndex - 1, segmentCount + 1]);
        Task.Run(() => processBatch.Invoke(output, [tx, snapshot, 1, segmentCount])).GetAwaiter().GetResult();
        var batchMs = (Stopwatch.GetTimestamp() - batchStarted) * 1000.0 / Stopwatch.Frequency;

        txType.GetMethod("RecordSegmentTime", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(tx, [batchMs, segmentCount]);

        txType.GetProperty("NextSegmentIndex")!.SetValue(tx, nextIndex + segmentCount);
        nextIndex += segmentCount;
        batches++;
        stamps += engine.LastStats.StampCount;

        if (segmentCount <= 0)
            break;
    }

    engine.EndStroke();
    sw.Stop();

    return new BenchResult("DirectDraw async (Task.Run batches + capture)", sw.Elapsed.TotalMilliseconds,
        stamps, engine.LastStats.Path, batches, queued.Count);
}

static void PrintResult(string brushLabel, bool prepaint, BenchResult r)
{
    var prep = prepaint ? "prepaint" : "empty";
    var perStamp = r.StampCount > 0 ? r.TotalMs / r.StampCount : 0;
    Console.WriteLine(
        $"  {r.Mode,-46} {r.TotalMs,8:F1} ms  stamps~={r.StampCount,4}  {perStamp,6:F2} ms/stamp  batches={r.SegmentBatches,3}  queuedPts={r.QueuedSamples,3}  path={r.Path}  [{prep}]");
}

Console.WriteLine("=== Floss pipeline benchmark (Release) ===");
Console.WriteLine($"Canvas {CanvasW}x{CanvasH}, stroke {StrokeLengthPx}px in {StepPx}px steps ({(int)(StrokeLengthPx / StepPx)} segments, NOT a single dab)\n");

foreach (var (brush, label) in GetBrushes())
{
    Console.WriteLine($"── {label} ──");
    foreach (var prepaint in new[] { false, true })
    {
        if (brush.ColorMix && !prepaint && brush.SmudgeMode == SmudgeMode.Smear)
        {
            // smear on empty is uninteresting noise
        }

        PrintResult(label, prepaint, BenchEngineOnly(brush, prepaint));
        PrintResult(label, prepaint, BenchDirectDrawAsync(brush, prepaint));
        PrintResult(label, prepaint, BenchDirectDrawFlush(brush, prepaint));
        Console.WriteLine();
    }
}

Console.WriteLine("Notes:");
Console.WriteLine("- Engine-only: BrushEngine.RasterizeSegment per 8px step, no tile capture, no Task.Run");
Console.WriteLine("- DirectDraw async: ProcessSegmentBatch on thread pool with 3ms slice budget + CaptureTiles (realistic)");
Console.WriteLine("- DirectDraw FlushPending: sync processing path used on Commit, includes capture, no Task.Run");

sealed record BenchResult(
    string Mode,
    double TotalMs,
    int StampCount,
    string Path,
    int SegmentBatches,
    int QueuedSamples);
