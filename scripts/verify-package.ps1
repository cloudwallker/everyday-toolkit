. "$PSScriptRoot/common.ps1"
Push-Location $ProjectRoot
try {
    $release = Join-Path $ProjectRoot 'artifacts/releases'
    $zip = Join-Path $release 'EverydayToolkit-0.1.0-win-x64.zip'
    $setup = Join-Path $release 'EverydayToolkit-0.1.0-Setup.exe'
    $testRoot = Join-Path $ProjectRoot "artifacts/package-tests/$([Guid]::NewGuid().ToString('N'))"
    $target = Join-Path $testRoot 'installed'
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    foreach ($line in [IO.File]::ReadAllLines("$release/SHA256SUMS.txt")) {
        $parts = $line -split '  ', 2
        if ($parts.Length -ne 2 -or [IO.Path]::GetFileName($parts[1]) -ne $parts[1]) { throw 'Invalid checksum manifest.' }
        if ((Get-FileHash -LiteralPath (Join-Path $release $parts[1]) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $parts[0]) { throw 'Release checksum mismatch.' }
    }
    . "$PSScriptRoot/release-privacy.ps1"
    Assert-ReleasePrivacy -Directory $release -WorkspaceDirectory $ProjectRoot
    $process = Start-Process -FilePath $setup -ArgumentList @('--quiet','--no-registration','--install-dir',"`"$target`"") -WorkingDirectory $ProjectRoot -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw 'Full release installation failed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    $count = 0
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $path = Join-Path $target $entry.FullName
            $stream = $entry.Open()
            try { $expected = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash } finally { $stream.Dispose() }
            if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) { throw 'Installed payload differs from release ZIP.' }
            $count++
        }
    } finally { $archive.Dispose() }
    foreach ($name in @('EverydayToolkit.App.exe','coreclr.dll','wpfgfx_cor3.dll','licenses/runtime-packages.json','LICENSE','THIRD-PARTY-NOTICES.md')) { if (!(Test-Path -LiteralPath "$target/$name")) { throw "Release runtime or license missing: $name" } }
    Assert-ReleasePrivacy -Directory $target -WorkspaceDirectory $ProjectRoot
    . "$PSScriptRoot/runtime-notices.ps1"
    Test-RuntimeNotices "$target/EverydayToolkit.App.deps.json" "$ProjectRoot/src/EverydayToolkit.App/obj/project.assets.json" $target
    Set-Content -LiteralPath "$target/unknown.txt" -Value 'synthetic user file'
    $process = Start-Process -FilePath "$target/Uninstall.exe" -ArgumentList @('--quiet','--no-registration','--uninstall-dir',"`"$target`"") -WorkingDirectory $ProjectRoot -PassThru -Wait -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ((Test-Path -LiteralPath "$target/EverydayToolkit.App.exe") -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath "$target/EverydayToolkit.App.exe") -or !(Test-Path -LiteralPath "$target/unknown.txt")) { throw 'Full release uninstall failed or removed unknown files.' }
    $remaining = @(Get-ChildItem -LiteralPath $target -Recurse -File)
    if ($remaining.Count -ne 1 -or $remaining[0].Name -ne 'unknown.txt') { throw 'Manifest uninstall left unexpected installed files.' }
    Write-Output "PASS release checksum, all $count payload hashes, self-contained runtime/notices, installed self-uninstaller, unknown-file preservation"
} finally { Pop-Location }
