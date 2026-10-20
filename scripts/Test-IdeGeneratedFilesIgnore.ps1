[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$requiredPattern = '**/Properties/launchSettings.json'
$requiredPaths = @(
    'tests/KeelMatrix.ForwardTrust.Tests/Properties/launchSettings.json',
    'src/KeelMatrix.ForwardTrust/Properties/launchSettings.json',
    'smoke/ForwardTrust.Consumer/Properties/launchSettings.json'
)

function Assert-LaunchSettingsIgnored([string]$Root) {
    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
    $ignorePath = Join-Path $resolvedRoot '.gitignore'
    if (-not (Test-Path -LiteralPath $ignorePath -PathType Leaf)) {
        throw "The repository has no root .gitignore: $ignorePath"
    }

    $ignoreLines = [IO.File]::ReadAllLines($ignorePath)
    if (-not ($ignoreLines | Where-Object { $_.Trim() -ceq $requiredPattern })) {
        throw "The root .gitignore does not contain required rule '$requiredPattern'."
    }

    foreach ($relativePath in $requiredPaths) {
        & git -C $resolvedRoot check-ignore --no-index --quiet -- $relativePath
        if ($LASTEXITCODE -ne 0) {
            throw "The root .gitignore does not ignore '$relativePath'."
        }
    }
}

Assert-LaunchSettingsIgnored $RepositoryRoot
Write-Output 'PASS: nested test, shipping, and smoke launch settings paths are ignored.'

if ($SelfTest) {
    $scratchParent = if ($env:PAPERCLIP_TASK_SCRATCH_DIR) {
        $env:PAPERCLIP_TASK_SCRATCH_DIR
    } elseif ($env:PAPERCLIP_RUN_SCRATCH_DIR) {
        $env:PAPERCLIP_RUN_SCRATCH_DIR
    } else {
        [IO.Path]::GetTempPath()
    }
    $scratchParent = [IO.Path]::GetFullPath($scratchParent)
    $negativeRoot = Join-Path $scratchParent ('ForwardTrust-IdeIgnore-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $negativeRoot -Force | Out-Null

    try {
        & git -C $negativeRoot init --quiet --initial-branch=main
        if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the generated negative-control repository.' }

        $actualIgnore = [IO.File]::ReadAllLines((Join-Path ([IO.Path]::GetFullPath($RepositoryRoot)) '.gitignore'))
        $withoutRequiredPattern = @($actualIgnore | Where-Object { $_.Trim() -cne $requiredPattern })
        $negativeIgnorePath = Join-Path $negativeRoot '.gitignore'
        [IO.File]::WriteAllLines($negativeIgnorePath, [string[]]$withoutRequiredPattern, [Text.UTF8Encoding]::new($false))

        $failedClosed = $false
        try {
            Assert-LaunchSettingsIgnored $negativeRoot
        } catch {
            if ($_.Exception.Message -notlike '*does not contain required rule*') { throw }
            $failedClosed = $true
        }

        if (-not $failedClosed) {
            throw 'Negative control failed: the guard accepted a .gitignore with the launch settings rule removed.'
        }

        Write-Output 'PASS: negative control fails closed when the launch settings rule is removed.'
    } finally {
        $resolvedScratchParent = [IO.Path]::GetFullPath($scratchParent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $resolvedNegativeRoot = [IO.Path]::GetFullPath($negativeRoot)
        if (-not $resolvedNegativeRoot.StartsWith($resolvedScratchParent, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove the negative-control directory outside its scratch parent.'
        }

        if (Test-Path -LiteralPath $resolvedNegativeRoot) {
            Remove-Item -LiteralPath $resolvedNegativeRoot -Recurse -Force
        }
    }
}
