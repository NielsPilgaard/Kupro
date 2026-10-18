namespace Kupro.Api.Common;

/// <summary>The agent-facing surfaces that must mirror the REST API (ADR-001).</summary>
[Flags]
public enum AgentSurface
{
    Cli = 1,
    Mcp = 2,
}

/// <summary>
/// Opts an endpoint out of a surface, with the reason (ADR-001 IMP-001). The parity test fails for any operation that
/// has neither a matching CLI command / MCP tool nor this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class NoAgentSurfaceAttribute(AgentSurface surfaces, string reason) : Attribute
{
    public AgentSurface Surfaces { get; } = surfaces;
    public string Reason { get; } = reason;
}

/// <summary>Declares which REST operation ids an MCP tool covers; read by the parity test.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CoversOperationsAttribute(params string[] operationIds) : Attribute
{
    public IReadOnlyList<string> OperationIds { get; } = operationIds;
}
