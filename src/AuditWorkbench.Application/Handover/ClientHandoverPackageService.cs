using System.Data.Common;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Application.Companies;
using AuditWorkbench.Application.Finalization;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Finalization;
using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.Identity;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuditWorkbench.Application.Handover;

/// <summary>Creates, validates and atomically imports AWB-CLIENT/1.0 ZIP packages.</summary>
public sealed class ClientHandoverPackageService : IClientHandoverPackageService
{
    public const string Format = "AWB-CLIENT/1.0";
    private const string MinSchema = "0005_client_handover";
    private const long MaxEntryBytes = 50L * 1024 * 1024;
    private const long MaxPackageBytes = 100L * 1024 * 1024;
    private const int MaxCompressionRatio = 200;

    private static readonly string[] DataPaths =
    {
        "data/company.json", "data/financial_years.json", "data/engagements.json",
        "data/accounts.jsonl", "data/financial_data.jsonl", "data/prior_year_relationships.json",
        "data/finalization_manifests.json", "data/principals.json", "history/team_members.json",
        "history/assignments.json", "audit/events.jsonl", "audit/source_chain.json",
    };
    private static readonly HashSet<string> AllowedPaths = new(DataPaths.Append("manifest.json"), StringComparer.Ordinal);

    private readonly AuditWorkbenchDbContext _db;
    private readonly UnitOfWork _uow;
    private readonly AuditTrailWriter _audit;
    private readonly EngagementAuthorizationService _authorization;
    private readonly CompanyAuthorizationService _companyAuthorization;
    private readonly FinalizationService _finalization;
    private readonly IClock _clock;
    private readonly ICurrentActor _actor;

    public ClientHandoverPackageService(AuditWorkbenchDbContext db, UnitOfWork uow, AuditTrailWriter audit,
        EngagementAuthorizationService authorization, CompanyAuthorizationService companyAuthorization,
        FinalizationService finalization, IClock clock, ICurrentActor actor)
    {
        _db = db; _uow = uow; _audit = audit; _authorization = authorization;
        _companyAuthorization = companyAuthorization;
        _finalization = finalization; _clock = clock; _actor = actor;
    }

    public async Task ExportAsync(Guid companyId, Stream destination, CancellationToken cancellationToken = default)
    {
        if (!destination.CanWrite) throw new ArgumentException("The destination stream is not writable.", nameof(destination));
        byte[] packageBytes = Array.Empty<byte>();

        await _uow.ExecuteAsync(async token =>
        {
            await _companyAuthorization.RequireAccessAsync(companyId, cancellationToken: token)
                .ConfigureAwait(false);
            var company = await _db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.CompanyId == companyId, token)
                ?? throw new NotFoundException("That company does not exist.");
            var engagements = await _db.Engagements.AsNoTracking().Where(e => e.CompanyId == companyId)
                .ToListAsync(token);
            foreach (var engagement in engagements)
                await _authorization.RequireAsync(engagement.EngagementId, Permissions.ExportEngagement, token);

            var engagementIds = engagements.Select(e => e.EngagementId).ToHashSet();
            var financialYearIds = engagements.Select(e => e.FinancialYearId).ToHashSet();
            var years = await _db.FinancialYears.AsNoTracking()
                .Where(y => financialYearIds.Contains(y.FinancialYearId)).ToListAsync(token);
            var accounts = await _db.Accounts.AsNoTracking().Where(a => engagementIds.Contains(a.EngagementId))
                .OrderBy(a => a.EngagementId).ThenBy(a => a.AccountCode).ToListAsync(token);
            var values = await _db.FinancialData.AsNoTracking().Where(v => engagementIds.Contains(v.EngagementId))
                .OrderBy(v => v.EngagementId).ThenBy(v => v.AccountId).ThenBy(v => v.RevisionNo).ToListAsync(token);
            var priors = await _db.PriorYearRelationships.AsNoTracking()
                .Where(r => engagementIds.Contains(r.CurrentEngagementId) && engagementIds.Contains(r.PriorEngagementId))
                .ToListAsync(token);
            var finalizations = await _db.FinalizationManifests.AsNoTracking()
                .Where(m => engagementIds.Contains(m.EngagementId)).ToListAsync(token);
            var members = await (from m in _db.EngagementMembers.AsNoTracking()
                                 join r in _db.Roles.AsNoTracking() on m.RoleId equals r.RoleId
                                 where engagementIds.Contains(m.EngagementId)
                                 select new TeamMemberTransfer(m.EngagementMemberId, m.EngagementId, m.UserId,
                                     r.RoleKey, m.Status, m.AddedAtUtc, m.AddedBy, m.UpdatedAtUtc, m.RowVersion))
                .ToListAsync(token);
            var assignments = await _db.Assignments.AsNoTracking().Where(a => engagementIds.Contains(a.EngagementId))
                .Select(a => new AssignmentTransfer(a.AssignmentId, a.EngagementId, a.AssigneeUserId, a.ScopeType,
                    a.ScopeId, a.Title, a.Status, a.AssignedAtUtc, a.AssignedBy, a.UpdatedAtUtc, a.RowVersion))
                .ToListAsync(token);
            var events = await _db.AuditEvents.AsNoTracking()
                .Where(e => e.CompanyId == companyId || (e.EngagementId != null && engagementIds.Contains(e.EngagementId.Value)))
                .OrderBy(e => e.SequenceNo).ToListAsync(token);
            var fullChain = await _db.AuditEvents.AsNoTracking().OrderBy(e => e.SequenceNo).ToListAsync(token);
            var chainVerified = VerifyFullAuditChain(fullChain);
            if (!chainVerified) throw new IntegrityGuardException("The source audit chain failed verification; export was refused.");

            var principalIds = new HashSet<Guid> { company.CreatedBy, _actor.UserId };
            foreach (var e in engagements) { principalIds.Add(e.CreatedBy); if (e.FinalizedBy is { } id) principalIds.Add(id); }
            foreach (var a in accounts) principalIds.Add(a.CreatedBy);
            foreach (var v in values) principalIds.Add(v.RecordedBy);
            foreach (var r in priors) principalIds.Add(r.LinkedBy);
            foreach (var m in finalizations) principalIds.Add(m.CreatedBy);
            foreach (var m in members) { principalIds.Add(m.UserId); principalIds.Add(m.AddedBy); }
            foreach (var a in assignments) { principalIds.Add(a.AssigneeUserId); principalIds.Add(a.AssignedBy); }
            foreach (var e in events) principalIds.Add(e.ActorUserId);
            var principals = await _db.Users.AsNoTracking().Where(u => principalIds.Contains(u.UserId))
                .OrderBy(u => u.UserId).Select(u => new PrincipalTransfer(u.UserId, u.Username, u.DisplayName))
                .ToListAsync(token);
            if (principals.Count != principalIds.Count)
                throw new IntegrityGuardException("Client data references an unknown principal.");

            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["data/company.json"] = CanonicalJson.Serialize(new CompanyTransfer(company.CompanyId, company.LegalName,
                    company.ShortName, company.Industry, company.CountryCode, company.TaxReference, company.Status,
                    company.CreatedAtUtc, company.CreatedBy, company.ArchivedAtUtc, company.RowVersion)),
                ["data/financial_years.json"] = CanonicalJson.Serialize(years.OrderBy(y => y.PeriodEnd).Select(y =>
                    new FinancialYearTransfer(y.FinancialYearId, y.Label, y.PeriodStart, y.PeriodEnd, y.CreatedAtUtc)).ToList()),
                ["data/engagements.json"] = CanonicalJson.Serialize(engagements.OrderBy(e =>
                    years.Single(y => y.FinancialYearId == e.FinancialYearId).PeriodEnd).Select(e =>
                    new EngagementTransfer(e.EngagementId, e.CompanyId, e.FinancialYearId, e.Status, e.CurrencyCode,
                        e.MinorUnitScale, e.CreatedAtUtc, e.CreatedBy, e.FinalizedAtUtc, e.FinalizedBy,
                        e.FinalizationDigest, e.FinalizationManifestVersion, e.RowVersion)).ToList()),
                ["data/accounts.jsonl"] = CanonicalJson.SerializeJsonLines(accounts.Select(a => new AccountTransfer(a.AccountId,
                    a.EngagementId, a.AccountCode, a.AccountName, a.AccountTypeCode, a.DisplayOrder, a.CreatedAtUtc, a.CreatedBy))),
                ["data/financial_data.jsonl"] = CanonicalJson.SerializeJsonLines(values.Select(v => new FinancialDataTransfer(
                    v.FinancialDataId, v.EngagementId, v.AccountId, v.RevisionNo, v.AmountMinor, v.CurrencyCode,
                    v.SupersedesId, v.CorrectionReason, v.RecordedAtUtc, v.RecordedBy))),
                ["data/prior_year_relationships.json"] = CanonicalJson.Serialize(priors.Select(r => new PriorYearTransfer(
                    r.RelationshipId, r.CurrentEngagementId, r.PriorEngagementId, r.LinkedAtUtc, r.LinkedBy)).ToList()),
                ["data/finalization_manifests.json"] = CanonicalJson.Serialize(finalizations.Select(m =>
                    new FinalizationManifestTransfer(m.ManifestId, m.EngagementId, m.ManifestVersion, m.RootDigest,
                        m.CanonicalContent, m.RecordCount, m.CreatedAtUtc, m.CreatedBy)).ToList()),
                ["data/principals.json"] = CanonicalJson.Serialize(principals),
                ["history/team_members.json"] = CanonicalJson.Serialize(members),
                ["history/assignments.json"] = CanonicalJson.Serialize(assignments),
                ["audit/events.jsonl"] = CanonicalJson.SerializeJsonLines(events.Select(ToTransfer)),
                ["audit/source_chain.json"] = CanonicalJson.Serialize(new SourceChainTransfer(fullChain.LastOrDefault()?.SequenceNo ?? 0,
                    fullChain.LastOrDefault()?.EventHash, true, events.Count)),
            };

            var descriptors = files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new PackageFileDescriptor(
                f.Key, f.Value.LongLength, CanonicalJson.Sha256(f.Value),
                f.Key.EndsWith(".jsonl", StringComparison.Ordinal) ? "application/x-ndjson" : "application/json",
                f.Key.StartsWith("history/", StringComparison.Ordinal) ? "history" :
                f.Key.StartsWith("audit/", StringComparison.Ordinal) ? "audit" : "client")).ToList();
            var manifest = new HandoverManifest(Format, Guid.NewGuid(), IClock.Format(_clock.UtcNow),
                new ExportActor(_actor.UserId, _actor.DisplayName),
                new SourceDescriptor(SqlMigrationRunner.SchemaVersion, SqlMigrationRunner.WorkspaceFormatVersion,
                    Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "development"),
                new RequirementDescriptor(MinSchema, new[] { FinalizationManifestBuilder.ManifestVersion }),
                new ManifestClient(company.CompanyId, company.ShortName, company.LegalName, company.Status),
                engagements.OrderBy(e => years.Single(y => y.FinancialYearId == e.FinancialYearId).PeriodEnd)
                    .Select(e => { var y = years.Single(x => x.FinancialYearId == e.FinancialYearId); return new ManifestEngagement(
                        e.EngagementId, e.FinancialYearId, y.Label, y.PeriodEnd, e.Status, e.FinalizationDigest); }).ToList(),
                new Dictionary<string, int> { ["accounts"] = accounts.Count, ["financial_data"] = values.Count,
                    ["audit_events"] = events.Count, ["principals"] = principals.Count }, "SHA-256", descriptors);
            var manifestBytes = CanonicalJson.Serialize(manifest);
            packageBytes = WriteZip(manifestBytes, files);
            var digest = CanonicalJson.Sha256(manifestBytes);
            await _audit.AppendAsync(AuditEventType.ClientExported, AuditEntityType.Company, companyId.ToString("D"),
                $"Client {company.ShortName} exported as a handover package.", companyId: companyId,
                details: AuditDetails.Empty().With("package_id", manifest.PackageId).With("package_digest", digest),
                cancellationToken: token);
            return true;
        }, cancellationToken);

        await destination.WriteAsync(packageBytes, cancellationToken);
    }

    public async Task<ClientHandoverValidationReport> ValidateAsync(Stream package,
        CancellationToken cancellationToken = default)
    {
        await _authorization.RequireWorkspacePrivilegeAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var parsed = await ReadPackageAsync(package, cancellationToken);
            return await ValidateParsedAsync(parsed, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException
                                      or InvalidOperationException or NullReferenceException)
        {
            return new ClientHandoverValidationReport(null, null, 0,
                new[] { Error("FORMAT_UNSUPPORTED", "package", null, "The package structure or JSON is invalid.") });
        }
    }

    public async Task<ClientHandoverImportResult> ImportAsync(Stream package,
        CancellationToken cancellationToken = default)
    {
        await _authorization.RequireWorkspacePrivilegeAsync(cancellationToken).ConfigureAwait(false);
        var parsed = await ReadPackageAsync(package, cancellationToken);
        var report = await ValidateParsedAsync(parsed, cancellationToken);
        if (!report.IsValid)
            throw new ValidationException("Client handover validation failed: " +
                string.Join(" ", report.Findings.Where(f => f.Severity == "ERROR").Select(f => $"{f.Code}: {f.Message}")));
        return await _uow.ExecuteAsync(async token =>
        {
            var connection = _db.Database.GetDbConnection();
            var transaction = _db.Database.CurrentTransaction?.GetDbTransaction()
                              ?? throw new InvalidOperationException("Import requires a database transaction.");
            var now = IClock.Format(_clock.UtcNow);
            foreach (var p in parsed.Principals)
            {
                if (!await ExistsAsync(connection, transaction, "SELECT 1 FROM app_user WHERE user_id=$id", token, ("$id", p.UserId)))
                    await ExecuteAsync(connection, transaction,
                        "INSERT INTO app_user(user_id,username,display_name,email,status,is_local_demo,created_at_utc,updated_at_utc,is_external_principal) VALUES($id,$username,$name,NULL,'DISABLED',0,$now,$now,1)", token,
                        ("$id", p.UserId), ("$username", p.Username), ("$name", p.DisplayName), ("$now", now));
            }
            foreach (var y in parsed.FinancialYears)
            {
                if (!await ExistsAsync(connection, transaction, "SELECT 1 FROM financial_year WHERE financial_year_id=$id", token, ("$id", y.FinancialYearId)))
                    await ExecuteAsync(connection, transaction,
                        "INSERT INTO financial_year VALUES($id,$label,$start,$end,$created)", token,
                        ("$id", y.FinancialYearId), ("$label", y.Label), ("$start", y.PeriodStart),
                        ("$end", y.PeriodEnd), ("$created", y.CreatedAtUtc));
            }
            var c = parsed.Company;
            await ExecuteAsync(connection, transaction,
                "INSERT INTO company VALUES($id,$legal,$short,$industry,$country,$tax,$status,$created,$by,$archived,$version)", token,
                ("$id", c.CompanyId), ("$legal", c.LegalName), ("$short", c.ShortName), ("$industry", c.Industry),
                ("$country", c.CountryCode), ("$tax", c.TaxReference), ("$status", c.Status), ("$created", c.CreatedAtUtc),
                ("$by", c.CreatedBy), ("$archived", c.ArchivedAtUtc), ("$version", c.RowVersion));

            var partnerRole = await ScalarAsync(connection, transaction,
                "SELECT role_id FROM app_role WHERE role_key='PARTNER'", token) ?? throw new ValidationException("ROLE_UNKNOWN: PARTNER");
            var yearEnds = parsed.FinancialYears.ToDictionary(y => y.FinancialYearId, y => y.PeriodEnd);
            foreach (var e in parsed.Engagements.OrderBy(e => yearEnds[e.FinancialYearId], StringComparer.Ordinal))
            {
                var initialStatus = e.Status == "FINALIZED" ? "DRAFT" : e.Status;
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO engagement VALUES($id,$company,$year,$status,$currency,$scale,$created,$by,NULL,NULL,NULL,NULL,$version)", token,
                    ("$id", e.EngagementId), ("$company", e.CompanyId), ("$year", e.FinancialYearId),
                    ("$status", initialStatus), ("$currency", e.CurrencyCode), ("$scale", e.MinorUnitScale),
                    ("$created", e.CreatedAtUtc), ("$by", e.CreatedBy), ("$version", e.RowVersion));

                // Every engagement owns exactly one financial period. A packaged
                // finalized year arrives locked, exactly as migration 0007 treats a
                // year that was finalized before the period existed.
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO financial_period (financial_period_id, engagement_id, financial_year_id, reporting_date, " +
                    "status, created_at_utc, created_by, updated_at_utc, row_version) " +
                    "VALUES($id,$eng,$year,$report,$status,$now,$by,$now,1)", token,
                    ("$id", Guid.NewGuid()), ("$eng", e.EngagementId), ("$year", e.FinancialYearId),
                    ("$report", yearEnds[e.FinancialYearId]),
                    ("$status", e.Status == "FINALIZED" ? FinancialPeriodStatus.Locked : FinancialPeriodStatus.Open),
                    ("$now", now), ("$by", e.CreatedBy));

                foreach (var a in parsed.Accounts.Where(a => a.EngagementId == e.EngagementId).OrderBy(a => a.AccountCode))
                    await ExecuteAsync(connection, transaction,
                        "INSERT INTO account (account_id, engagement_id, account_code, account_name, account_type, " +
                        "display_order, created_at_utc, created_by, normalized_code, account_group, account_origin, audit_area_id) " +
                        "VALUES($id,$eng,$code,$name,$type,$sort,$created,$by,$normalized,NULL,'MANUAL',NULL)", token,
                        ("$id", a.AccountId), ("$eng", a.EngagementId), ("$code", a.AccountCode), ("$name", a.AccountName),
                        ("$type", a.AccountType), ("$sort", a.DisplayOrder), ("$created", a.CreatedAtUtc), ("$by", a.CreatedBy),
                        ("$normalized", Account.NormalizeCode(a.AccountCode)));
                foreach (var v in parsed.FinancialData.Where(v => v.EngagementId == e.EngagementId)
                             .OrderBy(v => v.AccountId).ThenBy(v => v.RevisionNo))
                    await ExecuteAsync(connection, transaction,
                        "INSERT INTO financial_data VALUES($id,$eng,$account,$revision,$amount,$currency,$supersedes,$reason,$recorded,$by)", token,
                        ("$id", v.FinancialDataId), ("$eng", v.EngagementId), ("$account", v.AccountId),
                        ("$revision", v.RevisionNo), ("$amount", v.AmountMinor), ("$currency", v.CurrencyCode),
                        ("$supersedes", v.SupersedesId), ("$reason", v.CorrectionReason), ("$recorded", v.RecordedAtUtc), ("$by", v.RecordedBy));
                foreach (var r in parsed.PriorYears.Where(r => r.CurrentEngagementId == e.EngagementId))
                    await ExecuteAsync(connection, transaction, "INSERT INTO prior_year_relationship VALUES($id,$current,$prior,$linked,$by)", token,
                        ("$id", r.RelationshipId), ("$current", r.CurrentEngagementId), ("$prior", r.PriorEngagementId),
                        ("$linked", r.LinkedAtUtc), ("$by", r.LinkedBy));
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO engagement_member VALUES($id,$eng,$user,$role,'ACTIVE',$now,$user,$now,1)", token,
                    ("$id", Guid.NewGuid()), ("$eng", e.EngagementId), ("$user", _actor.UserId), ("$role", partnerRole), ("$now", now));
                if (e.Status == "FINALIZED")
                {
                    var m = parsed.FinalizationManifests.Single(m => m.EngagementId == e.EngagementId);
                    await ExecuteAsync(connection, transaction,
                        "INSERT INTO finalization_manifest VALUES($id,$eng,$format,$digest,$content,$count,$created,$by)", token,
                        ("$id", m.ManifestId), ("$eng", m.EngagementId), ("$format", m.ManifestVersion), ("$digest", m.RootDigest),
                        ("$content", m.CanonicalContent), ("$count", m.RecordCount), ("$created", m.CreatedAtUtc), ("$by", m.CreatedBy));
                    await ExecuteAsync(connection, transaction,
                        "UPDATE engagement SET status='FINALIZED',finalized_at_utc=$at,finalized_by=$by,finalization_digest=$digest,finalization_manifest_version=$format,row_version=$version WHERE engagement_id=$id", token,
                        ("$at", e.FinalizedAtUtc), ("$by", e.FinalizedBy), ("$digest", e.FinalizationDigest),
                        ("$format", e.FinalizationManifestVersion), ("$version", e.RowVersion), ("$id", e.EngagementId));
                }
            }

            var importId = Guid.NewGuid();
            var history = CanonicalJson.Serialize(new { team_members = parsed.TeamMembers, assignments = parsed.Assignments });
            await ExecuteAsync(connection, transaction,
                "INSERT INTO client_import VALUES($id,$package,$digest,$company,$at,$by,$manifest,$history)", token,
                ("$id", importId), ("$package", parsed.Manifest.PackageId), ("$digest", parsed.PackageDigest),
                ("$company", c.CompanyId), ("$at", now), ("$by", _actor.UserId),
                ("$manifest", Encoding.UTF8.GetString(parsed.ManifestBytes)), ("$history", Encoding.UTF8.GetString(history)));
            foreach (var e in parsed.AuditEvents)
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO imported_audit_event VALUES($id,$import,$source,$sequence,$at,$actor,$actor_name,$type,$outcome,$company,$engagement,$entity_type,$entity_id,$description,$details,$previous,$hash)", token,
                    ("$id", Guid.NewGuid()), ("$import", importId), ("$source", e.AuditEventId), ("$sequence", e.SequenceNo),
                    ("$at", e.OccurredAtUtc), ("$actor", e.ActorUserId), ("$actor_name", e.ActorDisplayName),
                    ("$type", e.EventType), ("$outcome", e.Outcome), ("$company", e.CompanyId), ("$engagement", e.EngagementId),
                    ("$entity_type", e.EntityType), ("$entity_id", e.EntityId), ("$description", e.Description),
                    ("$details", e.DetailsJson), ("$previous", e.PreviousEventHash), ("$hash", e.EventHash));

            _db.ChangeTracker.Clear();
            foreach (var e in parsed.Engagements.Where(e => e.Status == "FINALIZED"))
            {
                var rebuilt = await _finalization.BuildManifestDocumentAsync(e.EngagementId, token);
                var shipped = parsed.FinalizationManifests.Single(m => m.EngagementId == e.EngagementId);
                if (!string.Equals(rebuilt, shipped.CanonicalContent, StringComparison.Ordinal))
                    throw new IntegrityGuardException("Post-import manifest verification failed; no client data was committed.");
            }
            await _audit.AppendAsync(AuditEventType.ClientImported, AuditEntityType.Company, c.CompanyId.ToString("D"),
                $"Client {c.ShortName} imported from a handover package.", companyId: c.CompanyId,
                details: AuditDetails.Empty().With("package_id", parsed.Manifest.PackageId)
                    .With("package_digest", parsed.PackageDigest).With("import_id", importId), cancellationToken: token);
            return new ClientHandoverImportResult(importId, c.CompanyId, parsed.PackageDigest);
        }, cancellationToken);
    }

    private async Task<ClientHandoverValidationReport> ValidateParsedAsync(ParsedClientPackage p, CancellationToken token)
    {
        var findings = new List<ClientHandoverFinding>();
        if (!CanonicalJson.Serialize(p.Manifest).AsSpan().SequenceEqual(p.ManifestBytes))
            findings.Add(Error("FORMAT_UNSUPPORTED", "manifest", null, "manifest.json is not canonical JSON."));
        if (p.Manifest.PackageFormat != Format) findings.Add(Error("FORMAT_UNSUPPORTED", "manifest", null, "Only AWB-CLIENT/1.0 is supported."));
        if (!string.Equals(p.Manifest.HashAlgorithm, "SHA-256", StringComparison.Ordinal))
            findings.Add(Error("FORMAT_UNSUPPORTED", "manifest", null, "Only SHA-256 package hashes are supported."));
        if (p.Manifest.Requires.MinSchemaVersion != MinSchema)
            findings.Add(Error("SCHEMA_TOO_OLD", "manifest", null, $"Package requires unsupported schema {p.Manifest.Requires.MinSchemaVersion}."));
        if (!p.Manifest.Requires.ManifestVersions.All(v => v == FinalizationManifestBuilder.ManifestVersion))
            findings.Add(Error("FORMAT_UNSUPPORTED", "manifest", null, "The package requires an unsupported finalization manifest version."));
        foreach (var descriptor in p.Manifest.Files)
        {
            if (!p.Entries.TryGetValue(descriptor.Path, out var bytes) || bytes.LongLength != descriptor.Bytes ||
                CanonicalJson.Sha256(bytes) != descriptor.Sha256)
                findings.Add(Error("CHECKSUM_MISMATCH", "file", descriptor.Path, "The package entry length or SHA-256 does not match the manifest."));
        }
        if (p.Manifest.Files.Select(f => f.Path).Order().SequenceEqual(DataPaths.Order()) is false)
            findings.Add(Error("FORMAT_UNSUPPORTED", "manifest", null, "The manifest file list is incomplete or contains unknown entries."));
        if (p.Company.CompanyId != p.Manifest.Client.CompanyId)
            findings.Add(Error("REFERENCE_DANGLING", "company", p.Company.CompanyId.ToString("D"), "Company does not match the package manifest."));
        var expectedCounts = new Dictionary<string, int>
        {
            ["accounts"] = p.Accounts.Count, ["financial_data"] = p.FinancialData.Count,
            ["audit_events"] = p.AuditEvents.Count, ["principals"] = p.Principals.Count,
        };
        if (expectedCounts.Any(x => !p.Manifest.Counts.TryGetValue(x.Key, out var count) || count != x.Value))
            findings.Add(Error("FORMAT_UNSUPPORTED", "manifest", null, "One or more manifest counts are incorrect."));
        var duplicateIds = p.Engagements.Select(x => x.EngagementId).GroupBy(x => x).Any(g => g.Count() > 1)
                           || p.FinancialYears.Select(x => x.FinancialYearId).GroupBy(x => x).Any(g => g.Count() > 1)
                           || p.Accounts.Select(x => x.AccountId).GroupBy(x => x).Any(g => g.Count() > 1)
                           || p.FinancialData.Select(x => x.FinancialDataId).GroupBy(x => x).Any(g => g.Count() > 1)
                           || p.PriorYears.Select(x => x.RelationshipId).GroupBy(x => x).Any(g => g.Count() > 1)
                           || p.FinalizationManifests.Select(x => x.ManifestId).GroupBy(x => x).Any(g => g.Count() > 1)
                           || p.FinalizationManifests.Select(x => x.EngagementId).GroupBy(x => x).Any(g => g.Count() > 1)
                           || p.Principals.Select(x => x.UserId).GroupBy(x => x).Any(g => g.Count() > 1)
                           || p.AuditEvents.Select(x => x.SequenceNo).GroupBy(x => x).Any(g => g.Count() > 1);
        if (duplicateIds)
        {
            findings.Add(Error("REFERENCE_DANGLING", "package", null, "The package contains duplicate ids."));
            return new ClientHandoverValidationReport(p.Manifest.PackageId.ToString("D"), p.Company.LegalName,
                p.Engagements.Count, findings);
        }

        var engagementIds = p.Engagements.Select(e => e.EngagementId).ToHashSet();
        var yearIds = p.FinancialYears.Select(y => y.FinancialYearId).ToHashSet();
        var yearById = p.FinancialYears.ToDictionary(y => y.FinancialYearId);
        if (p.Engagements.Any(e => e.Status is not ("DRAFT" or "IN_PROGRESS" or "FINALIZED")) ||
            p.Engagements.Any(e => e.CurrencyCode.Length != 3 || e.MinorUnitScale is < 0 or > 6) ||
            p.FinancialYears.Any(y => y.PeriodStart.Length != 10 || y.PeriodEnd.Length != 10 ||
                                        string.CompareOrdinal(y.PeriodStart, y.PeriodEnd) > 0))
            findings.Add(Error("FORMAT_UNSUPPORTED", "package", null, "One or more transferred domain values are invalid."));
        var orderedPeriods = p.Engagements.Where(e => yearById.ContainsKey(e.FinancialYearId))
            .Select(e => yearById[e.FinancialYearId]).OrderBy(y => y.PeriodStart, StringComparer.Ordinal).ToList();
        if (orderedPeriods.Zip(orderedPeriods.Skip(1), (left, right) =>
                string.CompareOrdinal(left.PeriodEnd, right.PeriodStart) >= 0).Any(overlaps => overlaps))
            findings.Add(Error("REFERENCE_DANGLING", "financial_year", null, "Client financial-year periods overlap."));
        var finalizedIds = p.Engagements.Where(e => e.Status == "FINALIZED").Select(e => e.EngagementId).ToHashSet();
        if (!finalizedIds.SetEquals(p.FinalizationManifests.Select(m => m.EngagementId)))
            findings.Add(Error("MANIFEST_DIGEST_MISMATCH", "package", null, "Finalized engagements and finalization manifests do not pair one-to-one."));
        foreach (var prior in p.PriorYears)
        {
            var current = p.Engagements.FirstOrDefault(e => e.EngagementId == prior.CurrentEngagementId);
            var previous = p.Engagements.FirstOrDefault(e => e.EngagementId == prior.PriorEngagementId);
            if (current is not null && previous is not null &&
                (previous.Status != "FINALIZED" || !yearById.TryGetValue(current.FinancialYearId, out var currentYear) ||
                 !yearById.TryGetValue(previous.FinancialYearId, out var previousYear) ||
                 string.CompareOrdinal(previousYear.PeriodEnd, currentYear.PeriodEnd) >= 0))
                findings.Add(Error("REFERENCE_DANGLING", "prior_year_relationship", prior.RelationshipId.ToString("D"),
                    "The prior engagement must be finalized and earlier than the current engagement."));
        }
        var principalIds = p.Principals.Select(x => x.UserId).ToHashSet();
        var accountIds = p.Accounts.Select(a => a.AccountId).ToHashSet();
        if (p.Engagements.Any(e => e.CompanyId != p.Company.CompanyId || !yearIds.Contains(e.FinancialYearId)) ||
            p.Accounts.Any(a => !engagementIds.Contains(a.EngagementId)) ||
            p.FinancialData.Any(v => !engagementIds.Contains(v.EngagementId) || !accountIds.Contains(v.AccountId)) ||
            p.PriorYears.Any(r => !engagementIds.Contains(r.CurrentEngagementId) || !engagementIds.Contains(r.PriorEngagementId)))
            findings.Add(Error("REFERENCE_DANGLING", "package", null, "One or more client references do not resolve inside the package."));
        var referencedPrincipals = ReferencedPrincipalIds(p);
        if (referencedPrincipals.Any(id => !principalIds.Contains(id)))
            findings.Add(Error("REFERENCE_DANGLING", "principal", null, "One or more attribution principals are missing."));

        foreach (var account in p.Accounts)
        {
            var revisions = p.FinancialData.Where(v => v.AccountId == account.AccountId).OrderBy(v => v.RevisionNo).ToList();
            for (var i = 0; i < revisions.Count; i++)
            {
                var v = revisions[i];
                var engagement = p.Engagements.FirstOrDefault(e => e.EngagementId == v.EngagementId);
                if (v.RevisionNo != i + 1 || (i == 0 ? v.SupersedesId is not null : v.SupersedesId != revisions[i - 1].FinancialDataId)
                    || engagement is null || v.CurrencyCode != engagement.CurrencyCode)
                    findings.Add(Error("REFERENCE_DANGLING", "financial_data", v.FinancialDataId.ToString("D"), "Revision chain, ownership or currency is invalid."));
            }
        }
        foreach (var e in p.Engagements.Where(e => e.Status == "FINALIZED"))
        {
            var m = p.FinalizationManifests.SingleOrDefault(m => m.EngagementId == e.EngagementId);
            var rebuilt = BuildManifest(p, e);
            if (m is null || m.ManifestVersion != FinalizationManifestBuilder.ManifestVersion ||
                CanonicalJson.Sha256(Encoding.UTF8.GetBytes(m.CanonicalContent)) != m.RootDigest ||
                m.RootDigest != e.FinalizationDigest || rebuilt != m.CanonicalContent)
                findings.Add(Error("MANIFEST_DIGEST_MISMATCH", "engagement", e.EngagementId.ToString("D"), "Finalization evidence does not match the transferred business data."));
        }
        if (p.SourceChain.SubsetCount != p.AuditEvents.Count || !p.SourceChain.VerifiedAtExport ||
            !p.AuditEvents.Select(e => e.SequenceNo).SequenceEqual(p.AuditEvents.Select(e => e.SequenceNo).Order()) ||
            p.AuditEvents.Any(e => ComputeAuditHash(e) != e.EventHash))
            findings.Add(Error("CHECKSUM_MISMATCH", "audit", null, "Archived source audit evidence failed verification."));

        if (await _db.Companies.AnyAsync(c => c.CompanyId == p.Company.CompanyId, token))
            findings.Add(Error("CLIENT_ALREADY_EXISTS", "company", p.Company.CompanyId.ToString("D"), "This client id already exists."));
        if (await _db.Companies.AnyAsync(c => c.ShortName.ToLower() == p.Company.ShortName.ToLower() && c.CompanyId != p.Company.CompanyId, token))
            findings.Add(Error("CLIENT_SHORT_NAME_TAKEN", "company", p.Company.CompanyId.ToString("D"), "The client short name is already in use."));
        if (await ClientOwnedIdCollisionAsync(p, token))
            findings.Add(Error("ID_COLLISION", "package", null, "A transferred row id is already used in this workspace."));
        if (await _db.Database.SqlQueryRaw<int>(
                "SELECT 1 AS Value FROM client_import WHERE package_id={0}",
                _db.Database.IsSqlite() ? p.Manifest.PackageId.ToString("D") : p.Manifest.PackageId).AnyAsync(token))
            findings.Add(Error("PACKAGE_ALREADY_IMPORTED", "package", p.Manifest.PackageId.ToString("D"), "This package was already imported."));
        foreach (var y in p.FinancialYears)
        {
            var existing = await _db.FinancialYears.AsNoTracking().SingleOrDefaultAsync(x => x.FinancialYearId == y.FinancialYearId, token);
            if (existing is not null && (existing.Label != y.Label || existing.PeriodStart != y.PeriodStart || existing.PeriodEnd != y.PeriodEnd))
                findings.Add(Error("FINANCIAL_YEAR_CONFLICT", "financial_year", y.FinancialYearId.ToString("D"), "The id exists with a different definition."));
        }
        foreach (var principal in p.Principals)
        {
            var byId = await _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == principal.UserId, token);
            var byName = await _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Username == principal.Username, token);
            if ((byId is not null && byId.Username != principal.Username) || (byName is not null && byName.UserId != principal.UserId))
                findings.Add(Error("PRINCIPAL_CONFLICT", "principal", principal.UserId.ToString("D"), "The user id or username belongs to a different principal."));
        }
        if (p.Company.Status == "ARCHIVED") findings.Add(new ClientHandoverFinding("CLIENT_ARCHIVED", "WARNING", "company",
            p.Company.CompanyId.ToString("D"), "The imported client is archived and cannot receive a new financial year."));
        return new ClientHandoverValidationReport(p.Manifest.PackageId.ToString("D"), p.Company.LegalName, p.Engagements.Count, findings);
    }

    private async Task<ParsedClientPackage> ReadPackageAsync(Stream source, CancellationToken token)
    {
        if (!source.CanRead) throw new ArgumentException("The package stream is not readable.");
        using var copy = new MemoryStream();
        await source.CopyToAsync(copy, token);
        if (copy.Length > MaxPackageBytes) throw new InvalidDataException("Package exceeds the 100 MiB limit.");
        copy.Position = 0;
        using var zip = new ZipArchive(copy, ZipArchiveMode.Read, leaveOpen: false);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long totalUncompressed = 0;
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName;
            var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!AllowedPaths.Contains(path) || path.Contains("..", StringComparison.Ordinal) || path.StartsWith('/') ||
                path.Contains('\\') || unixFileType == 0xA000)
                throw new InvalidDataException($"Unsafe or unknown ZIP entry '{path}'.");
            if (!entries.TryAdd(path, Array.Empty<byte>())) throw new InvalidDataException($"Duplicate ZIP entry '{path}'.");
            totalUncompressed += entry.Length;
            if (entry.Length > MaxEntryBytes || totalUncompressed > MaxPackageBytes ||
                (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxCompressionRatio))
                throw new InvalidDataException($"ZIP entry '{path}' exceeds safety limits.");
            await using var input = entry.Open();
            using var output = new MemoryStream();
            await input.CopyToAsync(output, token);
            entries[path] = output.ToArray();
        }
        if (!AllowedPaths.SetEquals(entries.Keys)) throw new InvalidDataException("The package has missing or unexpected entries.");
        var manifestBytes = entries["manifest.json"];
        return new ParsedClientPackage
        {
            Manifest = CanonicalJson.Deserialize<HandoverManifest>(manifestBytes), ManifestBytes = manifestBytes,
            Company = CanonicalJson.Deserialize<CompanyTransfer>(entries["data/company.json"]),
            FinancialYears = CanonicalJson.Deserialize<List<FinancialYearTransfer>>(entries["data/financial_years.json"]),
            Engagements = CanonicalJson.Deserialize<List<EngagementTransfer>>(entries["data/engagements.json"]),
            Accounts = CanonicalJson.DeserializeJsonLines<AccountTransfer>(entries["data/accounts.jsonl"]),
            FinancialData = CanonicalJson.DeserializeJsonLines<FinancialDataTransfer>(entries["data/financial_data.jsonl"]),
            PriorYears = CanonicalJson.Deserialize<List<PriorYearTransfer>>(entries["data/prior_year_relationships.json"]),
            FinalizationManifests = CanonicalJson.Deserialize<List<FinalizationManifestTransfer>>(entries["data/finalization_manifests.json"]),
            Principals = CanonicalJson.Deserialize<List<PrincipalTransfer>>(entries["data/principals.json"]),
            TeamMembers = CanonicalJson.Deserialize<List<TeamMemberTransfer>>(entries["history/team_members.json"]),
            Assignments = CanonicalJson.Deserialize<List<AssignmentTransfer>>(entries["history/assignments.json"]),
            AuditEvents = CanonicalJson.DeserializeJsonLines<AuditEventTransfer>(entries["audit/events.jsonl"]),
            SourceChain = CanonicalJson.Deserialize<SourceChainTransfer>(entries["audit/source_chain.json"]), Entries = entries,
        };
    }

    private static byte[] WriteZip(byte[] manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "manifest.json", manifest);
            foreach (var file in files.OrderBy(f => f.Key, StringComparer.Ordinal)) WriteEntry(zip, file.Key, file.Value);
        }
        return output.ToArray();
    }
    private static void WriteEntry(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open(); stream.Write(bytes);
    }

    private static AuditEventTransfer ToTransfer(AuditWorkbench.Domain.Auditing.AuditEvent e) => new(e.AuditEventId, e.SequenceNo,
        e.OccurredAtUtc, e.ActorUserId, e.ActorDisplayName, e.EventType, e.Outcome, e.CompanyId, e.EngagementId,
        e.EntityType, e.EntityId, e.Description, e.DetailsJson, e.PreviousEventHash, e.EventHash);
    private static bool VerifyFullAuditChain(IReadOnlyList<AuditWorkbench.Domain.Auditing.AuditEvent> events)
    {
        string? previous = null; long sequence = 0;
        foreach (var e in events)
        {
            if (e.SequenceNo != sequence + 1 || e.PreviousEventHash != previous || !e.HashMatches()) return false;
            sequence = e.SequenceNo; previous = e.EventHash;
        }
        return true;
    }
    private static string ComputeAuditHash(AuditEventTransfer e)
    {
        var canonical = string.Join("|", e.SequenceNo, e.AuditEventId.ToString("D"), e.OccurredAtUtc,
            e.ActorUserId.ToString("D"), e.EventType, e.Outcome, e.CompanyId?.ToString("D") ?? "NONE",
            e.EngagementId?.ToString("D") ?? "NONE", e.EntityType, e.EntityId, e.Description, e.DetailsJson,
            e.PreviousEventHash ?? "GENESIS");
        return AuditWorkbench.Domain.Auditing.AuditEvent.Sha256Hex(canonical);
    }
    private static string BuildManifest(ParsedClientPackage p, EngagementTransfer e)
    {
        var y = p.FinancialYears.Single(y => y.FinancialYearId == e.FinancialYearId);
        var prior = p.PriorYears.SingleOrDefault(r => r.CurrentEngagementId == e.EngagementId);
        var latest = p.FinancialData.GroupBy(v => v.AccountId).ToDictionary(g => g.Key, g => g.MaxBy(v => v.RevisionNo)!);
        return FinalizationManifestBuilder.BuildDocument(new FinalizationManifestBuilder.ManifestInput
        {
            EngagementId = e.EngagementId, CompanyId = p.Company.CompanyId, CompanyLegalName = p.Company.LegalName,
            CompanyShortName = p.Company.ShortName, FinancialYearId = y.FinancialYearId, FinancialYearLabel = y.Label,
            PeriodStart = y.PeriodStart, PeriodEnd = y.PeriodEnd, CurrencyCode = e.CurrencyCode,
            MinorUnitScale = e.MinorUnitScale, PriorEngagementId = prior?.PriorEngagementId,
            PriorRootDigest = prior is null ? null : p.Engagements.Single(x => x.EngagementId == prior.PriorEngagementId).FinalizationDigest,
            Accounts = p.Accounts.Where(a => a.EngagementId == e.EngagementId).Select(a =>
            {
                latest.TryGetValue(a.AccountId, out var v);
                return new ManifestAccountLine { AccountCode = a.AccountCode, AccountName = a.AccountName,
                    AccountType = a.AccountType, RevisionNo = v?.RevisionNo, AmountMinor = v?.AmountMinor };
            }).ToList(),
        });
    }
    private static HashSet<Guid> ReferencedPrincipalIds(ParsedClientPackage p)
    {
        var ids = new HashSet<Guid> { p.Company.CreatedBy };
        foreach (var e in p.Engagements) { ids.Add(e.CreatedBy); if (e.FinalizedBy is { } id) ids.Add(id); }
        foreach (var x in p.Accounts) ids.Add(x.CreatedBy); foreach (var x in p.FinancialData) ids.Add(x.RecordedBy);
        foreach (var x in p.PriorYears) ids.Add(x.LinkedBy); foreach (var x in p.FinalizationManifests) ids.Add(x.CreatedBy);
        foreach (var x in p.TeamMembers) { ids.Add(x.UserId); ids.Add(x.AddedBy); }
        foreach (var x in p.Assignments) { ids.Add(x.AssigneeUserId); ids.Add(x.AssignedBy); }
        foreach (var x in p.AuditEvents) ids.Add(x.ActorUserId); return ids;
    }
    private async Task<bool> ClientOwnedIdCollisionAsync(ParsedClientPackage p, CancellationToken token)
    {
        // Identifiers are selected from a fixed query whitelist; package content
        // is always a bound parameter and can never become SQL syntax.
        foreach (var (sql, ids) in new (string, IEnumerable<Guid>)[]
        {
            ("SELECT 1 AS Value FROM engagement WHERE engagement_id={0}", p.Engagements.Select(x => x.EngagementId)),
            ("SELECT 1 AS Value FROM account WHERE account_id={0}", p.Accounts.Select(x => x.AccountId)),
            ("SELECT 1 AS Value FROM financial_data WHERE financial_data_id={0}", p.FinancialData.Select(x => x.FinancialDataId)),
            ("SELECT 1 AS Value FROM prior_year_relationship WHERE relationship_id={0}", p.PriorYears.Select(x => x.RelationshipId)),
            ("SELECT 1 AS Value FROM finalization_manifest WHERE manifest_id={0}", p.FinalizationManifests.Select(x => x.ManifestId)),
        })
            foreach (var id in ids)
                if (await _db.Database.SqlQueryRaw<int>(sql,
                        _db.Database.IsSqlite() ? id.ToString("D") : id).AnyAsync(token)) return true;
        return false;
    }
    private static ClientHandoverFinding Error(string code, string entity, string? id, string message) => new(code, "ERROR", entity, id, message);

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql,
        CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = CommandText(connection, sql);
        foreach (var p in parameters) { var parameter = command.CreateParameter();
            parameter.ParameterName = ParameterName(connection, p.Name);
            parameter.Value = ParameterValue(connection, p.Value); command.Parameters.Add(parameter); }
        await command.ExecuteNonQueryAsync(token);
    }
    private static async Task<bool> ExistsAsync(DbConnection c, DbTransaction t, string sql, CancellationToken token,
        params (string Name, object? Value)[] p) => await ScalarAsync(c, t, sql, token, p) is not null;
    private static async Task<string?> ScalarAsync(DbConnection connection, DbTransaction transaction, string sql,
        CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = CommandText(connection, sql);
        foreach (var p in parameters) { var parameter = command.CreateParameter();
            parameter.ParameterName = ParameterName(connection, p.Name);
            parameter.Value = ParameterValue(connection, p.Value); command.Parameters.Add(parameter); }
        return (await command.ExecuteScalarAsync(token))?.ToString();
    }

    private static bool IsSqlite(DbConnection connection) =>
        connection.GetType().Name == "SqliteConnection";

    private static string CommandText(DbConnection connection, string sql) =>
        IsSqlite(connection) ? sql : Regex.Replace(sql, @"\$([A-Za-z_][A-Za-z0-9_]*)", "@$1");

    private static string ParameterName(DbConnection connection, string name) =>
        IsSqlite(connection) ? name : "@" + name.TrimStart('$', '@', ':');

    private static object ParameterValue(DbConnection connection, object? value) => value switch
    {
        null => DBNull.Value,
        Guid guid when IsSqlite(connection) => guid.ToString("D"),
        _ => value,
    };
}
