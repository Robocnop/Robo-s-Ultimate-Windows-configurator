# Compile l'application en un seul exe portable : dist\RoboConfigurator.exe
# Usage : powershell -ExecutionPolicy Bypass -File build.ps1 [-Test]
#   -Test : lance aussi les tests unitaires (tests\RoboConfigurator.Tests) et le test de fumée (tests\smoke.ps1)
param([switch]$Test)
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

# L'exe fait environ 3 Mo : une dépendance ajoutée par erreur se voit tout de suite (fichiers de payloads\ intégrés non comptés)
$payloads = (Get-ChildItem (Join-Path $PSScriptRoot 'payloads') -File -Include *.exe, *.zip, *.cmd -Recurse -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
if (($exe.Length - $payloads) -gt 4MB) { throw ("L'exe dépasse 4 Mo hors fichiers intégrés ({0:0.0} Mo) : dépendance ajoutée ?" -f (($exe.Length - $payloads) / 1MB)) }

if ($Test) {
    & $dotnet test (Join-Path $PSScriptRoot 'tests\RoboConfigurator.Tests\RoboConfigurator.Tests.csproj') -c Release -nologo
    if ($LASTEXITCODE) { throw 'Les tests unitaires ont échoué' }
    # Processus à part : l'exe chargé par le test de fumée reste verrouillé jusqu'à la fin du processus
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'tests\smoke.ps1') -Exe $exe.FullName
    if ($LASTEXITCODE) { throw 'Le test de fumée a échoué' }
}
