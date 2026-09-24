param(
    [string[]] $Paths = @(
        "Views",
        "App.xaml",
        "MainWindow.xaml"
    )
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$pattern = '#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?|\b(?:Red|Blue|White|Black)\b'
$matches = @()

foreach ($path in $Paths) {
    $resolved = Join-Path $projectRoot $path
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw "Path does not exist: $resolved"
    }

    $files = if ((Get-Item -LiteralPath $resolved).PSIsContainer) {
        Get-ChildItem -LiteralPath $resolved -Recurse -File -Filter *.xaml
    }
    else {
        Get-Item -LiteralPath $resolved
    }

    foreach ($file in $files) {
        if ($file.FullName -match '[\\/]Themes[\\/]') {
            continue
        }

        $lineNumber = 0
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
            $lineNumber++
            if ($line -match $pattern) {
                $relative = [IO.Path]::GetRelativePath($projectRoot, $file.FullName)
                $matches += "${relative}:${lineNumber}: $($line.Trim())"
            }
        }
    }
}

if ($matches.Count -gt 0) {
    $matches | ForEach-Object { Write-Host $_ }
    Write-Host "RESULT hardcoded-colors=$($matches.Count)"
    exit 1
}

Write-Host "RESULT hardcoded-colors=0"
exit 0
