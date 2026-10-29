param(
    [string]$DataFile = "",
    [int]$IntervalMs = 2000
)

# WPF pet panel: mascot (APNG frames) with a dark, icon-based speech bubble.
# WPF is used instead of WinForms because a transparent window needs real
# per-pixel alpha. WinForms can only do a colour-key (TransparencyKey), which
# leaves a coloured fringe on anti-aliased edges.
#
# Skins: assets/skins.json holds the catalogue, assets/skins/<id>/ holds the
# extracted frames, assets/skins/current.txt records the active one. The panel
# polls current.txt, so switching a skin never needs a restart.
#
# NOTE: this file must stay UTF-8 **with BOM**; PowerShell 5.1 reads BOM-less
# files as ANSI and would garble the Chinese literals below.

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Drawing

# 未捕获异常写日志，避免窗口无声消失
try {
    $script:ErrLog = Join-Path $PSScriptRoot 'panel-err.log'
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.add_UnhandledException({
        param($s, $e)
        try {
            Add-Content -LiteralPath $script:ErrLog -Encoding UTF8 -Value ("[{0}] unhandled: {1}" -f (Get-Date -Format 's'), $e.Exception.Message)
        } catch { }
        $e.Handled = $true
    })
} catch { }

# single instance
$mutex = New-Object System.Threading.Mutex($false, 'Global\WorkBuddyUsagePanel')
if (-not $mutex.WaitOne(0)) { exit 0 }

$script:DataFile = $DataFile
$script:HookDir = $PSScriptRoot
if (-not $script:DataFile) { $script:DataFile = Join-Path $script:HookDir 'panel-data.txt' }

$script:AssetsDir = Join-Path $script:HookDir 'assets'
$script:SkinsDir = Join-Path $script:AssetsDir 'skins'
$script:CatalogFile = Join-Path $script:AssetsDir 'skins.json'
$script:CurrentFile = Join-Path $script:SkinsDir 'current.txt'
$script:StatusFile = Join-Path $script:SkinsDir 'status.txt'
$script:SwitchScript = Join-Path $script:HookDir 'tools\switch-skin.js'

$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize"
        SnapsToDevicePixels="True" UseLayoutRounding="True"
        Width="340" Height="448" FontFamily="Microsoft YaHei UI">
  <Grid>
    <StackPanel VerticalAlignment="Bottom" HorizontalAlignment="Center" Margin="0,0,0,6">

      <Grid x:Name="BubbleRoot" Visibility="Collapsed" Margin="0,0,0,8" HorizontalAlignment="Center">
        <Path x:Name="Tail" Fill="#1B1E26" Stroke="#333949" StrokeThickness="0.5"
              HorizontalAlignment="Center" VerticalAlignment="Bottom" Margin="0,0,0,-13"
              Data="M 0,0 L 26,0 L 13,13 Z" />
        <Border Background="#1B1E26" BorderBrush="#333949" BorderThickness="0.5"
                CornerRadius="16" Padding="15,11,15,12" Width="288">
          <Border.Effect>
            <DropShadowEffect BlurRadius="14" ShadowDepth="4" Direction="270" Opacity="0.32" Color="#000000"/>
          </Border.Effect>
          <StackPanel>

            <Grid>
              <TextBlock x:Name="TitleText" Text="WorkBuddy" FontSize="10.5"
                         Foreground="#8A93A8" HorizontalAlignment="Left" VerticalAlignment="Center"
                         TextOptions.TextFormattingMode="Display"/>
              <TextBlock x:Name="TimeText" Text="" FontSize="10.5"
                         Foreground="#5C6478" HorizontalAlignment="Right" VerticalAlignment="Center"
                         TextOptions.TextFormattingMode="Display"/>
            </Grid>

            <Border Height="1" Background="#262B36" Margin="0,9,0,0"/>

            <Grid Margin="0,11,0,12">
              <StackPanel Orientation="Horizontal" HorizontalAlignment="Left" VerticalAlignment="Bottom">
                <TextBlock Text="今日" FontSize="10" Foreground="#6B7385"
                           VerticalAlignment="Bottom" Margin="0,0,6,4"
                           TextOptions.TextFormattingMode="Display"/>
                <TextBlock x:Name="TodayValue" Text="—" FontSize="23" FontWeight="Medium"
                           Foreground="#EDEFF5" VerticalAlignment="Bottom" Margin="0,0,5,0"
                           TextOptions.TextFormattingMode="Display"/>
                <TextBlock Text="积分" FontSize="10" Foreground="#6B7385"
                           VerticalAlignment="Bottom" Margin="0,0,0,4"
                           TextOptions.TextFormattingMode="Display"/>
              </StackPanel>
              <TextBlock x:Name="TodaySessions" Text="" FontSize="10.5" Foreground="#5C6478"
                         HorizontalAlignment="Right" VerticalAlignment="Bottom" Margin="0,0,0,4"
                         TextOptions.TextFormattingMode="Display"/>
            </Grid>

            <Border Height="1" Background="#262B36"/>

            <Grid Margin="0,10,0,0">
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="*"/>
              </Grid.ColumnDefinitions>

              <StackPanel Grid.Column="0" Orientation="Horizontal" HorizontalAlignment="Left"
                          VerticalAlignment="Center" ToolTip="输入 token">
                <Path Fill="#5EEAD4" Stretch="Uniform" Width="9" Height="9" VerticalAlignment="Center" Margin="0,0,4,0"
                      Data="M8,2 L14,9 L10.5,9 L10.5,14 L5.5,14 L5.5,9 L2,9 Z"/>
                <TextBlock x:Name="TurnInVal" Text="—" FontSize="11" Foreground="#98A0B4"
                           VerticalAlignment="Center" TextOptions.TextFormattingMode="Display"/>
              </StackPanel>

              <StackPanel Grid.Column="1" Orientation="Horizontal" HorizontalAlignment="Center"
                          VerticalAlignment="Center" ToolTip="输出 token">
                <Path Fill="#A78BFA" Stretch="Uniform" Width="9" Height="9" VerticalAlignment="Center" Margin="0,0,4,0"
                      Data="M8,14 L2,7 L5.5,7 L5.5,2 L10.5,2 L10.5,7 L14,7 Z"/>
                <TextBlock x:Name="TurnOutVal" Text="—" FontSize="11" Foreground="#98A0B4"
                           VerticalAlignment="Center" TextOptions.TextFormattingMode="Display"/>
              </StackPanel>

              <StackPanel Grid.Column="2" Orientation="Horizontal" HorizontalAlignment="Center"
                          VerticalAlignment="Center" ToolTip="缓存命中率">
                <Path Fill="#FBBF24" Stretch="Uniform" Width="9" Height="9" VerticalAlignment="Center" Margin="0,0,4,0"
                      Data="M9.5,1 L3,9 L7,9 L6.5,15 L13,7 L9,7 Z"/>
                <TextBlock x:Name="CacheVal" Text="—" FontSize="11" Foreground="#98A0B4"
                           VerticalAlignment="Center" TextOptions.TextFormattingMode="Display"/>
              </StackPanel>

              <StackPanel Grid.Column="3" Orientation="Horizontal" HorizontalAlignment="Right"
                          VerticalAlignment="Center" ToolTip="本轮积分">
                <Path Fill="#34D399" Stretch="Uniform" Width="9" Height="9" VerticalAlignment="Center" Margin="0,0,4,0"
                      Data="M8,2 L13,8 L8,14 L3,8 Z"/>
                <TextBlock x:Name="CreditVal" Text="—" FontSize="11" Foreground="#98A0B4"
                           VerticalAlignment="Center" TextOptions.TextFormattingMode="Display"/>
              </StackPanel>
            </Grid>

            <TextBlock x:Name="FooterText" Text="" FontSize="10" Foreground="#6B7385"
                       HorizontalAlignment="Center" Margin="0,7,0,0" Visibility="Collapsed"
                       TextOptions.TextFormattingMode="Display"/>

          </StackPanel>
        </Border>
      </Grid>

      <Image x:Name="Mascot" Width="190" Height="208"
             RenderOptions.BitmapScalingMode="HighQuality" Stretch="Uniform">
        <Image.RenderTransform>
          <TranslateTransform x:Name="MascotShift" X="0" Y="0"/>
        </Image.RenderTransform>
      </Image>

    </StackPanel>
  </Grid>
</Window>
'@

$reader = New-Object System.Xml.XmlNodeReader ([xml]$xaml)
$win = [Windows.Markup.XamlReader]::Load($reader)

$script:Mascot = $win.FindName('Mascot')
$script:BubbleRoot = $win.FindName('BubbleRoot')
$script:TitleText = $win.FindName('TitleText')
$script:TimeText = $win.FindName('TimeText')
$script:TurnInVal = $win.FindName('TurnInVal')
$script:TurnOutVal = $win.FindName('TurnOutVal')
$script:CacheVal = $win.FindName('CacheVal')
$script:CreditVal = $win.FindName('CreditVal')
$script:TodayValue = $win.FindName('TodayValue')
$script:TodaySessions = $win.FindName('TodaySessions')
$script:FooterText = $win.FindName('FooterText')
$script:MascotShift = $win.FindName('MascotShift')

# position: bottom-right corner of the working area
$wa = [System.Windows.SystemParameters]::WorkArea
$win.Left = $wa.Right - $win.Width - 16
$win.Top = $wa.Bottom - $win.Height - 8

# ---- frames ---------------------------------------------------------------
$script:frames = New-Object System.Collections.ArrayList
$script:frameDelays = New-Object System.Collections.ArrayList
$script:frameIdx = 0
$script:loadedSkin = ''

function Get-Bitmap([string]$file) {
    $bmp = New-Object System.Windows.Media.Imaging.BitmapImage
    $stream = [System.IO.File]::OpenRead($file)
    try {
        $bmp.BeginInit()
        $bmp.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
        $bmp.StreamSource = $stream
        $bmp.EndInit()
        $bmp.Freeze()
    } finally {
        $stream.Close()
    }
    return $bmp
}

function Get-SkinCatalogue {
    if (-not (Test-Path -LiteralPath $script:CatalogFile)) { return @() }
    try {
        $json = Get-Content -LiteralPath $script:CatalogFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($json.skins) { return @($json.skins) }
    } catch { }
    return @()
}

function Get-CurrentSkin {
    try {
        if (-not (Test-Path -LiteralPath $script:CurrentFile)) { return @{ id = ''; name = '' } }
        $lines = Get-Content -LiteralPath $script:CurrentFile -Encoding UTF8
        $id = if ($lines.Count -ge 1) { $lines[0].Trim() } else { '' }
        $name = if ($lines.Count -ge 2) { $lines[1].Trim() } else { $id }
        return @{ id = $id; name = $name }
    } catch {
        return @{ id = ''; name = '' }
    }
}

function Get-FramesDir([string]$skinId) {
    if ($skinId) {
        $dir = Join-Path $script:SkinsDir $skinId
        if (Test-Path -LiteralPath (Join-Path $dir 'manifest.json')) { return $dir }
    }
    $legacy = Join-Path $script:AssetsDir 'frames'
    if (Test-Path -LiteralPath (Join-Path $legacy 'manifest.json')) { return $legacy }
    return $null
}

function Load-Frames([string]$dir) {
    $script:Mascot.Source = $null
    $script:frames.Clear()
    $script:frameDelays.Clear()
    $script:frameIdx = 0
    if (-not $dir) { return }

    $manifestPath = Join-Path $dir 'manifest.json'
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($f in $manifest.frames) {
            $fp = Join-Path $dir $f.file
            if (Test-Path -LiteralPath $fp) {
                try {
                    [void]$script:frames.Add((Get-Bitmap $fp))
                    [void]$script:frameDelays.Add([int]$f.delay)
                } catch {
                    try {
                        Add-Content -LiteralPath (Join-Path $script:HookDir 'panel-err.log') -Encoding UTF8 `
                            -Value ("[{0}] frame {1}: {2}" -f (Get-Date -Format 's'), $f.file, $_.Exception.Message)
                    } catch { }
                }
            }
        }
    } catch { }

    if ($script:frames.Count -gt 0) {
        $script:Mascot.Source = $script:frames[0]
        if ($script:frameTimer) {
            $script:frameTimer.Interval = [TimeSpan]::FromMilliseconds(83)
            if ($script:frames.Count -gt 1 -and -not $script:frameTimer.IsEnabled) { $script:frameTimer.Start() }
        }
    }
    # 换肤会留下上一套帧对象，主动回收一次
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
}

$script:frameTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:frameTimer.Interval = [TimeSpan]::FromMilliseconds(83)
$script:frameTimer.Add_Tick({
    if ($script:frames.Count -lt 2) { return }
    $script:frameIdx = ($script:frameIdx + 1) % $script:frames.Count
    $script:Mascot.Source = $script:frames[$script:frameIdx]
    $d = 83
    if ($script:frameIdx -lt $script:frameDelays.Count) { $d = $script:frameDelays[$script:frameIdx] }
    if ($d -lt 30) { $d = 30 }
    $script:frameTimer.Interval = [TimeSpan]::FromMilliseconds($d)
})

$skinNow = Get-CurrentSkin
$script:loadedSkin = $skinNow.id
Load-Frames (Get-FramesDir $skinNow.id)
if ($script:frames.Count -gt 1) { $script:frameTimer.Start() }

# ---- bubble visibility ----------------------------------------------------
$script:tempUntil = [DateTime]::MinValue
$script:hoverHideTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:hoverHideTimer.Interval = [TimeSpan]::FromMilliseconds(450)
$script:hoverHideTimer.Add_Tick({
    $script:hoverHideTimer.Stop()
    if ((Get-Date) -lt $script:tempUntil) { return }
    $script:BubbleRoot.Visibility = 'Collapsed'
})

function Show-BubbleFor([int]$seconds) {
    $script:tempUntil = (Get-Date).AddSeconds($seconds)
    $script:hoverHideTimer.Stop()
    $script:BubbleRoot.Visibility = 'Visible'
}

$script:Mascot.Add_MouseEnter({
    $script:hoverHideTimer.Stop()
    $script:BubbleRoot.Visibility = 'Visible'
})
$script:Mascot.Add_MouseLeave({
    if ((Get-Date) -lt $script:tempUntil) { return }
    $script:hoverHideTimer.Start()
})
$script:BubbleRoot.Add_MouseEnter({ $script:hoverHideTimer.Stop() })
$script:BubbleRoot.Add_MouseLeave({ $script:hoverHideTimer.Start() })

# ---- bounce ---------------------------------------------------------------
function Start-Bounce {
    $anim = New-Object System.Windows.Media.Animation.DoubleAnimation
    $anim.From = 0.0
    $anim.To = -9.0
    $anim.Duration = [System.Windows.Duration]::new([TimeSpan]::FromMilliseconds(280))
    $anim.AutoReverse = $true
    $anim.RepeatBehavior = New-Object System.Windows.Media.Animation.RepeatBehavior(2)
    $anim.EasingFunction = (New-Object System.Windows.Media.Animation.QuadraticEase)
    $script:MascotShift.BeginAnimation([System.Windows.Media.TranslateTransform]::YProperty, $anim)
}

# ---- data -----------------------------------------------------------------
$script:lastSig = ''
$script:firstLoad = $true

function Read-PanelData {
    $map = @{}
    try {
        if (-not (Test-Path -LiteralPath $script:DataFile)) { return $map }
        foreach ($line in (Get-Content -LiteralPath $script:DataFile -Encoding UTF8)) {
            $i = $line.IndexOf('=')
            if ($i -gt 0) {
                $map[$line.Substring(0, $i)] = $line.Substring($i + 1)
            }
        }
    } catch { }
    return $map
}

function Update-Panel {
    try {
        if (Test-Path -LiteralPath $script:DataFile) {
            $d = Read-PanelData
            if ($d.Count -gt 0) {
                # 用内容签名判重：同一轮重复写文件不会再弹一次（mtime 判定会重复弹）
                $sig = [string]::Join('|', @($d['time'], $d['turnIn'], $d['credit'], $d['cacheRate']))
                $changed = (-not $script:firstLoad) -and ($sig -ne $script:lastSig)
                $script:lastSig = $sig

                if ($d.ContainsKey('time')) { $script:TimeText.Text = $d['time'] }
                if ($d.ContainsKey('turnIn')) { $script:TurnInVal.Text = $d['turnIn'] }
                if ($d.ContainsKey('turnOut')) { $script:TurnOutVal.Text = $d['turnOut'] }
                if ($d.ContainsKey('cacheRate')) { $script:CacheVal.Text = $d['cacheRate'] + '%' }
                if ($d.ContainsKey('credit')) { $script:CreditVal.Text = $d['credit'] }

                $today = if ($d.ContainsKey('todayCredit')) { $d['todayCredit'] } else { '—' }
                $sess = if ($d.ContainsKey('todaySessions')) { $d['todaySessions'] } else { '0' }
                $script:TodayValue.Text = $today
                $script:TodaySessions.Text = "$sess 会话"

                if ($changed) {
                    Show-BubbleFor 6
                    Start-Bounce
                }
                $script:firstLoad = $false
            }
        }

        # 页脚只用于临时消息（换形象进度等），平时隐藏
        $hasStatus = $false
        if (Test-Path -LiteralPath $script:StatusFile) {
            try {
                $status = (Get-Content -LiteralPath $script:StatusFile -Raw -Encoding UTF8).Trim()
                if ($status) {
                    $script:FooterText.Text = $status
                    $hasStatus = $true
                }
            } catch { }
        }
        $script:FooterText.Visibility = if ($hasStatus) { 'Visible' } else { 'Collapsed' }
    } catch { }
}

$script:dataTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:dataTimer.Interval = [TimeSpan]::FromMilliseconds($IntervalMs)
$script:dataTimer.Add_Tick({ Update-Panel })
$script:dataTimer.Start()

# ---- skin switching -------------------------------------------------------
function Start-SkinSwitch([string]$skinId) {
    try {
        if (-not (Test-Path -LiteralPath $script:SwitchScript)) { return }
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'node'
        $psi.Arguments = '"' + $script:SwitchScript + '" ' + $skinId
        $psi.WorkingDirectory = $script:HookDir
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
        [void][System.Diagnostics.Process]::Start($psi)
        Show-BubbleFor 8
    } catch { }
}

$script:skinPoll = New-Object System.Windows.Threading.DispatcherTimer
$script:skinPoll.Interval = [TimeSpan]::FromMilliseconds(900)
$script:skinPoll.Add_Tick({
    try {
        $cur = Get-CurrentSkin
        if ($cur.id -and $cur.id -ne $script:loadedSkin) {
            $dir = Get-FramesDir $cur.id
            if ($dir) {
                $script:loadedSkin = $cur.id
                Load-Frames $dir
                Show-BubbleFor 4
                Start-Bounce
            }
        }
    } catch {
        try {
            $log = Join-Path $script:HookDir 'panel-err.log'
            Add-Content -LiteralPath $log -Value ("[{0}] skinPoll: {1}" -f (Get-Date -Format 's'), $_.Exception.Message) -Encoding UTF8
        } catch { }
    }
})
$script:skinPoll.Start()

# ---- drag / context menu --------------------------------------------------
$script:Mascot.Add_MouseLeftButtonDown({
    param($sender, $e)
    if ($e.ButtonState -eq [System.Windows.Input.MouseButtonState]::Pressed) {
        try { $win.DragMove() } catch { }
    }
})

$menu = New-Object System.Windows.Controls.ContextMenu

$miRefresh = New-Object System.Windows.Controls.MenuItem
$miRefresh.Header = 'Refresh'
$miRefresh.Add_Click({ Update-Panel })
[void]$menu.Items.Add($miRefresh)

$miSkin = New-Object System.Windows.Controls.MenuItem
$miSkin.Header = '换形象'
$script:skinItems = @{}
foreach ($s in (Get-SkinCatalogue)) {
    $item = New-Object System.Windows.Controls.MenuItem
    $item.Header = $s.name
    $item.Tag = $s.id
    if ($s.id -eq $script:loadedSkin) {
        $item.IsChecked = $true
        $item.FontWeight = [System.Windows.FontWeights]::Bold
    }
    $item.Add_Click({
        param($sender, $e)
        foreach ($k in $script:skinItems.Keys) {
            $script:skinItems[$k].IsChecked = $false
            $script:skinItems[$k].FontWeight = [System.Windows.FontWeights]::Normal
        }
        $sender.IsChecked = $true
        $sender.FontWeight = [System.Windows.FontWeights]::Bold
        Start-SkinSwitch $sender.Tag
    })
    $script:skinItems[$s.id] = $item
    [void]$miSkin.Items.Add($item)
}
if ($miSkin.Items.Count -gt 0) {
    [void]$menu.Items.Add($miSkin)
    [void]$menu.Items.Add((New-Object System.Windows.Controls.Separator))
}

$miHide = New-Object System.Windows.Controls.MenuItem
$miHide.Header = 'Hide for 10 min'
$miHide.Add_Click({
    $script:hideUntil = (Get-Date).AddMinutes(10)
    $win.Hide()
})
[void]$menu.Items.Add($miHide)

[void]$menu.Items.Add((New-Object System.Windows.Controls.Separator))

$miExit = New-Object System.Windows.Controls.MenuItem
$miExit.Header = 'Exit'
$miExit.Add_Click({ $win.Close() })
[void]$menu.Items.Add($miExit)

$script:Mascot.ContextMenu = $menu

# ---- lifecycle: follow the WorkBuddy main window --------------------------
function Test-WorkBuddyVisible {
    try {
        $procs = Get-Process -Name 'WorkBuddy' -ErrorAction SilentlyContinue
        if (-not $procs) { return $false }
        foreach ($p in $procs) {
            if ($p.MainWindowHandle -ne [IntPtr]::Zero) { return $true }
        }
        return $false
    } catch {
        return $true
    }
}

$script:hideUntil = [DateTime]::MinValue
$script:lifeTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:lifeTimer.Interval = [TimeSpan]::FromSeconds(3)
$script:lifeTimer.Add_Tick({
    $now = Get-Date
    if ($now -lt $script:hideUntil) { return }
    $vis = Test-WorkBuddyVisible
    if ($vis) {
        if (-not $win.IsVisible) { $win.Show() }
    } else {
        if ($win.IsVisible) { $win.Hide() }
    }
})
$script:lifeTimer.Start()

Update-Panel
[void]$win.ShowDialog()
