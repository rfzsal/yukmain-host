[CmdletBinding()]
param(
    [string]$Version = 'v2.2.0'
)

$ErrorActionPreference = 'Stop'
if ($Version -ne 'v2.2.0') {
    throw 'This project pins HIDMaestro v2.2.0. Update the checksum and review API changes before upgrading.'
}

$expected = '3616ab27f5315359289d7f2c7e27572137101aa8671c815c629d0cb051c90a6f'
$root = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $root 'vendor/HIDMaestro'
New-Item -ItemType Directory -Force -Path $vendor | Out-Null

$tempFolder = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tempFolder | Out-Null
try {
    $zip = Join-Path $tempFolder 'sdk.zip'
    $url = "https://github.com/hifihedgehog/HIDMaestro/releases/download/$Version/HIDMaestro-$Version.zip"
    Write-Host "Downloading pinned HIDMaestro SDK: $url"
    Invoke-WebRequest -Uri $url -OutFile $zip
    $hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $expected) {
        throw "HIDMaestro checksum mismatch. Expected $expected but got $hash"
    }

    $extracted = Join-Path $tempFolder 'extracted'
    Expand-Archive -Path $zip -DestinationPath $extracted
    $dll = Get-ChildItem -Path $extracted -Filter 'HIDMaestro.Core.dll' -File -Recurse | Select-Object -First 1
    if ($null -eq $dll) { throw 'HIDMaestro.Core.dll missing from verified SDK archive.' }
    Copy-Item $dll.FullName (Join-Path $vendor 'HIDMaestro.Core.dll') -Force

    $notice = Get-ChildItem -Path $extracted -Filter 'THIRD-PARTY-NOTICES.txt' -File -Recurse | Select-Object -First 1
    if ($notice) {
        Copy-Item $notice.FullName (Join-Path $vendor 'THIRD-PARTY-NOTICES.txt') -Force
    }

    Write-Host 'HIDMaestro SDK verified and staged.'
}
finally {
    Remove-Item $tempFolder -Recurse -Force -ErrorAction SilentlyContinue
}
