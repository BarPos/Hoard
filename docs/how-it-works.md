---
title: How it works
---

# How it works

## Scanning

Hoard reads folders with several threads at once, using `lstat` directly. It never follows symbolic links, and it stops at other disks mounted inside the folder unless you ask (`--cross-fs`). Results are stored compactly, at about 80 bytes per entry, so a million files take well under 100 MB of memory. Totals update as each folder is read, which is why you can browse while the scan runs.

For example, Homebrew (316 000 entries) scans in about a second, and a whole Mac in under 20 seconds.

## Sizes that add up

- **On-disk size** is the space a file really takes (its allocated blocks). **Apparent size** is its length. `z` switches between them. They differ for sparse files, compressed files and very small files.
- **Hard links:** one file with several names is counted once. The choice of which name carries the size doesn't depend on scan order, so totals match `du`.
- **Firmlinks:** on macOS, folders like `/Users` appear twice, under `/` and under `/System/Volumes/Data`. Hoard counts them once.
- **APFS clones:** copies made with Finder's Duplicate or `cp -c` share disk blocks. Hoard asks macOS how much of each copy is its own: Duplicates shows what deleting a copy really frees, and the Overview says how much shared space the totals count twice. (It can't tell which files share with which, so it can't take that out of the totals themselves.)

## Junk, duplicates and the rest

All analysis runs in the background after the scan, so the screen never waits for it:
- **Junk** checks each folder against 28 rules. Rules like "`node_modules` next to a `package.json`" look at the folder's neighbours; rules like `~/Library/Caches/*` are quick lookups by parent folder. About 25 ns per entry.
- **File types** come from extensions, with a few smarter cases (versioned libraries, programs in `bin/`, app bundles). About 80 ms per million entries.
- **Duplicates** narrow candidates step by step: same size, then the same first 64 KB, then the same full hash (XxHash3, then XxHash128). They read about two files at a time and remember hashes between runs.
- **Installed software is left alone:** Empty, Duplicates and Big & old don't look inside Homebrew's `Cellar`, `node_modules`, `site-packages`, app bundles or `.git`. Files there belong to something else, and removing them breaks it.

## Deleting safely

- Deleting moves things to the macOS Trash through the system's own API, and `u` in Hoard undoes the last move.
- Before deleting, Hoard checks each item again and skips anything that changed.
- It refuses system folders and the top folders of your home.
- Nested marks are handled once: deleting a marked folder also covers marked items inside it.

## Snapshots

A snapshot holds folder totals only (path, sizes, counts, times), never individual files. It's compressed with Brotli, and paths share their common starts, so snapshots stay small. Comparing two snapshots matches folders by path.

## Watching

Watch mode uses the system's file events (FSEvents on macOS). It waits until changes settle for a quarter of a second, then rescans only the folders that changed, one at a time, and puts the result in place without a full scan.

## Privacy

Hoard reads file names, sizes and dates, and reads file contents only to find duplicates. Nothing leaves your machine: there's no network access, no telemetry, and no account.

## Built with

C# and .NET 10, compiled ahead of time (Native AOT) into one binary, with [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) for the terminal and [System.CommandLine](https://github.com/dotnet/command-line-api) for arguments. Licences are in [THIRD-PARTY-NOTICES.txt](https://github.com/BarPos/Hoard/blob/main/THIRD-PARTY-NOTICES.txt). The screenshots on this site come from the real app: `tools/Hoard.Screenshots` scans a made-up home folder and renders each screen as SVG.
