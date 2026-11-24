using System.Text;

namespace Hoard.Screenshots;

/// <summary>A made-up home folder: what a developer's Mac might hold. Names and sizes are invented.</summary>
internal static class Demo
{
    public enum Phase { Earlier, Now }

    private const long G = 1_000_000_000, M = 1_000_000, K = 1_000;

    /// <param name="Earlier">Present in the earlier snapshots.</param>
    /// <param name="Gone">Deleted since (present only earlier).</param>
    /// <param name="Same">Copies with the same key get identical contents (duplicates).</param>
    private sealed record E(string Path, long Size, int Days, bool Earlier = true, bool Gone = false, string? Same = null);

    private static readonly E[] Files =
    [
        new("Library/Caches/com.spotify.client/Data/storage.bin", 3_200 * M, 2),
        new("Library/Caches/Homebrew/downloads/ffmpeg--7.1.arm64_tahoe.bottle.tar.gz", 1_400 * M, 20),
        new("Library/Caches/Homebrew/downloads/node--24.2.arm64_tahoe.bottle.tar.gz", 700 * M, 40),
        new("Library/Caches/com.apple.Safari/WebKitCache/Version 18/Blobs/cache.bin", 820 * M, 1),
        new("Library/Containers/com.docker.docker/Data/vms/0/data/Docker.raw", 38_400 * M, 1),
        new("Library/Developer/Xcode/DerivedData/Hoard-bxqfaerc/Build/Intermediates.noindex/main.o", 9_800 * M, 6),
        new("Library/Developer/CoreSimulator/Devices/4F2A91C0/data/container.img", 12_100 * M, 120),
        new("Library/Application Support/Code/CachedData/9f3a/cache.bin", 1_900 * M, 3),
        new("Library/Application Support/Slack/Cache/Cache_Data/data_1", 2_400 * M, 2),
        new("Library/Mail/V10/Mailboxes/Archive.mbox/messages.db", 4_100 * M, 400),
        new("Library/Logs/DiagnosticReports/Hoard-2025-06-01.ips", 120 * M, 300),
        new("Library/Logs/app.log", 0, 30),
        new("Library/Logs/sync.log", 0, 60),
        new("Library/Logs/update.log", 0, 90),

        new("dev/monolith/package.json", 2 * K, 3),
        new("dev/monolith/node_modules/react/index.js", 420 * M, 30),
        new("dev/monolith/node_modules/@babel/core/lib/index.js", 1_200 * M, 30),
        new("dev/monolith/node_modules/typescript/lib/typescript.js", 820 * M, 30),
        new("dev/monolith/node_modules/next/dist/server.js", 2_100 * M, 30),
        new("dev/monolith/node_modules/webpack/lib/index.js", 700 * M, 30),
        new("dev/monolith/node_modules/esbuild/bin/esbuild", 300 * M, 30),
        new("dev/monolith/src/app.ts", 48 * M, 1),
        new("dev/monolith/src/components/Chart.tsx", 210 * M, 2),
        new("dev/monolith/.git/objects/pack/pack-9f3ac21e.pack", 6_200 * M, 2),
        new("dev/monolith/dist/bundle.js", 4_100 * M, 3),
        new("dev/game-engine/CMakeLists.txt", 4 * K, 8),
        new("dev/game-engine/build/Release/engine.o", 7_300 * M, 10),
        new("dev/game-engine/assets/textures/terrain.ktx2", 11_200 * M, 200),
        new("dev/game-engine/third_party/physx/source/physx.a", 2_400 * M, 500),
        new("dev/ml-sandbox/requirements.txt", 1 * K, 60),
        new("dev/ml-sandbox/.venv/pyvenv.cfg", 1 * K, 60),
        new("dev/ml-sandbox/.venv/lib/python3.14/site-packages/torch/lib/libtorch.dylib", 2_100 * M, 60),
        new("dev/ml-sandbox/checkpoints/run-042/model.pt", 6_400 * M, 60),
        new("dev/ml-sandbox/checkpoints/run-043/model.pt", 6_400 * M, 2, Earlier: false),
        new("dev/ml-sandbox/data/dataset-v3.parquet", 1_200 * M, 120, Same: "dataset"),
        new("dev/ml-sandbox/src/__init__.py", 0, 60),
        new("dev/ml-sandbox/src/utils/__init__.py", 0, 60),
        new("dev/website/package.json", 1 * K, 400),
        new("dev/website/node_modules/next/dist/server.js", 1_900 * M, 400),
        new("dev/website/.next/cache/webpack/client.pack", 610 * M, 400),
        new("dev/website/.nojekyll", 0, 400),
        new("dev/rust-cli/Cargo.toml", 1 * K, 14),
        new("dev/rust-cli/target/debug/rust-cli", 2_200 * M, 14),
        new("dev/hoard/src/Program.cs", 90 * K, 1),

        new("Movies/holiday-2025.mov", 1_800 * M, 460, Same: "holiday"),
        new("Movies/Final Cut/wedding-raw/A001_C014.mov", 3_200 * M, 1180),
        new("Movies/Final Cut/wedding-raw/A001_C015.mov", 2_700 * M, 1180),
        new("Movies/screen-recordings/2026-11-21 10.14.mov", 2_400 * M, 3, Earlier: false),
        new("Pictures/Photos Library.photoslibrary/originals/0/IMG_4821.HEIC", 38_900 * M, 300),
        new("Pictures/exports/portfolio-2023.zip", 1_800 * M, 900),
        new("Downloads/fedora-35-workstation.iso", 2_600 * M, 1400),
        new("Downloads/Xcode_26.1.xip", 900 * M, 30, Same: "xcode"),
        new("Downloads/dataset-v3.parquet", 1_200 * M, 25, Same: "dataset"),
        new("Downloads/AirDrop/holiday-2025.mov", 1_800 * M, 459, Same: "holiday"),
        new("Downloads/old-backup.zip", 1_200 * M, 700, Gone: true),
        new("Downloads/BlenderAssets.zip", 900 * M, 1330),
        new("Downloads/zoom-recording-2022-07-13.mp4", 640 * M, 1590),
        new("Documents/VMs/old-windows-disk.vmdk", 2_100 * M, 1480),
        new("Documents/backup/phone-backup-2025.tar", 800 * M, 400),
        new("Documents/backup/ml/dataset-v3.parquet", 1_200 * M, 10, Same: "dataset"),
        new("Documents/notes/2026-11-23.md", 4 * K, 1),
        new("Documents/taxes/2025-return.pdf", 3 * M, 200),
        new("Music/Logic/Projects/demo-tape/Audio Files/take-1.wav", 6_200 * M, 1200),
        new("Music/Music/Media.localized/library.musicdb", 6_400 * M, 1500),
        new(".npm/_cacache/content-v2/sha512/9c/a1.bin", 4_800 * M, 5),
        new(".cache/pip/http-v2/c/d/blob.bin", 1_400 * M, 30),
        new(".docker/machine/machines/default/disk.vmdk", 9_100 * M, 4),
        new("Desktop/Xcode_26.1.xip", 900 * M, 32, Same: "xcode"),
        new("Desktop/old-screens.zip", 400 * M, 990),
    ];

    /// <summary>Lots of ordinary small files, so counts and times look like a real home folder.</summary>
    private static readonly (string Pattern, int Count, long Min, long Max, int Days)[] Fillers =
    [
        ("dev/monolith/node_modules/pkg-{0}/index.js", 1400, 20 * K, 400 * K, 30),
        ("Pictures/Photos Library.photoslibrary/resources/derivatives/{1}/IMG_{0}.jpg", 2400, 200 * K, 900 * K, 300),
        (".npm/_cacache/index-v5/{1}/{0}", 900, 1 * K, 40 * K, 5),
        ("Library/Caches/com.apple.Safari/WebKitCache/Version 18/Records/{1}/{0}.blob", 600, 4 * K, 300 * K, 1),
        ("dev/game-engine/assets/textures/tex_{0}.ktx2", 420, 2 * M, 18 * M, 200),
        ("Documents/notes/{0}.md", 260, 2 * K, 30 * K, 90),
        ("Music/Music/Media.localized/Music/track-{0}.m4a", 380, 4 * M, 9 * M, 900),
        ("dev/hoard/src/File{0}.cs", 70, 2 * K, 40 * K, 1),
        ("Library/Application Support/Code/User/History/{1}/{0}.json", 500, 1 * K, 60 * K, 3),
    ];

    private static readonly string[] EmptyFolders =
    [
        "Documents/old-project/tmp", "Documents/old-project/fixtures", "dev/monolith/packages/legacy/old-api/v1",
        "Downloads/untitled folder",
    ];

    /// <summary>Writes the folder as it was (scaled down a little) or as it is now, and tells the probe the sizes.</summary>
    public static void Write(string home, FakeProbe probe, long now, Phase phase, double scale)
    {
        probe.Files.Clear();
        foreach (E e in Files)
        {
            string full = Path.Combine(home, e.Path);
            bool present = phase == Phase.Earlier ? e.Earlier : !e.Gone;
            if (!present)
            {
                if (File.Exists(full))
                {
                    File.Delete(full);
                }
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (!File.Exists(full))
            {
                // Real contents are tiny; copies of one thing share them, everything else differs.
                File.WriteAllBytes(full, e.Size == 0 ? [] : Encoding.UTF8.GetBytes(e.Same ?? e.Path));
            }
            long size = phase == Phase.Earlier && e.Same is null ? (long)(e.Size * scale) : e.Size;
            probe.Files[full] = (size, now - e.Days * 86400L);
        }
        foreach (var (pattern, count, min, max, days) in Fillers)
        {
            for (int i = 0; i < count; i++)
            {
                string full = Path.Combine(home, string.Format(System.Globalization.CultureInfo.InvariantCulture, pattern, i, (i % 16).ToString("x")));
                if (!File.Exists(full))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, full);
                }
                // A spread of sizes and ages, the same every run.
                long size = min + (long)((uint)(i * 2654435761u) % (ulong)Math.Max(1, max - min));
                probe.Files[full] = ((long)(size * (phase == Phase.Earlier ? scale : 1)), now - (days + i % 37) * 86400L);
            }
        }
        foreach (string dir in EmptyFolders)
        {
            Directory.CreateDirectory(Path.Combine(home, dir));
        }
    }
}
