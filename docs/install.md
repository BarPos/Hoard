---
title: Install
---

# Install

Hoard runs on **macOS on Apple Silicon** (M1 and later). It's a single native binary of about 12 MB, with nothing else to install.

## The one-liner

```bash
curl -fsSL https://github.com/BarPos/Hoard/releases/latest/download/install.sh | bash
```

The installer asks two questions:

- **For you, or for all users?** For you puts `hoard` in `~/.local/bin` and needs no password. If that folder isn't on your `PATH` yet, the installer adds it to your shell profile. All users puts it in `/usr/local/bin` and asks for your password through `sudo`.
- **Which version?** The latest, unless you say otherwise.

It checks the download's SHA-256 before installing, then runs `hoard --version` to make sure the new binary works.

## Update, pick a version, remove

Run the installer again with a command. With `curl … | bash`, pass the arguments after `-s`:

```bash
curl -fsSL https://github.com/BarPos/Hoard/releases/latest/download/install.sh | bash -s update
curl -fsSL https://github.com/BarPos/Hoard/releases/latest/download/install.sh | bash -s install --version 1.0.0
curl -fsSL https://github.com/BarPos/Hoard/releases/latest/download/install.sh | bash -s uninstall
```

You can also save it once and run it directly:

```bash
curl -fsSLO https://github.com/BarPos/Hoard/releases/latest/download/install.sh
chmod +x install.sh
./install.sh help
```

| Command | Does |
|---|---|
| `install` | installs (asks for scope and version unless you pass them) |
| `update` | installs the newest release wherever Hoard is already installed |
| `uninstall` | removes the binary; asks before removing settings and data |
| `list` | lists the versions you can install |
| `status` | shows where Hoard is installed, and which version |
| `help` | shows all of this |

| Option | Means |
|---|---|
| `--version X.Y.Z` | install that version instead of the latest |
| `--user` | install for you, in `~/.local/bin` |
| `--system` | install for all users, in `/usr/local/bin` |
| `--purge` | with `uninstall`: also remove your settings, snapshots and hash cache |
| `--yes` | never ask; use the defaults (for you, the latest version) |

`HOARD_USER_DIR` and `HOARD_SYSTEM_DIR` change the two install folders.

## Manually

Each [release](https://github.com/BarPos/Hoard/releases) has `hoard-<version>-macos-arm64.tar.gz`, its `.sha256` and `install.sh`:

```bash
shasum -a 256 -c hoard-1.0.0-macos-arm64.tar.gz.sha256
tar -xzf hoard-1.0.0-macos-arm64.tar.gz
mv hoard ~/.local/bin/
```

The binary isn't notarised yet. If macOS blocks a binary you downloaded through a browser, run `xattr -d com.apple.quarantine ~/.local/bin/hoard` once. The installer handles this for you.

## From source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/BarPos/Hoard.git
cd Hoard
dotnet test Hoard.slnx                       # about 270 tests
dotnet run --project src/Hoard.Cli -- ~      # run it from source
build/package.sh                             # the release binary, in dist/
```

`build/package.sh` makes the same Native AOT binary and archive as a release.

## Making a release

Releases are made by hand:

1. Put the new version in the `VERSION` file (the app shows it in `--version`, the splash and help).
2. Push to `main`.
3. In GitHub, run **Actions → Release → Run workflow**. It tests, builds the Apple Silicon binary, and publishes a release tagged `v<version>` with the archive, its checksum and `install.sh`.

Nothing runs automatically on push.
