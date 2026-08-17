using Microsoft.AspNetCore.DataProtection;

namespace CitusManager.Security;

public interface IBackupSecretProtector
{
    string Protect(string value);
    string Unprotect(string protectedValue);
    string ProtectBytes(ReadOnlySpan<byte> value);
    byte[] UnprotectBytes(string protectedValue);
}

public sealed class BackupSecretProtector(IDataProtectionProvider provider) : IBackupSecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("CitusManager.BackupSecrets.v1");

    public string Protect(string value) => _protector.Protect(value);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);

    public string ProtectBytes(ReadOnlySpan<byte> value) =>
        _protector.Protect(Convert.ToBase64String(value));

    public byte[] UnprotectBytes(string protectedValue) =>
        Convert.FromBase64String(_protector.Unprotect(protectedValue));
}
