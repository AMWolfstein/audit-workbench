using System.IO.Compression;
using AuditWorkbench.Application.Handover;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.Finalization;
using AuditWorkbench.Domain.Companies;
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

    [Fact]
    public async Task Client_import_evidence_is_append_only()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.ExecuteRawAsync(
            "INSERT INTO client_import VALUES ($id,$package,$digest,$company,$at,$by,$manifest,$history)",
            ("$id", Guid.NewGuid().ToString("D")), ("$package", Guid.NewGuid().ToString("D")),
            ("$digest", new string('a', 64)), ("$company", Guid.NewGuid().ToString("D")),
            ("$at", "2026-01-01T00:00:00.000Z"), ("$by", LocalUser.LocalActorId.ToString("D")),
            ("$manifest", "{}"), ("$history", "{}"));

        var update = await workspace.ExpectRawFailureAsync("UPDATE client_import SET team_history_json='[]'");
        Assert.Contains("AWB-GUARD-IMPORT-APPEND-ONLY", update.Message);
        var delete = await workspace.ExpectRawFailureAsync("DELETE FROM client_import");
        Assert.Contains("AWB-GUARD-IMPORT-APPEND-ONLY", delete.Message);
        Assert.Equal(1, await workspace.ScalarAsync("SELECT COUNT(*) FROM client_import"));
    }

    [Fact]
    public async Task Imported_audit_history_is_append_only()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var importId = Guid.NewGuid();
        await workspace.ExecuteRawAsync(
            "INSERT INTO client_import VALUES ($id,$package,$digest,$company,$at,$by,$manifest,$history)",
            ("$id", importId.ToString("D")), ("$package", Guid.NewGuid().ToString("D")),
            ("$digest", new string('a', 64)), ("$company", Guid.NewGuid().ToString("D")),
            ("$at", "2026-01-01T00:00:00.000Z"), ("$by", LocalUser.LocalActorId.ToString("D")),
            ("$manifest", "{}"), ("$history", "{}"));
        await workspace.ExecuteRawAsync(
            "INSERT INTO imported_audit_event VALUES ($id,$import,$source,$sequence,$at,$actor,$actorName,$type,$outcome,$company,$engagement,$entityType,$entityId,$description,$details,$previous,$hash)",
            ("$id", Guid.NewGuid().ToString("D")), ("$import", importId.ToString("D")),
            ("$source", Guid.NewGuid().ToString("D")), ("$sequence", 1L),
            ("$at", "2026-01-01T00:00:00.000Z"), ("$actor", Guid.NewGuid().ToString("D")),
            ("$actorName", "External User"), ("$type", "COMPANY_CREATED"), ("$outcome", "SUCCESS"),
            ("$company", Guid.NewGuid().ToString("D")), ("$engagement", DBNull.Value), ("$entityType", "COMPANY"),
            ("$entityId", Guid.NewGuid().ToString("D")), ("$description", "Imported history."),
            ("$details", "{}"), ("$previous", DBNull.Value), ("$hash", new string('b', 64)));

        var update = await workspace.ExpectRawFailureAsync("UPDATE imported_audit_event SET description='rewritten'");
        Assert.Contains("AWB-GUARD-IMPORTED-AUDIT-APPEND-ONLY", update.Message);
        var delete = await workspace.ExpectRawFailureAsync("DELETE FROM imported_audit_event");
        Assert.Contains("AWB-GUARD-IMPORTED-AUDIT-APPEND-ONLY", delete.Message);
        Assert.Equal(1, await workspace.ScalarAsync("SELECT COUNT(*) FROM imported_audit_event"));
    }

    [Fact]
    public async Task External_principals_must_arrive_disabled_and_non_local()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var active = await workspace.ExpectRawFailureAsync(
            "INSERT INTO app_user(user_id,username,display_name,status,is_local_demo,created_at_utc,is_external_principal) VALUES($id,'ext.active','External Active','ACTIVE',0,'2026-01-01T00:00:00.000Z',1)",
            ("$id", Guid.NewGuid().ToString("D")));
        Assert.Contains("AWB-GUARD-EXTERNAL-PRINCIPAL", active.Message);
        var localDemo = await workspace.ExpectRawFailureAsync(
            "INSERT INTO app_user(user_id,username,display_name,status,is_local_demo,created_at_utc,is_external_principal) VALUES($id,'ext.local','External Local','DISABLED',1,'2026-01-01T00:00:00.000Z',1)",
            ("$id", Guid.NewGuid().ToString("D")));
        Assert.Contains("AWB-GUARD-EXTERNAL-PRINCIPAL", localDemo.Message);

        // The one legitimate shape: a disabled, non-local attribution-only identity.
        await workspace.ExecuteRawAsync(
            "INSERT INTO app_user(user_id,username,display_name,status,is_local_demo,created_at_utc,is_external_principal) VALUES($id,'ext.imported','External Imported','DISABLED',0,'2026-01-01T00:00:00.000Z',1)",
            ("$id", Guid.NewGuid().ToString("D")));
        Assert.Equal(1, await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM app_user WHERE username='ext.imported' AND status='DISABLED' AND is_local_demo=0 AND is_external_principal=1"));
    }

    [Fact]
    public async Task External_principals_cannot_shed_their_marker_or_receive_live_access()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var id = Guid.NewGuid();
        await workspace.ExecuteRawAsync(
            "INSERT INTO app_user(user_id,username,display_name,status,is_local_demo,created_at_utc,is_external_principal) VALUES($id,'ext.assigned','External Assigned','DISABLED',0,'2026-01-01T00:00:00.000Z',1)",
            ("$id", id.ToString("D")));
        var unflagged = await workspace.ExpectRawFailureAsync(
            "UPDATE app_user SET is_external_principal=0 WHERE user_id=$id", ("$id", id.ToString("D")));
        Assert.Contains("AWB-GUARD-EXTERNAL-PRINCIPAL", unflagged.Message);

        var companyId = await workspace.CreateCompanyAsync();
        var engagementId = await workspace.CreateYearAsync(companyId, "FY2026", 2026);
        var roleId = await workspace.TextScalarAsync("SELECT role_id FROM app_role WHERE role_key='PARTNER'");
        var membership = await workspace.ExpectRawFailureAsync(
            "INSERT INTO engagement_member VALUES ($id,$eng,$user,$role,'ACTIVE',$at,$by,$at,1)",
            ("$id", Guid.NewGuid().ToString("D")), ("$eng", engagementId.ToString("D")),
            ("$user", id.ToString("D")), ("$role", roleId ?? ""), ("$at", "2026-01-01T00:00:00.000Z"),
            ("$by", LocalUser.LocalActorId.ToString("D")));
        Assert.Contains("AWB-GUARD-EXTERNAL-PRINCIPAL", membership.Message);

        var assignment = await workspace.ExpectRawFailureAsync(
            "INSERT INTO assignment VALUES ($id,$eng,$user,'ENGAGEMENT','ALL','Imported follow-up','ACTIVE',$at,$by,$at,1)",
            ("$id", Guid.NewGuid().ToString("D")), ("$eng", engagementId.ToString("D")),
            ("$user", id.ToString("D")), ("$at", "2026-01-01T00:00:00.000Z"),
            ("$by", LocalUser.LocalActorId.ToString("D")));
        Assert.Contains("AWB-GUARD-EXTERNAL-PRINCIPAL", assignment.Message);
        Assert.Equal(0, await workspace.ScalarAsync("SELECT COUNT(*) FROM engagement_member WHERE user_id=$id", ("$id", id.ToString("D"))));
        Assert.Equal(0, await workspace.ScalarAsync("SELECT COUNT(*) FROM assignment WHERE assignee_user_id=$id", ("$id", id.ToString("D"))));
    }

    [Fact]
    public async Task Financial_year_definitions_may_be_duplicated_under_different_ids()
    {
        // D-1 (ADR-027): ux_financial_year_definition was dropped because financial-year
        // ids are embedded in finalized manifests and must survive a handover unchanged.
        await using var workspace = await TestWorkspace.CreateAsync();
        Assert.Equal(0, await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ux_financial_year_definition'"));

        await workspace.ExecuteRawAsync(
            "INSERT INTO financial_year VALUES ($id,'SHARED-FY','2026-01-01','2026-12-31','2026-01-01T00:00:00.000Z')",
            ("$id", Guid.NewGuid().ToString("D")));
        await workspace.ExecuteRawAsync(
            "INSERT INTO financial_year VALUES ($id,'SHARED-FY','2026-01-01','2026-12-31','2026-01-01T00:00:00.000Z')",
            ("$id", Guid.NewGuid().ToString("D")));
        Assert.Equal(2, await workspace.ScalarAsync("SELECT COUNT(*) FROM financial_year WHERE label='SHARED-FY'"));

        // Ordinary engagement creation still reuses one exact matching definition.
        var first = await workspace.CreateCompanyAsync("FIRST-DEMO");
        var second = await workspace.CreateCompanyAsync("SECOND-DEMO");
        var firstYear = await workspace.CreateYearAsync(first, "FY2026", 2026);
        var secondYear = await workspace.CreateYearAsync(second, "FY2026", 2026);
        Assert.Equal(
            await workspace.TextScalarAsync("SELECT financial_year_id FROM engagement WHERE engagement_id=$id", ("$id", firstYear.ToString("D"))),
            await workspace.TextScalarAsync("SELECT financial_year_id FROM engagement WHERE engagement_id=$id", ("$id", secondYear.ToString("D"))));
        Assert.Equal(1, await workspace.ScalarAsync("SELECT COUNT(*) FROM financial_year WHERE label='FY2026'"));
    }
}
