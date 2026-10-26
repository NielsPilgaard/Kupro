using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kupro.Api.Tenancy;

public static class WorkspaceForeignKeys
{
    /// <summary>
    /// Makes a reference between two tenant-owned entities a composite <c>(workspace_id, x) → (workspace_id, id)</c>
    /// key, so the database itself rejects a row pointing into another workspace (ADR-009). The principal gets an
    /// alternate key on <c>(workspace_id, id)</c> for the constraint to target.
    /// </summary>
    /// <remarks>
    /// <see cref="DeleteBehavior.SetNull"/> on such a key would null <c>workspace_id</c> too; the migration rewrites
    /// those constraints to <c>ON DELETE SET NULL (x)</c> instead.
    /// </remarks>
    public static ReferenceCollectionBuilder<TPrincipal, TDependent> HasWorkspaceForeignKey<TPrincipal, TDependent>(
        this ReferenceCollectionBuilder<TPrincipal, TDependent> builder, Expression<Func<TDependent, object?>> foreignKey)
        where TPrincipal : class, ITenantOwned
        where TDependent : class, ITenantOwned
    {
        var body = foreignKey.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : foreignKey.Body;
        var property = ((MemberExpression)body).Member.Name;
        return builder
            .HasForeignKey(nameof(ITenantOwned.WorkspaceId), property)
            .HasPrincipalKey(nameof(ITenantOwned.WorkspaceId), "Id");
    }
}
