using System.Text;
using System.Text.RegularExpressions;

namespace Hoard.Core.Filters;

/// <summary>
/// gitignore-style exclude patterns (plan.md §5.4).
/// <list type="bullet">
/// <item><c>*</c> matches within one path segment, <c>?</c> one character, <c>**</c> any number of segments.</item>
/// <item>A pattern without a slash (other than a trailing one) matches a name at any depth.</item>
/// <item>A leading <c>/</c> anchors the pattern to the scan root.</item>
/// <item>A pattern starting with <c>~/</c> (home) or <c>//</c> (file-system root, e.g. <c>//System/Volumes/**</c>)
/// is matched against the full path instead.</item>
/// <item>A trailing <c>/</c> matches directories only. <c>!</c> negates. The last matching pattern wins.</item>
/// </list>
/// </summary>
public sealed class GlobMatcher
{
    private readonly record struct Rule(Regex Regex, bool Negate, bool DirOnly, bool MatchFullPath);

    private readonly List<Rule> _rules = [];

    public GlobMatcher(IEnumerable<string> patterns, string? homeDirectory = null)
    {
        homeDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string raw in patterns)
        {
            string p = raw.Trim();
            if (p.Length == 0 || p.StartsWith('#'))
            {
                continue;
            }
            bool negate = p.StartsWith('!');
            if (negate)
            {
                p = p[1..];
            }
            bool dirOnly = p.EndsWith('/');
            p = p.TrimEnd('/');
            bool fullPath = false;
            if (p.StartsWith("~/", StringComparison.Ordinal))
            {
                p = homeDirectory.TrimEnd('/') + p[1..];
                fullPath = true;
            }
            else if (p.StartsWith("//", StringComparison.Ordinal))
            {
                p = p[1..];
                fullPath = true;
            }
            bool anchored = fullPath || p.Contains('/');
            if (!fullPath)
            {
                p = p.TrimStart('/');
            }
            if (p.Length == 0)
            {
                continue;
            }
            string body = Translate(p);
            string pattern = anchored ? $"^{body}$" : $"^(?:.*/)?{body}$";
            _rules.Add(new Rule(new Regex(pattern, RegexOptions.CultureInvariant), negate, dirOnly, fullPath));
        }
    }

    public bool IsEmpty => _rules.Count == 0;

    /// <param name="relativePath">Path relative to the scan root with '/' separators.</param>
    /// <param name="fullPath">Absolute path, used by <c>~/</c> and absolute patterns.</param>
    public bool IsExcluded(string relativePath, string fullPath, bool isDirectory)
    {
        bool excluded = false;
        foreach (Rule r in _rules)
        {
            if (r.DirOnly && !isDirectory)
            {
                continue;
            }
            if (r.Regex.IsMatch(r.MatchFullPath ? fullPath : relativePath))
            {
                excluded = !r.Negate;
            }
        }
        return excluded;
    }

    private static string Translate(string glob)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                bool slashAfter = i + 2 < glob.Length && glob[i + 2] == '/';
                if (slashAfter)
                {
                    sb.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    sb.Append(".*");
                    i += 1;
                }
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        return sb.ToString();
    }
}
