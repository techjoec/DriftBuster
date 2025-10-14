using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using DriftBuster.Backend;
using DriftBuster.Backend.Models;

namespace DriftBuster.Gui.Services
{
    public sealed class DriftbusterService : IDriftbusterService
    {
        private readonly IDriftbusterBackend _backend;

        public DriftbusterService()
            : this(new DriftbusterBackend())
        {
        }

        public DriftbusterService(IDriftbusterBackend backend)
        {
            _backend = backend;
        }

        public Task<string> PingAsync(CancellationToken cancellationToken = default)
        {
            return _backend.PingAsync(cancellationToken);
        }

        public Task<DiffResult> DiffAsync(IEnumerable<string?> versions, CancellationToken cancellationToken = default)
        {
            return _backend.DiffAsync(versions, cancellationToken);
        }

        public Task<HuntResult> HuntAsync(string? directory, string? pattern, CancellationToken cancellationToken = default)
        {
            return _backend.HuntAsync(directory, pattern, cancellationToken);
        }

        public Task<RunProfileListResult> ListProfilesAsync(CancellationToken cancellationToken = default)
        {
            return _backend.ListProfilesAsync(baseDir: null, cancellationToken);
        }

        public Task SaveProfileAsync(RunProfileDefinition profile, CancellationToken cancellationToken = default)
        {
            return _backend.SaveProfileAsync(profile, baseDir: null, cancellationToken);
        }

        public Task<RunProfileRunResult> RunProfileAsync(RunProfileDefinition profile, bool saveProfile, CancellationToken cancellationToken = default)
        {
            return _backend.RunProfileAsync(profile, saveProfile, baseDir: null, timestamp: null, cancellationToken);
        }
    }
}
