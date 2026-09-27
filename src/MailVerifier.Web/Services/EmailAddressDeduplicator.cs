namespace MailVerifier.Web.Services;

public static class EmailAddressDeduplicator
{
    /// <summary>
    /// Canonical form used for storage and lookups. Postgres compares text case-sensitively,
    /// so every stored address is lower-cased to keep matching consistent across tables.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant();
    }

    public static List<string> Deduplicate(IEnumerable<string> emails)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduplicated = new List<string>();

        foreach (var value in emails)
        {
            var email = Normalize(value);
            if (email != null && seen.Add(email))
                deduplicated.Add(email);
        }

        return deduplicated;
    }
}
