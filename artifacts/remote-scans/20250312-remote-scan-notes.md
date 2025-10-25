# Remote scan transcript — admin share & WinRM

```
PS> Invoke-DriftBusterRemoteScan -ComputerName branch-01 -RemotePath 'ProgramData\VendorA' -RunProfilePath profiles\vendor.json -OutputDirectory artifacts\captures
[remote] staging capture helper via \\branch-01\C$\ProgramData\VendorA
[remote] writing manifests to artifacts\captures\branch-01
```

```
PS> Invoke-DriftBusterRemoteScan -UseWinRM -ComputerName hq-core -RemotePath 'C:\ProgramData\VendorA' -RunProfilePath profiles\vendor.json -RemoteWorkingDirectory '$env:ProgramData\DriftBusterRemote' -OutputDirectory artifacts\captures
[remote] staging capture helper via WinRM at C:\ProgramData\DriftBusterRemote
[remote] copied captures into artifacts\captures\hq-core
```

Store the transcripts above with any exported manifests to preserve the remote command evidence trail.
