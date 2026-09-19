{
    param($Request)

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $queue = [System.Collections.Generic.Queue[object]]::new()
    foreach ($root in @($Request.roots)) {
        $queue.Enqueue(@{ hive = $root.hive; path = $root.path; view = $root.view; depth = 0 })
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $nodes = [System.Collections.Generic.List[object]]::new()
    $truncated = $false
    while ($queue.Count -gt 0) {
        if ($clock.Elapsed.TotalSeconds -ge $Request.budget_s -or $nodes.Count -ge $Request.max_keys) {
            $truncated = $true
            break
        }

        $item = $queue.Dequeue()
        if (-not $seen.Add("$($item.hive)`n$($item.path)`n$([string]$item.view)")) {
            continue
        }

        $hive = $(if ($item.hive -ceq 'HKCU') { [Microsoft.Win32.RegistryHive]::CurrentUser } else { [Microsoft.Win32.RegistryHive]::LocalMachine })
        $view = [Microsoft.Win32.RegistryView]::Default
        if ($item.view -ceq '64') { $view = [Microsoft.Win32.RegistryView]::Registry64 }
        elseif ($item.view -ceq '32') { $view = [Microsoft.Win32.RegistryView]::Registry32 }

        $subkeys = @()
        $values = [System.Collections.Generic.List[object]]::new()
        $key = $null
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            $key = $(if ($item.path.Length -eq 0) { $base } else { $base.OpenSubKey($item.path, $false) })
        }
        catch {
            $key = $null
        }

        if ($null -ne $key) {
            try {
                $subkeys = @($key.GetSubKeyNames())
                foreach ($name in $key.GetValueNames()) {
                    try {
                        $kind = $key.GetValueKind($name)
                        $data = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    }
                    catch {
                        break
                    }

                    if ($kind -eq [Microsoft.Win32.RegistryValueKind]::Unknown -or $kind -eq [Microsoft.Win32.RegistryValueKind]::None) {
                        $kind = [Microsoft.Win32.RegistryValueKind]::Binary
                        if ($data -isnot [byte[]]) { $data = $null }
                    }

                    $values.Add(@{ name = $name; kind = [string]$kind; data = $data })
                }
            }
            catch {
                $subkeys = @()
            }
            finally {
                $key.Dispose()
            }
        }

        $nodes.Add(@{ hive = $item.hive; path = $item.path; view = $item.view; subkeys = [string[]]$subkeys; values = $values.ToArray() })
        if ($item.depth -lt $Request.max_depth) {
            foreach ($child in $subkeys) {
                $queue.Enqueue(@{ hive = $item.hive; path = "$($item.path)\$child"; view = $item.view; depth = $item.depth + 1 })
            }
        }
    }

    return @{ nodes = $nodes.ToArray(); truncated = $truncated }
}
