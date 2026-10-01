. "$PSScriptRoot/../../scripts/common.ps1"
$testRoot = Join-Path $ProjectRoot "artifacts/installer-cli-tests/$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path "$testRoot/payload" -Force | Out-Null
Set-Content -LiteralPath "$testRoot/payload/EverydayToolkit.App.exe" -Value 'synthetic executable' -Encoding UTF8
Set-Content -LiteralPath "$testRoot/payload/sample.txt" -Value 'synthetic sample' -Encoding UTF8
Compress-Archive -Path "$testRoot/payload/*" -DestinationPath "$testRoot/payload.zip"
& "$ProjectRoot/scripts/build-installer.ps1" -PayloadZip "$testRoot/payload.zip" -OutputDirectory "$testRoot/bin"
function Invoke-Installer([string]$Executable, [string[]]$Arguments, [string]$DiagnosticPath = $null) {
    $options = @{ FilePath=$Executable; ArgumentList=$Arguments; WorkingDirectory=$ProjectRoot; PassThru=$true; Wait=$true; WindowStyle='Hidden' }
    if ($DiagnosticPath) { $options.RedirectStandardError = $DiagnosticPath }
    $process = Start-Process @options
    return $process.ExitCode
}
$setup = "$testRoot/bin/EverydayToolkit-0.1.0-Setup.exe"
$target = "$testRoot/installed"
$result = Invoke-Installer $setup @('--quiet','--no-registration','--install-dir', "`"$target`"")
if ($result -ne 0 -or !(Test-Path -LiteralPath "$target/EverydayToolkit.App.exe")) { throw 'FAIL CLI embedded payload installation' }
Write-Output 'PASS CLI embedded payload installation'
Set-Content -LiteralPath "$target/unknown.txt" -Value 'keep'
$diagnosticFile = "$testRoot/quiet-failure.txt"
$result = Invoke-Installer $setup @('--quiet','--no-registration','--install-dir', "`"$target`"") $diagnosticFile
if ($result -eq 0 -or !(Test-Path -LiteralPath "$target/unknown.txt")) { throw 'FAIL CLI refuses existing directory' }
Write-Output 'PASS CLI refuses existing directory'
$diagnostic = [IO.File]::ReadAllText($diagnosticFile)
if ($diagnostic -notmatch '^INSTALLER_FAILURE phase=[A-Za-z]+ type=IOException hresult=0x[0-9A-F]{8} site=[A-Za-z0-9_.]+') { throw 'FAIL quiet installer did not emit safe IO category/HResult/phase/site' }
if ($diagnostic -match '[\\/]|[A-Za-z]:') { throw 'FAIL quiet installer diagnostic included a filesystem path' }
Write-Output 'PASS quiet installer diagnostic contains only phase/category/HResult/site'
$result = Invoke-Installer "$testRoot/bin/Uninstall.exe" @('--quiet','--no-registration','--uninstall-dir', "`"$target`"")
if ($result -ne 0 -or (Test-Path -LiteralPath "$target/EverydayToolkit.App.exe") -or !(Test-Path -LiteralPath "$target/unknown.txt")) { throw 'FAIL CLI manifest uninstall' }
Write-Output 'PASS CLI manifest uninstall'
$result = Invoke-Installer $setup @('--quiet','--no-registration','--install-dir', "`"$ProjectRoot`"")
if ($result -eq 0) { throw 'FAIL CLI escaped artifacts boundary' }
Write-Output 'PASS CLI artifacts boundary'
$dataTarget = "$testRoot/data-install"
$data = "$testRoot/user-data"
New-Item -ItemType Directory -Path $data | Out-Null
Set-Content -LiteralPath "$data/toolkit-data.marker" -Value 'EverydayToolkit-v1'
Set-Content -LiteralPath "$data/content.db" -Value 'synthetic encrypted-data stand-in'
Set-Content -LiteralPath "$data/unknown.txt" -Value 'keep'
$result = Invoke-Installer $setup @('--quiet','--no-registration','--install-dir', "`"$dataTarget`"")
if ($result -ne 0) { throw 'FAIL CLI data fixture install' }
$result = Invoke-Installer "$testRoot/bin/Uninstall.exe" @('--quiet','--no-registration','--uninstall-dir', "`"$dataTarget`"",'--delete-data','--data-dir',"`"$data`"")
if ($result -ne 0 -or (Test-Path -LiteralPath "$data/content.db") -or !(Test-Path -LiteralPath "$data/unknown.txt")) { throw 'FAIL CLI controlled data deletion' }
Write-Output 'PASS CLI controlled data deletion'
$selfTarget = "$testRoot/self-installed"
$result = Invoke-Installer $setup @('--quiet','--no-registration','--install-dir', "`"$selfTarget`"")
if ($result -ne 0) { throw 'FAIL self-uninstall fixture' }
$result = Invoke-Installer "$selfTarget/Uninstall.exe" @('--quiet','--no-registration','--uninstall-dir', "`"$selfTarget`"")
$deadline = [DateTime]::UtcNow.AddSeconds(10)
while ((Test-Path -LiteralPath "$selfTarget/EverydayToolkit.App.exe") -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
if ($result -ne 0 -or (Test-Path -LiteralPath "$selfTarget/EverydayToolkit.App.exe")) { throw 'FAIL installed uninstaller removes itself' }
Write-Output 'PASS installed uninstaller removes itself'
Add-Type -AssemblyName System.IO.Compression
$badZip = "$testRoot/traversal.zip"
$zipStream = [IO.File]::Create($badZip)
$archive = [IO.Compression.ZipArchive]::new($zipStream,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in @('EverydayToolkit.App.exe','../escaped.txt')) {
        $entryStream = $archive.CreateEntry($name).Open()
        $writer = [IO.StreamWriter]::new($entryStream)
        try { $writer.Write('synthetic') } finally { $writer.Dispose() }
    }
} finally { $archive.Dispose(); $zipStream.Dispose() }
& "$ProjectRoot/scripts/build-installer.ps1" -PayloadZip $badZip -OutputDirectory "$testRoot/bad-bin"
$badTarget = "$testRoot/bad-installed"
$result = Invoke-Installer "$testRoot/bad-bin/EverydayToolkit-0.1.0-Setup.exe" @('--quiet','--no-registration','--install-dir', "`"$badTarget`"")
if ($result -eq 0 -or (Test-Path -LiteralPath $badTarget) -or (Test-Path -LiteralPath "$testRoot/escaped.txt")) { throw 'FAIL CLI traversal rejects without partial writes' }
Write-Output 'PASS CLI traversal rejects without partial writes'
