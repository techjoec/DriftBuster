using System;
using System.Collections.Generic;
using System.IO;

namespace DriftBuster.Backend
{
    public static class DriftbusterPaths
    {
        private const string AppFolderName = "DriftBuster";
        private const string DataRootEnvironmentVariable = "DRIFTBUSTER_DATA_ROOT";

        public static string GetDataRoot()
        {
            var overridePath = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                var expanded = ExpandPath(overridePath);
                Directory.CreateDirectory(expanded);
                return expanded;
            }

            if (OperatingSystem.IsWindows())
            {
                var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(baseDir))
                {
                    baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                }

                if (string.IsNullOrWhiteSpace(baseDir))
                {
                    baseDir = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.CurrentDirectory;
                }

                var windowsPath = Path.Combine(baseDir, AppFolderName);
                Directory.CreateDirectory(windowsPath);
                return windowsPath;
            }

            if (OperatingSystem.IsMacOS())
            {
                var home = ResolveHomeDirectory();
                var macPath = Path.Combine(home, "Library", "Application Support", AppFolderName);
                Directory.CreateDirectory(macPath);
                return macPath;
            }

            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            string linuxRoot;
            if (!string.IsNullOrWhiteSpace(dataHome))
            {
                linuxRoot = Path.Combine(dataHome, AppFolderName);
            }
            else
            {
                var home = ResolveHomeDirectory();
                linuxRoot = Path.Combine(home, ".local", "share", AppFolderName);
            }

            Directory.CreateDirectory(linuxRoot);
            return linuxRoot;
        }

        public static string GetCacheDirectory(params string[] segments)
        {
            var parts = new List<string> { GetDataRoot(), "cache" };
            AppendSegments(parts, segments);
            var path = Path.Combine(parts.ToArray());
            Directory.CreateDirectory(path);
            return path;
        }

        public static string GetSessionDirectory(params string[] segments)
        {
            var parts = new List<string> { GetDataRoot(), "sessions" };
            AppendSegments(parts, segments);
            var path = Path.Combine(parts.ToArray());
            Directory.CreateDirectory(path);
            return path;
        }

        private static void AppendSegments(List<string> parts, IReadOnlyList<string>? segments)
        {
            if (segments is null)
            {
                return;
            }

            foreach (var segment in segments)
            {
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    parts.Add(segment);
                }
            }
        }

        private static string ResolveHomeDirectory()
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                return NormalizeHomePath(home);
            }

            home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            if (!string.IsNullOrWhiteSpace(home))
            {
                return home;
            }

            return Environment.CurrentDirectory;
        }

        private static string ExpandPath(string path)
        {
            var trimmed = path.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return Environment.CurrentDirectory;
            }

            var expanded = Environment.ExpandEnvironmentVariables(trimmed);
            if (expanded.StartsWith("~", StringComparison.Ordinal))
            {
                return NormalizeHomePath(expanded);
            }

            return Path.GetFullPath(expanded);
        }

        private static string NormalizeHomePath(string value)
        {
            var trimmed = value.Trim();
            var expanded = Environment.ExpandEnvironmentVariables(trimmed);
            if (expanded.StartsWith("~", StringComparison.Ordinal))
            {
                var personal = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                if (string.IsNullOrWhiteSpace(personal))
                {
                    personal = Environment.CurrentDirectory;
                }

                var remainder = expanded[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.IsNullOrEmpty(remainder) ? personal : Path.Combine(personal, remainder);
            }

            return Path.GetFullPath(expanded);
        }
    }
}
