using System;
using System.Globalization;

namespace DriftBuster.Gui.Services
{
    /// <summary>
    /// Provides lightweight heuristics and overrides for GUI performance toggles.
    /// </summary>
    public sealed class PerformanceProfile
    {
        public const int DefaultVirtualizationThreshold = 400;

        private const string ThresholdVariable = "DRIFTBUSTER_GUI_VIRTUALIZATION_THRESHOLD";
        private const string ForceVariable = "DRIFTBUSTER_GUI_FORCE_VIRTUALIZATION";

        public PerformanceProfile(int virtualizationThreshold = DefaultVirtualizationThreshold, bool? forceVirtualizationOverride = null)
        {
            if (virtualizationThreshold <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(virtualizationThreshold), "Threshold must be greater than zero.");
            }

            VirtualizationThreshold = virtualizationThreshold;
            ForceVirtualizationOverride = forceVirtualizationOverride;
        }

        public int VirtualizationThreshold { get; }

        public bool? ForceVirtualizationOverride { get; }

        public bool ShouldVirtualize(int itemCount)
        {
            if (itemCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(itemCount), "Item count cannot be negative.");
            }

            if (ForceVirtualizationOverride.HasValue)
            {
                return ForceVirtualizationOverride.Value;
            }

            return itemCount >= VirtualizationThreshold;
        }

        public PerformanceProfile WithForceOverride(bool? forceVirtualizationOverride) => new(VirtualizationThreshold, forceVirtualizationOverride);

        public PerformanceProfile WithThreshold(int threshold) => new(threshold, ForceVirtualizationOverride);

        public static PerformanceProfile FromEnvironment()
        {
            var threshold = ParseThreshold(Environment.GetEnvironmentVariable(ThresholdVariable)) ?? DefaultVirtualizationThreshold;
            var force = ParseBoolean(Environment.GetEnvironmentVariable(ForceVariable));
            return new PerformanceProfile(threshold, force);
        }

        private static int? ParseThreshold(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            if (int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                return parsed;
            }

            return null;
        }

        private static bool? ParseBoolean(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            var normalised = candidate.Trim();
            if (string.Equals(normalised, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalised, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalised, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalised, "on", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(normalised, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalised, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalised, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalised, "off", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return null;
        }
    }
}
