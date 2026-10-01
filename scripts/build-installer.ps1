param([Parameter(Mandatory=$true)][string]$PayloadZip, [Parameter(Mandatory=$true)][string]$OutputDirectory)
. "$PSScriptRoot/common.ps1"
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework compiler is unavailable.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$framework = Split-Path $compiler -Parent
$references = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:Microsoft.CSharp.dll', "/r:$framework/System.IO.Compression.dll", "/r:$framework/System.IO.Compression.FileSystem.dll")
$sources = @((Join-Path $ProjectRoot 'installer/InstallerEngine.cs'), (Join-Path $ProjectRoot 'installer/Registration.cs'), (Join-Path $ProjectRoot 'installer/UninstallPolicy.cs'), (Join-Path $ProjectRoot 'installer/Program.cs'))
$uninstaller = Join-Path $OutputDirectory 'Uninstall.exe'
& $compiler /nologo /target:winexe /platform:x64 "/out:$uninstaller" @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller compilation failed.' }
$setup = Join-Path $OutputDirectory 'EverydayToolkit-0.1.0-Setup.exe'
& $compiler /nologo /target:winexe /platform:x64 "/out:$setup" "/resource:$PayloadZip,payload.zip" "/resource:$uninstaller,uninstaller.exe" @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
