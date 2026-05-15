namespace RdtClient.Service.Services;

public enum DownloadErrorCategory
{
    Transient,
    Infrastructure,
}

public static class DownloadErrorClassifier
{
    // aria2c error codes that indicate local infrastructure problems.
    // The release itself is fine — retrying after the issue is fixed will work.
    private static readonly HashSet<Int32> InfrastructureErrorCodes =
    [
        9,  // Disk full
        13, // File already exists
        14, // Renaming failed
        15, // Could not open/create file
        16, // Could not open existing file
        17, // File I/O error
        18, // Could not create directory
    ];

    public static DownloadErrorCategory Classify(String? error)
    {
        if (String.IsNullOrWhiteSpace(error))
        {
            return DownloadErrorCategory.Transient;
        }

        var colonIndex = error.IndexOf(':');

        if (colonIndex > 0 && Int32.TryParse(error[..colonIndex].Trim(), out var errorCode))
        {
            if (InfrastructureErrorCodes.Contains(errorCode))
            {
                return DownloadErrorCategory.Infrastructure;
            }
        }

        var lowerError = error.ToLowerInvariant();

        if (lowerError.Contains("permission denied") ||
            lowerError.Contains("no space left") ||
            lowerError.Contains("read-only file system") ||
            lowerError.Contains("disk quota exceeded"))
        {
            return DownloadErrorCategory.Infrastructure;
        }

        return DownloadErrorCategory.Transient;
    }
}
