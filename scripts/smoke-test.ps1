$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '..\src\GlassDesk\GlassDesk.csproj'
$exe = Join-Path $PSScriptRoot '..\src\GlassDesk\bin\Release\net8.0-windows10.0.22621.0\GlassDesk.exe'

dotnet build $project --configuration Release
if (-not (Test-Path -LiteralPath $exe)) {
    throw "构建输出不存在：$exe"
}

$process = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -PassThru
try {
    if (-not $process.WaitForExit(10000)) {
        throw "GlassDesk smoke test 未在 10 秒内正常退出"
    }
    if ($process.ExitCode -ne 0) {
        throw "GlassDesk smoke test 退出码为 $($process.ExitCode)"
    }
    Write-Output "PASS: GlassDesk started and exited through OnExit"
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
}
