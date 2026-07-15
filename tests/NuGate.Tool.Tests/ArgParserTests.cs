using Xunit;

namespace NuGate.Tool.Tests;

public class ArgParserTests
{
    [Fact]
    public void No_args_is_an_error()
    {
        var result = ArgParser.Parse(Array.Empty<string>());

        Assert.Equal(CliMode.Error, result.Mode);
        Assert.Contains("usage: nugate check", result.ErrorMessage);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Top_level_help_flags_show_help(string flag)
    {
        var result = ArgParser.Parse(new[] { flag });

        Assert.Equal(CliMode.ShowHelp, result.Mode);
    }

    [Fact]
    public void Top_level_version_flag_shows_version()
    {
        var result = ArgParser.Parse(new[] { "--version" });

        Assert.Equal(CliMode.ShowVersion, result.Mode);
    }

    [Fact]
    public void Unknown_command_is_an_error()
    {
        var result = ArgParser.Parse(new[] { "frobnicate" });

        Assert.Equal(CliMode.Error, result.Mode);
        Assert.Contains("Unknown command 'frobnicate'", result.ErrorMessage);
    }

    [Fact]
    public void Check_with_no_flags_defaults_path_to_cwd_marker()
    {
        var result = ArgParser.Parse(new[] { "check" });

        Assert.Equal(CliMode.RunCheck, result.Mode);
        Assert.Equal(".", result.Options!.Path);
        Assert.Null(result.Options.ConfigPath);
        Assert.False(result.Options.Json);
        Assert.Null(result.Options.MinAgeDaysOverride);
    }

    [Fact]
    public void Check_parses_positional_path()
    {
        var result = ArgParser.Parse(new[] { "check", "some/path" });

        Assert.Equal(CliMode.RunCheck, result.Mode);
        Assert.Equal("some/path", result.Options!.Path);
    }

    [Fact]
    public void Check_parses_all_flags_together()
    {
        var result = ArgParser.Parse(new[]
        {
            "check", "some/path", "--config", "custom.json", "--json", "--min-age-days", "14",
        });

        Assert.Equal(CliMode.RunCheck, result.Mode);
        var options = result.Options!;
        Assert.Equal("some/path", options.Path);
        Assert.Equal("custom.json", options.ConfigPath);
        Assert.True(options.Json);
        Assert.Equal(14, options.MinAgeDaysOverride);
    }

    [Fact]
    public void Check_flags_can_precede_the_positional_path()
    {
        var result = ArgParser.Parse(new[] { "check", "--json", "--min-age-days", "3", "some/path" });

        Assert.Equal(CliMode.RunCheck, result.Mode);
        var options = result.Options!;
        Assert.Equal("some/path", options.Path);
        Assert.True(options.Json);
        Assert.Equal(3, options.MinAgeDaysOverride);
    }

    [Fact]
    public void Check_help_flag_shows_help()
    {
        var result = ArgParser.Parse(new[] { "check", "--help" });

        Assert.Equal(CliMode.ShowHelp, result.Mode);
    }

    [Fact]
    public void Config_without_value_is_an_error()
    {
        var result = ArgParser.Parse(new[] { "check", "--config" });

        Assert.Equal(CliMode.Error, result.Mode);
        Assert.Contains("--config requires a value", result.ErrorMessage);
    }

    [Fact]
    public void Min_age_days_without_value_is_an_error()
    {
        var result = ArgParser.Parse(new[] { "check", "--min-age-days" });

        Assert.Equal(CliMode.Error, result.Mode);
        Assert.Contains("--min-age-days requires a value", result.ErrorMessage);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("3.5")]
    public void Min_age_days_rejects_non_negative_integer_violations(string value)
    {
        var result = ArgParser.Parse(new[] { "check", "--min-age-days", value });

        Assert.Equal(CliMode.Error, result.Mode);
        Assert.Contains("--min-age-days must be a non-negative integer", result.ErrorMessage);
    }

    [Fact]
    public void Min_age_days_zero_is_valid()
    {
        var result = ArgParser.Parse(new[] { "check", "--min-age-days", "0" });

        Assert.Equal(CliMode.RunCheck, result.Mode);
        Assert.Equal(0, result.Options!.MinAgeDaysOverride);
    }

    [Fact]
    public void Unknown_flag_is_an_error()
    {
        var result = ArgParser.Parse(new[] { "check", "--nope" });

        Assert.Equal(CliMode.Error, result.Mode);
        Assert.Contains("Unknown flag '--nope'", result.ErrorMessage);
    }

    [Fact]
    public void Second_positional_argument_is_an_error()
    {
        var result = ArgParser.Parse(new[] { "check", "path-one", "path-two" });

        Assert.Equal(CliMode.Error, result.Mode);
        Assert.Contains("Unexpected extra argument 'path-two'", result.ErrorMessage);
    }
}
