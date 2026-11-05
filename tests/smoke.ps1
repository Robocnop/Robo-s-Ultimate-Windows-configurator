# Test de fumée : charge l'exe sans ouvrir de fenêtre et vérifie les options de lancement et les traductions.
# Usage : powershell -ExecutionPolicy Bypass -File tests\smoke.ps1 [-Exe dist\RoboConfigurator.exe]
# Lancé dans un processus à part : l'assembly chargée reste verrouillée jusqu'à la fin du processus.
param([string]$Exe = (Join-Path $PSScriptRoot '..\dist\RoboConfigurator.exe'))
$ErrorActionPreference = 'Stop'

$asm = [Reflection.Assembly]::LoadFile((Resolve-Path $Exe).Path)
$lang = $asm.GetType('RoboConfigurator.Core.Lang')
$options = $asm.GetType('RoboConfigurator.Core.Options')
$fr = 'Termin' + [char]0xE9 + 'e'   # « Terminée » sans caractère accentué dans le script
$o = $options::Parse([string[]]@('-Lang', 'fr', '-Skip', 'Office,Taskbar', '-Simulation'))
if ($o.Errors.Count) { throw "Options refusées : $($o.Errors -join '; ')" }
if (-not $lang::French) { throw '-Lang fr ignoré' }
if (($o.Skip -join ',') -ne 'Office,Taskbar') { throw "Skip inattendu : $($o.Skip -join ',')" }
if ($lang::T('Done', $fr) -ne $fr) { throw 'Traduction française incorrecte' }
$o = $options::Parse([string[]]@('-Lang', 'en'))
if ($lang::T('Done', $fr) -ne 'Done') { throw 'Traduction anglaise incorrecte' }
$o = $options::Parse([string[]]@('-Inconnue'))
if ($o.Errors.Count -ne 1) { throw 'Une option inconnue doit être signalée' }
$o = $options::Parse([string[]]@('-Apps', 'VLC,7zip,inconnue'))
if (($o.Apps -join ',') -ne 'vlc,7zip' -or $o.Errors.Count -ne 1) { throw "-Apps inattendu : $($o.Apps -join ',') / $($o.Errors.Count) erreur(s)" }
$updates = $asm.GetType('RoboConfigurator.Core.Updates')
if (-not $updates::IsNewer('2.6.0', '2.6.0-beta.1') -or $updates::IsNewer('2.5.0', '2.6.0')) { throw 'Comparaison de versions incorrecte' }
Write-Host 'Test de fumée OK'
