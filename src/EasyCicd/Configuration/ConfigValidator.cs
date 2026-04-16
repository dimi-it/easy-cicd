namespace EasyCicd.Configuration;

public static class ConfigValidator
{
    public static List<string> ValidateEntry(RepoEntry entry)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(entry.Name))
            errors.Add("Name must not be empty");

        if (string.IsNullOrWhiteSpace(entry.Url) || !entry.Url.StartsWith("https://"))
            errors.Add("Url must start with https://");

        if (string.IsNullOrWhiteSpace(entry.Path) || !Path.IsPathRooted(entry.Path))
            errors.Add("Path must be an absolute path");

        if (string.IsNullOrWhiteSpace(entry.Branch))
            errors.Add("Branch must not be empty");

        if (entry.Retry < 0)
            errors.Add("Retry must be >= 0");

        return errors;
    }
}
