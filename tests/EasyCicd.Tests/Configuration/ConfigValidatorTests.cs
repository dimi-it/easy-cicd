using EasyCicd.Configuration;

namespace EasyCicd.Tests.Configuration;

public class ConfigValidatorTests
{
    private static RepoEntry ValidEntry() => new()
    {
        Name = "my-app",
        Url = "https://github.com/org/my-app.git",
        Path = "/opt/apps/my-app",
        Branch = "main",
        Retry = 0
    };

    [Fact]
    public void ValidateEntry_ValidEntry_ReturnsNoErrors()
    {
        var errors = ConfigValidator.ValidateEntry(ValidEntry());
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateEntry_EmptyName_ReturnsError()
    {
        var entry = ValidEntry();
        entry.Name = "";
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Single(errors);
        Assert.Contains("Name", errors[0]);
    }

    [Fact]
    public void ValidateEntry_NonHttpsUrl_ReturnsError()
    {
        var entry = ValidEntry();
        entry.Url = "http://github.com/org/my-app.git";
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Single(errors);
        Assert.Contains("https://", errors[0]);
    }

    [Fact]
    public void ValidateEntry_EmptyUrl_ReturnsError()
    {
        var entry = ValidEntry();
        entry.Url = "";
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Single(errors);
    }

    [Fact]
    public void ValidateEntry_RelativePath_ReturnsError()
    {
        var entry = ValidEntry();
        entry.Path = "relative/path";
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Single(errors);
        Assert.Contains("absolute", errors[0]);
    }

    [Fact]
    public void ValidateEntry_EmptyBranch_ReturnsError()
    {
        var entry = ValidEntry();
        entry.Branch = "";
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Single(errors);
        Assert.Contains("Branch", errors[0]);
    }

    [Fact]
    public void ValidateEntry_NegativeRetry_ReturnsError()
    {
        var entry = ValidEntry();
        entry.Retry = -1;
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Single(errors);
        Assert.Contains("Retry", errors[0]);
    }

    [Fact]
    public void ValidateEntry_MultipleErrors_ReturnsAll()
    {
        var entry = new RepoEntry { Name = "", Url = "", Path = "", Branch = "", Retry = -1 };
        var errors = ConfigValidator.ValidateEntry(entry);
        Assert.Equal(5, errors.Count);
    }

    [Fact]
    public void ValidateDuplicates_UniqueNames_ReturnsNoErrors()
    {
        var entries = new List<RepoEntry>
        {
            new() { Name = "app1" },
            new() { Name = "app2" }
        };
        var errors = ConfigValidator.ValidateDuplicates(entries);
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateDuplicates_DuplicateNames_ReturnsError()
    {
        var entries = new List<RepoEntry>
        {
            new() { Name = "app1" },
            new() { Name = "app1" }
        };
        var errors = ConfigValidator.ValidateDuplicates(entries);
        Assert.Single(errors);
        Assert.Contains("app1", errors[0]);
    }

    [Fact]
    public void ValidateDuplicates_MultipleDuplicates_ReturnsMultipleErrors()
    {
        var entries = new List<RepoEntry>
        {
            new() { Name = "app1" },
            new() { Name = "app1" },
            new() { Name = "app2" },
            new() { Name = "app2" }
        };
        var errors = ConfigValidator.ValidateDuplicates(entries);
        Assert.Equal(2, errors.Count);
    }
}
