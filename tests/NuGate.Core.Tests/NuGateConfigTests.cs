using System;
using System.IO;
using NuGate.Core;
using Xunit;

namespace NuGate.Core.Tests;

public class NuGateConfigTests
{
    [Fact]
    public void New_config_matches_spec_defaults()
    {
        var config = new NuGateConfig();

        Assert.Equal(7, config.MinAgeDays);
        Assert.Equal(PolicyMode.Enforce, config.Mode);
        Assert.Equal(ApiFailureMode.Fail, config.OnApiFailure); // fail-closed by default
        Assert.Empty(config.Allow);
        Assert.Empty(config.ExemptPrefixes);
    }

    [Fact]
    public void Load_null_path_returns_defaults()
    {
        var config = NuGateConfig.Load(null);

        Assert.Equal(7, config.MinAgeDays);
        Assert.Equal(PolicyMode.Enforce, config.Mode);
        Assert.Equal(ApiFailureMode.Fail, config.OnApiFailure);
    }

    [Fact]
    public void Load_missing_file_returns_defaults()
    {
        var config = NuGateConfig.Load(Path.Combine(Path.GetTempPath(), "nugate-does-not-exist-" + Guid.NewGuid().ToString("N") + ".json"));

        Assert.Equal(7, config.MinAgeDays);
        Assert.Equal(PolicyMode.Enforce, config.Mode);
        Assert.Equal(ApiFailureMode.Fail, config.OnApiFailure);
    }

    [Fact]
    public void Load_parses_all_fields_including_enums_and_expiry()
    {
        var config = NuGateConfig.Load(Fixtures.Path("nugate.json"));

        Assert.Equal(14, config.MinAgeDays);
        Assert.Equal(PolicyMode.Warn, config.Mode);
        Assert.Equal(ApiFailureMode.Warn, config.OnApiFailure);

        Assert.Equal(2, config.Allow.Count);
        Assert.Equal("SomePackage", config.Allow[0].Id);
        Assert.Equal("3.1.4", config.Allow[0].Version);
        Assert.Equal("hotfix", config.Allow[0].Reason);
        Assert.NotNull(config.Allow[0].Expires);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), config.Allow[0].Expires!.Value);

        // Entry without expires stays open-ended.
        Assert.Null(config.Allow[1].Expires);

        Assert.Equal(new[] { "MyCompany.", "Internal." }, config.ExemptPrefixes);
    }

    [Fact]
    public void Load_unknown_property_is_an_error()
    {
        var path = WriteTemp("{ \"minAgeDays\": 7, \"minAgeDayz\": 3 }");
        try
        {
            var ex = Assert.Throws<NuGateConfigException>(() => NuGateConfig.Load(path));
            Assert.Contains("minAgeDayz", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_negative_min_age_is_an_error()
    {
        var path = WriteTemp("{ \"minAgeDays\": -1 }");
        try
        {
            Assert.Throws<NuGateConfigException>(() => NuGateConfig.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_allow_entry_missing_version_is_an_error()
    {
        var path = WriteTemp("{ \"allow\": [ { \"id\": \"Foo\" } ] }");
        try
        {
            var ex = Assert.Throws<NuGateConfigException>(() => NuGateConfig.Load(path));
            Assert.Contains("allow[0]", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_allow_entry_missing_id_is_an_error()
    {
        var path = WriteTemp("{ \"allow\": [ { \"version\": \"1.0.0\" } ] }");
        try
        {
            Assert.Throws<NuGateConfigException>(() => NuGateConfig.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_tolerates_comments_and_trailing_commas()
    {
        var path = WriteTemp("{ \"minAgeDays\": 10, // rollout\n \"mode\": \"enforce\", }");
        try
        {
            var config = NuGateConfig.Load(path);
            Assert.Equal(10, config.MinAgeDays);
            Assert.Equal(PolicyMode.Enforce, config.Mode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_full_iso_expiry_timestamp_parses()
    {
        var path = WriteTemp("{ \"allow\": [ { \"id\": \"Foo\", \"version\": \"1.0.0\", \"expires\": \"2026-09-15T12:30:00Z\" } ] }");
        try
        {
            var config = NuGateConfig.Load(path);
            Assert.Equal(new DateTimeOffset(2026, 9, 15, 12, 30, 0, TimeSpan.Zero), config.Allow[0].Expires!.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "nugate-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        return path;
    }
}
