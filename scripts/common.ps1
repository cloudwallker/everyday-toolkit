$ErrorActionPreference = 'Stop'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$script:ProjectRoot = Split-Path $PSScriptRoot -Parent
$localSdk = Join-Path $script:ProjectRoot '.tools/dotnet/dotnet.exe'
if (Test-Path -LiteralPath $localSdk) { $script:DotNet = $localSdk } else { $script:DotNet = (Get-Command dotnet -ErrorAction Stop).Source }
function Invoke-DotNet { & $script:DotNet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LASTEXITCODE" } }
