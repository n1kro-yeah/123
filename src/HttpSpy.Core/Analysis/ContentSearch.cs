using System.Text;
using System.Text.RegularExpressions;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Analysis;

/// <summary>Which parts of a transaction a search looks at.</summary>
[Flags]
public enum SearchScope
{
    None = 0,
    Url = 1 << 0,
    RequestHeaders = 1 << 1,
    RequestBody = 1 << 2,
    ResponseHeaders = 1 << 3,
    ResponseBody = 1 << 4,
    Messages = 1 << 5,

    Headers = RequestHeaders | ResponseHeaders,
    Bodies = RequestBody | ResponseBody,
    Everything = Url | Headers | Bodies | Messages,
}

/// <summary>Where inside a transaction a hit was found.</summary>
public enum SearchLocation
{
    Url,
    RequestHeader,
    RequestBody,
    ResponseHeader,
    ResponseBody,
    Message
}

/// <summary>
/// One match, with enough context to show a meaningful result row and to
/// highlight the matched span inside it.
/// </summary>
/// <param name="Session">The transaction the match was found in.</param>
/// <param name="Location">Which part of the transaction matched.</param>
/// <param name="LineNumber">1-based line within that part, or 0 when it has no lines.</param>
/// <param name="Preview">The line the match sits on, trimmed to a readable width.</param>
/// <param name="MatchStart">Offset of the match inside <paramref name="Preview"/>.</param>
/// <param name="MatchLength">Length of the match inside <paramref name="Preview"/>.</param>
public sealed record SearchHit(
    HttpSession Session,
    SearchLocation Location,
    int LineNumber,
    string Preview,
    int MatchStart,
    int MatchLength)
{
    public string LocationLabel => Location switch
    {
        SearchLocation.Url => "URL",
        SearchLocation.RequestHeader => "Request header",
        SearchLocation.RequestBody => "Request body",
        SearchLocation.ResponseHeader => "Response header",
        SearchLocation.ResponseBody => "Response body",
        SearchLocation.Message => "Message",
        _ => Location.ToString(),
    };
}

/// <summary>What to look for and where.</summary>
public sealed class SearchQuery
{
    public string Text { get; set; } = string.Empty;
    public bool UseRegex { get; set; }
    public bool CaseSensitive { get; set; }
    public bool WholeWord { get; set; }
    public SearchScope Scope { get; set; } = SearchScope.Everything;

    /// <summary>Stop after this many hits in one transaction; 0 means no limit.</summary>
    public int MaxHitsPerSession { get; set; } = 20;

    /// <summary>Stop the whole search after this many hits; 0 means no limit.</summary>
    public int MaxTotalHits { get; set; } = 5000;
}

/// <summary>The outcome of a search, including why it stopped.</summary>
/// <param name="Hits">Matches in the order the sessions were supplied.</param>
/// <param name="SessionsSearched">How many transactions were examined.</param>
/// <param name="SessionsMatched">How many transactions contained at least one hit.</param>
/// <param name="Truncated">True when the total-hit ceiling cut the search short.</param>
/// <param name="Error">Set when the query itself was invalid (a bad regex).</param>
public sealed record SearchResults(
    IReadOnlyList<SearchHit> Hits,
    int SessionsSearched,
    int SessionsMatched,
    bool Truncated,
    string? Error = null)
{
    public static SearchResults Failed(string error) =>
        new(Array.Empty<SearchHit>(), 0, 0, false, error);
}

/// <summary>
/// Full-text search across every captured transaction.
///
/// The inspector's Find box only ever looked inside the one transaction already
/// open, and the grid filter only matches the columns it displays. Neither
/// answers "which of these 4 000 requests mentions this session token", which is
/// the question debugging usually starts from.
/// </summary>
public static class ContentSearch
{
    /// <summary>Matching is bounded so a pathological pattern cannot wedge the UI.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Result lines are trimmed to this width; a minified bundle is one long line.</summary>
    private const int PreviewWidth = 220;

    public static SearchResults Run(IEnumerable<HttpSession> sessions, SearchQuery query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query.Text) || query.Scope == SearchScope.None)
            return new SearchResults(Array.Empty<SearchHit>(), 0, 0, false);

        Regex regex;
        try { regex = BuildRegex(query); }
        catch (ArgumentException ex) { return SearchResults.Failed($"Invalid pattern: {ex.Message}"); }

        var hits = new List<SearchHit>();
        int searched = 0, matched = 0;
        bool truncated = false;

        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();
            searched++;

            int before = hits.Count;
            try
            {
                SearchSession(session, regex, query, hits);
            }
            catch (RegexMatchTimeoutException)
            {
                // One expensive body should not abort the whole search.
                continue;
            }

            if (hits.Count > before) matched++;

            if (query.MaxTotalHits > 0 && hits.Count >= query.MaxTotalHits)
            {
                truncated = true;
                break;
            }
        }

        return new SearchResults(hits, searched, matched, truncated);
    }

    private static Regex BuildRegex(SearchQuery query)
    {
        var options = RegexOptions.Compiled | RegexOptions.Multiline;
        if (!query.CaseSensitive) options |= RegexOptions.IgnoreCase;

        string pattern = query.UseRegex ? query.Text : Regex.Escape(query.Text);
        if (query.WholeWord) pattern = $@"\b(?:{pattern})\b";

        return new Regex(pattern, options, RegexTimeout);
    }

    private static void SearchSession(HttpSession session, Regex regex, SearchQuery query, List<SearchHit> hits)
    {
        int budget = query.MaxHitsPerSession > 0 ? query.MaxHitsPerSession : int.MaxValue;
        int found = 0;

        void Collect(SearchLocation location, string text, int lineNumber = 0)
        {
            if (found >= budget || string.IsNullOrEmpty(text)) return;
            foreach (var hit in Matches(session, regex, location, text, lineNumber, budget - found))
            {
                hits.Add(hit);
                if (++found >= budget) return;
            }
        }

        if (query.Scope.HasFlag(SearchScope.Url))
            Collect(SearchLocation.Url, session.FullUrl);

        if (query.Scope.HasFlag(SearchScope.RequestHeaders))
            CollectHeaders(session.RequestHeaders, SearchLocation.RequestHeader, Collect);

        if (query.Scope.HasFlag(SearchScope.ResponseHeaders))
            CollectHeaders(session.ResponseHeaders, SearchLocation.ResponseHeader, Collect);

        if (query.Scope.HasFlag(SearchScope.RequestBody))
            CollectBody(SearchLocation.RequestBody, session.RequestBodyText, Collect);

        if (query.Scope.HasFlag(SearchScope.ResponseBody))
            CollectBody(SearchLocation.ResponseBody, session.ResponseBodyText, Collect);

        if (query.Scope.HasFlag(SearchScope.Messages))
            CollectMessages(session, Collect);
    }

    private static void CollectHeaders(HeaderCollection headers, SearchLocation location,
        Action<SearchLocation, string, int> collect)
    {
        int line = 0;
        foreach (var header in headers)
        {
            line++;
            collect(location, $"{header.Name}: {header.Value}", line);
        }
    }

    private static void CollectBody(SearchLocation location, string body,
        Action<SearchLocation, string, int> collect)
    {
        if (string.IsNullOrEmpty(body)) return;

        int line = 0;
        foreach (var text in body.Split('\n'))
        {
            line++;
            collect(location, text.TrimEnd('\r'), line);
        }
    }

    private static void CollectMessages(HttpSession session, Action<SearchLocation, string, int> collect)
    {
        int index = 0;
        foreach (var frame in session.WebSocketFrames)
        {
            index++;
            // Binary frames render as a byte-count placeholder; searching that
            // would produce hits on text the user never sent.
            if (frame.IsText) collect(SearchLocation.Message, frame.TextPayload, index);
        }
        foreach (var evt in session.ServerSentEvents)
        {
            index++;
            collect(SearchLocation.Message, $"{evt.EventName ?? "message"}: {evt.Data}", index);
        }
    }

    private static IEnumerable<SearchHit> Matches(HttpSession session, Regex regex, SearchLocation location,
        string text, int lineNumber, int limit)
    {
        int emitted = 0;
        foreach (Match match in regex.Matches(text))
        {
            if (!match.Success || emitted >= limit) yield break;

            var (preview, offset) = Trim(text, match.Index, match.Length);
            yield return new SearchHit(session, location, lineNumber, preview, offset, match.Length);
            emitted++;

            // A zero-width pattern would otherwise emit one hit per character.
            if (match.Length == 0) yield break;
        }
    }

    /// <summary>
    /// Cuts a readable window out of a long line, keeping the match visible and
    /// marking either end that was cut. A minified JavaScript bundle is a single
    /// 400 KB line; showing it whole is useless and slow to render.
    /// </summary>
    private static (string Preview, int Offset) Trim(string text, int index, int length)
    {
        if (text.Length <= PreviewWidth) return (text, index);

        int contextBefore = Math.Min(60, index);
        int start = index - contextBefore;
        int take = Math.Min(PreviewWidth, text.Length - start);

        var sb = new StringBuilder();
        if (start > 0) sb.Append('…');
        int offset = sb.Length + contextBefore;
        sb.Append(text, start, take);
        if (start + take < text.Length) sb.Append('…');

        // The match may extend past the window; clamp so the highlight stays inside.
        int visible = Math.Max(0, Math.Min(length, sb.Length - offset));
        return (sb.ToString(), visible == 0 ? 0 : offset);
    }

    /// <summary>A one-line summary for the results header.</summary>
    public static string Describe(SearchResults results)
    {
        if (results.Error is not null) return results.Error;
        if (results.Hits.Count == 0) return $"No matches in {results.SessionsSearched} transactions.";

        var text = $"{results.Hits.Count} matches in {results.SessionsMatched} of " +
                   $"{results.SessionsSearched} transactions";
        return results.Truncated ? text + " (stopped at the result limit)" : text + ".";
    }
}
