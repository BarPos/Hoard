using Hoard.Core.Export;
using Hoard.Core.Formatting;

namespace Hoard.Tui.Browser;

public enum ExportScope { Folder, Everything, Marked }

/// <summary>The export dialog (<c>x</c>, plan.md §9.2): format, scope, options, where to save.</summary>
public sealed class ExportDialog
{
    public const int FieldCount = 7; // format, scope, files, depth, min size, path, export button
    public static readonly int[] DepthSteps = [0, 1, 2, 3, 4, 6, 8, 12];
    public static readonly long[] MinSizeSteps = [0, 100_000, 1_000_000, 10_000_000, 100_000_000];

    public ExportFormat Format { get; set; } = ExportFormat.PrettyHtml;
    public ExportScope Scope { get; set; } = ExportScope.Folder;
    public bool IncludeFiles { get; set; } = true;
    public int Depth { get; set; }
    public long MinSize { get; set; }
    public int Field { get; set; }
    public string Path { get; set; } = "";
    /// <summary>The user typed a path: changing the format no longer changes its extension.</summary>
    public bool CustomPath { get; set; }
    public bool EditingPath { get; set; }
    public Task<long>? Writing { get; set; }
    public string? Error { get; set; }
    /// <summary>The first lines of the output with the current options (refreshed when they change).</summary>
    public string? Preview { get; set; }
    public object? PreviewKey { get; set; }

    public string Describe(int field) => field switch
    {
        0 => Exporter.Name(Format),
        1 => Scope switch { ExportScope.Everything => "whole scan", ExportScope.Marked => "marked items only", _ => "open folder" },
        2 => IncludeFiles ? "folders and files" : "folders only",
        3 => Depth == 0 ? "everything" : $"{Depth} level{(Depth == 1 ? "" : "s")}",
        4 => MinSize == 0 ? "no minimum" : Units.Size(MinSize) + " or more",
        _ => Path,
    };

    public ExportOptions Options(bool apparent) => new(Format, Depth, MinSize, IncludeFiles, apparent);

    /// <summary>Puts the extension that matches the format on the default path.</summary>
    public void FixExtension()
    {
        if (!CustomPath && Path.Length > 0)
        {
            Path = System.IO.Path.ChangeExtension(Path, Exporter.Extension(Format));
        }
    }
}
