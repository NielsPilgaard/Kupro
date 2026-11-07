using System.Net;

namespace Kupro.Import;

/// <summary>
/// A source refused or failed a request: a rejected token, a missing scope, a project that is gone, or an outage that
/// outlasted the retries. The message says which, for the run report; the token is never part of it.
/// </summary>
public sealed class SourceAccessException : Exception
{
    public SourceAccessException() { }

    public SourceAccessException(string message) : base(message) { }

    public SourceAccessException(string message, Exception inner) : base(message, inner) { }

    public SourceAccessException(string message, HttpStatusCode? status) : base(message) => Status = status;

    /// <summary>The HTTP status the source answered with, when it answered.</summary>
    public HttpStatusCode? Status { get; }

    /// <summary>True when the token was rejected or lacks a scope, so the owner must paste a new one.</summary>
    public bool TokenRejected => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation;
}
