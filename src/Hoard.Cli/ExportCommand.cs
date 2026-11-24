using Hoard.Core.Analysis;
using Hoard.Core.Config;
using Hoard.Core.Export;
using Hoard.Core.Formatting;
using Hoard.Core.Model;
using Hoard.Core.Scanning;
using Hoard.Platform;

namespace Hoard.Cli;

/// <summary><c>hoard PATH --export FORMAT [-o FILE]</c>: the reports, without the interactive view.</summary>
internal static class ExportCommand
{
    public static int Write(ScanResult r, ExportFormat format, Settings settings, string? output, int depth, long minSize,
        bool includeFiles, bool apparent, bool dupes, Tty term, CancellationToken cancel)
    {
        NodeStore s = r.Store;
        string FullPath(int id)
        {
            string rel = s.RelativePath(id);
            return rel.Length == 0 ? r.RootPath : r.RootPath == "/" ? "/" + rel : r.RootPath + "/" + rel;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        DupReport? dup = null;
        Dictionary<int, long> own = [];
        if (dupes)
        {
            if (term.Interactive)
            {
                Console.Error.WriteLine("hoard: looking for duplicates (reading files)…");
            }
            dup = DuplicateFinder.Find(s, FullPath, cancel: cancel);
            ISharedBlocks blocks = SharedBlocks.ForCurrentPlatform();
            foreach (int id in dup.Groups.SelectMany(g => g.Files))
            {
                if (blocks.PrivateBytes(FullPath(id)) is long p)
                {
                    own[id] = p;
                }
            }
        }
        var data = new ExportData
        {
            Store = s,
            RootPath = r.RootPath,
            Roots = [NodeStore.Root],
            ScannedAt = r.ScannedAt,
            ExactSizes = r.ExactSizes,
            HardLinks = r.HardLinkDuplicates,
            HardLinkBytes = r.HardLinkBytesSaved,
            Disk = DiskSpace.For(r.RootPath),
            Types = FileTypes.Analyze(s, cancel),
            Junk = JunkFinder.Find(s, r.RootPath, cancel: cancel),
            BigOld = BigOld.Find(s, settings.BigOld, now, cancel),
            Empty = EmptyFinder.Find(s, cancel: cancel),
            Age = AgeBuckets.Analyze(s, NodeStore.Root, AgeAxis.Modified, now, 8, cancel),
            Dupes = dup,
            DupFrees = id => own.TryGetValue(id, out long p) ? Math.Min(p, s.Size(id)) : s.Size(id),
            Now = now,
        };
        var options = new ExportOptions(format, Math.Max(0, depth), minSize, includeFiles, apparent);
        try
        {
            if (output is null or "-")
            {
                using Stream stdout = Console.OpenStandardOutput();
                Exporter.Write(data, options, stdout);
            }
            else
            {
                string tmp = output + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    Exporter.Write(data, options, fs);
                }
                File.Move(tmp, output, overwrite: true);
                Console.Error.WriteLine($"hoard: wrote {Exporter.Name(format)} to {output} ({Units.Size(new FileInfo(output).Length)})");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("hoard: couldn't write the export: " + e.Message);
            return 1;
        }
        return 0;
    }
}
