using System.Text;

namespace Fisher.Internal;

/// <summary>
///     Builds the <c>fisher://</c> uri that identifies a store — <c>IEventStore.Subject</c> (fisher#279).
/// </summary>
/// <remarks>
///     <para>
///         <b>Sanitizing is not decoration.</b> A store name is user-supplied text and a uri host is
///         not. Verified against .NET 10 rather than assumed: <c>new Uri("fisher://my store")</c> throws
///         <see cref="System.UriFormatException" />, and <c>new Uri("fisher://a/b")</c> silently parses
///         the tail as a PATH, so two differently-named stores collide on host. The first would turn
///         naming a store into a crash at construction.
///     </para>
///     <para>
///         An ancillary store defaults its name to its marker type's <c>Name</c>, so a CLOSED GENERIC
///         marker reaches here carrying a backtick and arity — which is not a valid host either. Marten
///         met the same thing in marten#5039 and folds it the same way.
///     </para>
///     <para>
///         Letters, digits, '-', '.' and '_' survive, so an ordinary store name is unchanged and this
///         agrees with <c>marten://{storename}</c> and <c>polecat://{storename}</c> for every name
///         anybody actually writes.
///     </para>
/// </remarks>
internal static class StoreSubject
{
    internal const string Scheme = "fisher";

    /// <summary>The subject uri for a store called <paramref name="storeName" />.</summary>
    internal static Uri For(string storeName) => new($"{Scheme}://{Sanitize(storeName)}");

    /// <summary>
    ///     Fold a store name down to something that is unambiguously a uri host, and to the name
    ///     <c>IEventStore.Identity</c> carries so the two cannot disagree.
    /// </summary>
    /// <remarks>
    ///     An empty or all-punctuation name folds to the default rather than to an empty host, because
    ///     <c>new Uri("fisher://")</c> is not a uri either.
    /// </remarks>
    internal static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return StoreOptions.DefaultStoreName.ToLowerInvariant();

        var builder = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '-');
        }

        var sanitized = builder.ToString().Trim('-');

        return sanitized.Length == 0 ? StoreOptions.DefaultStoreName.ToLowerInvariant() : sanitized;
    }
}
