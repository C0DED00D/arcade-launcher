<#
.SYNOPSIS
  Installs the emulators the arcade launcher uses, from their official releases, and builds the launcher.

.DESCRIPTION
  Downloads each emulator into emulators\<name>\ beside this script. Files that are already there (including the
  launcher's own configuration files from this repository: mame.ini, the plugins, shaders...) are never overwritten,
  so the script is safe to run again. No ROMs, BIOS files or games are downloaded: you supply those.

  Emulators (all stock official builds, nothing custom-compiled):
    mame      MAME 0.289                https://www.mamedev.org            (arcade games)
    flycast   Flycast 2.7               https://github.com/flyinghead/flycast  (Dreamcast / Naomi)
    mednafen  Mednafen 1.32.1           https://mednafen.github.io         (Sega Saturn)
    bigpemu   BigPEmu 1.221             https://www.richwhitehouse.com/jaguar  (Atari Jaguar)
    reshade   ReShade 6.8.0             https://reshade.me                 (CRT shaders for MAME and Flycast)
  Only MAME is required; the console systems simply don't appear in the menu without theirs.

.PARAMETER Only
  Install just these (e.g. -Only mame,reshade).
.PARAMETER Skip
  Leave these out (e.g. -Skip bigpemu,mednafen).
.PARAMETER NoBuild
  Don't build the launcher (needs the .NET 10 SDK).
.PARAMETER Shortcut
  Put an "Arcade" shortcut on the desktop.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1
.EXAMPLE
  .\install.ps1 -Only mame,reshade -Shortcut
#>
[CmdletBinding()]
param(
    [string[]]$Only,
    [string[]]$Skip,
    [switch]$NoBuild,
    [switch]$Shortcut
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower with the progress bar on 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Root = $PSScriptRoot
$Emu = Join-Path $Root 'emulators'
$Downloads = Join-Path $Root 'downloads'

# name, version, url, sha256 (when the publisher lists one), kind
$Packages = @(
    @{ Name = 'mame'; Version = '0.289'; Kind = 'sfx'
       Url = 'https://github.com/mamedev/mame/releases/download/mame0289/mame0289b_x64.exe'
       Sha256 = 'a1aa7912168c9d1b05e611906bc21b8b9be3935822aead36d12a1da363150b7d' },
    @{ Name = 'flycast'; Version = '2.7'; Kind = 'zip'
       Url = 'https://github.com/flyinghead/flycast/releases/download/v2.7/flycast-win64-2.7.zip'
       Sha256 = '7edf489ba6b3053703a18ce8233767eb71a2d89e4bda0421b312c2167a739161' },
    @{ Name = 'mednafen'; Version = '1.32.1'; Kind = 'zip'
       Url = 'https://mednafen.github.io/releases/files/mednafen-1.32.1-win64.zip'; Sha256 = $null },
    @{ Name = 'bigpemu'; Version = '1.221'; Kind = 'zip'
       Url = 'https://www.richwhitehouse.com/jaguar/builds/BigPEmu_v1221.zip'; Sha256 = $null },
    @{ Name = 'reshade'; Version = '6.8.0'; Kind = 'reshade'
       Url = 'https://reshade.me/downloads/ReShade_Setup_6.8.0.exe'; Sha256 = $null }
)

$ReShadeThumbprint = '589690208A5E52FB96980C4A6698F50ACD47C49F'   # as published on reshade.me

# CRT-Guest-Advanced's colour look-up textures (the shader itself is in this repository, with a small patch)
$GuestTextures = 1..4 | ForEach-Object { "https://raw.githubusercontent.com/HelelSingh/CRT-Guest-ReShade/main/Textures/CRT-LUT-$_.png" }

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Note($msg) { Write-Host "    $msg" }

function Get-Download($url, $sha256) {
    New-Item -ItemType Directory -Force $Downloads | Out-Null
    $file = Join-Path $Downloads ([IO.Path]::GetFileName(([Uri]$url).AbsolutePath))
    if (-not (Test-Path $file)) {
        Write-Note "downloading $url"
        $tmp = "$file.part"
        Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -UserAgent 'arcade-launcher-install'
        Move-Item $tmp $file -Force
    } else { Write-Note "already downloaded: $file" }
    if ($sha256) {
        $got = (Get-FileHash $file -Algorithm SHA256).Hash
        if ($got -ne $sha256.ToUpperInvariant()) {
            Remove-Item $file -Force
            throw "checksum mismatch for $file (expected $sha256, got $got) - deleted, run again"
        }
        Write-Note 'checksum OK'
    }
    return $file
}

# Copies a folder's contents into the destination, keeping every file that's already there.
function Copy-KeepExisting($from, $to) {
    New-Item -ItemType Directory -Force $to | Out-Null
    # /XC /XN /XO: skip files that exist in the destination at all
    & robocopy $from $to /E /XC /XN /XO /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copying into $to failed (robocopy $LASTEXITCODE)" }
}

# A zip that holds a single top folder: use what's inside it.
function Get-ContentRoot($dir) {
    $items = @(Get-ChildItem $dir -Force)
    if ($items.Count -eq 1 -and $items[0].PSIsContainer) { return $items[0].FullName }
    return $dir
}

function New-TempDir {
    $d = Join-Path ([IO.Path]::GetTempPath()) ("arcade-install-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory $d | Out-Null
    return $d
}

function Install-Zip($pkg) {
    $file = Get-Download $pkg.Url $pkg.Sha256
    $tmp = New-TempDir
    try {
        [IO.Compression.ZipFile]::ExtractToDirectory($file, $tmp)
        Copy-KeepExisting (Get-ContentRoot $tmp) (Join-Path $Emu $pkg.Name)
    } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

function Install-Mame($pkg) {
    if (Test-Path (Join-Path $Emu 'mame\mame.exe')) { Write-Note 'mame.exe is already there'; return }
    $file = Get-Download $pkg.Url $pkg.Sha256
    $tmp = New-TempDir
    try {
        # MAME's Windows release is a 7-Zip self-extractor: -o<dir> -y unpacks it without asking
        $p = Start-Process -FilePath $file -ArgumentList "-o`"$tmp`"", '-y' -Wait -PassThru -WindowStyle Hidden
        if ($p.ExitCode -ne 0) { throw "the MAME self-extractor failed (exit code $($p.ExitCode))" }
        Copy-KeepExisting (Get-ContentRoot $tmp) (Join-Path $Emu 'mame')
    } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

# ReShade's setup program carries ReShade64.dll in a zip appended to the exe. Cut the zip out and take the DLL, so
# nothing is installed system-wide. The launcher switches it per game by renaming (d3d9.dll <-> d3d9.dll.off).
function Install-ReShade($pkg) {
    $targets = @(
        @{ Dir = 'mame'; Dll = 'd3d9.dll' },      # MAME runs Direct3D 9 for the Guest CRT shader
        @{ Dir = 'flycast'; Dll = 'dxgi.dll' }     # Flycast runs DirectX 11
    )
    $need = $targets | Where-Object {
        $d = Join-Path $Emu $_.Dir
        (Test-Path $d) -and -not (Test-Path (Join-Path $d $_.Dll)) -and -not (Test-Path (Join-Path $d "$($_.Dll).off"))
    }
    if (-not $need) { Write-Note 'ReShade is already in place (or MAME/Flycast are missing)'; return }
    $file = Get-Download $pkg.Url $pkg.Sha256
    # ReShade signs with its own certificate (not one Windows trusts by default), so check it's that certificate:
    # the thumbprint published on reshade.me.
    $sig = Get-AuthenticodeSignature $file
    if (-not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne $ReShadeThumbprint) {
        throw "ReShade setup isn't signed with ReShade's certificate ($($sig.Status)) - not using it"
    }
    Write-Note "signature OK: $($sig.SignerCertificate.Subject)"
    $bytes = [IO.File]::ReadAllBytes($file)
    # The zip sits before the Authenticode signature at the end: find its end-of-central-directory record and work
    # back to where it starts.
    $eocd = -1
    for ($i = $bytes.Length - 22; $i -ge [Math]::Max(0, $bytes.Length - 262144); $i--) {
        if ($bytes[$i] -eq 0x50 -and $bytes[$i + 1] -eq 0x4B -and $bytes[$i + 2] -eq 5 -and $bytes[$i + 3] -eq 6) { $eocd = $i; break }
    }
    if ($eocd -lt 0) { throw 'no archive found inside the ReShade setup' }
    $cdSize = [BitConverter]::ToUInt32($bytes, $eocd + 12)
    $cdOffset = [BitConverter]::ToUInt32($bytes, $eocd + 16)
    $commentLen = [BitConverter]::ToUInt16($bytes, $eocd + 20)
    $at = $eocd - $cdSize - $cdOffset
    $end = $eocd + 22 + $commentLen
    if ($at -lt 0) { throw 'the archive inside the ReShade setup looks damaged' }
    $tmp = New-TempDir
    try {
        $zip = Join-Path $tmp 'reshade.zip'
        $fs = [IO.File]::Create($zip); $fs.Write($bytes, $at, $end - $at); $fs.Close()
        $out = Join-Path $tmp 'x'
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $out)
        $dll = Get-ChildItem $out -Recurse -Filter 'ReShade64.dll' | Select-Object -First 1
        if (-not $dll) { throw 'ReShade64.dll not found in the ReShade setup' }
        foreach ($t in $need) {
            Copy-Item $dll.FullName (Join-Path (Join-Path $Emu $t.Dir) "$($t.Dll).off")
            Write-Note "ReShade -> emulators\$($t.Dir)\$($t.Dll).off"
        }
    } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

function Install-ShaderTextures {
    $dir = Join-Path $Emu 'reshade-shaders\Textures'
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($url in $GuestTextures) {
        $dst = Join-Path $dir ([IO.Path]::GetFileName($url))
        if (Test-Path $dst) { continue }
        Write-Note "downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $dst -UseBasicParsing -UserAgent 'arcade-launcher-install'
    }
}

# ---------------------------------------------------------------------------------------------------------------
$names = $Packages.Name
if ($Only) { $bad = $Only | Where-Object { $_ -notin $names }; if ($bad) { throw "unknown: $($bad -join ', ') (choose from $($names -join ', '))" } }
$selected = $Packages | Where-Object { (-not $Only -or $_.Name -in $Only) -and -not ($Skip -and $_.Name -in $Skip) }
# ReShade goes into the MAME / Flycast folders, so do it last
$selected = @($selected | Where-Object { $_.Name -ne 'reshade' }) + @($selected | Where-Object { $_.Name -eq 'reshade' })

$failed = @()
foreach ($pkg in $selected) {
    Write-Step "$($pkg.Name) $($pkg.Version)"
    try {
        switch ($pkg.Kind) {
            'sfx' { Install-Mame $pkg }
            'zip' { Install-Zip $pkg }
            'reshade' { Install-ReShade $pkg; Install-ShaderTextures }
        }
    } catch {
        Write-Host "    FAILED: $($_.Exception.Message)" -ForegroundColor Red
        $failed += $pkg.Name
    }
}

Write-Step 'folders and local config'
foreach ($d in 'roms', 'dreamcast', 'jaguar', 'saturn', 'data', 'work', 'app') { New-Item -ItemType Directory -Force (Join-Path $Root $d) | Out-Null }
$localLua = Join-Path $Emu 'mame\plugins\freeplay\local.lua'
if ((Test-Path (Split-Path $localLua)) -and -not (Test-Path $localLua)) {
    Copy-Item (Join-Path $Emu 'mame\plugins\freeplay\local.example.lua') $localLua
    Write-Note 'created emulators\mame\plugins\freeplay\local.lua'
}

if (-not $NoBuild) {
    Write-Step 'building the launcher'
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $sdkOk = $false
    if ($dotnet) { $sdkOk = [bool](& dotnet --list-sdks | Where-Object { $_ -match '^(1\d|[2-9]\d)\.' }) }
    if (-not $sdkOk) {
        Write-Host '    The .NET 10 SDK is needed to build it: https://dotnet.microsoft.com/download (or: winget install Microsoft.DotNet.SDK.10)' -ForegroundColor Yellow
        $failed += 'launcher build'
    } else {
        & dotnet publish (Join-Path $Root 'launcher') -c Release -o (Join-Path $Root 'app') --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { $failed += 'launcher build' } else { Write-Note 'app\ArcadeLauncher.exe' }
    }
}

if ($Shortcut) {
    $exe = Join-Path $Root 'app\ArcadeLauncher.exe'
    $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Arcade.lnk'
    $sh = New-Object -ComObject WScript.Shell
    $s = $sh.CreateShortcut($lnk); $s.TargetPath = $exe; $s.WorkingDirectory = Split-Path $exe; $s.Save()
    Write-Note "shortcut: $lnk"
}

Write-Host ''
if ($failed) { Write-Host "Finished with problems: $($failed -join ', ')" -ForegroundColor Yellow } else { Write-Host 'Done.' -ForegroundColor Green }
Write-Host @'

Next:
  * Put your own MAME ROM sets (matching MAME 0.289) in roms\.
  * Dreamcast discs in dreamcast\, Jaguar images in jaguar\, Saturn discs in saturn\ (see README.md).
  * Saturn needs its BIOS in emulators\mednafen\firmware\ (mpr-17933.bin for US/EU).
  * Controls: config\input.json is written on first start. Run  app\ArcadeLauncher.exe --input-test  to see the
    name of every button on your controllers. See docs\input.md.
  * Start app\ArcadeLauncher.exe.
'@
