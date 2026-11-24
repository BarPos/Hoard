using Hoard.Core.Model;

namespace Hoard.Core.Analysis;

public enum TypeCategory { Video, Images, Audio, Archives, DiskImages, Documents, Fonts, Code, Apps, Databases, Other }

public sealed record TypeTotal(TypeCategory Category, long Bytes, long Files);
public sealed record ExtensionTotal(string Extension, TypeCategory Category, long Bytes, long Files);

public sealed class TypeReport(List<TypeTotal> categories, List<ExtensionTotal> extensions, long totalBytes,
    long emptyFolders = 0, long zeroByteFiles = 0)
{
    /// <summary>Categories with any bytes, biggest first.</summary>
    public List<TypeTotal> Categories { get; } = categories;
    /// <summary>Extensions, biggest first (no extension is reported as "(none)").</summary>
    public List<ExtensionTotal> Extensions { get; } = extensions;
    public long TotalBytes { get; } = totalBytes;
    /// <summary>Readable folders with nothing in them (plan.md §6.6).</summary>
    public long EmptyFolders { get; } = emptyFolders;
    /// <summary>Files whose length is zero.</summary>
    public long ZeroByteFiles { get; } = zeroByteFiles;
}

/// <summary>
/// What the disk is made of (plan.md §6.5), grouped by file extension. Everything inside an
/// <c>.app</c> bundle counts as Apps, whatever its extension. Runs on a finished scan.
/// </summary>
public static class FileTypes
{
    public static string Label(TypeCategory c) => c switch
    {
        TypeCategory.DiskImages => "Disk images",
        TypeCategory.Apps => "Apps & binaries",
        TypeCategory.Code => "Code & text",
        _ => c.ToString(),
    };

    private static readonly Dictionary<string, TypeCategory> ByExtension = Build();

    private static Dictionary<string, TypeCategory> Build()
    {
        var d = new Dictionary<string, TypeCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(TypeCategory c, string list)
        {
            foreach (string e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                d[e] = c;
            }
        }
        Add(TypeCategory.Video, "mp4 mov m4v mkv avi webm wmv flv mpg mpeg 3gp mts m2ts mxf braw r3d");
        Add(TypeCategory.Images, "jpg jpeg png gif heic heif webp tif tiff bmp raw cr2 cr3 nef arw dng orf rw2 psd psb ai svg ico icns sketch fig xcf exr hdr");
        Add(TypeCategory.Audio, "mp3 m4a aac wav flac aif aiff ogg opus wma caf mid midi alac m4b");
        Add(TypeCategory.Archives, "zip tar gz tgz bz2 tbz xz txz 7z rar zst lz4 lzma jar war whl nupkg cab");
        Add(TypeCategory.DiskImages, "dmg iso img vmdk vdi qcow2 vhd vhdx sparseimage xip pkg ipsw toast");
        Add(TypeCategory.Documents, "pdf doc docx xls xlsx ppt pptx pages numbers key odt ods odp rtf epub mobi csv tsv");
        Add(TypeCategory.Fonts, "ttf otf ttc otc woff woff2 eot pfb pfm afm tfm vf fon dfont pcf bdf");
        Add(TypeCategory.Code, "tex sty cls dtx ins bib bst lua php pl pm r jl scala dart vue svelte el vim fish ipynb proto graphql patch diff cs fs vb js mjs cjs ts jsx tsx py pyc rb go rs java kt kts swift c cc cpp h hpp m mm json yaml yml toml xml html htm css scss less sql sh zsh bash ps1 lock map md txt log ini cfg conf env gradle cmake");
        Add(TypeCategory.Apps, "dylib so a o exe dll bin class wasm node jmod");
        Add(TypeCategory.Databases, "db sqlite sqlite3 sqlitedb realm ldb mdb accdb parquet duckdb arrow feather");
        return d;
    }

    /// <summary>Folders whose extensionless files are programs.</summary>
    private static readonly HashSet<string> ProgramFolders = new(StringComparer.Ordinal) { "bin", "sbin", "libexec", "MacOS" };

    /// <summary>The extension that says what a file is: "libssl.so.3" → "so", "a.tar.gz" → "gz", none → null.</summary>
    public static string? ExtensionOf(string fileName)
    {
        int end = fileName.Length;
        while (true)
        {
            int dot = fileName.LastIndexOf('.', end - 1);
            if (dot <= 0 || dot == end - 1)
            {
                return null;
            }
            string ext = fileName[(dot + 1)..end];
            if (!ext.All(char.IsAsciiDigit))
            {
                return ext;
            }
            end = dot; // a version number: look at the part before it
            if (end <= 1)
            {
                return null;
            }
        }
    }

    public static TypeCategory CategoryOf(string fileName) =>
        ExtensionOf(fileName) is { } ext && ByExtension.TryGetValue(ext, out TypeCategory c) ? c : TypeCategory.Other;

    /// <summary>
    /// Visits every file with the category and extension it's counted under, and calls
    /// <paramref name="emptyFolder"/> for every readable empty folder. Checks for cancellation as it goes.
    /// </summary>
    private static void Walk(NodeStore s, CancellationToken cancel, Action<int, TypeCategory, string> file, Action? emptyFolder = null)
    {
        var stack = new Stack<(int Id, bool InApp, bool Programs)>();
        stack.Push((NodeStore.Root, false, false));
        int n = 0;
        while (stack.Count > 0)
        {
            if ((++n & 4095) == 0)
            {
                cancel.ThrowIfCancellationRequested();
            }
            var (dir, inApp, programs) = stack.Pop();
            foreach (int id in s.Children(dir))
            {
                NodeKind kind = s.Kind(id);
                if (kind == NodeKind.Directory)
                {
                    if (emptyFolder is not null && (s.Flags(id) & (NodeFlags.Error | NodeFlags.Pending)) == 0 && !s.Children(id).Any())
                    {
                        emptyFolder();
                    }
                    string dirName = s.Name(id);
                    stack.Push((id, inApp || dirName.EndsWith(".app", StringComparison.OrdinalIgnoreCase), ProgramFolders.Contains(dirName)));
                    continue;
                }
                if (kind != NodeKind.File)
                {
                    continue;
                }
                string name = s.Name(id);
                string? found = ExtensionOf(name);
                TypeCategory own = found is not null && ByExtension.TryGetValue(found, out TypeCategory c) ? c : TypeCategory.Other;
                TypeCategory cat = inApp || (programs && found is null) ? TypeCategory.Apps : own;
                file(id, cat, found?.ToLowerInvariant() ?? NoExtension);
            }
        }
    }

    /// <summary>How files without an extension are listed.</summary>
    public const string NoExtension = "(none)";

    public static TypeReport Analyze(NodeStore s, CancellationToken cancel = default)
    {
        var catBytes = new long[Enum.GetValues<TypeCategory>().Length];
        var catFiles = new long[catBytes.Length];
        var exts = new Dictionary<string, (long Bytes, long Files, TypeCategory Cat)>(StringComparer.Ordinal);
        long total = 0, empty = 0, zero = 0;
        Walk(s, cancel, (id, cat, ext) =>
        {
            long size = s.Size(id);
            // Extra names of a hard-linked file carry no size but aren't empty.
            if (s.Apparent(id) == 0 && (s.Flags(id) & NodeFlags.HardLinkDuplicate) == 0)
            {
                zero++;
            }
            catBytes[(int)cat] += size;
            catFiles[(int)cat]++;
            total += size;
            exts.TryGetValue(ext, out var e);
            // An extension is listed under what it is, even when some copies sit inside app bundles.
            TypeCategory own = ext == NoExtension ? TypeCategory.Other : ByExtension.GetValueOrDefault(ext, TypeCategory.Other);
            exts[ext] = (e.Bytes + size, e.Files + 1, own);
        }, () => empty++);
        var categories = Enum.GetValues<TypeCategory>()
            .Select(c => new TypeTotal(c, catBytes[(int)c], catFiles[(int)c]))
            .Where(t => t.Files > 0)
            .OrderByDescending(t => t.Bytes).ToList();
        var extensions = exts.Select(kv => new ExtensionTotal(kv.Key, kv.Value.Cat, kv.Value.Bytes, kv.Value.Files))
            .OrderByDescending(e => e.Bytes).ThenBy(e => e.Extension, StringComparer.Ordinal).ToList();
        return new TypeReport(categories, extensions, total, empty, zero);
    }

    /// <summary>
    /// The biggest files of one category, or with one extension (as listed in
    /// <see cref="TypeReport.Extensions"/>), biggest first, at most <paramref name="limit"/>.
    /// </summary>
    public static List<int> LargestFiles(NodeStore s, TypeCategory? category, string? extension, int limit = 5000, CancellationToken cancel = default)
    {
        var heap = new PriorityQueue<int, long>();
        Walk(s, cancel, (id, cat, ext) =>
        {
            if ((category is { } want && cat != want) || (extension is not null && ext != extension))
            {
                return;
            }
            long size = s.Size(id);
            if (heap.Count < limit)
            {
                heap.Enqueue(id, size);
            }
            else if (heap.TryPeek(out _, out long smallest) && size > smallest)
            {
                heap.DequeueEnqueue(id, size);
            }
        });
        var list = new List<int>(heap.Count);
        while (heap.Count > 0)
        {
            list.Add(heap.Dequeue());
        }
        // Biggest first; equal sizes in tree order so the list is stable.
        list.Sort((a, b) => s.Size(b) != s.Size(a) ? s.Size(b).CompareTo(s.Size(a)) : a.CompareTo(b));
        return list;
    }
}
