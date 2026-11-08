using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Kupro.Api.Data;
using Kupro.Api.Features.Attachments;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Issues;
using Kupro.Api.Features.Projects;
using Kupro.Api.Features.Workspaces;
using Kupro.Api.Tenancy;
using Kupro.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kupro.Tests.Features;

/// <summary>The import tables and the source token at rest (task 023, ADR-019 IMP-001).</summary>
[Collection(nameof(StorageTests))]
public sealed class ImportDataTests(PostgresFixture postgres, StorageFixture storage)
    : ApiTestBase(postgres, storage.Settings), IClassFixture<StorageFixture>
{
    private WorkspaceSummary _mine = null!;
    private WorkspaceSummary _theirs = null!;
    private Guid _myMember;
    private Guid _myIssue;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        HttpClient client;
        (client, _mine, _) = await NewWorkspaceAsync("Mine");
        _myMember = (await GetAsync<List<MemberResponse>>(client, $"/api/v1/workspaces/{_mine.Slug}/members")).Single().Id;
        await ReadAsync<ProjectResponse>(await client.PostAsJsonAsync($"/api/v1/workspaces/{_mine.Slug}/projects",
            new CreateProjectRequest("KUP", "Kupro"), Json.Options, Ct), HttpStatusCode.Created);
        _myIssue = (await ReadAsync<IssueDetail>(await client.PostAsJsonAsync($"/api/v1/workspaces/{_mine.Slug}/issues",
            new CreateIssueRequest("KUP", "Imported"), Json.Options, Ct), HttpStatusCode.Created)).Id;
        (_, _theirs, _) = await NewWorkspaceAsync("Theirs");
    }

    [Fact]
    public async Task A_source_token_is_encrypted_with_keys_in_their_own_bucket_and_bound_to_its_import()
    {
        var tokens = Factory.Services.GetRequiredService<ImportTokens>();
        var (one, other) = (Guid.NewGuid(), Guid.NewGuid());

        var encrypted = tokens.Protect(one, "secret-jira-token");

        encrypted.ShouldNotContain("secret-jira-token");
        tokens.Unprotect(one, encrypted).ShouldBe("secret-jira-token");
        Should.Throw<CryptographicException>(() => tokens.Unprotect(other, encrypted));

        var keys = Factory.Services.GetRequiredKeyedService<ObjectStorage>(ImportTokens.KeyStorage);
        (await keys.ListAsync(Ct)).ShouldNotBeEmpty();
        (await Factory.Services.GetRequiredService<ObjectStorage>().ListAsync(Ct)).ShouldNotContain(k => k.StartsWith("key-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_restarted_api_still_reads_tokens_with_the_keys_in_the_bucket()
    {
        var keys = Factory.Services.GetRequiredKeyedService<ObjectStorage>(ImportTokens.KeyStorage);
        var import = Guid.NewGuid();
        var encrypted = Factory.Services.GetRequiredService<ImportTokens>().Protect(import, "secret-jira-token");

        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Kupro");
        services.Configure<KeyManagementOptions>(options => options.XmlRepository = new S3KeyRepository(keys));
        using var restarted = services.BuildServiceProvider();

        new ImportTokens(restarted.GetRequiredService<IDataProtectionProvider>(), keys).Unprotect(import, encrypted).ShouldBe("secret-jira-token");
    }

    [Fact]
    public void Without_key_storage_no_token_is_encrypted()
    {
        using var none = new ObjectStorage(Options.Create(new StorageOptions()));
        var tokens = new ImportTokens(new EphemeralDataProtectionProvider(), none);

        tokens.Enabled.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => tokens.Protect(Guid.NewGuid(), "secret-jira-token"));
    }

    [Fact]
    public async Task Import_tables_stay_inside_their_workspace()
    {
        var import = await AddImportAsync();

        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_theirs.Id);
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);

        (await db.WorkspaceImports.AnyAsync(i => i.Id == import, Ct)).ShouldBeFalse();
        (await db.ImportRuns.AnyAsync(r => r.ImportId == import, Ct)).ShouldBeFalse();
        (await db.ImportedItems.AnyAsync(i => i.ImportId == import, Ct)).ShouldBeFalse();
        (await db.IssueAliases.AnyAsync(a => a.ImportId == import, Ct)).ShouldBeFalse();

        // Even without the filter, the database refuses a row of theirs pointing at my import.
        db.ImportRuns.Add(new ImportRun { Id = Guid.CreateVersion7(), WorkspaceId = _theirs.Id, ImportId = import, CreatedAt = DateTimeOffset.UtcNow });
        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task A_source_record_maps_once_per_import_and_an_alias_once_per_workspace()
    {
        var import = await AddImportAsync();
        var other = await AddImportAsync();

        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_mine.Id);
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);

        // The same source ID is fine in another import or as another kind of record.
        db.ImportedItems.AddRange(Item(other, "item", "1"), Item(import, "comment", "1"));
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        db.ImportedItems.Add(Item(import, "item", "1"));
        await UniqueViolationAsync(db);

        db.IssueAliases.Add(new IssueAlias { Id = Guid.CreateVersion7(), WorkspaceId = _mine.Id, IssueId = _myIssue, ImportId = other, Alias = "#4567", CreatedAt = DateTimeOffset.UtcNow });
        await UniqueViolationAsync(db);
    }

    private static async Task UniqueViolationAsync(KuproDbContext db)
    {
        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        db.ChangeTracker.Clear();
    }

    private ImportedItem Item(Guid import, string kind, string sourceId) => new()
    {
        Id = Guid.CreateVersion7(),
        WorkspaceId = _mine.Id,
        ImportId = import,
        SourceKind = kind,
        SourceId = sourceId,
        EntityKind = ImportedEntityKind.Issue,
        EntityId = _myIssue,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>An import with a run, one item and one alias. Aliases are unique per workspace, so only the first import gets <c>#4567</c>.</summary>
    private async Task<Guid> AddImportAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().SetForSystem(_mine.Id);
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KuproDbContext>>().CreateDbContextAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var import = new WorkspaceImport { Id = Guid.CreateVersion7(), WorkspaceId = _mine.Id, SourceKind = "jira", CreatedById = _myMember, CreatedAt = now };
        db.WorkspaceImports.Add(import);
        db.ImportRuns.Add(new ImportRun { Id = Guid.CreateVersion7(), WorkspaceId = _mine.Id, ImportId = import.Id, CreatedAt = now });
        if (!await db.IssueAliases.AnyAsync(Ct))
        {
            db.ImportedItems.Add(Item(import.Id, "item", "1"));
            db.IssueAliases.Add(new IssueAlias { Id = Guid.CreateVersion7(), WorkspaceId = _mine.Id, IssueId = _myIssue, ImportId = import.Id, Alias = "#4567", CreatedAt = now });
        }

        await db.SaveChangesAsync(Ct);
        return import.Id;
    }
}
