using System.Collections.Generic;

using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.Services
{
    public sealed record ServerSelectionCacheEntry
    {
        public required string HostId { get; init; }

        public required string Label { get; init; }

        public bool Enabled { get; init; }

        public ServerScanScope Scope { get; init; }

        public IReadOnlyList<string> Roots { get; init => field = value ?? []; } = [];

        public IReadOnlyList<string> RegistryKeys { get; init => field = value ?? []; } = [];

        public string? Computer { get; init; }

        public string? CredentialFile { get; init; }
    }
}
