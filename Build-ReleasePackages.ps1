[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputRoot = '.artifacts\release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($null -eq ('System.IO.Compression.ZipFile' -as [type])) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
}

$repoRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$projectPath = Join-Path $repoRoot 'src\ControlTower.Desktop\ControlTower.Desktop.csproj'
$solutionPath = Join-Path $repoRoot 'DeveloperControlTower.sln'
$packagesPath = Join-Path $repoRoot 'Directory.Packages.props'
$toolManifestPath = Join-Path $repoRoot '.config\dotnet-tools.json'
$iconPath = Join-Path $repoRoot 'src\ControlTower.Desktop\Assets\app.ico'
$boundaryTestPath = Join-Path $repoRoot 'Test-PublicSourceBoundary.ps1'
$vulnerabilityTestPath = Join-Path $repoRoot 'Test-NuGetVulnerabilities.ps1'
$runtimeVersion = '8.0.30'
$requiredPayloadFiles = @(
    'LICENSE',
    'THIRD_PARTY_NOTICES.md',
    "licenses\dotnet-runtime-$runtimeVersion\LICENSE.TXT",
    "licenses\dotnet-runtime-$runtimeVersion\THIRD-PARTY-NOTICES.TXT"
)
$outputRootPath = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
}

[xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
$projectProperties = $projectXml.Project.PropertyGroup |
    Where-Object { $_.Version } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [string]$projectProperties.Version
}

$packId = [string]$projectProperties.PackageId
$packTitle = [string]$projectProperties.Product
$packAuthors = [string]$projectProperties.Authors
if ([string]::IsNullOrWhiteSpace($Version) -or
    [string]::IsNullOrWhiteSpace($packId) -or
    [string]::IsNullOrWhiteSpace($packTitle) -or
    [string]::IsNullOrWhiteSpace($packAuthors)) {
    throw 'Version, PackageId, Product, and Authors must be set in the Desktop project.'
}

[xml]$packagesXml = Get-Content -LiteralPath $packagesPath -Raw
$velopackPackage = $packagesXml.Project.ItemGroup.PackageVersion |
    Where-Object { $_.Include -eq 'Velopack' } |
    Select-Object -First 1
$velopackPackageVersion = [string]$velopackPackage.Version
$toolManifest = Get-Content -LiteralPath $toolManifestPath -Raw |
    ConvertFrom-Json
$velopackToolVersion = [string]$toolManifest.tools.vpk.version
if ($velopackPackageVersion -ne $velopackToolVersion) {
    throw "Velopack package version '$velopackPackageVersion' does not match vpk tool version '$velopackToolVersion'."
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$Command,
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$Command' exited with code $LASTEXITCODE."
    }
}

function Reset-OutputDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $requiredPrefix = $outputRootPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith(
        $requiredPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear output outside '$outputRootPath': $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

function Get-PeMachine {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset + 4
        return $reader.ReadUInt16()
    } finally {
        $stream.Dispose()
    }
}

function Assert-ReleaseMetadata {
    $globalJsonPath = Join-Path $repoRoot 'global.json'
    $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw |
        ConvertFrom-Json
    if ([string]$globalJson.sdk.version -ne '8.0.424' -or
        [string]$globalJson.sdk.rollForward -ne 'disable' -or
        [bool]$globalJson.sdk.allowPrerelease) {
        throw 'global.json must pin stable SDK 8.0.424 with rollForward disabled.'
    }

    foreach ($relativePath in $requiredPayloadFiles) {
        $sourcePath = Join-Path $repoRoot $relativePath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Required release notice is missing from source: $relativePath"
        }
    }

    $projectDirectories = @(
        'src\ControlTower.Core',
        'src\ControlTower.Desktop',
        'src\ControlTower.Infrastructure',
        'src\ControlTower.Tests'
    )
    foreach ($projectDirectory in $projectDirectories) {
        $baseLockPath = Join-Path $repoRoot "$projectDirectory\packages.lock.json"
        $baseLock = Get-Content -LiteralPath $baseLockPath -Raw |
            ConvertFrom-Json
        $baseTargets = @($baseLock.dependencies.PSObject.Properties.Name)
        $baseRidTargets = @($baseTargets | Where-Object { $_.Contains('/') })
        if ($baseRidTargets.Count -ne 0) {
            throw "Base lock contains RID targets: $baseLockPath ($($baseRidTargets -join ', '))"
        }
    }

    foreach ($projectDirectory in $projectDirectories[0..2]) {
        foreach ($rid in @('win-x64', 'win-arm64')) {
            $ridLockPath = Join-Path $repoRoot "$projectDirectory\packages.$rid.lock.json"
            $ridLock = Get-Content -LiteralPath $ridLockPath -Raw |
                ConvertFrom-Json
            $ridTargets = @(
                $ridLock.dependencies.PSObject.Properties.Name |
                    Where-Object { $_.Contains('/') }
            )
            if ($ridTargets.Count -ne 1 -or
                -not $ridTargets[0].EndsWith(
                    "/$rid",
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "RID lock must contain exactly one $rid graph: $ridLockPath ($($ridTargets -join ', '))"
            }
        }
    }
}

function Assert-RequiredPayloadFiles {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    foreach ($relativePath in $requiredPayloadFiles) {
        $payloadPath = Join-Path $Path $relativePath
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
            throw "Release payload is missing required notice: $payloadPath"
        }
    }
}

function Assert-NoBuildPathInStream {
    param(
        [Parameter(Mandatory)]
        [System.IO.Stream]$Stream,
        [Parameter(Mandatory)]
        [string]$Source
    )

    $pathVariants = @(
        $repoRoot.TrimEnd('\'),
        $repoRoot.TrimEnd('\').Replace('\', '/')
    ) | Select-Object -Unique
    $chunkSize = 1MB
    $overlap = 4096
    $buffer = [byte[]]::new($chunkSize + $overlap)
    $carry = 0

    while ($true) {
        $read = $Stream.Read($buffer, $carry, $chunkSize)
        if ($read -eq 0) {
            break
        }

        $total = $carry + $read
        $utf8Text = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $total)
        $utf16Length = $total - ($total % 2)
        $utf16Text = [System.Text.Encoding]::Unicode.GetString(
            $buffer,
            0,
            $utf16Length)
        $shiftedLength = ($total - 1) - (($total - 1) % 2)
        $shiftedUtf16Text = if ($shiftedLength -gt 0) {
            [System.Text.Encoding]::Unicode.GetString(
                $buffer,
                1,
                $shiftedLength)
        } else {
            ''
        }

        foreach ($pathVariant in $pathVariants) {
            if ($utf8Text.IndexOf(
                    $pathVariant,
                    [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $utf16Text.IndexOf(
                    $pathVariant,
                    [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $shiftedUtf16Text.IndexOf(
                    $pathVariant,
                    [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Private build path found in release payload: $Source"
            }
        }

        $carry = [System.Math]::Min($overlap, $total)
        [System.Buffer]::BlockCopy(
            $buffer,
            $total - $carry,
            $buffer,
            0,
            $carry)
    }
}

function Assert-NoBuildPathInDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [string[]]$ExcludeExtensions = @()
    )

    Get-ChildItem -LiteralPath $Path -Recurse -File |
        Where-Object { $ExcludeExtensions -notcontains $_.Extension } |
        ForEach-Object {
            $stream = [System.IO.File]::OpenRead($_.FullName)
            try {
                Assert-NoBuildPathInStream -Stream $stream -Source $_.FullName
            } finally {
                $stream.Dispose()
            }
        }
}

function Assert-ReleaseArchive {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entryNames = @(
            $archive.Entries |
                ForEach-Object { $_.FullName.Replace('\', '/') }
        )
        foreach ($relativePath in $requiredPayloadFiles) {
            $normalizedPath = $relativePath.Replace('\', '/')
            $matchingEntries = @(
                $entryNames | Where-Object {
                    $_.Equals(
                        $normalizedPath,
                        [System.StringComparison]::OrdinalIgnoreCase) -or
                    $_.EndsWith(
                        "/$normalizedPath",
                        [System.StringComparison]::OrdinalIgnoreCase)
                }
            )
            if ($matchingEntries.Count -eq 0) {
                throw "Release archive '$Path' is missing required notice '$normalizedPath'."
            }
        }

        foreach ($entry in $archive.Entries) {
            if ($entry.Length -eq 0 -and $entry.FullName.EndsWith('/')) {
                continue
            }

            $stream = $entry.Open()
            try {
                Assert-NoBuildPathInStream `
                    -Stream $stream `
                    -Source "$Path::$($entry.FullName)"
            } finally {
                $stream.Dispose()
            }
        }
    } finally {
        $archive.Dispose()
    }
}

# Some hosts inject process-scoped Git config entries with an empty value.
# Test child processes can drop that empty variable while retaining the
# original count, causing every Git fixture to fail before running a command.
# Compact only complete key/value pairs for this script and its children.
$gitConfigCount = 0
if ([int]::TryParse(
    [System.Environment]::GetEnvironmentVariable('GIT_CONFIG_COUNT'),
    [ref]$gitConfigCount) -and
    $gitConfigCount -gt 0) {
    $validGitConfig = @()
    for ($index = 0; $index -lt $gitConfigCount; $index++) {
        $key = [System.Environment]::GetEnvironmentVariable(
            "GIT_CONFIG_KEY_$index")
        $value = [System.Environment]::GetEnvironmentVariable(
            "GIT_CONFIG_VALUE_$index")
        if (-not [string]::IsNullOrWhiteSpace($key) -and
            -not [string]::IsNullOrEmpty($value)) {
            $validGitConfig += ,@($key, $value)
        }
    }

    for ($index = 0; $index -lt $gitConfigCount; $index++) {
        [System.Environment]::SetEnvironmentVariable(
            "GIT_CONFIG_KEY_$index", $null, 'Process')
        [System.Environment]::SetEnvironmentVariable(
            "GIT_CONFIG_VALUE_$index", $null, 'Process')
    }
    for ($index = 0; $index -lt $validGitConfig.Count; $index++) {
        [System.Environment]::SetEnvironmentVariable(
            "GIT_CONFIG_KEY_$index", $validGitConfig[$index][0], 'Process')
        [System.Environment]::SetEnvironmentVariable(
            "GIT_CONFIG_VALUE_$index", $validGitConfig[$index][1], 'Process')
    }
    [System.Environment]::SetEnvironmentVariable(
        'GIT_CONFIG_COUNT', [string]$validGitConfig.Count, 'Process')
}

$architectures = @(
    @{ Rid = 'win-x64'; Machine = [uint16]0x8664 },
    @{ Rid = 'win-arm64'; Machine = [uint16]0xAA64 }
)

Assert-ReleaseMetadata
Invoke-Checked -Command dotnet -Arguments @(
    'tool', 'restore')
& $boundaryTestPath
Invoke-Checked -Command dotnet -Arguments @(
    'restore', $solutionPath,
    '--locked-mode',
    '--nologo',
    '--verbosity', 'quiet')
& $vulnerabilityTestPath -SolutionPath $solutionPath
Invoke-Checked -Command dotnet -Arguments @(
    'test', $solutionPath,
    '-c', 'Release',
    '--no-restore',
    '--nologo',
    '--verbosity', 'quiet')

foreach ($architecture in $architectures) {
    $rid = [string]$architecture.Rid
    $publishPath = Join-Path $outputRootPath "publish\$rid"
    $releasePath = Join-Path $outputRootPath "releases\$rid"
    $lockFileName = "packages.$rid.lock.json"

    Reset-OutputDirectory $publishPath
    Reset-OutputDirectory $releasePath

    Invoke-Checked -Command dotnet -Arguments @(
        'restore', $projectPath,
        '-r', $rid,
        '--locked-mode',
        "-p:NuGetLockFilePath=$lockFileName",
        '--nologo',
        '--verbosity', 'quiet')

    Invoke-Checked -Command dotnet -Arguments @(
        'publish', $projectPath,
        '-c', 'Release',
        '-r', $rid,
        '--self-contained', 'true',
        '--no-restore',
        "-p:NuGetLockFilePath=$lockFileName",
        '--nologo',
        '--verbosity', 'quiet',
        '-o', $publishPath)

    $publishedExecutable = Join-Path $publishPath 'ControlTower.Desktop.exe'
    $machine = Get-PeMachine $publishedExecutable
    if ($machine -ne $architecture.Machine) {
        throw ('Published {0} executable has PE machine 0x{1:X4}; expected 0x{2:X4}.' -f
            $rid, $machine, $architecture.Machine)
    }

    if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'library-seed\library.yml'))) {
        throw "The $rid publish output does not contain the starter library."
    }
    if (Test-Path -LiteralPath (Join-Path $publishPath 'library\library.yml')) {
        throw "The $rid publish output contains the private legacy library."
    }
    Assert-RequiredPayloadFiles -Path $publishPath
    Assert-NoBuildPathInDirectory -Path $publishPath

    Invoke-Checked -Command dotnet -Arguments @(
        'tool', 'run', 'vpk', '--', 'pack',
        '--packId', $packId,
        '--packVersion', $Version,
        '--packDir', $publishPath,
        '--mainExe', 'ControlTower.Desktop.exe',
        '--packTitle', $packTitle,
        '--packAuthors', $packAuthors,
        '--runtime', $rid,
        '--channel', $rid,
        '--outputDir', $releasePath,
        '--icon', $iconPath,
        '--shortcuts', 'StartMenuRoot',
        '--instLocation', 'PerUser')

    $portableArchives = @(
        Get-ChildItem -LiteralPath $releasePath -File -Filter '*.zip'
    )
    $fullPackages = @(
        Get-ChildItem -LiteralPath $releasePath -File -Filter '*-full.nupkg'
    )
    if ($portableArchives.Count -ne 1) {
        throw "Expected exactly one portable ZIP for $rid; found $($portableArchives.Count)."
    }
    if ($fullPackages.Count -ne 1) {
        throw "Expected exactly one full NUPKG for $rid; found $($fullPackages.Count)."
    }

    Assert-NoBuildPathInDirectory `
        -Path $releasePath `
        -ExcludeExtensions @('.zip', '.nupkg')
    Assert-ReleaseArchive -Path $portableArchives[0].FullName
    Assert-ReleaseArchive -Path $fullPackages[0].FullName

    $checksumPath = Join-Path $releasePath "SHA256SUMS-$rid.txt"
    $checksumLines = Get-ChildItem -LiteralPath $releasePath -File |
        Where-Object { $_.FullName -ne $checksumPath } |
        Sort-Object Name |
        ForEach-Object {
            $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
            "$($hash.Hash.ToLowerInvariant())  $($_.Name)"
        }
    [System.IO.File]::WriteAllLines(
        $checksumPath,
        $checksumLines,
        [System.Text.UTF8Encoding]::new($false))
}

Write-Host "Release packages created under: $outputRootPath" -ForegroundColor Green
