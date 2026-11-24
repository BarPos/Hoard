---
title: Command line
---

# Command line

Without a terminal attached, or with `--no-tui`, `--json` or `--export`, Hoard prints its results instead of opening the interactive view. Every command honours your [settings](configuration.html): exclude patterns, units, threads and so on.

## `hoard [path]`

```bash
hoard                       # the interactive view, for the current folder
hoard ~ --no-tui -n 20      # print the 20 biggest entries
hoard ~ --json > scan.json  # the same, as JSON
```

| Option | Does |
|---|---|
| `--no-tui` | print a summary instead of opening the interactive view |
| `-n`, `--top N` | how many of the biggest entries to list (default 10) |
| `--json` | write the summary as JSON |
| `--export FORMAT` | write a report: `json`, `csv`, `html`, `pretty-html`, `markdown` or `ncdu` |
| `-o`, `--output FILE` | where `--export` writes (default: standard output) |
| `--depth N` | folder levels in an export (0 = all; pretty HTML defaults to 6) |
| `--min-size SIZE` | leave smaller entries out of an export, e.g. `1MB` |
| `--folders-only` | exports list folders only, not files |
| `--dupes` | include duplicates in the export (reads file contents) |
| `--fail-over SIZE` | exit with code 3 if the scanned total is over this, e.g. `50GB` |
| `--apparent` | file lengths instead of space used on disk |
| `--iec` | powers of 1024 (KiB, MiB, GiB) |
| `-x`, `--exclude PATTERN` | skip what matches (repeatable; [pattern syntax](actions.html#leave-things-out)) |
| `--threads N` | scanner threads (0 = automatic) |
| `--cross-fs` | scan into other disks mounted inside the folder |
| `--theme NAME` | a theme for this run: `hoard`, `neon`, `frost`, `mono`, `ember`, `forest`, `ocean`, `sunset`, `lilac`, `amber`, `phosphor`, `paper` or `daylight` |
| `--animate` · `--no-animate` | show or skip the splash |
| `--no-color` | no colours (`NO_COLOR` works too) |
| `--stats` | scanner statistics on stderr |
| `--version` · `--help` | the version · all of this |

Sizes can be written as `1500`, `200MB`, `1.5GB`, `10k` or `2GiB`.

## Reports

Each report scans the folder (`.` by default), then prints:

| Command | Prints |
|---|---|
| `hoard junk [path]` | known junk by category, with how much is safe to remove (`--json`, `--items N`) |
| `hoard types [path]` | the space by file type, and the biggest extensions |
| `hoard old [path]` | big files untouched for a long time (`--min-size`, `--days`, `--accessed`, `-n`) |
| `hoard empty [path]` | empty folders and zero-byte files (`-n`, `--keep-files-count`) |
| `hoard dupes [path]` | duplicate files, with how much deleting the extra copies would free (`--min-size`, `-n`) |

```bash
hoard old ~ --min-size 500MB --days 730
hoard dupes ~/Downloads --min-size 10MB -n 50
hoard junk ~/dev --json | jq '.categories[] | {name, bytes}'
```

## Snapshots

```bash
hoard snapshot ~                 # scan and save a snapshot
hoard diff last now --path ~     # what changed since the last snapshot
hoard diff 2026-11-01 last --path ~
```

`diff BASE COMPARE` takes `last`, a number (`1` is the newest), the start of a date (`2026-11-16`), or `now` (scan again), and lists the biggest changes two levels deep.

## Watching

```bash
hoard watch ~/Downloads          # print changes as they happen, until Ctrl-C
hoard watch ~ --log | tee changes.jsonl
```

With `--log`, each change is one JSON object per line: `{"time": …, "event": "created", "path": …, "size": …}`.

## Checks for scripts and cron

```bash
hoard check / --fail-above 90%           # is the disk over 90% full?
hoard check ~/builds --fail-above 50GB   # is this folder over 50 GB?
hoard ~/builds --no-tui --fail-over 50GB # the same, with a summary
```

## Exit codes

| Code | Means |
|---|---|
| `0` | fine |
| `1` | something went wrong (a missing folder, a file that couldn't be written) |
| `2` | a mistake in the command (unknown option, a size that isn't one) |
| `3` | over the limit given to `--fail-over` or `check --fail-above` |
| `130` | interrupted with `Ctrl-C` |
