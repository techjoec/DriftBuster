using System;
using System.Text.Json.Serialization;

namespace DriftBuster.Backend.Models
{
    /// <summary>The registry keys a host's scan reads, and the computer they are read from.</summary>
    public sealed class ServerScanRegistryOptions
    {
        /// <summary>Registry keys (<c>HKLM\SOFTWARE\Vendor</c>, optionally <c>,view=32</c> or <c>,view=64</c>) or application names whose keys are found from the installed applications.</summary>
        [JsonPropertyName("keys")]
        public string[] Keys { get; set; } = Array.Empty<string>();

        /// <summary>The computer to read over WinRM; empty reads this machine.</summary>
        [JsonPropertyName("computer")]
        public string? Computer { get; set; }

        /// <summary>A PSCredential file (Export-Clixml) for the remote computer; empty connects as the current user.</summary>
        [JsonPropertyName("credential_file")]
        public string? CredentialFile { get; set; }
    }
}
