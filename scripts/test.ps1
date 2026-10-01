param([ValidateSet('Debug','Release')][string]$Configuration='Release',[switch]$IncludeDesktop)
. "$PSScriptRoot/common.ps1"
Push-Location $ProjectRoot
try {
    $projects = @('tests/EverydayToolkit.Core.Tests/EverydayToolkit.Core.Tests.csproj','tests/EverydayToolkit.Windows.Tests/EverydayToolkit.Windows.Tests.csproj','tests/EverydayToolkit.App.Tests/EverydayToolkit.App.Tests.csproj','tests/EverydayToolkit.Installer.Tests/EverydayToolkit.Installer.Tests.csproj')
    foreach ($project in $projects) { if (!(Test-Path -LiteralPath $project)) { throw "Required test project missing: $project" } }
    $failures = @()
    foreach ($project in $projects) { & $DotNet run --project $project -c $Configuration; if ($LASTEXITCODE -ne 0) { $failures += $project } }
    & $DotNet run --project 'tests/EverydayToolkit.App.Tests/EverydayToolkit.App.Tests.csproj' -c $Configuration -- --settings-ui
    if ($LASTEXITCODE -ne 0) { $failures += 'EverydayToolkit.App.Tests --settings-ui' }
    & $DotNet run --project 'tests/EverydayToolkit.App.Tests/EverydayToolkit.App.Tests.csproj' -c $Configuration -- --ui
    if ($LASTEXITCODE -ne 0) { $failures += 'EverydayToolkit.App.Tests --ui' }
    if ($IncludeDesktop) {
        & $DotNet run --project 'tests/EverydayToolkit.App.Tests/EverydayToolkit.App.Tests.csproj' -c $Configuration -- --desktop
        if ($LASTEXITCODE -ne 0) { $failures += 'EverydayToolkit.App.Tests --desktop' }
    }
    try { & "$ProjectRoot/tests/EverydayToolkit.Installer.Tests/cli-tests.ps1" } catch { $failures += "Installer CLI: $($_.Exception.Message)" }
    try { & "$ProjectRoot/tests/EverydayToolkit.Installer.Tests/release-privacy-tests.ps1" } catch { $failures += "Release privacy: $($_.Exception.Message)" }
    if ($failures.Count -ne 0) { throw "Failed suites: $($failures -join ', ')" }
} finally { Pop-Location }
