using Microsoft.AspNetCore.Http;

namespace Fisher.AspNetCore;

/// <summary>
///     HTTP conditional requests — <c>ETag</c> / <c>If-None-Match</c> → <c>304 Not Modified</c>.
/// </summary>
/// <remarks>
///     Pure HTTP logic, ported near-verbatim from Polecat's and Marten's (JasperFx/marten#5015):
///     handles the <c>*</c> wildcard, comma-separated <c>If-None-Match</c> lists, and strips <c>W/</c>
///     weak validators. Weak comparison is the correct function for <c>If-None-Match</c> per RFC 7232
///     §3.2 — there is nothing dialect-specific here and the three stores should not drift.
/// </remarks>
public static class ETagHelpers
{
    /// <summary>A Guid document or stream version as a quoted, opaque ETag value.</summary>
    public static string Format(Guid version) => $"\"{version}\"";

    /// <summary>A numeric document or stream version as a quoted, opaque ETag value.</summary>
    public static string Format(long version) => $"\"{version}\"";

    /// <summary>
    ///     Whether the request's <c>If-None-Match</c> matches <paramref name="etag" /> under weak
    ///     comparison, or carries the <c>*</c> wildcard. A missing or empty header is false.
    /// </summary>
    public static bool IfNoneMatchMatches(HttpContext context, string etag)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Headers.TryGetValue("If-None-Match", out var values))
        {
            return false;
        }

        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (var candidate in raw.Split(','))
            {
                var trimmed = candidate.Trim();

                if (trimmed.Length == 0)
                {
                    continue;
                }

                // "*" matches any current representation (RFC 7232 §3.2).
                if (trimmed == "*" || WeakEquals(trimmed, etag))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool WeakEquals(string a, string b)
        => string.Equals(StripWeakPrefix(a), StripWeakPrefix(b), StringComparison.Ordinal);

    private static string StripWeakPrefix(string tag)
        => tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;
}
