using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using StyloMail.Assessment;
using StyloMail.Core;

namespace StyloMail.Host.Tests;

public sealed class SemanticFlightDeadlineTests
{
    [Theory]
    [InlineData("Jev", null, 2)]
    [InlineData("Nimble", null, 121)]
    [InlineData("Cascade", null, 121)]
    [InlineData("Jev", "7", 8)]
    [InlineData("Cascade", "150", 151)]
    public void Actual_host_composition_sets_provider_derived_flight_lifetime(string provider, string? jevSeconds, int expected)
    {
        using var host = new TestHost().WithAssessmentSecrets("test-only-provider-key", "test-only-profile-master-key-32-characters")
            .Configure("StyloMail:Assessment:Provider", provider);
        if (jevSeconds is not null) host.Configure("StyloMail:Jev:TimeoutSeconds", jevSeconds);
        var assessor = Assert.IsType<MailAssessor>(host.Services.GetRequiredService<IMailAssessor>());
        var options = Assert.IsType<MailAssessorOptions>(typeof(MailAssessor)
            .GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(assessor));
        Assert.Equal(TimeSpan.FromSeconds(expected), options.SemanticCache.FlightMaximumDuration);
    }
}
