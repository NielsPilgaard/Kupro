using Kupro.Api.Data;
using Kupro.Api.Features.Imports;
using Kupro.Api.Features.Meetings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Kupro.Api.Features.Workspaces;

/// <summary>
/// Moves everything that points at one member to another, for merging a placeholder (ADR-019). Foreign keys come from
/// the EF model, so a new table with a member reference is covered without touching this file.
/// </summary>
internal static class MemberMerge
{
    /// <summary>
    /// Member ids held in columns with no foreign key; moved by hand below. A guard test fails when a new column that
    /// looks like a member reference is neither a foreign key nor listed here.
    /// </summary>
    public static readonly IReadOnlySet<string> ColumnsWithoutForeignKey = new HashSet<string>
    {
        $"{nameof(TranscriptSegment)}.{nameof(TranscriptSegment.SpeakerId)}",
        $"{nameof(MeetingProposal)}.{nameof(MeetingProposal.ReviewedById)}",
    };

    public static IEnumerable<(string Table, string Column)> ForeignKeys(IModel model) =>
        from type in model.GetEntityTypes()
        from fk in type.GetForeignKeys()
        where fk.PrincipalEntityType.ClrType == typeof(Member)
        let table = type.GetTableName()!
        select (table, fk.Properties[^1].GetColumnName(StoreObjectIdentifier.Table(table, type.GetSchema()))!);

    /// <summary>Runs in the caller's transaction. <c>workspaceId</c> is explicit because raw SQL skips the tenant filter.</summary>
    public static async Task MoveReferencesAsync(KuproDbContext db, Guid workspaceId, Guid from, Guid into, CancellationToken ct)
    {
        foreach (var (table, column) in ForeignKeys(db.Model))
        {
            // Names come from the EF model, not from input; values are parameters.
            var sql = "UPDATE \"" + table + "\" SET \"" + column + "\" = {0} WHERE workspace_id = {1} AND \"" + column + "\" = {2}";
            await db.Database.ExecuteSqlRawAsync(sql, [into, workspaceId, from], ct);
        }

        await db.TranscriptSegments.Where(t => t.SpeakerId == from).ExecuteUpdateAsync(s => s.SetProperty(t => t.SpeakerId, into), ct);
        await db.MeetingProposals.Where(p => p.ReviewedById == from).ExecuteUpdateAsync(s => s.SetProperty(p => p.ReviewedById, into), ct);

        // Member ids held as values: a proposal's assignee, an activity entry's from/to, and an import's people map.
        var (fromText, intoText) = (from.ToString(), into.ToString());
        await db.MeetingProposals.Where(p => p.Value == fromText).ExecuteUpdateAsync(s => s.SetProperty(p => p.Value, intoText), ct);
        await db.MeetingProposals.Where(p => p.Before == fromText).ExecuteUpdateAsync(s => s.SetProperty(p => p.Before, intoText), ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE activity SET data = replace(data::text, {fromText}, {intoText})::jsonb
            WHERE workspace_id = {workspaceId} AND kind = 'updated'
              AND (data->'assignee'->>'from' = {fromText} OR data->'assignee'->>'to' = {fromText})
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE imports SET mapping = replace(mapping::text, {fromText}, {intoText})::jsonb
            WHERE workspace_id = {workspaceId} AND mapping::text LIKE '%' || {fromText} || '%'
            """, ct);
        await db.ImportedItems.Where(i => i.EntityKind == ImportedEntityKind.Member && i.EntityId == from)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.EntityId, into), ct);

        // A re-run compares an issue's assignee with the last imported one; a merge must not read as someone's edit.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE imported_items SET last_values = replace(last_values::text, {fromText}, {intoText})::jsonb
            WHERE workspace_id = {workspaceId} AND source_kind = 'item' AND last_values->>'assignee' = {fromText}
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE import_conflicts SET kupro_value = replace(kupro_value::text, {fromText}, {intoText})::jsonb,
                source_value = replace(source_value::text, {fromText}, {intoText})::jsonb
            WHERE workspace_id = {workspaceId} AND field = 'assignee'
              AND (kupro_value = to_jsonb({fromText}::text) OR source_value = to_jsonb({fromText}::text))
            """, ct);
    }
}
