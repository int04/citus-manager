using CitusManager.Services;
using Xunit;

namespace CitusManager.Tests;

public sealed class CitusVersionTests
{
    [Theory]
    [InlineData("14.1-1", "14")]
    [InlineData("14.1.0", "14")]
    [InlineData("Citus 14.1.0 on x86_64-pc-linux-gnu", "14")]
    [InlineData("13.2.0.citus-1", "13")]
    public void MajorVersion_accepts_extension_and_display_formats(string version, string expected) =>
        Assert.Equal(expected, CitusMutator.MajorVersion(version));

    [Theory]
    [InlineData("")]
    [InlineData("Citus")]
    [InlineData("unknown")]
    public void MajorVersion_rejects_unknown_formats(string version) =>
        Assert.Throws<InvalidOperationException>(() => CitusMutator.MajorVersion(version));

    [Fact]
    public void Worker_compatibility_accepts_matching_writable_endpoint()
    {
        var expected = Identity();
        var actual = Identity(user: "worker_user");

        Assert.Null(CitusInspector.WorkerCompatibilityError(expected, actual));
    }

    [Theory]
    [InlineData("other_db", "17.6", 170006, "14.1-1", false, false)]
    [InlineData("appdb", "16.10", 160010, "14.1-1", false, false)]
    [InlineData("appdb", "17.6", 170006, "13.2-1", false, false)]
    [InlineData("appdb", "17.6", 170006, "", false, false)]
    [InlineData("appdb", "17.6", 170006, "14.1-1", true, false)]
    [InlineData("appdb", "17.6", 170006, "14.1-1", false, true)]
    public void Worker_compatibility_rejects_incompatible_endpoint(
        string database, string postgresVersion, int postgresVersionNumber,
        string citusVersion, bool recovery, bool readOnly)
    {
        var actual = new NodeEndpointIdentity(database, "worker_user", postgresVersion,
            postgresVersionNumber, citusVersion, recovery, readOnly);

        Assert.NotNull(CitusInspector.WorkerCompatibilityError(Identity(), actual));
    }

    private static NodeEndpointIdentity Identity(string user = "control_user") =>
        new("appdb", user, "17.6", 170006, "14.1-1", false, false);
}
