using Microsoft.AspNetCore.DataProtection;

namespace CitusManager.Security;

public interface IClusterSecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

public sealed class ClusterSecretProtector(IDataProtectionProvider provider) : IClusterSecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("CitusManager.ClusterCredentials.v1");

    public string Protect(string value) => _protector.Protect(value);
    public string Unprotect(string value) => _protector.Unprotect(value);
}
