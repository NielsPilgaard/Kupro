using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Flowstate.Api.Features.Attachments;
using Flowstate.Api.Features.Issues;
using Flowstate.Api.Features.Projects;
using Flowstate.Api.Features.Workspaces;
using Flowstate.Tests.Infrastructure;

namespace Flowstate.Tests.Features;

/// <summary>Task 008: presigned upload to S3-compatible storage, read back only through workspace membership.</summary>
public sealed class AttachmentTests(PostgresFixture postgres, StorageFixture storage)
    : ApiTestBase(postgres, storage.Settings), IClassFixture<StorageFixture>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private readonly HttpClient _storage = new();
    private HttpClient _client = null!;
    private WorkspaceSummary _workspace = null!;
    private string Ws => $"/api/v1/workspaces/{_workspace.Slug}";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        (_client, _workspace, _) = await NewWorkspaceAsync();
        await ReadAsync<ProjectResponse>(await _client.PostAsJsonAsync($"{Ws}/projects", new CreateProjectRequest("FLOW", "Flowstate"), Json.Options, Ct),
            HttpStatusCode.Created);
        await ReadAsync<IssueDetail>(await _client.PostAsJsonAsync($"{Ws}/issues", new CreateIssueRequest("FLOW", "Screenshot"), Json.Options, Ct),
            HttpStatusCode.Created);
    }

    public override async ValueTask DisposeAsync()
    {
        _storage.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Upload_complete_and_download_round_trip()
    {
        var upload = await CreateAsync("screen shot.png", "image/png", Png.Length, "flow-1");
        upload.Attachment.Ready.ShouldBeFalse();
        upload.Attachment.IssueKey.ShouldBe("FLOW-1");
        upload.Attachment.Markdown.ShouldBe($"![screen shot.png](attachment:{upload.Attachment.Id})");

        await PutAsync(upload.UploadUrl, Png, "image/png");
        var done = await ReadAsync<AttachmentResponse>(await _client.PostAsync($"{Ws}/attachments/{upload.Attachment.Id}/complete", null, Ct));
        done.Ready.ShouldBeTrue();
        done.Size.ShouldBe(Png.Length);

        var read = await GetAsync<AttachmentResponse>(_client, $"{Ws}/attachments/{upload.Attachment.Id}");
        using var download = await _storage.GetAsync(read.DownloadUrl!, Ct);
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(Png);
        download.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        download.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
    }

    [Fact]
    public async Task Non_images_download_as_files()
    {
        var upload = await CreateAsync("page.html", "text/html", 13);
        await PutAsync(upload.UploadUrl, "<h1>hi</h1>\n\n"u8.ToArray(), "text/html");
        var done = await ReadAsync<AttachmentResponse>(await _client.PostAsync($"{Ws}/attachments/{upload.Attachment.Id}/complete", null, Ct));
        done.Markdown.ShouldStartWith("[page.html]");

        using var download = await _storage.GetAsync(done.DownloadUrl!, Ct);
        download.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        download.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
    }

    [Fact]
    public async Task Complete_before_upload_is_a_conflict()
    {
        var upload = await CreateAsync("late.png", "image/png", 10);
        await ShouldBeProblemAsync(await _client.PostAsync($"{Ws}/attachments/{upload.Attachment.Id}/complete", null, Ct),
            HttpStatusCode.Conflict, "upload_missing");
        (await GetAsync<AttachmentResponse>(_client, $"{Ws}/attachments/{upload.Attachment.Id}")).DownloadUrl.ShouldBeNull();
    }

    [Fact]
    public async Task Other_workspaces_cannot_read_an_attachment()
    {
        var upload = await CreateAsync("secret.png", "image/png", Png.Length);
        await PutAsync(upload.UploadUrl, Png, "image/png");
        await _client.PostAsync($"{Ws}/attachments/{upload.Attachment.Id}/complete", null, Ct);

        var (other, otherWorkspace, _) = await NewWorkspaceAsync("Other");
        await ShouldBeProblemAsync(await other.GetAsync($"/api/v1/workspaces/{otherWorkspace.Slug}/attachments/{upload.Attachment.Id}", Ct),
            HttpStatusCode.NotFound, "attachment_not_found");
        (await other.GetAsync($"{Ws}/attachments/{upload.Attachment.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("big.zip", "application/zip", AttachmentService.MaxSize + 1)]
    [InlineData("x.png", "not a type", 10)]
    public async Task Invalid_uploads_are_rejected(string name, string type, long size)
    {
        (await _client.PostAsJsonAsync($"{Ws}/attachments", new CreateAttachmentRequest(name, type, size), Json.Options, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<AttachmentUpload> CreateAsync(string name, string type, long size, string? issue = null) =>
        await ReadAsync<AttachmentUpload>(
            await _client.PostAsJsonAsync($"{Ws}/attachments", new CreateAttachmentRequest(name, type, size, issue), Json.Options, Ct),
            HttpStatusCode.Created);

    private async Task PutAsync(string url, byte[] body, string type)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        using var response = await _storage.PutAsync(url, content, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }
}
