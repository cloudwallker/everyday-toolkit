function Get-ResolvedRuntimeNotices([string]$DepsFile,[string]$AssetsFile) {
    $deps = [IO.File]::ReadAllText($DepsFile) | ConvertFrom-Json
    $assets = [IO.File]::ReadAllText($AssetsFile) | ConvertFrom-Json
    $resolved = @()
    foreach ($dependency in $deps.libraries.PSObject.Properties.Name) {
        if (!$dependency.StartsWith('runtimepack.')) { continue }
        $parts = $dependency.Substring('runtimepack.'.Length).Split('/')
        if ($parts.Length -ne 2 -or $parts[0] -notmatch '^[A-Za-z0-9.-]+$' -or $parts[1] -notmatch '^[A-Za-z0-9.+-]+$') { throw 'Invalid resolved runtime package identity.' }
        $id = $parts[0].ToLowerInvariant(); $version = $parts[1]; $package = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder "$id/$version"
            if (Test-Path -LiteralPath $candidate -PathType Container) { $package = $candidate; break }
        }
        if (!$package) { throw "Resolved runtime package unavailable: $id/$version" }
        $licenses = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -in @('LICENSE','LICENSE.txt') })
        if ($licenses.Count -ne 1) { throw "Runtime package license unavailable or ambiguous: $id/$version" }
        $licenseContent = [IO.File]::ReadAllText($licenses[0].FullName)
        if (!$licenseContent.Contains('MIT License') -or !$licenseContent.Contains('.NET Foundation')) { throw "Resolved runtime package license differs from documented MIT terms: $id/$version" }
        $notices = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '^THIRD-PARTY-NOTICES\.txt$' })
        if ($id -eq 'microsoft.netcore.app.runtime.win-x64' -and $notices.Count -ne 1) { throw 'Core runtime third-party notices unavailable.' }
        $resolved += [PSCustomObject]@{ Id=$id; Version=$version; LicenseSource=$licenses[0].FullName; NoticeSource=$(if ($notices.Count -gt 0) { $notices[0].FullName } else { $null }); RelativeDirectory="licenses/$id/$version" }
    }
    foreach ($required in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')) { if ($required -notin @($resolved.Id)) { throw "Required runtime package missing from actual publish dependency graph: $required" } }
    return $resolved
}
function Copy-RuntimeNotices([string]$DepsFile,[string]$AssetsFile,[string]$OutputDirectory) {
    $resolved = @(Get-ResolvedRuntimeNotices $DepsFile $AssetsFile)
    $catalog = @()
    foreach ($package in $resolved) {
        $directory = Join-Path $OutputDirectory $package.RelativeDirectory
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        Copy-Item -LiteralPath $package.LicenseSource -Destination "$directory/LICENSE.txt"
        $noticePath = $null
        if ($package.NoticeSource) { Copy-Item -LiteralPath $package.NoticeSource -Destination "$directory/THIRD-PARTY-NOTICES.txt"; $noticePath="$($package.RelativeDirectory)/THIRD-PARTY-NOTICES.txt" }
        $catalog += [PSCustomObject]@{ id=$package.Id; version=$package.Version; license="$($package.RelativeDirectory)/LICENSE.txt"; notices=$noticePath }
    }
    $json = [PSCustomObject]@{ version=1; packages=$catalog } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'licenses/runtime-packages.json'),$json,[Text.UTF8Encoding]::new($false))
}
function Test-RuntimeNotices([string]$DepsFile,[string]$AssetsFile,[string]$OutputDirectory) {
    $resolved = @(Get-ResolvedRuntimeNotices $DepsFile $AssetsFile)
    $catalogFile = Join-Path $OutputDirectory 'licenses/runtime-packages.json'
    $catalog = [IO.File]::ReadAllText($catalogFile) | ConvertFrom-Json
    if ($catalog.version -ne 1 -or @($catalog.packages).Count -ne $resolved.Count) { throw 'Runtime notice provenance catalog is incomplete.' }
    foreach ($package in $resolved) {
        $records = @($catalog.packages | Where-Object { $_.id -eq $package.Id -and $_.version -eq $package.Version })
        if ($records.Count -ne 1 -or $records[0].license -ne "$($package.RelativeDirectory)/LICENSE.txt") { throw 'Runtime notice provenance does not match published package identity.' }
        $license = Join-Path $OutputDirectory "$($package.RelativeDirectory)/LICENSE.txt"
        if ((Get-FileHash -LiteralPath $license).Hash -ne (Get-FileHash -LiteralPath $package.LicenseSource).Hash) { throw 'Distributed runtime license differs from actual resolved package.' }
        if ($package.NoticeSource) {
            $notice = Join-Path $OutputDirectory "$($package.RelativeDirectory)/THIRD-PARTY-NOTICES.txt"
            if ($records[0].notices -ne "$($package.RelativeDirectory)/THIRD-PARTY-NOTICES.txt" -or (Get-FileHash -LiteralPath $notice).Hash -ne (Get-FileHash -LiteralPath $package.NoticeSource).Hash) { throw 'Distributed runtime third-party notices differ from actual resolved package.' }
        } elseif ($null -ne $records[0].notices) { throw 'Runtime notice provenance declares unavailable notices.' }
    }
}
