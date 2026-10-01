param(
    [string]$ApplicationDirectory = 'artifacts/publish/win-x64',
    [ValidateRange(10,90)][int]$TimeoutSeconds = 30
)
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path $PSScriptRoot -Parent

# This smoke test requires Windows PowerShell 5.1 and an interactive Windows desktop.
# It never reads the user's default toolkit directory or the system clipboard.
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run this script with Windows PowerShell 5.1 (powershell.exe), which can load Framework UIAutomation.' }
if ([IO.Path]::IsPathRooted($ApplicationDirectory)) { $applicationRoot = [IO.Path]::GetFullPath($ApplicationDirectory) }
else { $applicationRoot = [IO.Path]::GetFullPath((Join-Path $ProjectRoot $ApplicationDirectory)) }
$artifactsBoundary = [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (!$applicationRoot.StartsWith($artifactsBoundary,[StringComparison]::OrdinalIgnoreCase)) { throw 'ApplicationDirectory must be a published or installed copy inside project artifacts.' }
$application = Join-Path $applicationRoot 'EverydayToolkit.App.exe'
foreach ($required in @('EverydayToolkit.App.exe','EverydayToolkit.App.deps.json','coreclr.dll','hostfxr.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $applicationRoot $required) -PathType Leaf)) { throw "Required self-contained release file missing: $required" }
}

$testRoot = Join-Path $ProjectRoot "artifacts/release-smoke/$([Guid]::NewGuid().ToString('N'))"
$dataRoot = Join-Path $testRoot 'data'
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
$seed = [ordered]@{ FirstRunComplete=$true; RecordingEnabled=$false; StartWithWindows=$false; Hotkey='Ctrl+Alt+Shift+F10' }
[IO.File]::WriteAllText((Join-Path $dataRoot 'settings.json'),($seed | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $dataRoot 'toolkit-data.marker'),'EverydayToolkit-v1',[Text.UTF8Encoding]::new($false))

$frameworkWpf = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/WPF'
$uiReferences = @((Join-Path $frameworkWpf 'UIAutomationClient.dll'),(Join-Path $frameworkWpf 'UIAutomationTypes.dll'),(Join-Path $frameworkWpf 'WindowsBase.dll'))
foreach ($reference in $uiReferences) { if (!(Test-Path -LiteralPath $reference)) { throw 'Windows Framework UIAutomation assemblies are unavailable.' } }
Add-Type -Path $uiReferences
if ($null -eq ('EverydayToolkit.ReleaseSmoke.OwnProcessProbe' -as [type])) {
    Add-Type -ReferencedAssemblies (@('System.dll','System.Core.dll') + $uiReferences) -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
namespace EverydayToolkit.ReleaseSmoke {
    public sealed class ProbeState {
        public bool WpfInitialized;
        public bool RecordingPaused;
        public int OwnedWindowCount;
        public int ProviderFailures;
        public int MatchingChildCount;
        public bool StatusElementFound;
        public bool StatusIsHotkeyWarning;
        public bool StatusIsLoadError;
    }
    public static class OwnProcessProbe {
        private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, WindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        public static ProbeState Read(Process ownedProcess) {
            var state = new ProbeState();
            if (ownedProcess.HasExited) return state;
            int processId = ownedProcess.Id;
            var windows = new HashSet<IntPtr>();
            WindowCallback callback = delegate(IntPtr window, IntPtr ignored) {
                uint actualProcess;
                GetWindowThreadProcessId(window, out actualProcess);
                if (actualProcess == (uint)processId) windows.Add(window);
                return true;
            };
            // EnumThreadWindows examines only the threads of our retained Process handle.
            // It does not walk AutomationElement.RootElement or inspect personal windows.
            foreach (ProcessThread thread in ownedProcess.Threads) {
                EnumThreadWindows((uint)thread.Id, callback, IntPtr.Zero);
            }
            foreach (IntPtr window in windows) {
                try {
                    var element = AutomationElement.FromHandle(window);
                    if (element == null || element.Current.ProcessId != processId) continue;
                    state.OwnedWindowCount++;
                    if (element.Current.FrameworkId == "WPF") state.WpfInitialized = true;
                    var children = element.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
                    foreach (AutomationElement child in children) {
                        if (child.Current.ProcessId != processId) continue;
                        state.MatchingChildCount++;
                        if (child.Current.FrameworkId == "WPF") state.WpfInitialized = true;
                        // This is the production readiness text, set after history/snippets load.
                        string name = child.Current.Name;
                        if (child.Current.AutomationId == "StatusText") {
                            state.StatusElementFound = true;
                            state.StatusIsHotkeyWarning = name != null && name.Contains("\u5feb\u6377\u952e");
                            state.StatusIsLoadError = name != null && name.StartsWith("\u52a0\u8f7d\u5931\u8d25", StringComparison.Ordinal);
                        }
                        if (name != null && name.StartsWith("\u8bb0\u5f55\u5df2\u6682\u505c", StringComparison.Ordinal))
                            state.RecordingPaused = true;
                    }
                } catch (ElementNotAvailableException) { state.ProviderFailures++; }
                  catch (System.Runtime.InteropServices.COMException) { state.ProviderFailures++; }
            }
            return state;
        }
    }
}
'@
}

function Assert-OwnedDatabaseReady([string]$Root) {
    $database = Join-Path $Root 'content.db'
    if (!(Test-Path -LiteralPath $database -PathType Leaf)) { return $false }
    $stream = [IO.File]::Open($database,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    try {
        if ($stream.Length -lt 512) { return $false }
        $header = New-Object byte[] 16
        if ($stream.Read($header,0,16) -ne 16) { return $false }
        return [Text.Encoding]::ASCII.GetString($header) -eq "SQLite format 3`0"
    } finally { $stream.Dispose() }
}

function Get-OwnedRuntimeEvidence([Diagnostics.Process]$OwnedProcess,[string]$ExpectedRoot) {
    $OwnedProcess.Refresh()
    if ($OwnedProcess.HasExited) { throw 'The owned release process exited before readiness.' }
    $evidence = @()
    foreach ($name in @('coreclr.dll','hostfxr.dll')) {
        $modules = @($OwnedProcess.Modules | Where-Object { $_.ModuleName -ieq $name })
        if ($modules.Count -ne 1) { return $null }
        $actual = [IO.Path]::GetFullPath($modules[0].FileName)
        $expected = [IO.Path]::GetFullPath((Join-Path $ExpectedRoot $name))
        if (![string]::Equals($actual,$expected,[StringComparison]::OrdinalIgnoreCase)) { throw "Owned process loaded $name outside the self-contained application directory." }
        $evidence += $name
    }
    return $evidence
}

$environmentNames = @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_MULTILEVEL_LOOKUP')
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
$ownedProcesses = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
$cleanupFailures = New-Object 'System.Collections.Generic.List[string]'
$phase = 'launch'
$evidence = $null
$lastProbe = $null
$modules = @()
$databaseReady = $false
try {
    $missingRuntime = Join-Path $testRoot 'no-installed-runtime'
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT',$missingRuntime,'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64',$missingRuntime,'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_MULTILEVEL_LOOKUP','0','Process')
    $first = Start-Process -FilePath $application -ArgumentList @('--tray','--data-dir',"`"$dataRoot`"") -WorkingDirectory $applicationRoot -PassThru -WindowStyle Hidden
    $ownedProcesses.Add($first)
    $phase = 'runtime-and-database-readiness'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $ready = $false
    $lastProbe = $null
    do {
        $modules = @(Get-OwnedRuntimeEvidence $first $applicationRoot)
        $lastProbe = [EverydayToolkit.ReleaseSmoke.OwnProcessProbe]::Read($first)
        $databaseReady = Assert-OwnedDatabaseReady $dataRoot
        if ($modules.Count -eq 2 -and $databaseReady) { $ready = $true; break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$ready) { throw 'Release startup did not prove local coreclr/hostfxr and SQLite creation.' }

    $phase = 'second-instance-path-identity'
    # Windows quoting treats a trailing backslash before a quote specially. A forward
    # slash suffix tests production normalization without that command-line ambiguity.
    $sameDataWithSlash = $dataRoot.Replace('\','/').TrimEnd('/') + '/'
    $second = Start-Process -FilePath $application -ArgumentList @('--tray','--data-dir',"`"$sameDataWithSlash`"") -WorkingDirectory $applicationRoot -PassThru -WindowStyle Hidden
    $ownedProcesses.Add($second)
    if (!$second.WaitForExit($TimeoutSeconds * 1000)) { throw 'Equivalent data path launched a second persistent instance.' }
    if ($second.ExitCode -ne 0) { throw 'Equivalent data path second instance did not exit successfully.' }
    $first.Refresh()
    if ($first.HasExited) { throw 'Original owned release process exited during second-instance activation.' }
    # The production second-instance signal shows the tray window. A hidden WPF
    # window may expose only a Win32 UIA provider; require the actual WPF and
    # paused status after activation, rather than skipping that proof.
    $phase = 'activated-WPF-readiness'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $uiReady = $false
    do {
        $lastProbe = [EverydayToolkit.ReleaseSmoke.OwnProcessProbe]::Read($first)
        $modules = @(Get-OwnedRuntimeEvidence $first $applicationRoot)
        $databaseReady = Assert-OwnedDatabaseReady $dataRoot
        if ($lastProbe.WpfInitialized -and $lastProbe.RecordingPaused -and $modules.Count -eq 2 -and $databaseReady) { $uiReady = $true; break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$uiReady) { throw 'Activated original instance did not prove initialized WPF, paused status, local runtime and SQLite. UIAutomation restrictions count as a failure, not a skipped pass.' }
    $finalModules = $modules
    $settingsAfter = [IO.File]::ReadAllText((Join-Path $dataRoot 'settings.json')) | ConvertFrom-Json
    if (!$settingsAfter.FirstRunComplete -or $settingsAfter.RecordingEnabled -or $settingsAfter.StartWithWindows) { throw 'Smoke settings unexpectedly enabled recording, startup or first-run flow.' }
    $evidence = [ordered]@{
        result='PASS'; firstProcessId=$first.Id; secondProcessId=$second.Id; secondExitCode=$second.ExitCode
        runtimeModules=$finalModules; installedRuntimeLookupDisabled=$true; wpfInitialized=$true; recordingPaused=$true
        sqliteHeaderVerified=$true; sameDataTrailingForwardSlashVerified=$true
        cleanMachineAcceptance='Not performed: this does not replace a clean Windows machine without an SDK.'
    }
    [IO.File]::WriteAllText((Join-Path $testRoot 'evidence.json'),($evidence | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
} catch {
    $failureEvidence = [ordered]@{
        result='FAIL'; phase=$phase; category=$_.Exception.GetType().Name; cleanMachineAcceptance='Not performed'
        runtimeModuleCount=$modules.Count; sqliteReady=$databaseReady
        wpfInitialized=($null -ne $lastProbe -and $lastProbe.WpfInitialized)
        recordingPaused=($null -ne $lastProbe -and $lastProbe.RecordingPaused)
        ownedWindowCount=$(if ($null -ne $lastProbe) { $lastProbe.OwnedWindowCount } else { 0 })
        providerFailures=$(if ($null -ne $lastProbe) { $lastProbe.ProviderFailures } else { 0 })
        matchingChildCount=$(if ($null -ne $lastProbe) { $lastProbe.MatchingChildCount } else { 0 })
        statusElementFound=($null -ne $lastProbe -and $lastProbe.StatusElementFound)
        statusIsHotkeyWarning=($null -ne $lastProbe -and $lastProbe.StatusIsHotkeyWarning)
        statusIsLoadError=($null -ne $lastProbe -and $lastProbe.StatusIsLoadError)
    }
    [IO.File]::WriteAllText((Join-Path $testRoot 'evidence.json'),($failureEvidence | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Write-Output ($failureEvidence | ConvertTo-Json -Compress)
    throw
} finally {
    try {
        foreach ($process in $ownedProcesses) {
            try {
                if (!$process.HasExited) {
                    [void]$process.CloseMainWindow()
                    if (!$process.WaitForExit(2000)) { $process.Kill(); if (!$process.WaitForExit(5000)) { $cleanupFailures.Add('An owned process did not exit.') } }
                }
            } catch { $cleanupFailures.Add('Unable to stop an owned process.') }
            finally { $process.Dispose() }
        }
    } finally {
        foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process') }
    }
    if ($cleanupFailures.Count -gt 0) {
        $cleanupEvidence = [ordered]@{ result='FAIL'; phase='owned-process-cleanup'; category='CleanupFailure'; cleanMachineAcceptance='Not performed' }
        [IO.File]::WriteAllText((Join-Path $testRoot 'evidence.json'),($cleanupEvidence | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
        throw 'Release smoke could not confirm cleanup of its own processes. No unrelated process was targeted.'
    }
}
Write-Output 'PASS self-contained release startup: local coreclr/hostfxr, WPF paused state, synthetic SQLite, equivalent-path second instance exits 0'
Write-Output 'Clean machine without an SDK remains a separate acceptance check.'
