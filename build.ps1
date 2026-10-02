# Compile l'application en un seul exe portable : dist\RoboConfigurator.exe
# Usage : powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'

# SDK .NET : celui du système s'il existe, sinon une copie locale dans %USERPROFILE%\.dotnet (installée au besoin)
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -or -not (& $dotnet --list-sdks)) {
    $dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) {
        Write-Host 'Installation du SDK .NET 8 dans %USERPROFILE%\.dotnet...'
        $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
        Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $installer -UseBasicParsing
        & $installer -Channel 8.0 -InstallDir (Split-Path $dotnet) -NoPath
    }
}

$project = Join-Path $PSScriptRoot 'src\RoboConfigurator\RoboConfigurator.csproj'
& $dotnet build $project -c Release -nologo
if ($LASTEXITCODE) { throw 'La compilation a échoué' }

$dist = New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'dist')
Copy-Item (Join-Path $PSScriptRoot 'src\RoboConfigurator\bin\Release\net472\RoboConfigurator.exe') $dist -Force
$exe = Get-Item (Join-Path $dist 'RoboConfigurator.exe')
Write-Host ("OK : {0} ({1:0.0} Mo)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green
