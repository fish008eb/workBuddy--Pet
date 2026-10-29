#Requires -Version 5.1
<#
  uninstall.ps1 — 卸载 WorkBuddy 用量宠物面板

  做的事：
    1. 关闭正在运行的面板进程
    2. 从 ~/.workbuddy/settings.json 移除 SessionStart / Stop hook
    3. 删除已安装的脚本与资源（保留配置备份）

  用法：
    powershell -ExecutionPolicy Bypass -File uninstall.ps1
    powershell -ExecutionPolicy Bypass -File uninstall.ps1 -Force     # 不再确认
#>
param(
    [string]$HooksDir = (Join-Path $env:USERPROFILE '.workbuddy\hooks'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$settingsPath = Join-Path $env:USERPROFILE '.workbuddy\settings.json'

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn2($text){ Write-Host "    $text" -ForegroundColor Yellow }

if (-not $Force) {
    Write-Host '将关闭面板、移除 hooks 并删除已安装的脚本文件。'
    $answer = Read-Host '继续？(y/N)'
    if ($answer -notmatch '^(y|Y)') {
        Write-Host '已取消。'
        exit 0
    }
}

# ---------- 1. 关闭进程 ----------
Write-Step '关闭面板进程'
try {
    Get-Process -Name 'WbPet' -ErrorAction SilentlyContinue | ForEach-Object {
        $_ | Stop-Process -Force -ErrorAction SilentlyContinue
        Write-Ok "已结束 WbPet.exe (PID $($_.Id))"
    }
} catch { }
try {
    Get-CimInstance Win32_Process | Where-Object {
        $_.CommandLine -like '*panel-wpf.ps1*' -and $_.Name -like 'powershell*'
    } | ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        Write-Ok "已结束 PowerShell 面板 (PID $($_.ProcessId))"
    }
} catch { }

# ---------- 2. 移除 hooks ----------
Write-Step '移除 hooks 配置'
if (Test-Path $settingsPath) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    Copy-Item -LiteralPath $settingsPath -Destination "$settingsPath.bak-$stamp" -Force
    Write-Ok "已备份配置到 settings.json.bak-$stamp"

    $cfg = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json

    if ($cfg.PSObject.Properties.Name -contains 'hooks') {
        foreach ($event in @('SessionStart', 'Stop')) {
            if (-not ($cfg.hooks.PSObject.Properties.Name -contains $event)) { continue }
            $groups = @()
            foreach ($g in @($cfg.hooks.$event)) {
                if (-not $g -or -not $g.hooks) { continue }
                $kept = @($g.hooks | Where-Object {
                    $_.command -and $_.command -notlike '*start-panel.js*' -and $_.command -notlike '*turn-usage.js*'
                })
                if ($kept.Count -gt 0) {
                    $groups += [pscustomobject]@{ hooks = $kept }
                }
            }
            $cfg.hooks | Add-Member -NotePropertyName $event -NotePropertyValue @($groups) -Force
            Write-Ok "已清理 $event"
        }

        $json = $cfg | ConvertTo-Json -Depth 20
        [System.IO.File]::WriteAllText($settingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    }
} else {
    Write-Warn2 "没有找到 $settingsPath，跳过"
}

# ---------- 3. 删除文件 ----------
Write-Step "清理 $HooksDir"
$files = @(
    'turn-usage.js', 'start-panel.js', 'panel-wpf.ps1', 'WbPet.cs', 'WbPet.exe',
    'run-panel.vbs', 'run-panel.cmd', 'build-pet.cmd', 'toast.ps1',
    'pet-ledger.js', 'pet-emotion.js', 'pet-event-writer.js',
    'panel-data.txt', 'panel-lines.txt', 'last-turn.txt',
    'pet-events.txt', 'pet-events.old.txt', 'pet-ledger.txt', 'pet-state.txt', 'pet-state.txt.tmp',
    'turn-usage.log', 'panel.log', 'panel-err.log'
)
foreach ($f in $files) {
    $p = Join-Path $HooksDir $f
    if (Test-Path $p) {
        Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
        Write-Ok "已删除 $f"
    }
}

$frames = Join-Path $HooksDir 'assets\frames'
if (Test-Path $frames) {
    Remove-Item -LiteralPath $frames -Recurse -Force -ErrorAction SilentlyContinue
    Write-Ok '已删除形象帧（legacy 目录）'
}

$skins = Join-Path $HooksDir 'assets\skins'
if (Test-Path $skins) {
    Remove-Item -LiteralPath $skins -Recurse -Force -ErrorAction SilentlyContinue
    Write-Ok '已删除形象缓存（skins 目录）'
}

$catalog = Join-Path $HooksDir 'assets\skins.json'
if (Test-Path $catalog) {
    Remove-Item -LiteralPath $catalog -Force -ErrorAction SilentlyContinue
    Write-Ok '已删除 skins.json'
}

$personas = Join-Path $HooksDir 'assets\personas.txt'
if (Test-Path $personas) {
    Remove-Item -LiteralPath $personas -Force -ErrorAction SilentlyContinue
    Write-Ok '已删除 personas.txt'
}

$tools = Join-Path $HooksDir 'tools'
if (Test-Path $tools) {
    Remove-Item -LiteralPath $tools -Recurse -Force -ErrorAction SilentlyContinue
    Write-Ok '已删除 tools 目录'
}

Write-Host ''
Write-Host '卸载完成。配置备份保留在 ~/.workbuddy/settings.json.bak-*' -ForegroundColor Green
Write-Host '如需回滚：把备份文件改名为 settings.json 即可。'
Write-Host ''
