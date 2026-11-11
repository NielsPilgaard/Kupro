using Kupro.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Kupro.Api.Features.Issues;

/// <summary>
/// Turns what someone typed into issue keys: a key, or a source reference an import kept as an alias (<c>PROJ-123</c>
/// from Jira, <c>#4567</c>, a source URL; ADR-019 Keys). The alias answers with the issue itself, not a redirect.
/// </summary>
public static class IssueKeys
{
    /// <summary>Each asked-for value that leads to an issue, with that issue's key. A real key wins over an alias.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> ResolveAsync(KuproDbContext db, IEnumerable<string> asked, CancellationToken ct)
    {
        var wanted = asked.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct().ToList();
        var upper = wanted.Select(k => k.ToUpperInvariant()).Distinct().ToList();
        var keys = (await db.Issues.AsNoTracking().Where(i => upper.Contains(i.Key)).Select(i => i.Key).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in wanted.Where(v => keys.Contains(v.ToUpperInvariant())))
        {
            resolved[value] = value.ToUpperInvariant();
        }

        var rest = wanted.Where(v => !resolved.ContainsKey(v)).ToList();
        if (rest.Count == 0)
        {
            return resolved;
        }

        // Aliases are stored as the source wrote them; a typed key may differ in case.
        var candidates = rest.Concat(rest.Select(v => v.ToUpperInvariant())).Distinct().ToList();
        var aliases = await db.IssueAliases.AsNoTracking().Where(a => candidates.Contains(a.Alias))
            .Join(db.Issues, a => a.IssueId, i => i.Id, (a, i) => new { a.Alias, i.Key })
            .ToDictionaryAsync(a => a.Alias, a => a.Key, StringComparer.Ordinal, ct);
        foreach (var value in rest)
        {
            if (aliases.TryGetValue(value, out var key) || aliases.TryGetValue(value.ToUpperInvariant(), out key))
            {
                resolved[value] = key;
            }
        }

        return resolved;
    }
}
