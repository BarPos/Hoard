using Hoard.Core.Filters;
using Hoard.Core.Model;

namespace Hoard.Core.Analysis;

/// <summary>All items one rule found, biggest first.</summary>
public sealed class JunkCategory(JunkRule rule, List<int> items)
{
    public JunkRule Rule { get; } = rule;
    public List<int> Items { get; } = items;

    /// <summary>Current total (items deleted since the report was made no longer count).</summary>
    public long Size(NodeStore s) => Items.Where(i => !s.IsDeleted(i)).Sum(i => s.Size(i));
    public int Count(NodeStore s) => Items.Count(i => !s.IsDeleted(i));
}

public sealed class JunkReport(List<JunkCategory> categories)
{
    public List<JunkCategory> Categories { get; } = categories;
    public long Total(NodeStore s) => Categories.Sum(c => c.Size(s));
    public long SafeTotal(NodeStore s) => Categories.Where(c => c.Rule.Safety == Safety.Safe).Sum(c => c.Size(s));
}

/// <summary>Live counter for a running junk search, safe to read from another thread.</summary>
public sealed class JunkProgress
{
    private long _folders;
    public long FoldersChecked => Interlocked.Read(ref _folders);
    internal void Add(long n) => Interlocked.Add(ref _folders, n);
}

/// <summary>
/// Finds known junk in a scanned tree (plan.md §6.1). Designed for millions of entries:
/// path rules like <c>~/Library/Caches/*</c> become hash lookups on the parent folder, files
/// are only looked at where a path rule applies to their folder, and full paths are built for
/// folders only.
/// </summary>
public static class JunkFinder
{
    private readonly record struct PathRule(int Rule, string Pattern);

    public static JunkReport Find(NodeStore s, string rootPath, IReadOnlyList<JunkRule>? rules = null, string? home = null,
        JunkProgress? progress = null, CancellationToken cancel = default)
    {
        rules ??= JunkRules.BuiltIn;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        home = home.TrimEnd('/');

        // Path patterns, keyed by the folder they apply in. "~/Library/Caches/*" → parent
        // "/Users/x/Library/Caches", last segment "*". Patterns with wildcards before the last
        // segment fall back to a (slower) full-path matcher.
        var byParent = new Dictionary<string, List<PathRule>>(StringComparer.Ordinal);
        var slow = new List<(int Rule, GlobMatcher Matcher)>();
        for (int r = 0; r < rules.Count; r++)
        {
            foreach (string raw in rules[r].Paths ?? [])
            {
                string full = raw.StartsWith("~/", StringComparison.Ordinal) ? home + raw[1..] : raw;
                int cut = full.LastIndexOf('/');
                string parent = cut <= 0 ? "/" : full[..cut], last = full[(cut + 1)..];
                if (parent.IndexOfAny(['*', '?']) >= 0)
                {
                    slow.Add((r, new GlobMatcher(["//" + full.TrimStart('/')], home)));
                    continue;
                }
                if (!byParent.TryGetValue(parent, out var list))
                {
                    byParent[parent] = list = [];
                }
                list.Add(new PathRule(r, last));
            }
        }
        var names = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int r = 0; r < rules.Count; r++)
        {
            foreach (string n in rules[r].Names ?? [])
            {
                if (!names.TryGetValue(n, out var list))
                {
                    names[n] = list = [];
                }
                list.Add(r);
            }
        }
        var installed = new HashSet<string>(JunkRules.InstalledTrees, StringComparer.Ordinal);
        var found = rules.Select(_ => new List<int>()).ToArray();

        var stack = new Stack<(int Id, string Path, bool Installed)>();
        bool rootInstalled = rootPath.Split('/').Any(installed.Contains);
        stack.Push((NodeStore.Root, rootPath, rootInstalled));
        int sinceReport = 0;
        while (stack.Count > 0)
        {
            if ((++sinceReport & 1023) == 0)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Add(1024);
            }
            var (dir, dirPath, inInstalled) = stack.Pop();
            byParent.TryGetValue(dirPath, out List<PathRule>? here);
            HashSet<string>? siblings = null;

            foreach (int id in s.Children(dir))
            {
                NodeKind kind = s.Kind(id);
                bool isDir = kind == NodeKind.Directory;
                if (!isDir && (kind != NodeKind.File || (here is null && slow.Count == 0)))
                {
                    continue; // a file with no path rule for its folder can't be junk
                }
                string name = s.Name(id);
                int matched = -1;

                if (here is not null)
                {
                    foreach (PathRule pr in here)
                    {
                        if (WildcardMatch(pr.Pattern, name) && (matched < 0 || pr.Rule < matched))
                        {
                            matched = pr.Rule;
                        }
                    }
                }
                if (slow.Count > 0)
                {
                    string path = dirPath == "/" ? "/" + name : dirPath + "/" + name;
                    foreach (var (r, m) in slow)
                    {
                        if ((matched < 0 || r < matched) && m.IsExcluded("", path, isDir))
                        {
                            matched = r;
                        }
                    }
                }
                if (isDir && !inInstalled && names.TryGetValue(name, out List<int>? candidates))
                {
                    foreach (int r in candidates)
                    {
                        if (matched >= 0 && r > matched)
                        {
                            continue;
                        }
                        JunkRule rule = rules[r];
                        if (rule.SiblingMarkers is not null && !HasAny(siblings ??= [.. s.Children(dir).Select(s.Name)], rule.SiblingMarkers))
                        {
                            continue;
                        }
                        if (rule.InsideMarkers is not null && !HasAny([.. s.Children(id).Select(s.Name)], rule.InsideMarkers))
                        {
                            continue;
                        }
                        matched = r;
                        break;
                    }
                }

                if (matched >= 0)
                {
                    if (s.Size(id) > 0)
                    {
                        found[matched].Add(id);
                    }
                }
                else if (isDir)
                {
                    string path = dirPath == "/" ? "/" + name : dirPath + "/" + name;
                    stack.Push((id, path, inInstalled || installed.Contains(name)));
                }
            }
        }
        progress?.Add(sinceReport & 1023);

        var categories = new List<JunkCategory>();
        for (int r = 0; r < rules.Count; r++)
        {
            if (found[r].Count > 0)
            {
                found[r].Sort((a, b) => s.Size(b).CompareTo(s.Size(a)));
                categories.Add(new JunkCategory(rules[r], found[r]));
            }
        }
        categories.Sort((a, b) => b.Size(s).CompareTo(a.Size(s)));
        return new JunkReport(categories);
    }

    /// <summary>Matches one path segment against a pattern with <c>*</c> and <c>?</c>.</summary>
    internal static bool WildcardMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }

    private static bool HasAny(HashSet<string> names, string[] markers)
    {
        foreach (string m in markers)
        {
            if (m.StartsWith('*'))
            {
                string suffix = m[1..];
                if (names.Any(n => n.EndsWith(suffix, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
            else if (names.Contains(m))
            {
                return true;
            }
        }
        return false;
    }
}
