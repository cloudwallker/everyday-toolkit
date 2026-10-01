. "$PSScriptRoot/../../scripts/common.ps1"
. "$ProjectRoot/scripts/runtime-notices.ps1"
$testRoot = Join-Path $ProjectRoot "artifacts/runtime-notices-tests/$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null
$depsFile = "$ProjectRoot/artifacts/publish/win-x64/EverydayToolkit.App.deps.json"
$assetsFile = "$ProjectRoot/src/EverydayToolkit.App/obj/project.assets.json"
Copy-RuntimeNotices $depsFile $assetsFile $testRoot
$deps = [IO.File]::ReadAllText($depsFile) | ConvertFrom-Json
$assets = [IO.File]::ReadAllText($assetsFile) | ConvertFrom-Json
$count = 0
foreach ($dependency in $deps.libraries.PSObject.Properties.Name) {
    if (!$dependency.StartsWith('runtimepack.')) { continue }
    $parts = $dependency.Substring('runtimepack.'.Length).Split('/')
    $id = $parts[0].ToLowerInvariant(); $version = $parts[1]
    $package = Join-Path (@($assets.packageFolders.PSObject.Properties)[0].Name) "$id/$version"
    $source = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -in @('LICENSE','LICENSE.txt') })[0].FullName
    $copied = "$testRoot/licenses/$id/$version/LICENSE.txt"
    if (!(Test-Path -LiteralPath $copied)) { throw 'FAIL resolved runtime package license was not included' }
    if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $copied).Hash) { throw 'FAIL runtime package license source mismatch' }
    $content = [IO.File]::ReadAllText($copied)
    if (!$content.Contains('MIT License') -or !$content.Contains('.NET Foundation')) { throw 'FAIL distributed runtime license is not the resolved package MIT license' }
    $notice = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '^THIRD-PARTY-NOTICES\.(txt)$' })
    if ($notice.Count -gt 0 -and (!(Test-Path -LiteralPath "$testRoot/licenses/$id/$version/THIRD-PARTY-NOTICES.txt") -or (Get-FileHash -LiteralPath $notice[0].FullName).Hash -ne (Get-FileHash -LiteralPath "$testRoot/licenses/$id/$version/THIRD-PARTY-NOTICES.txt").Hash)) { throw 'FAIL resolved runtime third-party notice mismatch' }
    $count++
}
if ($count -ne 2) { throw 'FAIL expected the actual Core and WindowsDesktop runtime packs' }
if (!(Test-Path -LiteralPath "$testRoot/licenses/runtime-packages.json")) { throw 'FAIL runtime notice provenance catalog missing' }
Test-RuntimeNotices $depsFile $assetsFile $testRoot
Write-Output "PASS actual resolved runtime packages: $count exact MIT licenses and available third-party notices"
$catalog = [IO.File]::ReadAllText("$testRoot/licenses/runtime-packages.json") | ConvertFrom-Json
$core = @($catalog.packages | Where-Object { $_.id -eq 'microsoft.netcore.app.runtime.win-x64' })[0]
Copy-Item -LiteralPath "$(Split-Path $DotNet -Parent)/LICENSE.txt" -Destination (Join-Path $testRoot $core.license) -Force
$rejected = $false
try { Test-RuntimeNotices $depsFile $assetsFile $testRoot } catch { $rejected = $true }
if (!$rejected) { throw 'FAIL SDK license substitution was accepted as runtime license' }
Write-Output 'PASS SDK-root license substitution rejected against actual runtime package source'
