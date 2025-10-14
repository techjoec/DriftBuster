using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Gui.Models;

namespace DriftBuster.Gui.Services
{
    public sealed class DriftbusterService : IDriftbusterService
    {
        private static readonly PythonBackend Backend = new();
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };

        public async Task<string> PingAsync(CancellationToken cancellationToken = default)
        {
            var payload = await Backend.SendAsync(new { cmd = "ping" }, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("status", out var status))
            {
                return status.GetString() ?? string.Empty;
            }

            return payload;
        }

        public async Task<DiffResult> DiffAsync(IEnumerable<string?> versions, CancellationToken cancellationToken = default)
        {
            var versionArray = new List<string?>();
            if (versions is not null)
            {
                versionArray.AddRange(versions);
            }

            var payload = await Backend.SendAsync(new { cmd = "diff", versions = versionArray }, cancellationToken)
                .ConfigureAwait(false);

            var result = JsonSerializer.Deserialize<DiffResult>(payload, SerializerOptions);
            if (result is null)
            {
                throw new InvalidOperationException("Backend returned an empty diff payload.");
            }

            result.RawJson = payload;
            return result;
        }

        public async Task<HuntResult> HuntAsync(string? directory, string? pattern, CancellationToken cancellationToken = default)
        {
            var payload = await Backend.SendAsync(new { cmd = "hunt", directory, pattern }, cancellationToken)
                .ConfigureAwait(false);

            var result = JsonSerializer.Deserialize<HuntResult>(payload, SerializerOptions);
            if (result is null)
            {
                throw new InvalidOperationException("Backend returned an empty hunt payload.");
            }

            result.RawJson = payload;
            return result;
        }

        public async Task<RunProfileListResult> ListProfilesAsync(CancellationToken cancellationToken = default)
        {
            var payload = await Backend.SendAsync(new { cmd = "profile-list" }, cancellationToken)
                .ConfigureAwait(false);

            return JsonSerializer.Deserialize<RunProfileListResult>(payload, SerializerOptions)
                   ?? new RunProfileListResult();
        }

        public async Task SaveProfileAsync(RunProfileDefinition profile, CancellationToken cancellationToken = default)
        {
            await Backend.SendAsync(new { cmd = "profile-save", profile }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RunProfileRunResult> RunProfileAsync(RunProfileDefinition profile, bool saveProfile, CancellationToken cancellationToken = default)
        {
            var payload = await Backend.SendAsync(
                new
                {
                    cmd = "profile-run",
                    profile,
                    save = saveProfile,
                },
                cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Deserialize<RunProfileRunResult>(payload, SerializerOptions)
                   ?? new RunProfileRunResult();
        }

        public static ValueTask ShutdownAsync() => Backend.DisposeAsync();
    }
}
