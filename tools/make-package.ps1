#Requires -Version 5.1
<#
  make-package.ps1 - assemble a distributable folder + zip.

  Layout produced:
    dist/wb-usage-pet-v<version>/
      install.cmd            double-click entry (checks Node, runs install.ps1)
      install.ps1            copies files, registers hooks, starts the panel
      uninstall.ps1          removes everything again
      README.md              developer-facing docs
      安装说明.txt            end-user instructions
      src/                   scripts + prebuilt WbPet.exe
      tools/                 sprite / skin / sound helpers
      assets/                skins.json, personas.txt, sounds/

  Usage (from the repo root or anywhere):
    powershell -ExecutionPolicy Bypass -File tools\make-package.ps1
    powershell -ExecutionPolicy Bypass -File tools\make-package.ps1 -Exe "C:\path\WbPet.exe"

  The script refuses to build when the exe is older than WbPet.cs, because that
  would ship a panel without the latest UI / emotion / sound code.
#>
param(
    [string]$Exe = "",
    [string]$OutDir = "",
    [switch]$AllowStale
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn2($text){ Write-Host "    $text" -ForegroundColor Yellow }

# ---------- version ----------
$pkg = Get-Content (Join-Path $root 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$version = $pkg.version
$name = "wb-usage-pet-v$version"
Write-Step "packaging $name"

# ---------- locate the compiled exe ----------
if (-not $Exe) {
    $candidates = @(
        (Join-Path $root 'src\WbPet.exe'),
        (Join-Path $env:USERPROFILE '.workbuddy\hooks\WbPet.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { $Exe = $c; break }
    }
}

if (-not $Exe -or -not (Test-Path -LiteralPath $Exe)) {
    Write-Warn2 'no compiled WbPet.exe found.'
    Write-Warn2 'run src\build-pet.cmd first, then re-run this script.'
    exit 1
}

$exeItem = Get-Item -LiteralPath $Exe
$csItem = Get-Item -LiteralPath (Join-Path $root 'src\WbPet.cs')
if ($exeItem.LastWriteTime -lt $csItem.LastWriteTime -and -not $AllowStale) {
    Write-Warn2 "WbPet.exe is older than WbPet.cs - the package would ship stale code."
    Write-Warn2 ("  exe: {0}" -f $exeItem.LastWriteTime)
    Write-Warn2 ("  cs : {0}" -f $csItem.LastWriteTime)
    Write-Warn2 'rebuild with src\build-pet.cmd, or pass -AllowStale to force.'
    exit 1
}
Write-Ok ("exe: {0} ({1} bytes)" -f $exeItem.FullName, $exeItem.Length)

# ---------- stage ----------
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
$stage = Join-Path $OutDir $name
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Write-Step "staging into $stage"

Copy-Item (Join-Path $root 'install.cmd')     $stage -Force
Copy-Item (Join-Path $root 'install.ps1')     $stage -Force
Copy-Item (Join-Path $root 'uninstall.ps1')   $stage -Force
Copy-Item (Join-Path $root 'README.md')       $stage -Force
Copy-Item (Join-Path $root '安装说明.txt')     $stage -Force

$srcOut = Join-Path $stage 'src'
New-Item -ItemType Directory -Force -Path $srcOut | Out-Null
# 排除本地调试留下的运行时文件，只带源码
$srcSkip = @('*.log', 'panel-data.txt', 'panel-lines.txt', 'last-turn.txt',
             'pet-events.txt', 'pet-events.old.txt', 'pet-ledger.txt', 'pet-state.txt',
             'pet-state.txt.tmp', '*.exe', '*.pdb')
Copy-Item (Join-Path $root 'src\*') $srcOut -Recurse -Force -Exclude $srcSkip
Copy-Item -LiteralPath $Exe (Join-Path $srcOut 'WbPet.exe') -Force

$toolsOut = Join-Path $stage 'tools'
New-Item -ItemType Directory -Force -Path $toolsOut | Out-Null
Copy-Item (Join-Path $root 'tools\*.js') $toolsOut -Force

$assetsOut = Join-Path $stage 'assets'
New-Item -ItemType Directory -Force -Path (Join-Path $assetsOut 'sounds') | Out-Null
Copy-Item (Join-Path $root 'assets\skins.json')   $assetsOut -Force
Copy-Item (Join-Path $root 'assets\personas.txt') $assetsOut -Force
Copy-Item (Join-Path $root 'assets\sounds\*.wav') (Join-Path $assetsOut 'sounds') -Force -ErrorAction SilentlyContinue

# 说明文档转成带 BOM，避免旧版记事本打开是乱码
$readmeTxt = Join-Path $stage '安装说明.txt'
if (Test-Path -LiteralPath $readmeTxt) {
    $content = [System.IO.File]::ReadAllText($readmeTxt, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($readmeTxt, $content, (New-Object System.Text.UTF8Encoding($true)))
}

$files = Get-ChildItem $stage -Recurse -File
Write-Ok ("{0} files, {1:N0} KB" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1KB))

# ---------- zip ----------
$zip = Join-Path $OutDir "$name.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Write-Step "compressing"
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$zipItem = Get-Item -LiteralPath $zip
Write-Ok ("{0}  {1:N0} KB" -f $zipItem.FullName, ($zipItem.Length / 1KB))

Write-Host ''
Write-Host 'Package ready.' -ForegroundColor Green
Write-Host ("  folder : {0}" -f $stage)
Write-Host ("  zip    : {0}" -f $zip)
Write-Host '  share the zip; the recipient extracts it and double-clicks install.cmd'
