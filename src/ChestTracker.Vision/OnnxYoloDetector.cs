using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;

namespace ChestTracker.Vision;

/// <summary>Anything that turns a BGRA frame into detections (the ONNX model, or a fake in tests).</summary>
public interface IObjectDetector : IDisposable
{
    ModelLabels Labels { get; }

    IReadOnlyList<Detection> Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride);
}

public sealed record YoloModelInfo(string Path, string InputName, string OutputName, int InputSize, ModelLabels Labels, string LabelSource);

/// <summary>
/// EXPERIMENTAL. Runs a YOLOv8 detection model exported to ONNX (<c>yolo export format=onnx</c>) on the CPU with
/// ONNX Runtime. Class names come from the model's Ultralytics "names" metadata, else from labels.txt next to the
/// model, else the default label set of tools/yolo. Not thread-safe: one frame at a time.
/// </summary>
public sealed class OnnxYoloDetector : IObjectDetector
{
    public const int DefaultInputSize = 640;

    private readonly InferenceSession _session;
    private readonly float[] _input;
    private readonly long[] _inputShape;

    private OnnxYoloDetector(InferenceSession session, YoloModelInfo info)
    {
        _session = session;
        Info = info;
        _input = new float[3 * info.InputSize * info.InputSize];
        _inputShape = [1, 3, info.InputSize, info.InputSize];
    }

    public YoloModelInfo Info { get; }

    public ModelLabels Labels => Info.Labels;

    public float ConfidenceThreshold { get; set; } = 0.35f;

    public float IoUThreshold { get; set; } = 0.45f;

    /// <summary>Duration of the last <see cref="Detect"/> call (preprocessing + inference + decoding).</summary>
    public double LastElapsedMs { get; private set; }

    public static OnnxYoloDetector Load(string modelPath, int threads = 2)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("YOLO model not found.", modelPath);
        }

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Clamp(threads, 1, Environment.ProcessorCount),
            InterOpNumThreads = 1,
        };
        var session = new InferenceSession(modelPath, options);
        try
        {
            var input = session.InputMetadata.First();
            var dims = input.Value.Dimensions;
            if (dims.Length != 4 || (dims[1] != 3 && dims[1] > 0))
            {
                throw new InvalidDataException($"Expected a 1×3×H×W image input, the model has [{string.Join(",", dims)}].");
            }

            var size = dims[2] > 0 ? dims[2] : DefaultInputSize;
            if (dims[3] > 0 && dims[3] != size)
            {
                throw new InvalidDataException("Only square model inputs are supported (export with imgsz=640).");
            }

            var output = session.OutputMetadata.First();
            var outDims = output.Value.Dimensions;
            var classes = outDims.Length == 3 && outDims[1] > 4 && outDims[1] < outDims[2] ? outDims[1] - 4 : -1;

            var (labels, source) = ReadLabels(session, modelPath, classes);
            return new OnnxYoloDetector(session, new YoloModelInfo(modelPath, input.Key, output.Key, size, labels, source));
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static (ModelLabels Labels, string Source) ReadLabels(InferenceSession session, string modelPath, int classes)
    {
        if (session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var names)
            && ModelLabels.ParseUltralyticsNames(names) is { } fromModel && (classes < 0 || fromModel.Count == classes))
        {
            return (fromModel, "model metadata");
        }

        var file = Path.Combine(Path.GetDirectoryName(modelPath) ?? ".", "labels.txt");
        if (File.Exists(file) && ModelLabels.FromLines(File.ReadAllLines(file)) is { } fromFile && (classes < 0 || fromFile.Count == classes))
        {
            return (fromFile, "labels.txt");
        }

        if (classes < 0 || classes == ModelLabels.Default.Count)
        {
            return (new ModelLabels(ModelLabels.Default), "default label set");
        }

        // Unknown classes: nothing is treated as a chest, so nothing can be counted by mistake.
        return (new ModelLabels(Enumerable.Range(0, classes).Select(i => $"class{i}").ToArray()), "unknown (no chest classes)");
    }

    public IReadOnlyList<Detection> Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        var sw = Stopwatch.StartNew();
        var letterbox = YoloPreprocessing.ToTensor(bgra, width, height, stride, Info.InputSize, _input);
        using var tensor = OrtValue.CreateTensorValueFromMemory(_input, _inputShape);
        using var runOptions = new RunOptions();
        using var results = _session.Run(runOptions, [Info.InputName], [tensor], [Info.OutputName]);
        var output = results[0];
        var shape = output.GetTensorTypeAndShape().Shape.Select(d => (int)d).ToArray();
        var detections = YoloDecoder.Decode(output.GetTensorDataAsSpan<float>(), shape, Labels, letterbox, ConfidenceThreshold, IoUThreshold);
        LastElapsedMs = sw.Elapsed.TotalMilliseconds;
        return detections;
    }

    public void Dispose() => _session.Dispose();
}

public sealed record PipelineFrame(DateTimeOffset Timestamp, IReadOnlyList<Detection> Detections, TrackingUpdate Update, double ElapsedMs);

/// <summary>Detector + tracker + per-chest state machines: frame in, counted openings out.</summary>
public sealed class YoloChestPipeline : IDisposable
{
    private readonly IObjectDetector _detector;

    public YoloChestPipeline(IObjectDetector detector, ChestTrackingOptions? options = null)
    {
        _detector = detector;
        Tracker = new ChestTrackManager(options);
    }

    public IObjectDetector Detector => _detector;

    public ChestTrackManager Tracker { get; }

    public PipelineFrame Process(ReadOnlySpan<byte> bgra, int width, int height, int stride, DateTimeOffset timestamp)
    {
        var sw = Stopwatch.StartNew();
        var detections = _detector.Detect(bgra, width, height, stride);
        var update = Tracker.Update(detections, timestamp);
        return new PipelineFrame(timestamp, detections, update, sw.Elapsed.TotalMilliseconds);
    }

    public void Reset() => Tracker.Reset();

    public void Dispose() => _detector.Dispose();
}
