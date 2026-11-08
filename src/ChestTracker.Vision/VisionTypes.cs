using System.Globalization;
using System.Text.RegularExpressions;

namespace ChestTracker.Vision;

/// <summary>Axis-aligned box in source-frame pixels (x1, y1 top-left; x2, y2 bottom-right).</summary>
public readonly record struct Box(float X1, float Y1, float X2, float Y2)
{
    public float Width => Math.Max(0, X2 - X1);
    public float Height => Math.Max(0, Y2 - Y1);
    public float Area => Width * Height;
    public float CenterX => (X1 + X2) / 2;
    public float CenterY => (Y1 + Y2) / 2;

    public static Box FromCenter(float cx, float cy, float w, float h) => new(cx - (w / 2), cy - (h / 2), cx + (w / 2), cy + (h / 2));

    public float IoU(Box other)
    {
        var ix = Math.Max(0, Math.Min(X2, other.X2) - Math.Max(X1, other.X1));
        var iy = Math.Max(0, Math.Min(Y2, other.Y2) - Math.Max(Y1, other.Y1));
        var inter = ix * iy;
        var union = Area + other.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{X1:0},{Y1:0},{X2:0},{Y2:0}]");
}

/// <summary>What a model class means for counting.</summary>
public enum ObjectRole
{
    /// <summary>Treasure chest, lid closed (countable once it is opened).</summary>
    ChestClosed,

    /// <summary>Treasure chest during the opening animation (lid lifting, light burst).</summary>
    ChestOpening,

    /// <summary>Treasure chest, already open.</summary>
    ChestOpened,

    /// <summary>Ammo boxes, coolers, safes, supply drops... detected so they are never confused with chests.</summary>
    OtherContainer,

    /// <summary>Any other class the model may have: ignored.</summary>
    Ignore,
}

public sealed record Detection(int ClassId, string Label, ObjectRole Role, float Confidence, Box Box)
{
    public bool IsChest => Role is ObjectRole.ChestClosed or ObjectRole.ChestOpening or ObjectRole.ChestOpened;
}

/// <summary>Class names of a model and the role of each class.</summary>
public sealed class ModelLabels
{
    /// <summary>The label set used by tools/yolo (dataset.yaml). Order = class id.</summary>
    public static readonly IReadOnlyList<string> Default = ["chest_closed", "chest_opening", "chest_opened", "ammo_box", "other_container"];

    public ModelLabels(IReadOnlyList<string> names)
    {
        Names = names;
        Roles = names.Select(RoleOf).ToArray();
    }

    public IReadOnlyList<string> Names { get; }

    public IReadOnlyList<ObjectRole> Roles { get; }

    public int Count => Names.Count;

    public string Name(int id) => id >= 0 && id < Names.Count ? Names[id] : $"class{id}";

    public ObjectRole Role(int id) => id >= 0 && id < Roles.Count ? Roles[id] : ObjectRole.Ignore;

    public static ObjectRole RoleOf(string name)
    {
        var n = name.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        return n switch
        {
            "chest_closed" or "chest" or "closed_chest" => ObjectRole.ChestClosed,
            "chest_opening" or "opening_chest" => ObjectRole.ChestOpening,
            "chest_opened" or "chest_open" or "opened_chest" or "open_chest" => ObjectRole.ChestOpened,
            _ when n.Contains("ammo") || n.Contains("container") || n.Contains("cooler") || n.Contains("safe")
                   || n.Contains("supply") || n.Contains("drop") || n.Contains("box") => ObjectRole.OtherContainer,
            _ => ObjectRole.Ignore,
        };
    }

    /// <summary>
    /// Parses the "names" metadata Ultralytics writes into exported ONNX models, e.g.
    /// <c>{0: 'chest_closed', 1: 'chest_opening'}</c>. Returns null when it cannot be read.
    /// </summary>
    public static ModelLabels? ParseUltralyticsNames(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        var pairs = Regex.Matches(metadata, @"(\d+)\s*:\s*(?:'([^']*)'|""([^""]*)"")")
            .Select(m => (Id: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                Name: m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value))
            .ToList();
        if (pairs.Count == 0 || pairs.Select(p => p.Id).Distinct().Count() != pairs.Count)
        {
            return null;
        }

        var names = new string[pairs.Max(p => p.Id) + 1];
        foreach (var (id, name) in pairs)
        {
            names[id] = name;
        }

        return names.Any(n => n is null) ? null : new ModelLabels(names);
    }

    /// <summary>One class name per line (labels.txt next to the model).</summary>
    public static ModelLabels? FromLines(IEnumerable<string> lines)
    {
        var names = lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        return names.Count == 0 ? null : new ModelLabels(names);
    }
}
