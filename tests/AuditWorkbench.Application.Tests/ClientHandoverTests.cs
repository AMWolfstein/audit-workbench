using System.IO.Compression;
using AuditWorkbench.Application.Handover;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.Finalization;
using Microsoft.Extensions.DependencyInjection;

namespace AuditWorkbench.Application.Tests;

public sealed class ClientHandoverTests
{
    [Fact]
    public async Task Complete_client_round_trips_with_finalization_chain_intact()
    {
        await using var source = await TestWorkspace.CreateAsync();
        var seeded = await source.SeedDemoAsync();
        await using var package = new MemoryStream();
        await source.UseAsync(scope => scope.GetRequiredService<IClientHandoverPackageService>()
            .ExportAsync(seeded.CompanyId, package));

        await using var destination = await TestWorkspace.CreateAsync();
        package.Position = 0;
        var report = await destination.UseAsync(scope => scope.GetRequiredService<IClientHandoverPackageService>()
            .ValidateAsync(package));
        Assert.True(report.IsValid, string.Join("; ", report.Findings.Select(f => $"{f.Code}: {f.Message}")));
        Assert.Equal(2, report.EngagementCount);

        package.Position = 0;
        var imported = await destination.UseAsync(scope => scope.GetRequiredService<IClientHandoverPackageService>()
            .ImportAsync(package));
        Assert.Equal(seeded.CompanyId, imported.CompanyId);
        Assert.Equal(1, await destination.ScalarAsync("SELECT count(*) FROM client_import"));
        Assert.True(await destination.UseAsync(scope => scope.GetRequiredService<FinalizationService>()
            .VerifyDigestAsync(seeded.PriorId)));

        var current = await destination.UseAsync(scope => scope.GetRequiredService<EngagementService>()
            .GetAsync(seeded.CurrentId));
        Assert.Equal(seeded.PriorId, current.PriorEngagementId);
        Assert.Equal(1, await destination.ScalarAsync(
            "SELECT count(*) FROM engagement_member WHERE engagement_id=$id AND status='ACTIVE'", ("$id", seeded.CurrentId.ToString("D"))));
    }

    [Fact]
    public async Task Tampered_entry_is_reported_without_writing_any_data()
    {
        await using var source = await TestWorkspace.CreateAsync();
        var seeded = await source.SeedDemoAsync();
        await using var original = new MemoryStream();
        await source.UseAsync(scope => scope.GetRequiredService<IClientHandoverPackageService>()
            .ExportAsync(seeded.CompanyId, original));

        await using var tampered = new MemoryStream();
        original.Position = 0;
        using (var input = new ZipArchive(original, ZipArchiveMode.Read, true))
        using (var output = new ZipArchive(tampered, ZipArchiveMode.Create, true))
        {
            foreach (var sourceEntry in input.Entries)
            {
                var target = output.CreateEntry(sourceEntry.FullName);
                await using var targetStream = target.Open();
                await using var sourceStream = sourceEntry.Open();
                if (sourceEntry.FullName == "data/company.json")
                {
                    using var reader = new StreamReader(sourceStream);
                    var json = (await reader.ReadToEndAsync()).Replace("(Demo)", "(Damo)", StringComparison.Ordinal);
                    await targetStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
                }
                else
                {
                    await sourceStream.CopyToAsync(targetStream);
                }
            }
        }

        await using var destination = await TestWorkspace.CreateAsync();
        tampered.Position = 0;
        var report = await destination.UseAsync(scope => scope.GetRequiredService<IClientHandoverPackageService>()
            .ValidateAsync(tampered));
        Assert.False(report.IsValid);
        Assert.Contains(report.Findings, f => f.Code is "CHECKSUM_MISMATCH" or "FORMAT_UNSUPPORTED");
        Assert.Equal(0, await destination.ScalarAsync("SELECT count(*) FROM company"));
    }

    [Fact]
    public async Task External_principals_are_permanently_inert_at_database_boundary()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var id = Guid.NewGuid();
        await workspace.ExecuteRawAsync(
            "INSERT INTO app_user(user_id,username,display_name,status,is_local_demo,created_at_utc,is_external_principal) VALUES($id,'source.user','Source User','DISABLED',0,'2026-01-01T00:00:00.000Z',1)",
            ("$id", id.ToString("D")));
        var error = await workspace.ExpectRawFailureAsync(
            "UPDATE app_user SET status='ACTIVE' WHERE user_id=$id", ("$id", id.ToString("D")));
        Assert.Contains("AWB-GUARD-EXTERNAL-PRINCIPAL", error.Message);
    }
}
