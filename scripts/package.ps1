param([switch]$SkipTests)
. "$PSScriptRoot/common.ps1"
Push-Location $ProjectRoot
try {
    if (!$SkipTests) { & "$PSScriptRoot/test.ps1" -Configuration Release }
    $project = 'src/EverydayToolkit.App/EverydayToolkit.App.csproj'
    if (!(Test-Path -LiteralPath $project)) { throw "Required publish project missing: $project" }
    $publish = Join-Path $ProjectRoot 'artifacts/publish/win-x64'
    # Never merge stale files into a release payload; existing output is preserved for inspection.
    if (Test-Path -LiteralPath $publish) { throw 'Publish output exists. Move or remove artifacts/publish/win-x64 after reviewing it, then retry.' }
    Invoke-DotNet publish $project -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishAot=false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $publish
    foreach ($document in @('LICENSE','THIRD-PARTY-NOTICES.md','README.md','README_ZH.md','CONTRIBUTING.md')) { Copy-Item -LiteralPath (Join-Path $ProjectRoot $document) -Destination $publish }
    New-Item -ItemType Directory -Path "$publish/docs/releases","$publish/docs/verification","$publish/docs/screenshots" -Force | Out-Null
    Copy-Item -LiteralPath "$ProjectRoot/docs/user-guide.md" -Destination "$publish/docs/user-guide.md"
    Copy-Item -LiteralPath "$ProjectRoot/docs/releases/v0.1.0.md" -Destination "$publish/docs/releases/v0.1.0.md"
    Copy-Item -LiteralPath "$ProjectRoot/docs/verification/v0.1.0.md" -Destination "$publish/docs/verification/v0.1.0.md"
    Copy-Item -LiteralPath "$ProjectRoot/docs/verification/v0.1.0-manual.md" -Destination "$publish/docs/verification/v0.1.0-manual.md"
    Get-ChildItem -LiteralPath "$ProjectRoot/docs/screenshots" -Filter '*.png' | Copy-Item -Destination "$publish/docs/screenshots"
    # Resolve the exact published runtime identities, not the SDK's unrelated root license.
    . "$PSScriptRoot/runtime-notices.ps1"
    $depsFile = Join-Path $publish 'EverydayToolkit.App.deps.json'
    $assetsFile = Join-Path $ProjectRoot 'src/EverydayToolkit.App/obj/project.assets.json'
    Copy-RuntimeNotices $depsFile $assetsFile $publish
    Test-RuntimeNotices $depsFile $assetsFile $publish
    $forbidden = Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object { $_.Name -match '(?i)(^\.env($|\.)|\.(db|sqlite|sqlite3|pfx|p12|pem|key|pdb)$|^settings\.json$|^toolkit-data\.marker$)' }
    if ($forbidden) { throw 'Release payload contains private data, key material, or debug files. Review before packaging.' }
    $textFiles = Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object { $_.Extension -in @('.json','.config','.xml','.md','.txt','.yml','.yaml','.ps1') }
    foreach ($file in $textFiles) {
        $content = [IO.File]::ReadAllText($file.FullName)
        if ($content -match '(?i)(-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|sk-(?:proj-)?[A-Za-z0-9_-]{32,}|(?:password|passwd|api[_-]?key|access[_-]?token|client[_-]?secret)\s*[=:]\s*["'']?[A-Za-z0-9/+_=-]{16,})') { throw 'Potential secret found in release text; review without printing its contents.' }
    }
    . "$PSScriptRoot/release-privacy.ps1"
    Assert-ReleasePrivacy -Directory $publish -WorkspaceDirectory $ProjectRoot
    $release = Join-Path $ProjectRoot 'artifacts/releases'
    New-Item -ItemType Directory -Path $release -Force | Out-Null
    $zip = Join-Path $release 'EverydayToolkit-0.1.0-win-x64.zip'
    if (Test-Path -LiteralPath $zip) { throw 'Release ZIP exists. Review and move it before packaging again.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($publish,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
    & "$PSScriptRoot/build-installer.ps1" -PayloadZip $zip -OutputDirectory $release
    Assert-ReleasePrivacy -Directory $release -WorkspaceDirectory $ProjectRoot
    $assets = @($zip,(Join-Path $release 'EverydayToolkit-0.1.0-Setup.exe'))
    $checksums = foreach ($asset in $assets) { "$( (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $asset -Leaf)" }
    $checksums | Set-Content -LiteralPath "$release/SHA256SUMS.txt" -Encoding ASCII
    Get-Item -LiteralPath $assets | Select-Object Name,Length
    Write-Output $checksums
} finally { Pop-Location }
