using CitusManager.Domain;
using CitusManager.Security;

namespace CitusManager.Services.BackupStorage;

public sealed record BackupStorageOptions
{
    public string LocalRootPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "backup-data");
}

public interface IBackupStorageProviderFactory
{
    IBackupStorageProvider Create(StorageProfileVersion profileVersion);
}

public sealed class BackupStorageProviderFactory(
    BackupStorageOptions options,
    IBackupSecretProtector secretProtector,
    IHttpClientFactory httpClientFactory) : IBackupStorageProviderFactory
{
    public IBackupStorageProvider Create(StorageProfileVersion profileVersion)
    {
        ArgumentNullException.ThrowIfNull(profileVersion);
        return profileVersion.Type switch
        {
            StorageType.Local => CreateLocal(profileVersion),
            StorageType.S3Compatible => CreateS3(profileVersion),
            StorageType.GoogleDrive => CreateGoogleDrive(profileVersion),
            _ => throw new NotSupportedException($"Unsupported backup storage type: {profileVersion.Type}.")
        };
    }

    private IBackupStorageProvider CreateLocal(StorageProfileVersion profile)
    {
        var root = Path.GetFullPath(options.LocalRootPath);
        if (string.IsNullOrWhiteSpace(profile.LocalSubdirectory))
        {
            return new LocalBackupStorageProvider(new LocalBackupStorageOptions(root));
        }

        var subdirectory = BackupStorageKey.Validate(profile.LocalSubdirectory).Replace('/', Path.DirectorySeparatorChar);
        return new LocalBackupStorageProvider(new LocalBackupStorageOptions(Path.Combine(root, subdirectory)));
    }

    private IBackupStorageProvider CreateS3(StorageProfileVersion profile) =>
        new S3CompatibleBackupStorageProvider(new S3CompatibleBackupStorageOptions(
            Required(profile.Endpoint, nameof(profile.Endpoint)),
            Required(profile.Bucket, nameof(profile.Bucket)),
            Required(profile.Region, nameof(profile.Region)),
            UnprotectRequired(profile.ProtectedAccessKey, nameof(profile.ProtectedAccessKey)),
            UnprotectRequired(profile.ProtectedSecretKey, nameof(profile.ProtectedSecretKey)),
            profile.ObjectPrefix ?? string.Empty));

    private IBackupStorageProvider CreateGoogleDrive(StorageProfileVersion profile) =>
        new GoogleDriveBackupStorageProvider(
            new GoogleDriveBackupStorageOptions(
                UnprotectRequired(profile.ProtectedGoogleClientId, nameof(profile.ProtectedGoogleClientId)),
                UnprotectRequired(profile.ProtectedGoogleClientSecret, nameof(profile.ProtectedGoogleClientSecret)),
                UnprotectRequired(profile.ProtectedGoogleRefreshToken, nameof(profile.ProtectedGoogleRefreshToken)),
                Required(profile.GoogleDriveFolderId, nameof(profile.GoogleDriveFolderId))),
            httpClientFactory.CreateClient("backup-google-drive"));

    private string UnprotectRequired(string? value, string name) =>
        secretProtector.Unprotect(Required(value, name));

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Storage profile field {name} is required.")
            : value;
}
