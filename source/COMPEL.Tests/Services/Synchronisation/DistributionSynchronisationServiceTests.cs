namespace COMPEL.Tests.Services.Synchronisation;

/// <summary>
///     Verifies the placeholder that stands in for the distribution version until it has been resolved from the manifest.
/// </summary>
public sealed class DistributionSynchronisationServiceTests
{
    [Test]
    public async Task The_Unresolved_Version_Placeholder_Is_Version_Shaped()
    {
        string placeholder = DistributionSynchronisationService.UnknownDistributionVersion;

        using (Assert.Multiple())
        {
            await Assert.That(placeholder).IsNotEmpty();

            // Consumers Parse The Field By Splitting It On Full Stops, So The Placeholder Carries The Same Four Components A Real Version Does
            await Assert.That(placeholder.Split('.').Length).IsEqualTo(4);
        }
    }

    [Test]
    public async Task The_Unresolved_Version_Placeholder_Is_Not_Mistakable_For_A_Real_Version()
    {
        await Assert.That(Version.TryParse(DistributionSynchronisationService.UnknownDistributionVersion, out _)).IsFalse();
    }

    // The Host Is Deliberately Unreachable: With Synchronisation Disabled The CDN Must Not Be Contacted At All, So Readiness Cannot Depend On It Answering
    [Test]
    public async Task Disabling_Synchronisation_Opens_The_Readiness_Gate_Without_Contacting_The_CDN()
    {
        CDNOptions options = new () { Synchronisation = false, Host = "https://10.255.255.1/" };

        DistributionSynchronisationService service = new (Options.Create(options), NullLogger<DistributionSynchronisationService>.Instance);

        using CancellationTokenSource cancellation = new (TimeSpan.FromSeconds(10));

        await service.StartAsync(cancellation.Token);

        await service.WaitUntilReady(cancellation.Token);

        using (Assert.Multiple())
        {
            await Assert.That(service.SynchronisationState).IsEqualTo("Disabled");

            // Still The Placeholder, Which Is Only Possible If No Manifest Was Fetched
            await Assert.That(service.DistributionVersion).IsEqualTo(DistributionSynchronisationService.UnknownDistributionVersion);
        }

        await service.StopAsync(CancellationToken.None);
    }
}
