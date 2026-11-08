using System.Xml.Linq;
using Kupro.Api.Features.Attachments;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Kupro.Api.Features.Imports;

/// <summary>
/// Encrypts source tokens at rest with ASP.NET Data Protection, keys in their own bucket (ADR-019). The purpose includes
/// the import's id, so a ciphertext copied onto another row doesn't decrypt. Without key storage nothing is encrypted:
/// the framework's fallback keys are lost with the container, and every token with them.
/// </summary>
public sealed class ImportTokens(IDataProtectionProvider protection, [FromKeyedServices(ImportTokens.KeyStorage)] ObjectStorage keys)
{
    /// <summary>Configuration section of the key bucket, and the service key of its <see cref="ObjectStorage"/>.</summary>
    public const string KeyStorage = "KeyStorage";

    public bool Enabled => keys.Enabled;

    public string Protect(Guid importId, string token) => Protector(importId).Protect(token);

    public string Unprotect(Guid importId, string encrypted) => Protector(importId).Unprotect(encrypted);

    private IDataProtector Protector(Guid importId) => Enabled
        ? protection.CreateProtector("Kupro.Imports.SourceToken", importId.ToString())
        : throw new InvalidOperationException("Key storage is not configured.");
}

/// <summary>
/// The Data Protection key ring as one object per key. Failures propagate: if the bucket can't be read, the framework
/// must not conclude there are no keys and start a new ring.
/// </summary>
public sealed class S3KeyRepository(ObjectStorage storage) : IXmlRepository
{
    // The interface is synchronous. Task.Run because the caller may hold a thread we shouldn't block on; both calls are
    // rare, at startup and when the ring refreshes.
    public IReadOnlyCollection<XElement> GetAllElements() => Task.Run(ReadAllAsync).GetAwaiter().GetResult();

    public void StoreElement(XElement element, string friendlyName) => Task.Run(() => storage.PutTextAsync(
        $"{friendlyName}.xml", element.ToString(SaveOptions.DisableFormatting), "application/xml", CancellationToken.None)).GetAwaiter().GetResult();

    private async Task<IReadOnlyCollection<XElement>> ReadAllAsync()
    {
        var elements = new List<XElement>();
        foreach (var key in await storage.ListAsync(CancellationToken.None))
        {
            await using var stream = await storage.OpenReadAsync(key, CancellationToken.None)
                ?? throw new InvalidOperationException($"Key {key} disappeared while reading the key ring.");
            elements.Add(await XElement.LoadAsync(stream, LoadOptions.None, CancellationToken.None));
        }

        return elements;
    }
}
