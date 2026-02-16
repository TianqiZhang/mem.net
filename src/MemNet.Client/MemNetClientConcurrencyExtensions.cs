using System.Net;

namespace MemNet.Client;

public static class MemNetClientConcurrencyExtensions
{
    public static async Task<DocumentMutationResult> UpdateWithRetryAsync(
        this MemNetClient client,
        MemNetScope scope,
        DocumentRef document,
        Func<DocumentReadResult, DocumentUpdate> updateFactory,
        string? serviceId = null,
        int maxConflictRetries = 3,
        CancellationToken cancellationToken = default)
    {
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (updateFactory is null)
        {
            throw new ArgumentNullException(nameof(updateFactory));
        }

        if (maxConflictRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConflictRetries), "maxConflictRetries must be >= 0.");
        }

        for (var attempt = 0; attempt <= maxConflictRetries; attempt++)
        {
            var current = await client.GetDocumentAsync(scope, document, cancellationToken);
            var update = updateFactory(current);
            try
            {
                if (update.Patch is not null)
                {
                    return await client.PatchDocumentAsync(scope, document, update.Patch, current.ETag, serviceId, cancellationToken);
                }

                if (update.Replace is not null)
                {
                    return await client.ReplaceDocumentAsync(scope, document, update.Replace, current.ETag, serviceId, cancellationToken);
                }

                throw new MemNetException("DocumentUpdate must provide either patch or replace request.");
            }
            catch (MemNetApiException ex) when (
                ex.StatusCode == HttpStatusCode.PreconditionFailed
                && string.Equals(ex.Code, "ETAG_MISMATCH", StringComparison.Ordinal)
                && attempt < maxConflictRetries)
            {
                continue;
            }
        }

        throw new MemNetException($"Failed to update document after {maxConflictRetries + 1} attempts due to repeated ETag conflicts.");
    }
}

public sealed record DocumentUpdate(
    PatchDocumentRequest? Patch,
    ReplaceDocumentRequest? Replace)
{
    public static DocumentUpdate FromPatch(PatchDocumentRequest patch)
    {
        if (patch is null)
        {
            throw new ArgumentNullException(nameof(patch));
        }

        return new DocumentUpdate(patch, null);
    }

    public static DocumentUpdate FromReplace(ReplaceDocumentRequest replace)
    {
        if (replace is null)
        {
            throw new ArgumentNullException(nameof(replace));
        }

        return new DocumentUpdate(null, replace);
    }
}
