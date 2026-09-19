using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DriftBuster.Gui.Services
{
    /// <summary>
    /// Sends a bug report to a receiver when one is configured with <c>DRIFTBUSTER_BUG_REPORT_URL</c>; nothing is sent
    /// otherwise, and never without the user pressing send.
    /// </summary>
    public sealed class BugReportSender
    {
        public const string EndpointVariable = "DRIFTBUSTER_BUG_REPORT_URL";

        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

        public BugReportSender(string? endpoint = null)
        {
            Endpoint = endpoint ?? Environment.GetEnvironmentVariable(EndpointVariable)?.Trim() ?? string.Empty;
        }

        public string Endpoint { get; }

        public bool IsConfigured => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal));

        /// <summary>Posts the JSON payload; returns a line for the user saying what happened.</summary>
        public async Task<string> SendAsync(string payload, CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
            {
                return $"No receiver is configured. Set {EndpointVariable} to the receiver's address.";
            }

            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await Client.PostAsync(new Uri(Endpoint), content, cancellationToken).ConfigureAwait(true);
                return response.IsSuccessStatusCode ? "Report sent." : $"The receiver answered {(int)response.StatusCode} {response.ReasonPhrase}.";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return $"The report could not be sent: {ex.Message}";
            }
        }
    }
}
