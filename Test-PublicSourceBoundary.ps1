[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath($PSScriptRoot)
$gitDirectory = Join-Path $root '.git'
$trackedPaths = @()
if (Test-Path -LiteralPath $gitDirectory) {
    $trackedPaths = @(& git -C $root ls-files --cached)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not enumerate the Git index (exit $LASTEXITCODE)."
    }
}
else {
    $trackedPaths = @(
        Get-ChildItem -LiteralPath $root -Recurse -File -Force |
            ForEach-Object {
                [System.IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
            }
    )
}

$deniedRootPaths = @(
    '.copilot',
    '.squad',
    '.controltower',
    'library',
    'portfolio.yml',
    'portfolio-projects',
    'profiles.yml',
    'publish'
)
$deniedDirectoryNames = @(
    '.artifacts',
    'bin',
    'obj',
    'TestResults'
)
$deniedExtensions = @(
    '.pfx', '.p12', '.snk', '.key', '.pem',
    '.pptx', '.docx', '.xlsx', '.zip', '.pyc',
    '.trx', '.coverage', '.coveragexml', '.dmp', '.log'
)

foreach ($trackedPath in $trackedPaths) {
    if ([string]::IsNullOrWhiteSpace($trackedPath) -or
        $trackedPath.IndexOfAny(@([char]0, [char]10, [char]13)) -ge 0) {
        throw 'Public source boundary violation: tracked paths must be printable single-line values.'
    }

    $normalizedPath = $trackedPath.Replace('\', '/').TrimStart('/')
    $segments = $normalizedPath -split '/'
    if ($deniedRootPaths -contains $segments[0]) {
        throw "Public source boundary violation: denied tracked path '$normalizedPath'."
    }
    if ($segments | Where-Object { $_ -in $deniedDirectoryNames }) {
        throw "Public source boundary violation: generated directory is tracked in '$normalizedPath'."
    }

    $extension = [System.IO.Path]::GetExtension($normalizedPath).ToLowerInvariant()
    if ($deniedExtensions -contains $extension) {
        throw "Public source boundary violation: denied tracked file '$normalizedPath'."
    }
}

$workflowPaths = $trackedPaths |
    Where-Object { $_ -match '^\.github/workflows/[^/]+\.(?:yml|yaml)$' }
foreach ($workflowPath in $workflowPaths) {
    $workflowLines = if (Test-Path -LiteralPath $gitDirectory) {
        @(& git -C $root show ":$workflowPath")
        if ($LASTEXITCODE -ne 0) {
            throw "Could not read '$workflowPath' from the Git index."
        }
    }
    else {
        @(Get-Content -LiteralPath (Join-Path $root $workflowPath))
    }
    $lineNumber = 0
    foreach ($line in $workflowLines) {
        $lineNumber++
        if ($line -match '^\s*(?:-\s*)?uses:\s*([^@\s]+)@([^\s#]+)') {
            if ($Matches[2] -notmatch '^[0-9a-f]{40}$') {
                throw "Workflow action is not pinned to a full SHA: $workflowPath`:$lineNumber"
            }
        }
    }
}

$textExtensions = @(
    '.cmd', '.config', '.cs', '.csproj', '.editorconfig', '.gitignore',
    '.json', '.md', '.props', '.ps1', '.psm1', '.sln', '.targets',
    '.txt', '.xaml', '.xml', '.yaml', '.yml'
)
$textFileNames = @(
    '.gitignore',
    'CODEOWNERS',
    'LICENSE'
)
$privateUsername = 'ccun' + 'ningham'
$privateRepoName = 'Project' + 'PMJobs'
$privateRepoSlug = 'NZCypher819/' + 'projectmgr'
$contentPatterns = @(
    @{
        Name = 'absolute Windows user-profile path'
        Pattern = '[A-Za-z]:[\\/]' + 'Users[\\/][^\\/\r\n]+'
        Options = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    },
    @{
        Name = 'private username'
        Pattern = [regex]::Escape($privateUsername)
        Options = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    },
    @{
        Name = 'private repository name'
        Pattern = [regex]::Escape($privateRepoName)
        Options = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    },
    @{
        Name = 'private repository slug'
        Pattern = [regex]::Escape($privateRepoSlug)
        Options = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    },
    @{
        Name = 'private key'
        Pattern = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'
        Options = [System.Text.RegularExpressions.RegexOptions]::None
    },
    @{
        Name = 'GitHub token'
        Pattern = '(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})'
        Options = [System.Text.RegularExpressions.RegexOptions]::None
    },
    @{
        Name = 'Azure DevOps token'
        Pattern = '(?<![A-Za-z0-9_])[A-Za-z0-9]{75}AZDO[A-Za-z0-9]{5}(?![A-Za-z0-9_])'
        Options = [System.Text.RegularExpressions.RegexOptions]::None
    },
    @{
        Name = 'legacy Azure DevOps token assignment'
        Pattern = '(?:pat|token|authorization|secret)\s*[:=]\s*[''"]?[A-Za-z0-9]{52}(?![A-Za-z0-9_])'
        Options = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    },
    @{
        Name = 'AWS access key'
        Pattern = '(?<![A-Z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Z0-9])'
        Options = [System.Text.RegularExpressions.RegexOptions]::None
    },
    @{
        Name = 'Slack token'
        Pattern = '(?<![A-Za-z0-9])xox[baprs]-[A-Za-z0-9-]{10,}'
        Options = [System.Text.RegularExpressions.RegexOptions]::None
    },
    @{
        Name = 'URL with embedded credentials'
        Pattern = 'https?://[^\s/@]+@[^\s/]+'
        Options = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    }
)

foreach ($trackedPath in $trackedPaths) {
    $normalizedPath = $trackedPath.Replace('\', '/')
    $extension = [System.IO.Path]::GetExtension($normalizedPath).ToLowerInvariant()
    $fileName = [System.IO.Path]::GetFileName($normalizedPath)
    if ($extension -notin $textExtensions -and $fileName -notin $textFileNames) {
        continue
    }

    $content = if (Test-Path -LiteralPath $gitDirectory) {
        $stagedLines = @(& git -C $root show ":$normalizedPath")
        if ($LASTEXITCODE -ne 0) {
            throw "Could not read '$normalizedPath' from the Git index."
        }
        $stagedLines -join "`n"
    }
    else {
        Get-Content -LiteralPath (Join-Path $root $trackedPath) -Raw
    }

    foreach ($contentPattern in $contentPatterns) {
        if ([regex]::IsMatch(
                $content,
                [string]$contentPattern.Pattern,
                [System.Text.RegularExpressions.RegexOptions]$contentPattern.Options)) {
            throw "Public source boundary violation: $($contentPattern.Name) found in '$normalizedPath'."
        }
    }
}

foreach ($requiredPath in @(
    'LICENSE',
    'SECURITY.md',
    'global.json',
    'Build-ReleasePackages.ps1',
    'library-seed\library.yml',
    '.config\dotnet-tools.json',
    'licenses\dotnet-runtime-8.0.30\LICENSE.TXT',
    'licenses\dotnet-runtime-8.0.30\THIRD-PARTY-NOTICES.TXT')) {
    $normalizedRequiredPath = $requiredPath.Replace('\', '/')
    if ($trackedPaths -notcontains $normalizedRequiredPath) {
        throw "Required public file is missing: '$requiredPath'."
    }
}

Write-Host 'Public source boundary is clean.' -ForegroundColor Green
