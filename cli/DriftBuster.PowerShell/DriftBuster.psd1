@{
    RootModule        = 'DriftBuster.psm1'
    ModuleVersion     = '0.0.1'
    GUID              = 'f5bd9648-0c2a-4b6e-87fc-43a69d61af1d'
    Author            = 'DriftBuster'
    CompanyName       = 'DriftBuster'
    Copyright         = '(c) DriftBuster'
    Description       = 'Windows-first PowerShell helper for DriftBuster diff, hunt, and run-profile workflows.'
    PowerShellVersion = '7.3'
    FunctionsToExport = @(
        'Test-DriftBusterPing'
        'Invoke-DriftBusterDiff'
        'Invoke-DriftBusterHunt'
        'Get-DriftBusterRunProfile'
        'Save-DriftBusterRunProfile'
        'Invoke-DriftBusterRunProfile'
    )
    CmdletsToExport   = @()
    AliasesToExport   = @()
    PrivateData       = @{
                                BackendVersion = '0.0.1'
    }
}
