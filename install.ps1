#Requires -Version 5.1
<#
  install.ps1 — 安装 WorkBuddy 用量宠物面板

  做的事：
    1. 把 src\ 下的脚本、tools\ 下的工具复制到 WorkBuddy 的 hooks 目录
    2. 抓取并拆分默认形象（可用 -Skin 指定，-SkipSprite 跳过）
    3. 往 ~/.workbuddy/settings.json 注册 SessionStart / Stop 两个 hook
    4. 立即启动面板

  用法：
    powershell -ExecutionPolicy Bypass -File install.ps1
    powershell -ExecutionPolicy Bypass -File install.ps1 -Skin quantum    # 指定初始形象
    powershell -ExecutionPolicy Bypass -File install.ps1 -SkipSprite      # 不下载形象
    powershell -ExecutionPolicy Bypass -File install.ps1 -BuildNative     # 顺手编译原生 exe
#>
param(
    [string]$HooksDir = (Join-Path $env:USERPROFILE '.workbuddy\hooks'),
    [string]$SettingsPath = (Join-Path $env:USERPROFILE '.workbuddy\settings.json'),
    [string]$Skin = 'dragon',
    [switch]$SkipSprite,
    [switch]$BuildNative
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$settingsPath = $SettingsPath

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn2($text){ Write-Host "    $text" -ForegroundColor Yellow }

# ---------- 0. 环境检查 ----------
Write-Step '检查运行环境'

$nodeCmd = Get-Command node -ErrorAction SilentlyContinue
if (-not $nodeCmd) {
    Write-Warn2 '没有找到 node，请先安装 Node.js 18+ 后重试：https://nodejs.org/'
    exit 1
}
$nodeVersion = (& node --version).Trim()
Write-Ok "Node $nodeVersion"

if (-not (Test-Path $settingsPath)) {
    Write-Warn2 "没有找到 $settingsPath"
    Write-Warn2 '请先启动一次 WorkBuddy 桌面端，让它生成配置后再运行本脚本。'
    exit 1
}

# ---------- 1. 复制脚本 ----------
Write-Step "复制脚本到 $HooksDir"
New-Item -ItemType Directory -Force -Path $HooksDir | Out-Null
Copy-Item -Path (Join-Path $root 'src\*') -Destination $HooksDir -Recurse -Force
Write-Ok '已复制 turn-usage.js / start-panel.js / panel-wpf.ps1 / WbPet.cs / run-panel.vbs / build-pet.cmd / toast.ps1'

$toolsTarget = Join-Path $HooksDir 'tools'
New-Item -ItemType Directory -Force -Path $toolsTarget | Out-Null
Copy-Item -Path (Join-Path $root 'tools\*.js') -Destination $toolsTarget -Force
Write-Ok '已复制 tools\fetch-sprite.js / switch-skin.js'

$assetsTarget = Join-Path $HooksDir 'assets'
New-Item -ItemType Directory -Force -Path $assetsTarget | Out-Null
Copy-Item -Path (Join-Path $root 'assets\skins.json') -Destination $assetsTarget -Force
Copy-Item -Path (Join-Path $root 'assets\personas.txt') -Destination $assetsTarget -Force
Write-Ok '已复制 assets\skins.json（形象清单）与 assets\personas.txt（人格文案）'

# 面板第一次启动前没有 pet-state.txt，先放一份默认偏好，避免面板与 hook 各写一份
$stateFile = Join-Path $HooksDir 'pet-state.txt'
if (-not (Test-Path $stateFile)) {
    $defaults = @(
        'version=1',
        'persona=cute',
        'verbosity=normal',
        'winX=0',
        'winY=0',
        'eventOffset=0',
        'lastEventSeq=0',
        'quietUntil=0'
    )
    [System.IO.File]::WriteAllLines($stateFile, $defaults, (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok '已写入默认 pet-state.txt（人格=可爱治愈，唠叨=克制）'
}

# ---------- 2. 形象资源 ----------
$currentFile = Join-Path $HooksDir 'assets\skins\current.txt'
$switcher = Join-Path $toolsTarget 'switch-skin.js'
$hasSkin = Test-Path $currentFile

if ($SkipSprite) {
    Write-Warn2 '已跳过形象下载（-SkipSprite）'
} elseif ($hasSkin) {
    $cur = (Get-Content $currentFile -Encoding UTF8 | Select-Object -First 1)
    Write-Ok "已有形象（$cur），跳过下载"
} else {
    Write-Step "下载并拆分形象：$Skin"
    try {
        & node $switcher $Skin
        if (Test-Path $currentFile) { Write-Ok '形象就绪' }
        else { Write-Warn2 '形象下载未完成，可在面板右键菜单里重新选择' }
    } catch {
        Write-Warn2 "形象下载失败：$($_.Exception.Message)"
        Write-Warn2 '面板仍可运行，稍后右键「换形象」重试'
    }
}

# ---------- 3. 注册 hooks ----------
Write-Step '注册 hooks（SessionStart / Stop）'

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = "$settingsPath.bak-$stamp"
Copy-Item -LiteralPath $settingsPath -Destination $backup -Force
Write-Ok "已备份配置到 $(Split-Path -Leaf $backup)"

$cfg = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json

if (-not ($cfg.PSObject.Properties.Name -contains 'hooks')) {
    $cfg | Add-Member -NotePropertyName 'hooks' -NotePropertyValue ([pscustomobject]@{}) -Force
}
$hooksNode = $cfg.hooks

function Set-HookGroup {
    param($hooksNode, [string]$event, [string]$scriptName, [int]$timeout)

    $groups = @()
    if ($hooksNode.PSObject.Properties.Name -contains $event) {
        foreach ($g in @($hooksNode.$event)) {
            if (-not $g -or -not $g.hooks) { continue }
            # 去掉旧版本写进去的同名 hook，避免重复执行
            $kept = @($g.hooks | Where-Object { $_.command -and $_.command -notlike "*$scriptName*" })
            if ($kept.Count -gt 0) {
                $groups += [pscustomobject]@{ hooks = $kept }
            }
        }
    }

    $command = 'node "' + (Join-Path $HooksDir $scriptName) + '"'
    $groups += [pscustomobject]@{
        hooks = @([pscustomobject]@{ type = 'command'; command = $command; timeout = $timeout })
    }

    $hooksNode | Add-Member -NotePropertyName $event -NotePropertyValue @($groups) -Force
}

Set-HookGroup -hooksNode $hooksNode -event 'SessionStart' -scriptName 'start-panel.js' -timeout 10
Set-HookGroup -hooksNode $hooksNode -event 'Stop' -scriptName 'turn-usage.js' -timeout 15

$json = $cfg | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($settingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Ok '已写入 settings.json（UTF-8 无 BOM）'

# ---------- 4. 可选：编译原生版 ----------
if ($BuildNative) {
    Write-Step '编译原生宿主 WbPet.exe'
    $builder = Join-Path $HooksDir 'build-pet.cmd'
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) {
        $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    }
    if (-not (Test-Path $csc)) {
        Write-Warn2 '没有找到 csc.exe（需要 .NET Framework 4.x），跳过编译'
    } else {
        $fw = Split-Path $csc
        Push-Location $HooksDir
        try {
            & $csc /nologo /target:winexe /platform:anycpu /optimize+ `
                /out:WbPet.exe `
                /lib:"$fw;$fw\WPF" `
                /r:System.dll /r:System.Core.dll /r:System.Xml.dll `
                /r:WindowsBase.dll /r:PresentationCore.dll /r:PresentationFramework.dll /r:System.Xaml.dll `
                WbPet.cs
            if (Test-Path (Join-Path $HooksDir 'WbPet.exe')) {
                Write-Ok 'WbPet.exe 编译成功（内存占用更低、无 PowerShell 宿主）'
            } else {
                Write-Warn2 '编译失败，面板将回退到 PowerShell 版'
            }
        } catch {
            Write-Warn2 "编译异常：$($_.Exception.Message)（不影响使用）"
        }
        Pop-Location
    }
}

# ---------- 5. 启动 ----------
Write-Step '启动面板'
try {
    & node (Join-Path $HooksDir 'start-panel.js')
    Start-Sleep -Milliseconds 1500
    Write-Ok '已发出启动指令'
} catch {
    Write-Warn2 "启动失败：$($_.Exception.Message)"
}

Write-Host ''
Write-Host '安装完成。' -ForegroundColor Green
Write-Host '  · 面板应出现在屏幕右下角，鼠标移上去可看用量'
Write-Host '  · 若面板没出现，重启一次 WorkBuddy 再发一条消息'
Write-Host '  · 卸载：powershell -ExecutionPolicy Bypass -File uninstall.ps1'
Write-Host ''
