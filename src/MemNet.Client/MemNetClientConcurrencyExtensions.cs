using System.Net;

namespace MemNet.Client;

public static class MemNetClientConcurrencyExtensions
{
    public static async Task<FileMutationResult> UpdateWithRetryAsync(
        this MemNetClient client,
        MemNetScope scope,
        FileRef file,
        Func<FileReadResult, FileUpdate> updateFactory,
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
            var current = await client.GetFileAsync(scope, file, cancellationToken);
            var update = updateFactory(current);
            try
            {
                if (update.Patch is not null)
                {
                    return await client.PatchFileAsync(scope, file, update.Patch, current.ETag, serviceId, cancellationToken);
                }

                if (update.Write is not null)
                {
                    return await client.WriteFileAsync(scope, file, update.Write, current.ETag, serviceId, cancellationToken);
                }

                throw new MemNetException("FileUpdate must provide either patch or write request.");
            }
            catch (MemNetApiException ex) when (
                ex.StatusCode == HttpStatusCode.PreconditionFailed
                && string.Equals(ex.Code, "ETAG_MISMATCH", StringComparison.Ordinal)
                && attempt < maxConflictRetries)
            {
                continue;
            }
        }

        throw new MemNetException($"Failed to update file after {maxConflictRetries + 1} attempts due to repeated ETag conflicts.");
    }
}

public sealed record FileUpdate(
    PatchDocumentRequest? Patch,
    ReplaceDocumentRequest? Write)
{
    public static FileUpdate FromPatch(PatchDocumentRequest patch)
    {
        if (patch is null)
        {
            throw new ArgumentNullException(nameof(patch));
        }

        return new FileUpdate(patch, null);
    }

    public static FileUpdate FromWrite(ReplaceDocumentRequest write)
    {
        if (write is null)
        {
            throw new ArgumentNullException(nameof(write));
        }

        return new FileUpdate(null, write);
    }
}
