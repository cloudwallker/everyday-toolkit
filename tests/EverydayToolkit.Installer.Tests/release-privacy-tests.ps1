. "$PSScriptRoot/../../scripts/common.ps1"
. "$ProjectRoot/scripts/release-privacy.ps1"

$testRoot = Join-Path $ProjectRoot "artifacts/release-privacy-tests/$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null
$syntheticWorkspace = 'R:\Build-' + [char]0x5DE5 + [char]0x4F5C + '\Toolkit'
$syntheticUser = 'R:\SyntheticAccount'
$passed = 0

function Check-PrivacySample([string]$Name, [byte[]]$Bytes, [bool]$Reject) {
    $sample = Join-Path $testRoot $Name
    New-Item -ItemType Directory -Path $sample | Out-Null
    # Use an unknown binary extension so this cannot accidentally become a text-only scan.
    [IO.File]::WriteAllBytes((Join-Path $sample 'synthetic-account-EXAMPLE.payload'), $Bytes)
    $failure = $null
    try { Assert-ReleasePrivacy -Directory $sample -WorkspaceDirectory $syntheticWorkspace -UserDirectory $syntheticUser | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($Reject) {
        if ($failure -ne 'Release contains a local absolute path.') { throw "FAIL privacy rejection diagnostic was not generic: $Name" }
        if ($failure.Contains($syntheticWorkspace) -or $failure.Contains($syntheticUser) -or $failure.Contains($sample)) { throw "FAIL privacy diagnostic exposed a path: $Name" }
    } elseif ($null -ne $failure) { throw "FAIL safe privacy sample was rejected: $Name" }
    $script:passed++
    Write-Output "PASS release privacy $Name"
}

Check-PrivacySample 'SafeBinary' ([byte[]]@(0,255,254,128,1,82,83,68,83,0,0,0)) $false
Check-PrivacySample 'AsciiUserPathCaseInsensitive' ([Text.Encoding]::ASCII.GetBytes(('RSDS' + $syntheticUser.ToLowerInvariant() + '\source.pdb' + [char]0))) $true
Check-PrivacySample 'Utf8WorkspacePath' ([Text.Encoding]::UTF8.GetBytes(('RSDS' + $syntheticWorkspace + '\source.pdb' + [char]0))) $true
Check-PrivacySample 'Utf16ForwardSlashes' ([Text.Encoding]::Unicode.GetBytes(('prefix:' + $syntheticWorkspace.Replace('\','/') + '/source.pdb'))) $true
Check-PrivacySample 'JsonEscapedPath' ([Text.Encoding]::UTF8.GetBytes(('{"source":"' + $syntheticUser.Replace('\','\\') + '\\source.pdb"}'))) $true

$lockedSample = Join-Path $testRoot 'ReadFailureDiagnostic'
New-Item -ItemType Directory -Path $lockedSample | Out-Null
$lockedFile = Join-Path $lockedSample 'synthetic-account-EXAMPLE.payload'
$lock = [IO.File]::Open($lockedFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $failure = $null
    try { Assert-ReleasePrivacy -Directory $lockedSample -WorkspaceDirectory $syntheticWorkspace -UserDirectory $syntheticUser | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($failure -ne 'Release privacy scan could not read a file.') { throw 'FAIL read-error diagnostic was not generic.' }
    $passed++
    Write-Output 'PASS release privacy ReadFailureDiagnostic'
} finally { $lock.Dispose() }

if ($passed -ne 6) { throw 'FAIL incomplete release privacy tests.' }
Write-Output "Release privacy tests: $passed passed"
