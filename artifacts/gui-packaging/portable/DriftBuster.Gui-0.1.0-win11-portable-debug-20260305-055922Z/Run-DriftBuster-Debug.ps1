$env:DRIFTBUSTER_DEBUG = "1"
Start-Process -FilePath (Join-Path $PSScriptRoot "DriftBuster.Gui.exe") -ArgumentList $args
