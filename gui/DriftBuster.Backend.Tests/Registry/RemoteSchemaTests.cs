using System.Numerics;

using DriftBuster.Backend.Infrastructure;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>
/// Remote registry target descriptors: <see cref="RegistryCommands.ParseRemoteTargetArg"/>, which refuses bad input with
/// <see cref="FormatException"/>.
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
    public void ParseRemoteTargetArgErrors(string value)
    {
        var act = () => RegistryCommands.ParseRemoteTargetArg(value);
        act.Should().Throw<FormatException>();
    }
}
