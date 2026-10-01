function Test-ReleasePrivacyBytes([byte[]]$Content, [byte[]]$Needle) {
    if ($Needle.Length -eq 0 -or $Content.Length -lt $Needle.Length) { return $false }
    $first = [int]$Needle[0]
    $otherFirst = -1
    if ($first -ge 65 -and $first -le 90) { $otherFirst = $first + 32 }
    elseif ($first -ge 97 -and $first -le 122) { $otherFirst = $first - 32 }
    $offset = 0
    while ($offset -le $Content.Length - $Needle.Length) {
        $found = [Array]::IndexOf($Content, [byte]$first, $offset)
        if ($otherFirst -ge 0) {
            $alternate = [Array]::IndexOf($Content, [byte]$otherFirst, $offset)
            if ($found -lt 0 -or ($alternate -ge 0 -and $alternate -lt $found)) { $found = $alternate }
        }
        if ($found -lt 0 -or $found -gt $Content.Length - $Needle.Length) { return $false }
        $same = $true
        for ($i = 1; $i -lt $Needle.Length; $i++) {
            $actual = [int]$Content[$found + $i]
            $expected = [int]$Needle[$i]
            if ($actual -ge 65 -and $actual -le 90) { $actual += 32 }
            if ($expected -ge 65 -and $expected -le 90) { $expected += 32 }
            if ($actual -ne $expected) { $same = $false; break }
        }
        if ($same) { return $true }
        $offset = $found + 1
    }
    return $false
}

function Assert-ReleasePrivacy {
    param(
        [Parameter(Mandatory=$true)][string]$Directory,
        [Parameter(Mandatory=$true)][string]$WorkspaceDirectory,
        [string]$UserDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    )
    if (!(Test-Path -LiteralPath $Directory -PathType Container)) { throw 'Release privacy scan directory is unavailable.' }

    $needles = [Collections.Generic.List[byte[]]]::new()
    $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($directoryPath in @($WorkspaceDirectory,$UserDirectory)) {
        if ([string]::IsNullOrWhiteSpace($directoryPath)) { continue }
        $fullPath = [IO.Path]::GetFullPath($directoryPath)
        if ($fullPath.Length -gt [IO.Path]::GetPathRoot($fullPath).Length) { $fullPath = $fullPath.TrimEnd([char[]]@('\','/')) }
        foreach ($form in @($fullPath,$fullPath.Replace('\','/'),$fullPath.Replace('\','\\'))) {
            foreach ($encoding in @([Text.Encoding]::UTF8,[Text.Encoding]::Unicode)) {
                # UTF-8 also covers pure ASCII paths byte-for-byte.
                $bytes = $encoding.GetBytes($form)
                if ($unique.Add([Convert]::ToBase64String($bytes))) { $needles.Add($bytes) }
            }
        }
    }

    try { $files = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force -File -ErrorAction Stop) }
    catch { throw 'Release privacy scan could not enumerate files.' }
    foreach ($file in $files) {
        try { $content = [IO.File]::ReadAllBytes($file.FullName) }
        catch { throw 'Release privacy scan could not read a file.' }
        foreach ($needle in $needles) {
            if (Test-ReleasePrivacyBytes $content $needle) {
                throw 'Release contains a local absolute path.'
            }
        }
    }
    Write-Output "PASS release privacy path scan: $($files.Count) files"
}
