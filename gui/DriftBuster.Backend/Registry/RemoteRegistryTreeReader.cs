using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DriftBuster.Backend.Registry;

/// <summary>
/// Reads a remote host's registry over WinRM: Windows PowerShell 5.1 (built into Windows) opens a session to the host, as the
/// current user or with a PSCredential saved by <see cref="SaveCredential"/>, and runs the same key dump the offline runner
/// uses (<c>Resources/remote_registry_dump.ps1</c>). Nothing is installed on the remote host. Windows only.
/// </summary>
public sealed class RemoteRegistryTreeReader : IRegistryTreeReader
{
    internal const string DumpResource = "DriftBuster.Backend.Resources.remote_registry_dump.ps1";
    private const string RequestVariable = "DRIFTBUSTER_REGISTRY_REQUEST";

    private readonly string _computer;
    private readonly string? _credentialFile;

    public RemoteRegistryTreeReader(string computer, string? credentialFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(computer);
        _computer = computer.Trim();
        _credentialFile = string.IsNullOrWhiteSpace(credentialFile) ? null : credentialFile;
    }

    /// <summary>Seam for running Windows PowerShell: the script, its environment, and the process's stdout and exit code.</summary>
    internal static Func<string, IReadOnlyDictionary<string, string>, CancellationToken, (string Output, string Error, int ExitCode)> RunPowerShell { get; set; } = RunWindowsPowerShell;

    public RegistryTreeRead Read(IReadOnlyList<RegistryRoot> roots, int maxDepth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var request = new JsonObject
        {
            ["computer"] = _computer,
            ["credential_file"] = _credentialFile,
            ["roots"] = new JsonArray(roots.Select(root => (JsonNode)new JsonObject { ["hive"] = root.Hive, ["path"] = root.Path, ["view"] = root.View }).ToArray()),
            ["max_depth"] = maxDepth,
            ["budget_s"] = RegistryTreeLimits.BudgetSeconds,
            ["max_keys"] = RegistryTreeLimits.MaxKeys,
        };
        var (output, error, exitCode) = RunPowerShell(Script(), new Dictionary<string, string>(StringComparer.Ordinal) { [RequestVariable] = request.ToJsonString() }, cancellationToken);
        var reply = ParseReply(output);
        if (reply?["error"] is { } failure)
        {
            throw new IOException($"{_computer}: {failure.GetValue<string>()}");
        }

        if (exitCode != 0 || reply is null)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? $"Windows PowerShell exited with code {exitCode}" : error.Trim();
            throw new IOException($"{_computer}: {detail}");
        }

        var nodes = (reply["nodes"] as JsonArray ?? []).OfType<JsonObject>().Select(ToNode).ToList();
        return new RegistryTreeRead(nodes, reply["truncated"]?.GetValue<bool>() ?? false);
    }

    /// <summary>
    /// Saves <paramref name="userName"/> and <paramref name="password"/> as a PSCredential file (<c>Export-Clixml</c>, protected
    /// with DPAPI for the current user on this machine) that <see cref="RemoteRegistryTreeReader"/> and the offline runner's
    /// <c>credential_profile</c> read back. The password goes to Windows PowerShell on stdin, never on a command line.
    /// </summary>
    public static void SaveCredential(string path, string userName, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);
        const string script = """
            $ErrorActionPreference = 'Stop'
            $password = [Console]::In.ReadLine()
            $secure = [System.Security.SecureString]::new()
            foreach ($character in $password.ToCharArray()) { $secure.AppendChar($character) }
            $secure.MakeReadOnly()
            [System.Management.Automation.PSCredential]::new($env:DRIFTBUSTER_CREDENTIAL_USER, $secure) | Export-Clixml -LiteralPath $env:DRIFTBUSTER_CREDENTIAL_PATH
            """;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DRIFTBUSTER_CREDENTIAL_USER"] = userName.Trim(),
            ["DRIFTBUSTER_CREDENTIAL_PATH"] = Path.GetFullPath(path),
        };
        var (_, error, exitCode) = RunWindowsPowerShell(script, environment, cancellationToken, standardInput: password);
        if (exitCode != 0)
        {
            throw new IOException(string.IsNullOrWhiteSpace(error) ? $"Saving the credential failed (exit code {exitCode})." : error.Trim());
        }
    }

    internal static string Script()
    {
        using var stream = typeof(RemoteRegistryTreeReader).Assembly.GetManifestResourceStream(DumpResource)
            ?? throw new InvalidOperationException($"Missing embedded resource {DumpResource}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var dump = reader.ReadToEnd().Trim();
        return $$"""
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $ErrorActionPreference = 'Stop'
            $dump = {{dump}}
            try {
                $request = $env:{{RequestVariable}} | ConvertFrom-Json
                $parameters = @{ ComputerName = $request.computer; ErrorAction = 'Stop' }
                if ($request.credential_file) {
                    $parameters.Credential = Import-Clixml -LiteralPath $request.credential_file
                }

                $session = New-PSSession @parameters
                try {
                    $reply = Invoke-Command -Session $session -ScriptBlock $dump -ArgumentList $request -ErrorAction Stop
                }
                finally {
                    Remove-PSSession -Session $session -ErrorAction SilentlyContinue
                }

                $reply | ConvertTo-Json -Depth 8 -Compress
            }
            catch {
                @{ error = $_.Exception.Message } | ConvertTo-Json -Compress
                exit 1
            }
            """;
    }

    private static JsonObject? ParseReply(string output)
    {
        var line = output.Split('\n').Select(text => text.Trim()).LastOrDefault(text => text.StartsWith('{'));
        if (line is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A node as the dump returns it: kinds are RegistryValueKind names and data the value .NET read, turned back into bytes.
    internal static RegistryTreeNode ToNode(JsonObject node)
    {
        var values = new List<RegistryRawValue>();
        foreach (var value in (node["values"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var kind = value["kind"]?.GetValue<string>() ?? "Binary";
            var type = kind switch
            {
                "String" => RegistryValueDecoder.RegSz,
                "ExpandString" => RegistryValueDecoder.RegExpandSz,
                "DWord" => RegistryValueDecoder.RegDword,
                "QWord" => RegistryValueDecoder.RegQword,
                "MultiString" => RegistryValueDecoder.RegMultiSz,
                _ => RegistryValueDecoder.RegBinary,
            };
            values.Add(new RegistryRawValue(value["name"]?.GetValue<string>() ?? string.Empty, type, Bytes(type, value["data"])));
        }

        var subkeys = node["subkeys"] switch
        {
            JsonArray array => array.Select(item => item?.GetValue<string>() ?? string.Empty).ToList(),
            JsonValue single => [single.GetValue<string>()],
            _ => [],
        };
        return new RegistryTreeNode(
            node["hive"]?.GetValue<string>() ?? "HKLM",
            node["path"]?.GetValue<string>() ?? string.Empty,
            node["view"]?.GetValue<string>(),
            subkeys,
            values);
    }

    private static byte[] Bytes(int type, JsonNode? data)
    {
        switch (type)
        {
            case RegistryValueDecoder.RegSz or RegistryValueDecoder.RegExpandSz:
                return Encoding.Unicode.GetBytes((data?.GetValue<string>() ?? string.Empty) + "\0");
            case RegistryValueDecoder.RegMultiSz:
                var items = data switch
                {
                    JsonArray array => array.Select(item => item?.GetValue<string>() ?? string.Empty),
                    JsonValue single => [single.GetValue<string>()],
                    _ => [],
                };
                return Encoding.Unicode.GetBytes(string.Concat(items.Select(item => item + "\0")) + "\0");
            case RegistryValueDecoder.RegDword:
                return BitConverter.GetBytes(unchecked((uint)Number(data)));
            case RegistryValueDecoder.RegQword:
                return BitConverter.GetBytes(unchecked((ulong)Number(data)));
            default:
                return data is JsonArray bytes ? bytes.Select(item => (byte)(item?.GetValue<int>() ?? 0)).ToArray() : [];
        }
    }

    private static long Number(JsonNode? data) =>
        data is JsonValue value && long.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static (string Output, string Error, int ExitCode) RunWindowsPowerShell(string script, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
        => RunWindowsPowerShell(script, environment, cancellationToken, standardInput: null);

    private static (string Output, string Error, int ExitCode) RunWindowsPowerShell(
        string script,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken,
        string? standardInput)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Remote registry reads need Windows PowerShell.");
        }

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var start = new ProcessStartInfo(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new IOException("Windows PowerShell did not start.");
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });
        if (standardInput is not null)
        {
            process.StandardInput.WriteLine(standardInput);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        return (output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult(), process.ExitCode);
    }
}
