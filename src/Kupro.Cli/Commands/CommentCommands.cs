using System.CommandLine;
using System.Globalization;
using Kupro.Cli.Api.Models;

namespace Kupro.Cli.Commands;

/// <summary>
/// issue activity|comment|attach, comment edit|delete, attachment get (task 008). Comment bodies come from the
/// arguments, <c>--file</c>, or stdin with <c>-</c>.
/// </summary>
internal static class CommentCommands
{
    private static readonly HttpClient Storage = new();

    /// <summary>Subcommands that hang off <c>issue</c>.</summary>
    public static IEnumerable<Command> IssueSubcommands() => [Activity(), Add(), Attach()];

    public static IEnumerable<Command> Build()
    {
        var comment = new Command("comment", "Edit or delete your comments. Add one with `issue comment KEY text`.");
        comment.Subcommands.Add(Edit());
        comment.Subcommands.Add(Delete());
        yield return comment;

        var attachment = new Command("attachment", "Files referenced as attachment:ID in descriptions and comments.");
        attachment.Subcommands.Add(GetAttachment());
        yield return attachment;
    }

    private static Command Activity()
    {
        var key = new Argument<string>("key") { Description = "Issue key, e.g. FLOW-12." };
        return new ApiCommand("activity", "Show an issue's history and comments.", "listIssueActivity") { key }.Handle(async (c, ct) =>
        {
            var items = await (await c.InWorkspaceAsync(ct)).Issues[c.Get(key)!].Activity.GetAsync(cancellationToken: ct) ?? [];
            await c.Out.ListAsync(items, ["WHEN", "WHO", "VIA", "WHAT"], i =>
                [i.At?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), i.Actor?.Name,
                    i.Via?.ToString()?.ToLowerInvariant(), Describe(i)]);
        });
    }

    private static Command Add()
    {
        var key = new Argument<string>("key") { Description = "Issue key." };
        var text = BodyArgument();
        var file = FileOption();
        return new ApiCommand("comment", "Comment on an issue (markdown).", "addComment") { key, text, file }.Handle(async (c, ct) =>
        {
            var comment = await (await c.InWorkspaceAsync(ct)).Issues[c.Get(key)!].Comments.PostAsync(
                new CommentRequest { Body = await BodyAsync(c, text, file, ct) }, cancellationToken: ct);
            await c.Out.DoneAsync(comment!, $"Commented on {comment!.IssueKey} ({comment.Id}).");
        });
    }

    private static Command Edit()
    {
        var id = new Argument<Guid>("id") { Description = "Comment id (see `issue activity`)." };
        var text = BodyArgument();
        var file = FileOption();
        return new ApiCommand("edit", "Replace the text of your comment.", "updateComment") { id, text, file }.Handle(async (c, ct) =>
        {
            var comment = await (await c.InWorkspaceAsync(ct)).Comments[c.Get(id)].PatchAsync(
                new CommentRequest { Body = await BodyAsync(c, text, file, ct) }, cancellationToken: ct);
            await c.Out.DoneAsync(comment!, $"Edited comment {comment!.Id}.");
        });
    }

    private static Command Delete()
    {
        var id = new Argument<Guid>("id") { Description = "Comment id." };
        return new ApiCommand("delete", "Delete your comment.", "deleteComment") { id }.Handle(async (c, ct) =>
        {
            await (await c.InWorkspaceAsync(ct)).Comments[c.Get(id)].DeleteAsync(cancellationToken: ct);
            await c.Out.MessageAsync($"Deleted comment {c.Get(id)}.");
        });
    }

    private static Command Attach()
    {
        var key = new Argument<string>("key") { Description = "Issue key." };
        var path = new Argument<FileInfo>("file") { Description = "File to upload (at most 25 MB)." };
        var type = new Option<string?>("--type") { Description = "MIME type; guessed from the extension by default." };
        return new ApiCommand("attach", "Upload a file to an issue; prints the markdown to reference it.", "createAttachment", "completeAttachment")
            { key, path, type }.Handle(async (c, ct) =>
        {
            var file = c.Get(path)!;
            if (!file.Exists)
            {
                throw new CliException($"No file {file.FullName}.");
            }

            var contentType = c.Get(type) ?? ContentTypeOf(file.Extension);
            var attachments = (await c.InWorkspaceAsync(ct)).Attachments;
            var upload = await attachments.PostAsync(new CreateAttachmentRequest
            {
                FileName = file.Name,
                ContentType = contentType,
                Size = file.Length,
                Issue = c.Get(key),
            }, cancellationToken: ct);

            // Straight to storage: the presigned URL is the credential, so no Authorization header goes with it.
            await using (var stream = file.OpenRead())
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, upload!.UploadUrl) { Content = new StreamContent(stream) };
                request.Content.Headers.ContentType = new(contentType);
                using var response = await Storage.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new CliException($"Upload failed: storage answered {(int)response.StatusCode}.");
                }
            }

            var done = await attachments[upload.Attachment!.Id!.Value].Complete.PostAsync(cancellationToken: ct);
            await c.Out.DoneAsync(done!, done!.Markdown!);
        });
    }

    private static Command GetAttachment()
    {
        var id = new Argument<Guid>("id") { Description = "Attachment id (from attachment:ID)." };
        var output = new Option<FileInfo?>("--output", "-o") { Description = "Download to this file." };
        return new ApiCommand("get", "Show an attachment and a 5-minute download URL, or download it with -o.", "getAttachment")
            { id, output }.Handle(async (c, ct) =>
        {
            var a = await (await c.InWorkspaceAsync(ct)).Attachments[c.Get(id)].GetAsync(cancellationToken: ct);
            if (c.Get(output) is { } target && a!.DownloadUrl is { } url)
            {
                await using var source = await Storage.GetStreamAsync(new Uri(url), ct);
                await using var sink = target.Create();
                await source.CopyToAsync(sink, ct);
                await c.Out.MessageAsync($"Saved {a.FileName} to {target.FullName}.");
                return;
            }

            await c.Out.ItemAsync(a!, ("Name", a!.FileName), ("Type", a.ContentType), ("Size", $"{a.Size} bytes"),
                ("Issue", a.IssueKey), ("Uploaded by", a.UploadedBy?.Name), ("Ready", a.Ready == true ? "yes" : "no"),
                ("Markdown", a.Markdown), ("Download", a.DownloadUrl));
        });
    }

    private static Argument<string[]> BodyArgument() =>
        new("text") { Description = "Comment text (markdown); `-` reads stdin.", Arity = ArgumentArity.ZeroOrMore };

    private static Option<FileInfo?> FileOption() => new("--file", "-f") { Description = "Read the comment from a file." };

    private static async Task<string> BodyAsync(CommandContext c, Argument<string[]> text, Option<FileInfo?> file, CancellationToken ct)
    {
        var words = c.Get(text) ?? [];
        var body = c.Get(file) is { } f ? await File.ReadAllTextAsync(f.FullName, ct)
            : words is ["-"] ? await Console.In.ReadToEndAsync(ct)
            : string.Join(' ', words);
        return string.IsNullOrWhiteSpace(body) ? throw new CliException("Give the comment text, --file, or - for stdin.") : body;
    }

    private static string Describe(FeedItem i) => i.Kind switch
    {
        FeedItemKind.Comment => $"commented ({i.Id}){(i.EditedAt is null ? "" : " [edited]")}:\n{i.Body}",
        FeedItemKind.Created => "created the issue",
        FeedItemKind.RelationAdded => string.Join("; ", (i.Changes ?? []).Select(ch => $"added {Cli.Dashed(ch.Field!)} {ch.To}")),
        FeedItemKind.RelationRemoved => string.Join("; ", (i.Changes ?? []).Select(ch => $"removed {Cli.Dashed(ch.Field!)} {ch.From}")),
        _ => string.Join("; ", (i.Changes ?? []).Select(ch => ch.Field == "description"
            ? "changed description"
            : $"{Cli.Dashed(ch.Field!)}: {ch.From ?? "-"} -> {ch.To ?? "-"}")),
    };

    private static string ContentTypeOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        ".txt" or ".log" => "text/plain",
        ".md" => "text/markdown",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };
}
