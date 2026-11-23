namespace Hoard.Core.Analysis;

public enum Safety
{
    /// <summary>Recreated automatically (rebuilt, re-downloaded, regenerated) when needed.</summary>
    Safe,
    /// <summary>Usually fine to remove, but worth a look first (could be slow or impossible to get back).</summary>
    Review,
}

/// <summary>
/// One kind of known junk (plan.md §6.1). An entry matches when any of these holds:
/// <list type="bullet">
/// <item>its name is in <see cref="Names"/> and, if <see cref="SiblingMarkers"/> is set, its parent
///   folder contains one of them (e.g. <c>node_modules</c> next to <c>package.json</c>), and, if
///   <see cref="InsideMarkers"/> is set, it contains one of them itself;</item>
/// <item>its full path matches one of <see cref="Paths"/> (<c>~</c> is the home folder); files and folders both count.</item>
/// </list>
/// Markers may use <c>*</c>, e.g. <c>*.csproj</c>. A matched folder is taken whole and not looked into further.
/// Name rules never match inside <see cref="JunkRules.InstalledTrees"/> (installed software, not your projects):
/// e.g. npm's own <c>node_modules</c> under a global install would break npm if deleted.
/// </summary>
public sealed record JunkRule(
    string Id,
    string Name,
    Safety Safety,
    string Why,
    string[]? Names = null,
    string[]? SiblingMarkers = null,
    string[]? InsideMarkers = null,
    string[]? Paths = null,
    string? Command = null);

public static class JunkRules
{
    /// <summary>Folders that hold installed software; project-style rules don't look inside them.</summary>
    public static readonly string[] InstalledTrees =
        ["node_modules", "Cellar", "Caskroom", "site-packages", "dist-packages", ".cargo", ".rustup", ".nvm", ".pyenv", "Frameworks"];

    /// <summary>
    /// Built-in rules, most specific first: the first rule that matches an entry wins.
    /// User rules from ~/.config/hoard/junk.toml are planned (plan.md §6.1).
    /// </summary>
    public static readonly IReadOnlyList<JunkRule> BuiltIn =
    [
        // ── Xcode / Apple development ────────────────────────────────────────
        new("xcode-derived-data", "Xcode DerivedData", Safety.Safe, "rebuilt by Xcode on the next build",
            Paths: ["~/Library/Developer/Xcode/DerivedData/*"]),
        new("xcode-device-support", "Xcode device support files", Safety.Safe, "re-created when a device is connected again",
            Paths: ["~/Library/Developer/Xcode/iOS DeviceSupport/*", "~/Library/Developer/Xcode/watchOS DeviceSupport/*",
                    "~/Library/Developer/Xcode/tvOS DeviceSupport/*", "~/Library/Developer/Xcode/visionOS DeviceSupport/*"]),
        new("xcode-archives", "Xcode archives", Safety.Review, "old app builds; keep the ones you may need to symbolicate crash reports",
            Paths: ["~/Library/Developer/Xcode/Archives/*"]),
        new("simulator-caches", "Simulator caches", Safety.Safe, "simulator caches, recreated on demand",
            Paths: ["~/Library/Developer/CoreSimulator/Caches/*"], Command: "xcrun simctl delete unavailable"),

        // ── Project build outputs (need the project file next to them) ──────
        new("node-modules", "node_modules", Safety.Safe, "reinstalled by npm / yarn / pnpm install",
            Names: ["node_modules"], SiblingMarkers: ["package.json"]),
        new("js-build-caches", "JavaScript build caches", Safety.Safe, "framework build caches, rebuilt on the next build",
            Names: [".next", ".nuxt", ".parcel-cache", ".turbo", ".svelte-kit", ".angular", ".vite"], SiblingMarkers: ["package.json"]),
        new("dotnet-build", ".NET bin / obj", Safety.Safe, "rebuilt by dotnet build",
            Names: ["bin", "obj"], SiblingMarkers: ["*.csproj", "*.fsproj", "*.vbproj", "*.sln", "*.slnx"]),
        new("rust-target", "Rust target", Safety.Safe, "rebuilt by cargo build",
            Names: ["target"], SiblingMarkers: ["Cargo.toml"]),
        new("maven-target", "Maven target", Safety.Safe, "rebuilt by mvn package",
            Names: ["target"], SiblingMarkers: ["pom.xml"]),
        new("gradle-build", "Gradle build outputs", Safety.Safe, "rebuilt by gradle",
            Names: ["build", ".gradle"], SiblingMarkers: ["build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"]),
        new("swift-build", "Swift package builds", Safety.Safe, "rebuilt by swift build",
            Names: [".build"], SiblingMarkers: ["Package.swift"]),
        new("cmake-build", "CMake build folders", Safety.Safe, "rebuilt by cmake",
            Names: ["cmake-build-debug", "cmake-build-release", "cmake-build-relwithdebinfo"], SiblingMarkers: ["CMakeLists.txt"]),
        new("dart-tool", "Dart / Flutter tool caches", Safety.Safe, "rebuilt by flutter pub get",
            Names: [".dart_tool"], SiblingMarkers: ["pubspec.yaml"]),
        new("terraform", "Terraform plugin caches", Safety.Safe, "re-downloaded by terraform init",
            Names: [".terraform"], SiblingMarkers: ["*.tf"]),

        // ── Python ───────────────────────────────────────────────────────────
        new("python-caches", "Python caches", Safety.Safe, "bytecode and tool caches, recreated automatically",
            Names: ["__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".hypothesis", ".tox", ".nox"]),
        new("python-venv", "Python virtual environments", Safety.Review, "recreate with python -m venv and pip install",
            Names: [".venv", "venv", "env", "virtualenv"], InsideMarkers: ["pyvenv.cfg"]),

        // ── Package-manager and tool caches in the home folder ──────────────
        new("npm-cache", "npm · yarn · pnpm caches", Safety.Safe, "package caches, re-downloaded when needed",
            Paths: ["~/.npm/_cacache", "~/.npm/_npx", "~/Library/pnpm/store", "~/.pnpm-store", "~/.yarn/berry/cache"]),
        new("cargo-cache", "Cargo registry cache", Safety.Safe, "downloaded crates, re-downloaded when needed",
            Paths: ["~/.cargo/registry/cache", "~/.cargo/registry/src", "~/.cargo/git/checkouts"]),
        new("gradle-cache", "Gradle caches", Safety.Safe, "re-downloaded by the next Gradle build",
            Paths: ["~/.gradle/caches", "~/.gradle/wrapper/dists"]),
        new("nuget-packages", "NuGet packages", Safety.Review, "restored on the next build; slow offline",
            Paths: ["~/.nuget/packages"]),
        new("maven-repo", "Maven repository", Safety.Review, "re-downloaded on the next build; slow offline",
            Paths: ["~/.m2/repository"]),
        new("go-mod-cache", "Go module cache", Safety.Safe, "re-downloaded by go build",
            Paths: ["~/go/pkg/mod"], Command: "go clean -modcache"),
        new("docker-data", "Docker disk image", Safety.Review, "images, containers and volumes; prune instead of deleting the disk",
            Paths: ["~/Library/Containers/com.docker.docker/Data/vms"], Command: "docker system prune"),

        // ── App caches and logs ─────────────────────────────────────────────
        new("user-caches", "App caches (~/Library/Caches)", Safety.Safe, "apps recreate their caches",
            Paths: ["~/Library/Caches/*"]),
        new("xdg-caches", "App caches (~/.cache)", Safety.Review, "tools recreate these, but some hold large downloads (models, browsers)",
            Paths: ["~/.cache/*"]),
        new("logs", "Logs and crash reports", Safety.Safe, "old log files; apps start new ones",
            Paths: ["~/Library/Logs/*"]),
        new("trash", "Trash", Safety.Review, "already deleted; empty the Trash to free this",
            Paths: ["~/.Trash/*"]),

        // ── Downloads ───────────────────────────────────────────────────────
        new("installers", "Downloaded installers", Safety.Review, "disk images and installers you have probably already used",
            Paths: ["~/Downloads/*.dmg", "~/Downloads/*.pkg", "~/Downloads/*.iso", "~/Downloads/*.xip", "~/Downloads/*.msi", "~/Downloads/*.exe"]),
    ];
}
