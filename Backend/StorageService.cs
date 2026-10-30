#nullable enable
using System.Threading.Tasks;

namespace Backend;

public static class StorageProviderKeys
{
    public const string OneDrive = "onedrive";
}

/// <summary>Outcome of uploading an item PDF to external storage.</summary>
public readonly record struct StorageUploadResult(
    bool Ok,
    string? ProviderItemId,
    string? StoragePath,
    string? OpenUrl,
    string? ErrorMessage)
{
    public static StorageUploadResult Success(string providerItemId, string storagePath, string? openUrl = null)
        => new(true, providerItemId, storagePath, openUrl, null);

    public static StorageUploadResult Fail(string message)
        => new(false, null, null, null, message);
}

/// <summary>Outcome of deleting a stored item PDF.</summary>
public readonly record struct StorageDeleteResult(bool Ok, string? ErrorMessage)
{
    public static StorageDeleteResult Success() => new(true, null);
    public static StorageDeleteResult Fail(string message) => new(false, message);
}

/// <summary>PDF byte storage (OneDrive, local disk, S3, …).</summary>
public interface StorageService
{
    /// <summary>Stable provider id stored on the item (e.g. <c>onedrive</c>).</summary>
    string ProviderKey { get; }

    Task<StorageUploadResult> UploadPdfAsync(string localPdfPath, string remoteFileName);

    Task<StorageDeleteResult> DeletePdfAsync(string providerItemId);

    /// <summary>Resolve an OS-openable URL from provider-native <paramref name="pdfPath"/> (desktop admin reads).</summary>
    Task<string?> ResolveOpenUrlAsync(string pdfPath)
        => Task.FromResult<string?>(null);

    /// <summary>Delete stored PDF using provider-native <paramref name="pdfPath"/> when item id is encoded in the path.</summary>
    Task<StorageDeleteResult> DeletePdfByPathAsync(string pdfPath)
        => Task.FromResult(StorageDeleteResult.Fail("PDF delete by path is not supported for this storage provider."));
}
