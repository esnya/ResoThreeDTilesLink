param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('discover', 'repl', 'send-json', 'benchmark-send', 'cleanup-slot')]
    [string]$Command,

    [Parameter(Position = 1)]
    [string]$LinkHost = 'localhost',

    [Nullable[int]]$Port = $null,

    [string]$SessionId,

    [string]$SessionName,

    [ValidateRange(1, 120)]
    [int]$DiscoveryTimeoutSec = 12,

    [switch]$NoBuild,

    [string]$Json,

    [string]$JsonFile,

    [int]$TimeoutSec = 15,

    [switch]$Compact,

    [int]$MeshCount = 8,

    [string]$Parallelism = '1,2,4,8',

    [string]$SlotId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-DiscoveredResoniteLinkSessions {
    param(
        [int]$TimeoutSec
    )

    $announcePort = 12512
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $sessionsById = @{}
    $listener = [System.Net.Sockets.UdpClient]::new()

    try {
        $listener.Client.ExclusiveAddressUse = $false
        $listener.Client.SetSocketOption(
            [System.Net.Sockets.SocketOptionLevel]::Socket,
            [System.Net.Sockets.SocketOptionName]::ReuseAddress,
            $true)
        $listener.Client.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, $announcePort))

        while ([DateTime]::UtcNow -lt $deadline) {
            $remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
            if (-not $listener.Client.Poll($remaining * 1000, [System.Net.Sockets.SelectMode]::SelectRead)) {
                continue
            }

            $remoteEndPoint = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
            $payload = $listener.Receive([ref]$remoteEndPoint)
            if ($null -eq $payload -or $payload.Length -eq 0) {
                continue
            }

            try {
                $announcement = [System.Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
            }
            catch {
                continue
            }

            if ($null -eq $announcement.sessionID -or [string]::IsNullOrWhiteSpace([string]$announcement.sessionID)) {
                continue
            }

            if ([int]$announcement.linkPort -lt 0) {
                $sessionsById.Remove([string]$announcement.sessionID)
                continue
            }

            $sessionsById[[string]$announcement.sessionID] = [pscustomobject]@{
                SessionName      = [string]$announcement.sessionName
                SessionId        = [string]$announcement.sessionID
                LinkPort         = [int]$announcement.linkPort
                AnnounceAddress  = $remoteEndPoint.Address.IPAddressToString
                LastSeenUtc      = [DateTime]::UtcNow
            }
        }
    }
    finally {
        $listener.Dispose()
    }

    return @($sessionsById.Values | Sort-Object SessionName, SessionId)
}

function Resolve-ResoniteLinkTarget {
    if ($null -ne $Port) {
        return [pscustomobject]@{
            Host = $LinkHost
            Port = $Port.Value
            Session = $null
        }
    }

    $sessions = Get-DiscoveredResoniteLinkSessions -TimeoutSec $DiscoveryTimeoutSec

    if (-not [string]::IsNullOrWhiteSpace($SessionId)) {
        $sessions = @($sessions | Where-Object { $_.SessionId -eq $SessionId })
    }

    if (-not [string]::IsNullOrWhiteSpace($SessionName)) {
        $sessions = @($sessions | Where-Object { $_.SessionName -eq $SessionName })
    }

    if ($sessions.Count -eq 0) {
        throw "No Resonite Link session was discovered within ${DiscoveryTimeoutSec}s. Start Resonite, enable Resonite Link, or pass -Port explicitly."
    }

    if ($sessions.Count -gt 1) {
        $choices = ($sessions | ForEach-Object {
            "SessionName='$($_.SessionName)' SessionId='$($_.SessionId)' Port=$($_.LinkPort) Address=$($_.AnnounceAddress)"
        }) -join [Environment]::NewLine
        throw "Multiple Resonite Link sessions were discovered. Re-run with -SessionId, -SessionName, or -Port.`n$choices"
    }

    return [pscustomobject]@{
        Host = $LinkHost
        Port = $sessions[0].LinkPort
        Session = $sessions[0]
    }
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

if ($Command -eq 'discover') {
    $sessions = Get-DiscoveredResoniteLinkSessions -TimeoutSec $DiscoveryTimeoutSec
    if ($sessions.Count -eq 0) {
        throw "No Resonite Link session was discovered within ${DiscoveryTimeoutSec}s."
    }

    $sessions | Select-Object SessionName, SessionId, LinkPort, AnnounceAddress, LastSeenUtc | Format-Table -AutoSize
    return
}

$target = Resolve-ResoniteLinkTarget
$resolvedHost = $target.Host
$resolvedPort = $target.Port

$projectPath = switch ($Command) {
    'discover' { $null }
    'repl' { Join-Path $repoRoot 'tools/ResoniteRepl/ResoniteRepl.csproj' }
    'send-json' { Join-Path $repoRoot 'tools/ResoniteRawJson/ResoniteRawJson.csproj' }
    'benchmark-send' { Join-Path $repoRoot 'tools/ResoniteSendBenchmark/ResoniteSendBenchmark.csproj' }
    'cleanup-slot' { Join-Path $repoRoot 'tools/ResoniteSendBenchmark/ResoniteSendBenchmark.csproj' }
    default { throw "Unsupported command: $Command" }
}

$dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    $fallbackDotnet = 'C:\Program Files\dotnet\dotnet.exe'
    if (-not (Test-Path $fallbackDotnet)) {
        throw 'dotnet.exe was not found. Install the Windows .NET SDK or add dotnet.exe to PATH.'
    }

    $dotnet = $fallbackDotnet
}
else {
    $dotnet = $dotnetCommand.Source
}

$dotnetArgs = @(
    'run'
    '--project'
    $projectPath
)

if ($NoBuild) {
    $dotnetArgs += '--no-build'
}

$dotnetArgs += '--'

switch ($Command) {
    'send-json' {
        if ([string]::IsNullOrWhiteSpace($Json) -eq [string]::IsNullOrWhiteSpace($JsonFile)) {
            throw 'For send-json, specify exactly one of -Json or -JsonFile.'
        }

        $dotnetArgs += '--host'
        $dotnetArgs += $resolvedHost
        $dotnetArgs += '--port'
        $dotnetArgs += $resolvedPort.ToString()
        $dotnetArgs += '--timeout-sec'
        $dotnetArgs += $TimeoutSec.ToString()

        if ([string]::IsNullOrWhiteSpace($Json) -eq $false) {
            $dotnetArgs += '--json'
            $dotnetArgs += $Json
        }
        else {
            $dotnetArgs += '--json-file'
            $dotnetArgs += $JsonFile
        }

        if ($Compact) {
            $dotnetArgs += '--compact'
        }
        else {
            $dotnetArgs += '--pretty'
        }
        break
    }

    'benchmark-send' {
        $dotnetArgs += '--host'
        $dotnetArgs += $resolvedHost
        $dotnetArgs += '--port'
        $dotnetArgs += $resolvedPort.ToString()
        $dotnetArgs += '--mesh-count'
        $dotnetArgs += $MeshCount.ToString()
        $dotnetArgs += '--parallelism'
        $dotnetArgs += $Parallelism
        break
    }

    'cleanup-slot' {
        if ([string]::IsNullOrWhiteSpace($SlotId)) {
            throw 'For cleanup-slot, specify -SlotId.'
        }

        $dotnetArgs += '--host'
        $dotnetArgs += $resolvedHost
        $dotnetArgs += '--port'
        $dotnetArgs += $resolvedPort.ToString()
        $dotnetArgs += '--remove-slot-id'
        $dotnetArgs += $SlotId
        break
    }

    default {
        $dotnetArgs += $resolvedHost
        $dotnetArgs += $resolvedPort.ToString()
        break
    }
}

if ($target.Session -ne $null) {
    Write-Host "Auto-discovered session '$($target.Session.SessionName)' ($($target.Session.SessionId)) on port $resolvedPort via $($target.Session.AnnounceAddress)."
}

Write-Host "Running $Command against ws://$resolvedHost`:$resolvedPort/"
Write-Host "Project: $projectPath"

$process = Start-Process -FilePath $dotnet -ArgumentList $dotnetArgs -Wait -NoNewWindow -PassThru
$exitCode = $process.ExitCode

if ($exitCode -ne 0) {
    throw "dotnet run failed with exit code $exitCode."
}
