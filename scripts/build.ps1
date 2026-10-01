param([ValidateSet('Debug','Release')][string]$Configuration='Release')
. "$PSScriptRoot/common.ps1"
Push-Location $ProjectRoot
try {
    $projects = @('src/EverydayToolkit.Core/EverydayToolkit.Core.csproj','src/EverydayToolkit.Windows/EverydayToolkit.Windows.csproj','src/EverydayToolkit.App/EverydayToolkit.App.csproj')
    foreach ($project in $projects) { if (!(Test-Path -LiteralPath $project)) { throw "Required project missing: $project" } }
    foreach ($project in $projects) { Invoke-DotNet build $project -c $Configuration }
} finally { Pop-Location }
