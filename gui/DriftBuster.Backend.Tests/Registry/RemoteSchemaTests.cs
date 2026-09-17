using System.Numerics;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// Mirror of tests/registry/test_remote_schema.py: <c>OfflineRegistryScanSource.from_dict</c> is
/// <see cref="OfflineRegistryScanSource.FromDict"/>, <c>_parse_remote_target_arg</c> is <see cref="RegistryCommands.ParseRemoteTargetArg"/>
/// and Python's <c>ValueError</c> is <see cref="PythonValueException"/>.
/// </summary>
public sealed class RemoteSchemaTests
{
    internal static OrderedDictionary<string, object?> Map(params (string Key, object? Value)[] items)
    {
        var map = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            map[key] = value;
        }

        return map;
    }

    [Fact]
    public void RemoteSchemaParsesSingleTarget()
    {
        var payload = Map(
            ("alias", "hq-remote"),
            ("registry_scan", Map(
                ("token", "VendorA"),
                ("remote", Map(
                    ("host", "hq-gateway"),
                    ("username", "DOMAIN\\collector"),
                    ("password_env", "DRIFTBUSTER_REMOTE_PASS"),
                    ("transport", "winrm"),
                    ("port", 5986),
                    ("use_ssl", true),
                    ("credential_profile", "hq-collector"))))));

        var source = OfflineRegistryScanSource.FromDict(payload);
        source.Remote.Should().NotBeNull();
        source.Remote!.Host.Should().Be("hq-gateway");
        source.Remote.Username.Should().Be("DOMAIN\\collector");
        source.Remote.PasswordEnv.Should().Be("DRIFTBUSTER_REMOTE_PASS");
        source.Remote.Transport.Should().Be("winrm");
        source.Remote.Port.Should().Be(new BigInteger(5986));
        source.Remote.UseSsl.Should().BeTrue();
        source.Remote.CredentialProfile.Should().Be("hq-collector");
    }

    [Fact]
    public void RemoteSchemaSupportsBatchTargets()
    {
        var payload = Map(
            ("registry_scan", Map(
                ("token", "VendorA"),
                ("remote", "branch-gateway"),
                ("remote_batch", new List<object?>
                {
                    Map(("host", "branch-01"), ("username", "svc-collector")),
                    "branch-02",
                    Map(("host", "branch-03"), ("use_ssl", false), ("transport", "winrm"), ("port", 5985)),
                }))));

        var source = OfflineRegistryScanSource.FromDict(payload);
        source.Remote.Should().NotBeNull();
        source.Remote!.Host.Should().Be("branch-gateway");
        source.RemoteBatch.Should().HaveCount(3);
        source.RemoteBatch.Select(target => target.Host).Should().Equal("branch-01", "branch-02", "branch-03");
        source.RemoteBatch[0].Username.Should().Be("svc-collector");
        source.RemoteBatch[2].UseSsl.Should().BeFalse();
        source.RemoteBatch[2].Port.Should().Be(new BigInteger(5985));
    }

    [Fact]
    public void RemoteSchemaRejectsInlinePasswords()
    {
        var payload = Map(("registry_scan", Map(("token", "VendorA"), ("remote", Map(("host", "forbidden"), ("password", "super-secret"))))));

        var act = () => OfflineRegistryScanSource.FromDict(payload);
        act.Should().Throw<PythonValueException>();
    }

    public static TheoryData<string, OrderedDictionary<string, object?>> RoundtripCases => new()
    {
        {
            "branch-01,username=domain\\\\collector,password-env=REMOTE_PASS,use-ssl=false,port=5985",
            Map(("host", "branch-01"), ("username", "domain\\\\collector"), ("password_env", "REMOTE_PASS"), ("use_ssl", false), ("port", 5985))
        },
        { "branch-02,transport=smb,alias=branch02", Map(("host", "branch-02"), ("transport", "smb"), ("alias", "branch02")) },
    };

    [Theory]
    [MemberData(nameof(RoundtripCases))]
    public void ParseRemoteTargetArgRoundtrip(string value, OrderedDictionary<string, object?> expected)
    {
        var result = RegistryCommands.ParseRemoteTargetArg(value);
        foreach (var (key, val) in expected)
        {
            result[key].Should().Be(val);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("username=missing")]
    [InlineData("branch-01,password-env=")]
    public void ParseRemoteTargetArgErrors(string value)
    {
        var act = () => RegistryCommands.ParseRemoteTargetArg(value);
        act.Should().Throw<PythonValueException>();
    }

    [Fact]
    public void RemoteBatchAllowsMappingPayload()
    {
        var payload = Map(("registry_scan", Map(("token", "VendorA"), ("remote_batch", Map(("host", "branch-unique"), ("credential_profile", "branch-profile"))))));

        var source = OfflineRegistryScanSource.FromDict(payload);
        source.Remote.Should().BeNull();
        source.RemoteBatch.Should().HaveCount(1);
        source.RemoteBatch[0].Should().Be(new RemoteRegistryTarget(
            Host: "branch-unique",
            Transport: "winrm",
            Port: null,
            UseSsl: null,
            Username: null,
            PasswordEnv: null,
            CredentialProfile: "branch-profile",
            Alias: null));
    }
}
