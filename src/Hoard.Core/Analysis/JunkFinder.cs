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

/// <summary>Finds known junk in a scanned tree (plan.md §6.1). Runs on a finished scan.</summary>
public static class JunkFinder
{
    public static JunkReport Find(NodeStore s, string rootPath, IReadOnlyList<JunkRule>? rules = null, string? home = null)
    {
        rules ??= JunkRules.BuiltIn;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pathMatchers = rules.Select(r => r.Paths is { Length: > 0 } ? new GlobMatcher(r.Paths, home) : null).ToArray();
        var found = rules.Select(_ => new List<int>()).ToArray();

        // Walk depth-first carrying full paths; a matched entry is taken whole (not descended into).
        var stack = new Stack<(int Id, string Path, bool Installed)>();
        bool rootInstalled = rootPath.Split('/').Any(seg => Array.IndexOf(JunkRules.InstalledTrees, seg) >= 0);
        stack.Push((NodeStore.Root, rootPath, rootInstalled));
        while (stack.Count > 0)
        {
            var (dir, dirPath, installed) = stack.Pop();
            List<int> kids = [.. s.Children(dir)];
            HashSet<string>? names = null;
            foreach (int id in kids)
            {
                NodeKind kind = s.Kind(id);
                if (kind is not (NodeKind.Directory or NodeKind.File))
                {
                    continue;
                }
                string name = s.Name(id);
                string path = dirPath == "/" ? "/" + name : dirPath + "/" + name;
                bool isDir = kind == NodeKind.Directory;
                int matched = -1;
                for (int r = 0; r < rules.Count && matched < 0; r++)
                {
                    JunkRule rule = rules[r];
                    if (pathMatchers[r] is { } pm && pm.IsExcluded("", path, isDir))
                    {
                        matched = r;
                    }
                    else if (isDir && !installed && rule.Names is { } ns && Array.IndexOf(ns, name) >= 0
                             && (rule.SiblingMarkers is null || HasAny(names ??= [.. kids.Select(s.Name)], rule.SiblingMarkers))
                             && (rule.InsideMarkers is null || HasAny([.. s.Children(id).Select(s.Name)], rule.InsideMarkers)))
                    {
                        matched = r;
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
                    stack.Push((id, path, installed || Array.IndexOf(JunkRules.InstalledTrees, name) >= 0));
                }
            }
        }

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
