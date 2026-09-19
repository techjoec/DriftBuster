using DriftBuster.Backend.Json;
using DriftBuster.Backend.Registry;

namespace DriftBuster.Backend.Tests.Registry;

/// <summary>Remote registry target descriptors (<see cref="RegistryRemoteTarget.Parse"/>) and the emitted scan config.</summary>
public sealed class RemoteSchemaTests
{
    [Fact]
    public void A_descriptor_parses_every_key()
    {
        RegistryRemoteTarget.Parse(@"branch-01, username=domain\collector, password-env=REMOTE_PASS, use-ssl=false, port=5985, credential_profile=p, transport=smb, alias=b1")
            .Should().Be(new RegistryRemoteTarget
            {
                Host = "branch-01",
                Username = @"domain\collector",
                PasswordEnv = "REMOTE_PASS",
                UseSsl = false,
                Port = 5985,
                CredentialProfile = "p",
                Transport = "smb",
                Alias = "b1",
            });
    }

    [Theory]
    [InlineData("")]
    [InlineData("username=missing")]
    [InlineData("host,port=0")]
    [InlineData("host,use-ssl=maybe")]
    [InlineData("host,colour=red")]
    [InlineData("host,alias=")]
    public void Bad_descriptors_are_refused(string value)
        => FluentActions.Invoking(() => RegistryRemoteTarget.Parse(value)).Should().Throw<FormatException>();

    [Fact]
    public void The_emitted_config_omits_what_was_not_given()
    {
        var config = new RegistryScanConfig(
            new RegistryScanSpec("VendorA", ["server"], [], 12, 200, 10.0, null, new RegistryRemoteTarget { Host = "h" }, null),
            null);

        ModelJson.Serialize(config).Should().Be("""
            {
              "registry_scan": {
                "token": "VendorA",
                "keywords": [
                  "server"
                ],
                "patterns": [],
                "max_depth": 12,
                "max_hits": 200,
                "time_budget_s": 10,
                "remote": {
                  "host": "h"
                }
              }
            }

            """.ReplaceLineEndings("\n"));
    }
}
