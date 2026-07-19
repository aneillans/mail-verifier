namespace MailVerifier.Web.Services;

public static class EmailAddressDeduplicator
{
    public static List<string> Deduplicate(IEnumerable<string> emails)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduplicated = new List<string>();

        foreach (var value in emails)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var email = value.Trim();
            if (seen.Add(email))
                deduplicated.Add(email);
        }

        return deduplicated;
    }
}
