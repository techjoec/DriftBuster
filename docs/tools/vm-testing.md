# VM Testing Guide

How to deploy and test DriftBuster GUI builds on the lab VM.

## VM Details

| Property | Value |
|----------|-------|
| Hostname | DriftBuster01 |
| OS | Windows Server 2022 Desktop Experience |
| IP | 10.1.0.10 |
| Host IP | 10.1.0.1 |
| Remote exec | `vm-exec.sh` from WasItTested repo |

## Prerequisites

- `/staging/WasItTested/cmdb/cmdb.db` — CMDB with guest records
- `vm-exec.sh` — WinRM/SSH/agent transport script at `/github/repos/WasItTested/scripts/vm-exec.sh`
- HTTP server on host for file transfer (SCP not configured — no SSH key auth on VM)

## .NET Runtimes on VM

Install the WindowsDesktop runtime to match the user's target environment:

```bash
# Install .NET 10.0.1 (matches user's Win11 laptop)
vm-exec.sh DriftBuster01 --ps "
  Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile C:\dotnet-install.ps1 -UseBasicParsing
  C:\dotnet-install.ps1 -Channel 10.0 -Version 10.0.1 -Runtime dotnet -InstallDir 'C:\Program Files\dotnet'
  C:\dotnet-install.ps1 -Channel 10.0 -Version 10.0.1 -Runtime windowsdesktop -InstallDir 'C:\Program Files\dotnet'
"

# Verify
vm-exec.sh DriftBuster01 --ps "dotnet --list-runtimes"
```

## Build Types

### Framework-Dependent (small, requires .NET on target)

```bash
dotnet publish gui/DriftBuster.Gui/DriftBuster.Gui.csproj \
  -c Release -r win-x64 --self-contained false \
  -o build/artifacts/gui/win-x64-fd -p:PublishSingleFile=false
```

- ~11 MB compressed, 44 files
- Requires matching .NET runtime on target
- **Known issue on .NET 10.0.1**: `$Default` font crash — fixed with try/catch fallback in `App.axaml.cs`

### Self-Contained (large, standalone)

```bash
python scripts/release_build.py --skip-tests --runtime win-x64 --no-installer
```

- ~28 MB, bundles .NET runtime
- Works regardless of installed .NET version
- Output: `build/artifacts/gui/win-x64/`

## Deploy and Test Workflow

### 1. Package the build

```bash
cd build/artifacts/gui/win-x64-fd   # or win-x64 for self-contained
tar czf /tmp/driftbuster-build.tar.gz .
```

### 2. Serve via HTTP (SCP unavailable)

```bash
cd /tmp && nohup python3 -m http.server 8899 --bind 0.0.0.0 > /tmp/httpserver.log 2>&1 &
```

### 3. Deploy to VM

```bash
vm-exec.sh DriftBuster01 --ps "
  Remove-Item -Recurse -Force C:\DriftBuster-FD -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Force C:\DriftBuster-FD | Out-Null
  Invoke-WebRequest -Uri 'http://10.1.0.1:8899/driftbuster-build.tar.gz' \
    -OutFile C:\DriftBuster-FD\build.tar.gz -UseBasicParsing
  cd C:\DriftBuster-FD; tar xzf build.tar.gz; Remove-Item build.tar.gz
"
```

### 4. Pre-launch PID check

```bash
vm-exec.sh DriftBuster01 --ps "
  Get-Process | Where-Object { \$_.ProcessName -like '*DriftBuster*' } |
    Select-Object Id, ProcessName, StartTime | Format-Table -AutoSize
"
```

### 5. Launch and verify

```bash
# Option A: Background launch + polling
vm-exec.sh DriftBuster01 --ps "
  Start-Process -FilePath 'C:\DriftBuster-FD\DriftBuster.Gui.exe' -WorkingDirectory 'C:\DriftBuster-FD'
  Start-Sleep -Seconds 8
  Get-Process | Where-Object { \$_.ProcessName -like '*DriftBuster*' } |
    Select-Object Id, ProcessName, StartTime, @{N='MB';E={[math]::Round(\$_.WorkingSet64/1MB,1)}} |
    Format-Table -AutoSize
"

# Option B: Captured output (blocks until exit or timeout)
vm-exec.sh DriftBuster01 --ps "
  \$pinfo = New-Object System.Diagnostics.ProcessStartInfo
  \$pinfo.FileName = 'C:\DriftBuster-FD\DriftBuster.Gui.exe'
  \$pinfo.WorkingDirectory = 'C:\DriftBuster-FD'
  \$pinfo.RedirectStandardOutput = \$true
  \$pinfo.RedirectStandardError = \$true
  \$pinfo.UseShellExecute = \$false
  \$pinfo.CreateNoWindow = \$true
  \$proc = New-Object System.Diagnostics.Process
  \$proc.StartInfo = \$pinfo
  \$proc.Start() | Out-Null
  \$exited = \$proc.WaitForExit(15000)
  if (\$exited) {
    Write-Output \"EXITED code=\$(\$proc.ExitCode)\"
    Write-Output \$proc.StandardError.ReadToEnd()
  } else {
    Write-Output 'SUCCESS: Still running after 15s'
  }
"
```

### 6. Post-test cleanup

```bash
vm-exec.sh DriftBuster01 --ps "
  Get-Process | Where-Object { \$_.ProcessName -like '*DriftBuster*' } |
    ForEach-Object { Stop-Process -Id \$_.Id -Force }
  Start-Sleep -Seconds 2
  Get-Process | Where-Object { \$_.ProcessName -like '*DriftBuster*' } |
    Format-Table Id, ProcessName -AutoSize
"

# Kill HTTP server on host
kill $(lsof -ti:8899) 2>/dev/null
```

## Stray PID Discipline

Always check for stray processes **before** and **after** testing. The GUI can leave orphan processes on crash:

```bash
# Before
vm-exec.sh DriftBuster01 --ps "Get-Process *DriftBuster* -ErrorAction SilentlyContinue"

# After (force kill if needed)
vm-exec.sh DriftBuster01 --ps "
  Stop-Process -Name DriftBuster* -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 2
  Get-Process *DriftBuster* -ErrorAction SilentlyContinue
"
```

## Troubleshooting

### `$Default (key: )` font crash

Affects framework-dependent builds on .NET 10.0.1. `FontFamily("Inter")` has no URI key, so FontManager only searches SystemFonts. Fixed in `App.axaml.cs` by catching the `InvalidOperationException` and retargeting the default to `FontFamily("fonts:Inter#Inter")` which explicitly hits the InterFontCollection.

**Diagnostic approach**: Add `Console.Error.WriteLine` calls in the catch block to dump:
- `FontManager.Current` field state (via reflection)
- Per-collection `TryGetGlyphTypeface("Inter", ...)` results
- System font probes (`Segoe UI`, `Arial`)

### vm-exec.sh single-quote issue

PowerShell commands containing single quotes break the `--agent` transport (Python `json.dumps` fails). Use `--ps` transport (WinRM) and avoid bare single quotes in commands. Use `--winrm` for manual string escaping.

### HTTP server dying between commands

Use `nohup` to keep it alive across separate terminal commands:
```bash
cd /tmp && nohup python3 -m http.server 8899 --bind 0.0.0.0 > /tmp/httpserver.log 2>&1 &
```

### No display on headless launch

`CreateNoWindow = $true` runs the GUI in background (no interactive session). Processes still launch and run, but the window won't be visible to a console-only session. For visibility testing, use RDP.
