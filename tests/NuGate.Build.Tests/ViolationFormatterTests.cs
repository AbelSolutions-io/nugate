using NuGate.Core;
using Xunit;

namespace NuGate.Build.Tests;

public class ViolationFormatterTests
{
    private static Violation TooYoung(double ageDays)
        => new Violation(new PackageIdentity("Example.Pkg", "2.0.1"), ViolationKind.TooYoung,
            created: new DateTimeOffset(2026, 7, 12, 9, 14, 0, TimeSpan.Zero), ageDays: ageDays);

    [Fact]
    public void TooYoung_message_carries_id_version_age_policy_and_allowlist_hint()
    {
        var message = ViolationFormatter.Format(TooYoung(2.9), minAgeDays: 7);

        Assert.Contains("Example.Pkg", message);
        Assert.Contains("2.0.1", message);
        Assert.Contains("2.9", message);          // age in days
        Assert.Contains("7", message);            // policy minAgeDays
        Assert.Contains("nugate.json", message);
        // The exact override snippet (from Violation.AllowlistHint) is embedded verbatim.
        Assert.Contains("\"id\": \"Example.Pkg\"", message);
        Assert.Contains("\"version\": \"2.0.1\"", message);
        Assert.Contains("\"expires\"", message);
        Assert.Contains("\"reason\"", message);
    }

    [Fact]
    public void Unlisted_message_explains_the_kind_and_still_offers_an_override()
    {
        var v = new Violation(new PackageIdentity("Bad.Pkg", "1.0.0"), ViolationKind.Unlisted, null, null);

        var message = ViolationFormatter.Format(v, minAgeDays: 7);

        Assert.Contains("unlisted", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bad.Pkg", message);
        Assert.Contains("\"id\": \"Bad.Pkg\"", message);
    }

    [Fact]
    public void LookupFailed_message_notes_fail_closed_and_offers_an_override()
    {
        var v = new Violation(new PackageIdentity("Net.Pkg", "3.2.1"), ViolationKind.LookupFailed, null, null);

        var message = ViolationFormatter.Format(v, minAgeDays: 7);

        Assert.Contains("Net.Pkg", message);
        Assert.Contains("nuget.org", message);
        Assert.Contains("\"id\": \"Net.Pkg\"", message);
    }

    [Theory]
    [InlineData(ViolationKind.TooYoung)]
    [InlineData(ViolationKind.Unlisted)]
    [InlineData(ViolationKind.LookupFailed)]
    public void Messages_obey_copy_discipline(ViolationKind kind)
    {
        var v = new Violation(new PackageIdentity("P", "1"), kind,
            kind == ViolationKind.TooYoung ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
            kind == ViolationKind.TooYoung ? 1.0 : (double?)null);

        var message = ViolationFormatter.Format(v, minAgeDays: 7);

        foreach (var banned in new[] { "protect", "prevent", "secure", "detect", "malware" })
        {
            Assert.DoesNotContain(banned, message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("dependency age policy", message);
    }
}
