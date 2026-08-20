[CmdletBinding()]
param(
    [string]$SolutionPath = 'DeveloperControlTower.sln'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolvedSolutionPath = if ([System.IO.Path]::IsPathRooted($SolutionPath)) {
    [System.IO.Path]::GetFullPath($SolutionPath)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $SolutionPath))
}
if (-not (Test-Path -LiteralPath $resolvedSolutionPath -PathType Leaf)) {
    throw "Solution not found: $resolvedSolutionPath"
}

$jsonLines = @(
    & dotnet list $resolvedSolutionPath package `
        --vulnerable `
        --include-transitive `
        --format json `
        --output-version 1
)
if ($LASTEXITCODE -ne 0) {
    throw "NuGet vulnerability audit exited with code $LASTEXITCODE."
}

$audit = ($jsonLines -join [System.Environment]::NewLine) | ConvertFrom-Json
$findings = @(
    foreach ($project in @($audit.projects)) {
        $frameworksProperty = $project.PSObject.Properties['frameworks']
        if ($null -eq $frameworksProperty) {
            continue
        }

        foreach ($framework in @($frameworksProperty.Value)) {
            foreach ($propertyName in @('topLevelPackages', 'transitivePackages')) {
                $property = $framework.PSObject.Properties[$propertyName]
                if ($null -eq $property) {
                    continue
                }

                foreach ($package in @($property.Value)) {
                    $vulnerabilitiesProperty = $package.PSObject.Properties['vulnerabilities']
                    if ($null -eq $vulnerabilitiesProperty) {
                        continue
                    }

                    foreach ($vulnerability in @($vulnerabilitiesProperty.Value)) {
                        [pscustomobject]@{
                            Project = [System.IO.Path]::GetFileNameWithoutExtension(
                                [string]$project.path)
                            Framework = [string]$framework.framework
                            Package = [string]$package.id
                            Version = [string]$package.resolvedVersion
                            Severity = [string]$vulnerability.severity
                            Advisory = [string]$vulnerability.advisoryurl
                        }
                    }
                }
            }
        }
    }
)

if ($findings.Count -gt 0) {
    $findings | Format-Table -AutoSize | Out-Host
    throw "NuGet audit found $($findings.Count) vulnerable package advisory record(s)."
}

Write-Host 'NuGet audit found no vulnerable direct or transitive packages.' -ForegroundColor Green
