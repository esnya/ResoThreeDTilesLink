param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('inspect', 'probe', 'send-json', 'benchmark-send', 'cleanup-slot')]
    [string]$Command,

    [Parameter(Position = 1)]
    [string]$LinkHost = 'localhost',

    [Parameter(Position = 2)]
    [int]$Port = 49379,

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

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

$projectPath = switch ($Command) {
    'inspect' { Join-Path $repoRoot 'tools/ResoniteInspect/ResoniteInspect.csproj' }
    'probe' { Join-Path $repoRoot 'tools/ResoniteProbe/ResoniteProbe.csproj' }
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
        $dotnetArgs += $LinkHost
        $dotnetArgs += '--port'
        $dotnetArgs += $Port.ToString()
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
        $dotnetArgs += $LinkHost
        $dotnetArgs += '--port'
        $dotnetArgs += $Port.ToString()
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
        $dotnetArgs += $LinkHost
        $dotnetArgs += '--port'
        $dotnetArgs += $Port.ToString()
        $dotnetArgs += '--remove-slot-id'
        $dotnetArgs += $SlotId
        break
    }

    default {
        $dotnetArgs += $LinkHost
        $dotnetArgs += $Port.ToString()
        break
    }
}

Write-Host "Running $Command against ws://$LinkHost`:$Port/"
Write-Host "Project: $projectPath"

$process = Start-Process -FilePath $dotnet -ArgumentList $dotnetArgs -Wait -NoNewWindow -PassThru
$exitCode = $process.ExitCode

if ($exitCode -ne 0) {
    throw "dotnet run failed with exit code $exitCode."
}
