using CitusManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace CitusManager.Tests;

public sealed class SecretProtectorTests
{
    [Fact]
    public void Protected_secret_round_trips_without_plaintext_storage()
    {
        var provider = DataProtectionProvider.Create("CitusManager.Tests");
        var protector = new ClusterSecretProtector(provider);
        const string secret = "correct-horse-battery-staple";
        var protectedValue = protector.Protect(secret);
        Assert.NotEqual(secret, protectedValue);
        Assert.DoesNotContain(secret, protectedValue, StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(protectedValue));
    }
}
