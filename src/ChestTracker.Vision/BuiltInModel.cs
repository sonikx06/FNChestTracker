using Microsoft.ML.OnnxRuntime;

namespace ChestTracker.Vision;

/// <summary>The model is missing, damaged, or not the expected chest model. The message is shown to the user.</summary>
public sealed class BuiltInModelException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The chest model that ships with the app (<c>models\best.onnx</c> next to the executable). It is the only detector:
/// there is no setting to choose, import or replace a model (the file itself can of course still be swapped on disk;
/// this is not copy protection).
/// </summary>
public static class BuiltInModel
{
    public const string FileName = "best.onnx";
    public const string Folder = "models";

    /// <summary>Path of the built-in model for an application directory.</summary>
    public static string PathIn(string appDirectory) => Path.Combine(appDirectory, Folder, FileName);

    /// <summary>
    /// Loads and checks the model: it must load in ONNX Runtime and have the closed_chest and open_chest classes.
    /// GPU (DirectML) first, CPU if the GPU is unavailable.
    /// </summary>
    /// <exception cref="BuiltInModelException">With a message for the user.</exception>
    public static OnnxYoloDetector Load(string path, int threads = 2, InferenceDevice device = InferenceDevice.Auto,
        Action<SessionOptions>? gpuSetup = null)
    {
        if (!File.Exists(path))
        {
            throw new BuiltInModelException(
                $"The built-in chest model is missing ({path}). Reinstall the app: the \"{Folder}\" folder must stay next to FortniteChestTracker.exe. Manual counting keeps working.");
        }

        OnnxYoloDetector detector;
        try
        {
            detector = OnnxYoloDetector.Load(path, threads, device, gpuSetup);
        }
        catch (Exception ex)
        {
            throw new BuiltInModelException(
                $"The built-in chest model could not be loaded; the file is probably damaged ({FirstLine(ex.Message)}). Reinstall the app. Manual counting keeps working.", ex);
        }

        var roles = detector.Labels.Roles;
        if (!roles.Contains(ObjectRole.ChestClosed) || !roles.Contains(ObjectRole.ChestOpened))
        {
            var names = string.Join(", ", detector.Labels.Names);
            detector.Dispose();
            throw new BuiltInModelException(
                $"The built-in chest model has the wrong classes ({names}); expected closed_chest and open_chest. Reinstall the app.");
        }

        return detector;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
