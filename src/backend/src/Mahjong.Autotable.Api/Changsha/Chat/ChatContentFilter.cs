using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Changsha.Chat;

/// <summary>
/// Phase J Wave 9 — wordlist-based content filter for table chat.
/// <para>The filter operates by substitution, not rejection: when a
/// banned token appears in the message body, every occurrence is
/// replaced by an asterisk run of the same length. This keeps the
/// chat conversational (callers see "**** happens" rather than a hard
/// "Message blocked by chat filter" error) while ensuring the
/// persisted body — and any downstream audit log — never contains
/// the original profanity.</para>
///
/// <para>Tokens are matched on word boundaries, case-insensitive.
/// The seed wordlist is intentionally minimal (English placeholders
/// the test harness probes for); operators extend the list at runtime
/// via <see cref="Add"/>.</para>
///
/// <para>The type name is one of the names probed by Vasquez's
/// <c>ChatProfanityFilterTests.FindFilterType</c> reflection-based
/// contract test, and the <see cref="Sanitize"/> method matches one
/// of the canonical method names that probe walks. Do not rename
/// without coordinating with the test surface.</para>
/// </summary>
public sealed class ChatContentFilter
{
    private readonly HashSet<string> _wordlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // Minimal English seed — kept small on purpose; production
        // deployments seed the catalog from a config file / external
        // source via Add(). The specific tokens here are the ones the
        // Wave 9 contract test references.
        "badword",
        "censorme",
        "shit",
        "fuck",
        "damn",
    };
    private readonly object _lock = new();
    private Regex _matcher;

    public ChatContentFilter()
    {
        _matcher = BuildMatcher();
    }

    /// <summary>
    /// Returns a copy of <paramref name="input"/> with every banned
    /// token replaced by an asterisk run of the same length. Returns
    /// the input unchanged when it is null/empty or contains no
    /// banned tokens.
    /// </summary>
    public string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        Regex matcher;
        lock (_lock) { matcher = _matcher; }
        return matcher.Replace(input, m => new string('*', m.Length));
    }

    /// <summary>
    /// Adds <paramref name="word"/> to the runtime wordlist. No-op for
    /// blank input. Subsequent calls to <see cref="Sanitize"/> mask
    /// occurrences of <paramref name="word"/>.
    /// </summary>
    public void Add(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        lock (_lock)
        {
            if (_wordlist.Add(word.Trim()))
            {
                _matcher = BuildMatcher();
            }
        }
    }

    private Regex BuildMatcher()
    {
        // Build an alternation of escaped tokens with word boundaries.
        // If the list is empty we still need a Regex that never matches.
        if (_wordlist.Count == 0)
        {
            return new Regex("(?!)", RegexOptions.Compiled);
        }
        var alternation = string.Join("|", _wordlist.Select(Regex.Escape));
        return new Regex($@"\b({alternation})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
