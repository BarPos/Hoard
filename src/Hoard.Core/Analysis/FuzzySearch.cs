using Hoard.Core.Model;

namespace Hoard.Core.Analysis;

public enum SearchKind { Any, Folders, Files }

public sealed record SearchHit(int Node, int Score);

public sealed class SearchResult(List<SearchHit> hits, int total, TimeSpan took)
{
    /// <summary>The best matches, best first (at most the limit asked for).</summary>
    public List<SearchHit> Hits { get; } = hits;
    /// <summary>How many entries matched in all.</summary>
    public int Total { get; } = total;
    public TimeSpan Took { get; } = took;
}

/// <summary>
/// fzf-style fuzzy matching over a whole scan (plan.md §7.5). Every space-separated word of the
/// query must appear in the path, letters in order; the score favours runs of consecutive letters,
/// letters at the start of a path segment or word, and matches in the entry's own name.
/// </summary>
public static class FuzzySearch
{
    /// <summary>Scores <paramref name="text"/> against one query word; -1 when it doesn't match. Case-insensitive.</summary>
    public static int Score(string text, string word, int nameStart, List<int>? positions = null)
    {
        if (word.Length == 0)
        {
            return 0;
        }
        // Find the match that ends as late as possible in the name: scan backwards greedily from the
        // end, then forwards to tighten it (the usual two-pass fzf approach).
        int ti = text.Length - 1, wi = word.Length - 1;
        while (ti >= 0 && wi >= 0)
        {
            if (char.ToLowerInvariant(text[ti]) == char.ToLowerInvariant(word[wi]))
            {
                wi--;
            }
            ti--;
        }
        if (wi >= 0)
        {
            return -1;
        }
        int start = ti + 1;
        // Letters scattered across a long path aren't a match anyone means: keep each word compact.
        int window = word.Length * 5 + 6;
        // Forward from there, scoring.
        int score = 0, run = 0, w = 0, last = -2;
        for (int i = start; i < text.Length && w < word.Length; i++)
        {
            if (i - start > window)
            {
                return -1;
            }
            if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(word[w]))
            {
                run = 0;
                continue;
            }
            int s = 1;
            bool boundary = i == 0 || text[i - 1] is '/' or '_' or '-' or '.' or ' ' || (char.IsUpper(text[i]) && char.IsLower(text[i - 1]));
            if (boundary)
            {
                s += 8;
            }
            if (last == i - 1)
            {
                run++;
                s += 6 + run * 3;
            }
            else
            {
                run = 0;
            }
            if (i >= nameStart)
            {
                s += 3;
            }
            score += s;
            positions?.Add(i);
            last = i;
            w++;
        }
        // Shorter paths win ties: "src" beats "src/very/deep/src-ish".
        return score * 4 - Math.Min(text.Length, 200) / 10;
    }

    /// <summary>Scores a path against a whole query (all words must match); -1 if any word doesn't.</summary>
    public static int ScoreAll(string path, string[] words, int nameStart, List<int>? positions = null)
    {
        int total = 0;
        foreach (string word in words)
        {
            int s = Score(path, word, nameStart, positions);
            if (s < 0)
            {
                return -1;
            }
            total += s;
        }
        return total;
    }

    public static string[] Words(string query) => query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when every letter of every word appears in order in <paramref name="text"/> (quick pre-check).</summary>
    public static bool Contains(ReadOnlySpan<char> dir, ReadOnlySpan<char> name, string word)
    {
        int w = Advance(dir, word, 0);
        return Advance(name, word, w) == word.Length;
    }

    private static int Advance(ReadOnlySpan<char> part, string word, int w)
    {
        for (int i = 0; i < part.Length && w < word.Length; i++)
        {
            if (char.ToLowerInvariant(part[i]) == char.ToLowerInvariant(word[w]))
            {
                w++;
            }
        }
        return w;
    }

    /// <summary>Searches the whole scan; paths are relative to the scanned folder.</summary>
    public static SearchResult Search(NodeStore s, string query, SearchKind kind = SearchKind.Any, long minSize = 0, int limit = 500,
        CancellationToken cancel = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string[] words = Words(query);
        var best = new PriorityQueue<SearchHit, (int Score, long Size)>();
        int total = 0;
        if (words.Length == 0)
        {
            return new SearchResult([], 0, clock.Elapsed);
        }
        var stack = new Stack<(int Id, string Path)>();
        stack.Push((NodeStore.Root, ""));
        int n = 0;
        while (stack.Count > 0)
        {
            var (dir, dirPath) = stack.Pop();
            string prefix = dirPath.Length == 0 ? "" : dirPath + "/";
            foreach (int id in s.Children(dir))
            {
                if ((++n & 8191) == 0)
                {
                    cancel.ThrowIfCancellationRequested();
                }
                NodeKind k = s.Kind(id);
                string name = s.Name(id);
                if (k == NodeKind.Directory)
                {
                    stack.Push((id, prefix + name));
                }
                if ((kind == SearchKind.Folders && k != NodeKind.Directory) || (kind == SearchKind.Files && k == NodeKind.Directory)
                    || s.Size(id) < minSize)
                {
                    continue;
                }
                bool all = true;
                foreach (string word in words)
                {
                    if (!Contains(prefix, name, word))
                    {
                        all = false;
                        break;
                    }
                }
                if (!all)
                {
                    continue;
                }
                int score = ScoreAll(prefix + name, words, prefix.Length);
                if (score < 0)
                {
                    continue;
                }
                total++;
                var priority = (score, s.Size(id));
                if (best.Count < limit)
                {
                    best.Enqueue(new SearchHit(id, score), priority);
                }
                else if (best.TryPeek(out _, out var worst) && (score > worst.Score || (score == worst.Score && s.Size(id) > worst.Size)))
                {
                    best.DequeueEnqueue(new SearchHit(id, score), priority);
                }
            }
        }
        var hits = new List<SearchHit>(best.Count);
        while (best.Count > 0)
        {
            hits.Add(best.Dequeue());
        }
        hits.Reverse(); // best first
        return new SearchResult(hits, total, clock.Elapsed);
    }
}
