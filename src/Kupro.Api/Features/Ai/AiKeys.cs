using Kupro.Api.Features.Attachments;
using Kupro.Api.Features.Imports;
using Microsoft.AspNetCore.DataProtection;

namespace Kupro.Api.Features.Ai;

/// <summary>
/// Encrypts workspace AI keys at rest (ADR-022 decision 4), the same way as import tokens: Data Protection, key ring in
/// its own bucket, so a database dump alone can't decrypt them. The purpose includes the connection's id, so a ciphertext
/// copied onto another row doesn't decrypt. Without key storage, connections can't be saved.
/// </summary>
public sealed class AiKeys(IDataProtectionProvider protection, [FromKeyedServices(ImportTokens.KeyStorage)] ObjectStorage keys)
{
    public bool Enabled => keys.Enabled;

    public string Protect(Guid connectionId, string key) => Protector(connectionId).Protect(key);

    public string Unprotect(Guid connectionId, string encrypted) => Protector(connectionId).Unprotect(encrypted);

    /// <summary>The last four characters, for telling keys apart in lists.</summary>
    public static string Hint(string key) => key.Length <= 8 ? "" : key[^4..];

    private IDataProtector Protector(Guid connectionId) => Enabled
        ? protection.CreateProtector("Kupro.Ai.ConnectionKey", connectionId.ToString())
        : throw new InvalidOperationException("Key storage is not configured.");
}
