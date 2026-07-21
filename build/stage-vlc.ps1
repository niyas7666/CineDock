$ErrorActionPreference = 'Stop'
$programFiles = if ([string]::IsNullOrWhiteSpace($env:ProgramFiles)) { 'C:\Program Files' } else { $env:ProgramFiles }
$source = Join-Path $programFiles 'VideoLAN\VLC'
$destination = Join-Path $PSScriptRoot 'vlc'
if (!(Test-Path (Join-Path $source 'libvlc.dll'))) {
  throw "64-bit VLC was not found at '$source'. Install VLC first, then run npm run package."
}
Remove-Item $destination -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item (Join-Path $source '*') $destination -Recurse -Force
if (!(Test-Path (Join-Path $destination 'plugins'))) { throw 'The VLC plugin directory was not staged.' }
Write-Host "Staged VLC runtime from $source"
