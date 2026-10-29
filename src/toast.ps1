param([Parameter(Mandatory=$true)][string]$MsgFile)

# NOTE: keep this file pure ASCII. Windows PowerShell 5.1 reads .ps1 without BOM
# as ANSI, so non-ASCII literals here would be corrupted. All display text is
# passed in via $MsgFile (UTF-8).

$ErrorActionPreference = 'Stop'

try {
    $raw = Get-Content -LiteralPath $MsgFile -Raw -Encoding UTF8
} catch {
    exit 1
}

$lines = @($raw -split "`r?`n" | Where-Object { $_ -ne '' })
if ($lines.Count -eq 0) { exit 0 }

$title = $lines[0]
if ($lines.Count -gt 1) {
    $body = ($lines[1..($lines.Count - 1)] -join "`n")
} else {
    $body = ''
}

function Esc([string]$s) {
    if ([string]::IsNullOrEmpty($s)) { return '' }
    $s = $s -replace '&', '&amp;'
    $s = $s -replace '<', '&lt;'
    $s = $s -replace '>', '&gt;'
    $s = $s -replace '"', '&quot;'
    return $s
}

$shown = $false

# Path 1: WinRT toast (modern, Windows 10/11)
try {
    [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
    [Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null

    $xmlString = @"
<toast activationType="foreground" duration="short">
  <visual>
    <binding template="ToastGeneric">
      <text>$(Esc $title)</text>
      <text>$(Esc $body)</text>
    </binding>
  </visual>
</toast>
"@

    $doc = New-Object Windows.Data.Xml.Dom.XmlDocument
    $doc.LoadXml($xmlString)
    $toast = New-Object Windows.UI.Notifications.ToastNotification($doc)
    $appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
    $shown = $true
} catch {
    $shown = $false
}

# Path 2: tray balloon fallback
if (-not $shown) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $ni = New-Object System.Windows.Forms.NotifyIcon
        $ni.Icon = [System.Drawing.SystemIcons]::Information
        $ni.Visible = $true
        $ni.ShowBalloonTip(6000, $title, $body, [System.Windows.Forms.ToolTipIcon]::None)
        Start-Sleep -Milliseconds 1200
        $ni.Dispose()
    } catch {
    }
}

exit 0
