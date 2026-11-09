using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ChestTracker.Vision;

/// <summary>Anything that turns a BGRA frame into detections (the ONNX model, or a fake in tests).</summary>
public interface IObjectDetector : IDisposable
{
    ModelLabels Labels { get; }

    IReadOnlyList<Detection> Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride);
}

/// <param name="Provider">Execution provider actually used: "DirectML (GPU)" or "CPU".</param>
/// <param name="ProviderNote">Why the GPU is not used (unavailable, failed to initialise), null otherwise.</param>
public sealed record YoloModelInfo(string Path, string InputName, string OutputName, int InputSize, ModelLabels Labels, string LabelSource,
    string Provider = "CPU", string? ProviderNote = null)
{
    public bool UsesGpu => Provider != "CPU";
}

/// <summary>Where inference should run. <see cref="Auto"/> tries the GPU and falls back to the CPU.</summary>
public enum InferenceDevice
{
    Auto,
    Cpu,
}

/// <summary>
/// Runs a YOLOv8 detection model exported to ONNX (<c>yolo export format=onnx</c>) with ONNX Runtime.
/// The GPU is used through DirectML (any DirectX 12 GPU, e.g. an RTX 2060; no CUDA install needed); if it cannot be
/// initialised or fails its first inference, the CPU is used instead. Class names come from the model's Ultralytics
/// "names" metadata, else from labels.txt next to the model, else the default label set of tools/yolo.
/// One session per model, reused for every frame. Not thread-safe: one frame at a time.
/// </summary>
public sealed class OnnxYoloDetector : IObjectDetector
{
    public const int DefaultInputSize = 640;
    public const string CpuProvider = "CPU";
    public const string GpuProvider = "DirectML (GPU)";

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

    /// <param name="threads">CPU threads (CPU provider only).</param>
    /// <param name="gpuSetup">Adds the GPU execution provider; tests pass a failing one to exercise the fallback.</param>
    public static OnnxYoloDetector Load(string modelPath, int threads = 2, InferenceDevice device = InferenceDevice.Auto,
        Action<SessionOptions>? gpuSetup = null)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("YOLO model not found.", modelPath);
        }

        string? note = null;
        if (device == InferenceDevice.Auto)
        {
            try
            {
                var gpu = Create(modelPath, threads, gpuSetup ?? (o => o.AppendExecutionProvider_DML(0)), GpuProvider, null);
                try
                {
                    gpu.WarmUp();
                    return gpu;
                }
                catch
                {
                    gpu.Dispose();
                    throw;
                }
            }
            catch (Exception ex) when (ex is not FileNotFoundException and not InvalidDataException)
            {
                note = "GPU not used: " + FirstLine(ex.Message);
            }
        }

        var cpu = Create(modelPath, threads, null, CpuProvider, note);
        cpu.WarmUp();
        return cpu;
    }

    private static OnnxYoloDetector Create(string modelPath, int threads, Action<SessionOptions>? provider, string providerName, string? note)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Clamp(threads, 1, Environment.ProcessorCount),
            InterOpNumThreads = 1,
        };
        if (provider is not null)
        {
            // DirectML does not support memory patterns or parallel execution.
            options.EnableMemoryPattern = false;
            provider(options);
        }

        var session = new InferenceSession(modelPath, options);
        try
        {
            var input = session.InputMetadata.First();
            var dims = input.Value.Dimensions;
            if (input.Value.ElementDataType != TensorElementType.Float)
            {
                throw new InvalidDataException($"Expected a float32 image input, the model has {input.Value.ElementDataType}.");
            }

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
            if (output.Value.ElementDataType != TensorElementType.Float)
            {
                throw new InvalidDataException($"Expected a float32 detection output, the model has {output.Value.ElementDataType}.");
            }

            // Layout from the shape itself: [1, 4 + classes, anchors] (Ultralytics default) or [1, anchors, 4 + classes].
            var outDims = output.Value.Dimensions;
            var classes = YoloDecoder.ClassCount(outDims);

            var (labels, source) = ReadLabels(session, modelPath, classes);
            return new OnnxYoloDetector(session, new YoloModelInfo(modelPath, input.Key, output.Key, size, labels, source, providerName, note));
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>One inference on a blank frame: catches GPU drivers that accept the session but fail to run it.</summary>
    private void WarmUp()
    {
        var blank = new byte[64 * 64 * 4];
        Detect(blank, 64, 64, 64 * 4);
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

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
        Tracker = new ChestTrackManager(ForModel(options ?? new ChestTrackingOptions(), detector.Labels));
    }

    public IObjectDetector Detector => _detector;

    public ChestTrackManager Tracker { get; }

    /// <summary>Tracking options, adapted to the model's classes (see <see cref="ChestTrackingOptions.ModelHasOpeningClass"/>).</summary>
    public ChestTrackingOptions Options
    {
        get => Tracker.Options;
        set => Tracker.Options = ForModel(value, _detector.Labels);
    }

    public static ChestTrackingOptions ForModel(ChestTrackingOptions options, ModelLabels labels) =>
        options with { ModelHasOpeningClass = labels.Roles.Contains(ObjectRole.ChestOpening) };

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
