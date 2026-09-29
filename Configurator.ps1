#Requires -Version 5.1
<#
.SYNOPSIS
    Robo's Ultimate Windows Configurator - configuration post-installation de Windows 10/11.

.DESCRIPTION
    Télécharge puis enchaîne :
      1. Ninite        : installation des logiciels de base
      2. CrapFixer     : tweaks recommandés + thème sombre + suppression des applis inutiles
      3. Edge          : désinstallation de Microsoft Edge (WebView2 conservé)
      4. Office        : installation d'Office
      5. Activation    : vérification de l'activation de Windows
    Au démarrage, un assistant demande tout (nom du PC, étapes, tweaks, clé Windows, redémarrage) ;
    une fois validé, le script tourne seul, renomme le PC, écrit le rapport du poste et redémarre.

    Les étapes terminées sont mémorisées : relancer le script reprend là où il s'était arrêté.

.PARAMETER Skip
    Étapes à décocher par défaut dans l'assistant : Ninite, CrapFixer, Edge, Office, Activation (séparées par des virgules).

.PARAMETER AllTweaks
    Pré-sélectionne TOUS les tweaks de CrapFixer au lieu des seuls tweaks recommandés.

.PARAMETER WindowsKey
    Clé produit Windows pré-remplie dans l'assistant, installée si Windows n'est pas déjà activé.

.PARAMETER Reset
    Oublie les étapes déjà faites et la configuration enregistrée, et recommence tout.

.EXAMPLE
    .\Configurator.ps1
.EXAMPLE
    .\Configurator.ps1 -Skip Office,Activation -AllTweaks
#>
[CmdletBinding()]
param(
    [string[]]$Skip = @(),
    [switch]$AllTweaks,
    [string]$WindowsKey,
    [switch]$Reset
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # sinon Invoke-WebRequest est très lent sous PowerShell 5.1

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
$WorkDir = Join-Path $env:SystemDrive 'RoboConfigurator'
$StepNames = @('Ninite', 'CrapFixer', 'Edge', 'Office', 'Activation')
$Downloads = [ordered]@{
    Ninite    = @{ Url = 'https://nextcloud.robocnop.freeddns.org:4444/s/Cg9d7m58fWtJY8n/download'; File = 'Ninite.exe' }
    CrapFixer = @{ Url = 'https://nextcloud.robocnop.freeddns.org:4444/s/Czayj7x7AmdAsJj/download'; File = 'CrapFixer.zip' }
    Office    = @{ Url = 'https://nextcloud.robocnop.freeddns.org:4444/s/AabmSfHFBTR6e6J/download'; File = 'OfficeSetup.exe' }
}

# -File transmet "-Skip a,b" comme une seule chaîne : on la redécoupe
$Skip = @($Skip | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($s in $Skip) {
    if ($StepNames -notcontains $s) { throw "Étape inconnue pour -Skip : '$s'. Valeurs possibles : $($StepNames -join ', ')" }
}

# ---------------------------------------------------------------------------
# Élévation administrateur
# ---------------------------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Skip)       { $argList += " -Skip $($Skip -join ',')" }
    if ($AllTweaks)  { $argList += ' -AllTweaks' }
    if ($WindowsKey) { $argList += " -WindowsKey $WindowsKey" }
    if ($Reset)      { $argList += ' -Reset' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$Host.UI.RawUI.WindowTitle = "Robo's Ultimate Windows Configurator"
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
Start-Transcript -Path (Join-Path $WorkDir 'journal.txt') -Append | Out-Null

$StateFile  = Join-Path $WorkDir 'etapes-terminees.txt'
$ConfigFile = Join-Path $WorkDir 'configuration.json'
if ($Reset) { Remove-Item $StateFile, $ConfigFile -Force -ErrorAction SilentlyContinue }

$script:IsWin11 = [int](Get-CimInstance Win32_OperatingSystem).BuildNumber -ge 22000

# ---------------------------------------------------------------------------
# Affichage
# ---------------------------------------------------------------------------
function Write-Title([string]$Text) { Write-Host ''; Write-Host "=== $Text ===" -ForegroundColor Cyan }
function Write-Ok([string]$Text)    { Write-Host "  [OK] $Text" -ForegroundColor Green }
function Write-Info([string]$Text)  { Write-Host "  $Text" }
function Write-Warn([string]$Text)  { Write-Host "  [!] $Text" -ForegroundColor Yellow }
function Write-Fail([string]$Text)  { Write-Host "  [ERREUR] $Text" -ForegroundColor Red }

# ---------------------------------------------------------------------------
# Suivi des étapes
# ---------------------------------------------------------------------------
function Test-StepDone([string]$Name) {
    (Test-Path $StateFile) -and (@(Get-Content $StateFile) -contains $Name)
}
function Set-StepDone([string]$Name) { Add-Content -Path $StateFile -Value $Name }

# ---------------------------------------------------------------------------
# Configuration de départ : tout est demandé ici, ensuite le script tourne seul
# ---------------------------------------------------------------------------
function Test-PcName([string]$Name) {
    $Name -match '^[A-Za-z0-9-]{1,15}$' -and $Name -notmatch '^\d+$'
}

function Save-Config($Config) { $Config | ConvertTo-Json | Set-Content -Path $ConfigFile -Encoding UTF8 }

function Read-Config {
    $c = Get-Content $ConfigFile -Raw | ConvertFrom-Json
    [ordered]@{
        NewName     = [string]$c.NewName
        Steps       = @($c.Steps | Where-Object { $StepNames -contains $_ })
        AllTweaks   = [bool]$c.AllTweaks
        WindowsKey  = [string]$c.WindowsKey
        AutoRestart = [bool]$c.AutoRestart
    }
}

function Show-Config($Config) {
    $name  = if ($Config.NewName) { "$env:COMPUTERNAME -> $($Config.NewName)" } else { "$env:COMPUTERNAME (inchangé)" }
    $steps = ($StepNames | ForEach-Object { if ($Config.Steps -contains $_) { "[x] $_" } else { "[ ] $_" } }) -join '  '
    $key   = if ($Config.WindowsKey) { "$($Config.WindowsKey.Substring(0, 5))-..." } else { 'aucune' }
    Write-Host ''
    Write-Host "  1. Nom du PC             : $name"
    Write-Host "  2. Étapes                : $steps"
    Write-Host "  3. Tweaks CrapFixer      : $(if ($Config.AllTweaks) { 'tous' } else { 'recommandés' })"
    Write-Host "  4. Clé Windows           : $key"
    Write-Host "  5. Redémarrage à la fin  : $(if ($Config.AutoRestart) { 'automatique' } else { 'non' })"
}

function Edit-Config($Config) {
    while ($true) {
        Write-Title 'Configuration'
        Show-Config $Config
        Write-Host ''
        $choice = (Read-Host 'Numéro à modifier, ou Entrée pour valider').Trim()
        switch ($choice) {
            '' {
                if ((Read-Host "C'est parti ? Le script va tourner seul jusqu'à la fin (O/N)") -match '^[oOyY]') { return $Config }
            }
            '1' {
                $n = (Read-Host 'Nouveau nom (1 à 15 caractères : lettres, chiffres, tirets ; vide = garder le nom actuel)').Trim()
                if (-not $n -or $n -eq $env:COMPUTERNAME) { $Config.NewName = '' }
                elseif (Test-PcName $n) { $Config.NewName = $n.ToUpper() }
                else { Write-Warn 'Nom invalide, pas de changement.' }
            }
            '2' {
                while ($true) {
                    Write-Host ''
                    for ($i = 0; $i -lt $StepNames.Count; $i++) {
                        $mark = if ($Config.Steps -contains $StepNames[$i]) { 'x' } else { ' ' }
                        Write-Host "    $($i + 1). [$mark] $($StepNames[$i])"
                    }
                    $s = (Read-Host '  Numéro à cocher/décocher, ou Entrée pour revenir').Trim()
                    if (-not $s) { break }
                    if ($s -match '^\d+$' -and [int]$s -ge 1 -and [int]$s -le $StepNames.Count) {
                        $step = $StepNames[[int]$s - 1]
                        $Config.Steps = if ($Config.Steps -contains $step) { @($Config.Steps | Where-Object { $_ -ne $step }) }
                                        else { @($StepNames | Where-Object { $Config.Steps -contains $_ -or $_ -eq $step }) }
                    }
                }
            }
            '3' { $Config.AllTweaks = -not $Config.AllTweaks }
            '4' {
                $k = (Read-Host 'Clé Windows XXXXX-XXXXX-XXXXX-XXXXX-XXXXX (vide = aucune)').Trim().ToUpper()
                if (-not $k) { $Config.WindowsKey = '' }
                elseif ($k -match '^([A-Z0-9]{5}-){4}[A-Z0-9]{5}$') { $Config.WindowsKey = $k }
                else { Write-Warn 'Format de clé invalide, pas de changement.' }
            }
            '5' { $Config.AutoRestart = -not $Config.AutoRestart }
            default { Write-Warn 'Choix inconnu.' }
        }
    }
}

# ---------------------------------------------------------------------------
# Téléchargement
# ---------------------------------------------------------------------------
function Get-Payload([string]$Name) {
    $item = $Downloads[$Name]
    $path = Join-Path $WorkDir $item.File
    if ((Test-Path $path) -and (Get-Item $path).Length -gt 0) {
        Write-Ok "$($item.File) déjà téléchargé"
        return $path
    }
    for ($try = 1; $try -le 3; $try++) {
        try {
            Write-Info "Téléchargement de $($item.File) (essai $try/3)..."
            Invoke-WebRequest -Uri $item.Url -OutFile "$path.part" -UseBasicParsing
            Move-Item "$path.part" $path -Force
            Unblock-File $path
            Write-Ok "$($item.File) ($([math]::Round((Get-Item $path).Length / 1MB, 1)) Mo)"
            return $path
        } catch {
            Write-Warn "Échec : $($_.Exception.Message)"
            Start-Sleep -Seconds 5
        }
    }
    throw "Impossible de télécharger $($item.File)"
}

# ---------------------------------------------------------------------------
# CrapFixer : lecture des fichiers .ini et application des tweaks
# ---------------------------------------------------------------------------
function Read-CrapFixerIni([string]$Path) {
    $sections = New-Object System.Collections.Generic.List[object]
    $current = $null
    foreach ($raw in Get-Content -LiteralPath $Path -Encoding UTF8) {
        $line = $raw.Trim()
        if ($line -eq '' -or $line.StartsWith(';')) { continue }
        if ($line -match '^\[(.+)\]$') {
            $current = [ordered]@{ Name = $Matches[1] }
            $sections.Add($current)
            continue
        }
        if ($current -and $line -match '^([^=]+)=(.*)$') {
            # "DefaultSelected=true   ; recommended" -> commentaire en fin de ligne
            $current[$Matches[1].Trim()] = ($Matches[2] -replace '\s+;.*$', '').Trim()
        }
    }
    $sections
}

function Set-RegistryEntry([string]$Path, [string]$Name, [string]$Type, [string]$Value) {
    $hive, $subKey = $Path -split '\\', 2
    $base = switch ($hive) {
        { $_ -in 'HKEY_CURRENT_USER', 'HKCU' }  { [Microsoft.Win32.Registry]::CurrentUser }
        { $_ -in 'HKEY_LOCAL_MACHINE', 'HKLM' } { [Microsoft.Win32.Registry]::LocalMachine }
        { $_ -in 'HKEY_CLASSES_ROOT', 'HKCR' }  { [Microsoft.Win32.Registry]::ClassesRoot }
        { $_ -in 'HKEY_USERS', 'HKU' }          { [Microsoft.Win32.Registry]::Users }
        default { throw "Ruche de registre inconnue : $hive" }
    }
    if ($Name -eq '(Default)') { $Name = '' }

    if ($Value -eq '<delete>') { $base.DeleteSubKeyTree($subKey, $false); return }
    if ($Value -eq '<deletevalue>') {
        $key = $base.OpenSubKey($subKey, $true)
        if ($key) { $key.DeleteValue($Name, $false); $key.Close() }
        return
    }

    $key = $base.CreateSubKey($subKey)
    try {
        switch ($Type) {
            'DWORD'  { $key.SetValue($Name, [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32]$Value), 0), 'DWord') }
            'QWord'  { $key.SetValue($Name, [BitConverter]::ToInt64([BitConverter]::GetBytes([uint64]$Value), 0), 'QWord') }
            'String' { $key.SetValue($Name, $Value, 'String') }
            'Binary' {
                $bytes = [byte[]]@($Value -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { [Convert]::ToByte($_, 16) })
                $key.SetValue($Name, $bytes, 'Binary')
            }
            default  { throw "Type de valeur inconnu : $Type" }
        }
    } finally {
        $key.Close()
    }
}

function Invoke-Tweak($Tweak) {
    # Une entrée peut modifier plusieurs valeurs : Path, Path2, Path3...
    for ($i = 1; ; $i++) {
        $n = if ($i -eq 1) { '' } else { "$i" }
        if (-not $Tweak.Contains("Path$n")) { break }
        $type = if ($Tweak.Contains("ValueType$n")) { $Tweak["ValueType$n"] } else { 'DWORD' }
        Set-RegistryEntry $Tweak["Path$n"] $Tweak["ValueName$n"] $type $Tweak["RecommendedValue$n"]
    }
}

function Restart-Explorer {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    # Windows relance normalement l'explorateur tout seul
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
}

# ---------------------------------------------------------------------------
# Rapport du poste
# ---------------------------------------------------------------------------
function Get-GB([double]$Bytes) { '{0:0} Go' -f ($Bytes / 1GB) }
function Format-Date([datetime]$Date) { $Date.ToString('dd/MM/yyyy', [Globalization.CultureInfo]::InvariantCulture) }

# Renvoie le résultat du bloc, ou $Default si l'information n'est pas disponible sur ce poste
function Get-OrDefault([scriptblock]$Block, $Default = 'inconnu') {
    try { $r = & $Block; if ($null -ne $r -and "$r" -ne '') { $r } else { $Default } } catch { $Default }
}

function New-Report([string]$PendingName, [string[]]$StepStatus) {
    $os    = Get-CimInstance Win32_OperatingSystem
    $cs    = Get-CimInstance Win32_ComputerSystem
    $bios  = Get-CimInstance Win32_BIOS
    $board = Get-CimInstance Win32_BaseBoard
    $cv    = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'

    $activated = Get-OrDefault {
        $lic = Get-CimInstance SoftwareLicensingProduct -Filter "ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL" |
            Sort-Object LicenseStatus | Select-Object -First 1
        if ($lic.LicenseStatus -eq 1) { 'oui' } else { 'non' }
    }
    $secureBoot = try { if (Confirm-SecureBootUEFI) { 'activé' } else { 'désactivé' } }
                  catch [System.PlatformNotSupportedException] { 'non pris en charge (BIOS hérité)' }
                  catch { 'inconnu' }
    $tpm = try {
        $t = Get-CimInstance -Namespace 'root\cimv2\Security\MicrosoftTpm' -ClassName Win32_Tpm
        if ($t) { "présent, version $(($t.SpecVersion -split ',')[0].Trim())" } else { 'absent' }
    } catch { 'inconnu' }

    $r = New-Object System.Collections.Generic.List[string]
    $r.Add("RAPPORT DU POSTE - $(Format-Date (Get-Date)) $(Get-Date -Format 'HH:mm')")
    $r.Add('')
    $r.Add('POSTE')
    $r.Add("  Nom             : $env:COMPUTERNAME$(if ($PendingName) { " -> $PendingName (après redémarrage)" })")
    $r.Add("  Fabricant       : $($cs.Manufacturer) $($cs.Model)")
    $r.Add("  N° de série     : $($bios.SerialNumber)")
    $r.Add("  Carte mère      : $($board.Manufacturer) $($board.Product)")
    $r.Add("  BIOS            : $($bios.SMBIOSBIOSVersion) du $(Get-OrDefault { Format-Date $bios.ReleaseDate })")
    $r.Add("  Secure Boot     : $secureBoot")
    $r.Add("  TPM             : $tpm")
    $r.Add('')
    $r.Add('WINDOWS')
    $r.Add("  Édition         : $($os.Caption) $($cv.DisplayVersion) (build $($os.BuildNumber).$($cv.UBR))")
    $r.Add("  Activé          : $activated")
    $r.Add("  Installé le     : $(Format-Date $os.InstallDate)")
    $r.Add('')
    $r.Add('PROCESSEUR')
    foreach ($cpu in Get-CimInstance Win32_Processor) {
        $r.Add("  $($cpu.Name.Trim()) - $($cpu.NumberOfCores) cœurs / $($cpu.NumberOfLogicalProcessors) threads")
    }
    $r.Add('')
    $r.Add('MÉMOIRE')
    $sticks = @(Get-CimInstance Win32_PhysicalMemory)
    $slots  = Get-OrDefault { (Get-CimInstance Win32_PhysicalMemoryArray | Measure-Object MemoryDevices -Sum).Sum } '?'
    $r.Add("  Total           : $(Get-GB $cs.TotalPhysicalMemory) ($($sticks.Count) barrette(s) sur $slots emplacement(s))")
    foreach ($m in $sticks) {
        $speed = if ($m.ConfiguredClockSpeed) { $m.ConfiguredClockSpeed } else { $m.Speed }
        $r.Add("  - $($m.DeviceLocator) : $(Get-GB $m.Capacity) à $speed MHz ($("$($m.Manufacturer)".Trim()) $("$($m.PartNumber)".Trim()))")
    }
    $r.Add('')
    $r.Add('STOCKAGE')
    foreach ($d in Get-OrDefault { @(Get-PhysicalDisk | Sort-Object DeviceId) } @()) {
        $r.Add("  - $($d.FriendlyName) : $(Get-GB $d.Size), $($d.MediaType) $($d.BusType), santé $($d.HealthStatus)")
    }
    foreach ($v in Get-Volume | Where-Object { $_.DriveLetter -and $_.DriveType -eq 'Fixed' } | Sort-Object DriveLetter) {
        $bl = Get-OrDefault { if ((Get-BitLockerVolume -MountPoint "$($v.DriveLetter):").ProtectionStatus -eq 'On') { ', BitLocker activé' } else { '' } } ''
        $r.Add("  $($v.DriveLetter): $(Get-GB $v.SizeRemaining) libres sur $(Get-GB $v.Size) ($($v.FileSystem)$bl)")
    }
    $r.Add('')
    $r.Add('CARTE GRAPHIQUE')
    foreach ($g in Get-CimInstance Win32_VideoController) {
        $r.Add("  $($g.Name) (pilote $($g.DriverVersion))")
    }
    $r.Add('')
    $r.Add('RÉSEAU')
    foreach ($n in Get-OrDefault { @(Get-NetAdapter -Physical | Sort-Object Name) } @()) {
        $ip = Get-OrDefault { (Get-NetIPAddress -InterfaceIndex $n.ifIndex -AddressFamily IPv4 -ErrorAction Stop).IPAddress -join ', ' } '-'
        $state = if ($n.Status -eq 'Up') { "connecté $($n.LinkSpeed), IP $ip" } else { 'déconnecté' }
        $r.Add("  - $($n.InterfaceDescription) : MAC $($n.MacAddress), $state")
    }
    $r.Add('')
    $r.Add('CONFIGURATEUR')
    foreach ($s in $StepStatus) { $r.Add("  $s") }
    $r
}

# ---------------------------------------------------------------------------
# Étapes
# ---------------------------------------------------------------------------
$Steps = [ordered]@{
    Ninite = {
        Write-Info 'Ninite installe les logiciels automatiquement.'
        Write-Warn 'Quand il affiche "Done", clique sur "Close" pour passer à la suite.'
        Start-Process -FilePath (Join-Path $WorkDir $Downloads.Ninite.File) -Wait
    }

    CrapFixer = {
        $dest = Join-Path $WorkDir 'CrapFixer'
        Expand-Archive -Path (Join-Path $WorkDir $Downloads.CrapFixer.File) -DestinationPath $dest -Force
        $tweakIni = Get-ChildItem $dest -Recurse -Filter 'Wintweak2.ini' | Select-Object -First 1
        $appIni   = Get-ChildItem $dest -Recurse -Filter 'Winappx.ini'   | Select-Object -First 1
        if (-not $tweakIni -or -not $appIni) { throw "Wintweak2.ini ou Winappx.ini introuvable dans l'archive CrapFixer" }

        # Tweaks registre : ceux cochés par défaut dans CrapFixer (ou tous avec -AllTweaks) + thème sombre
        $tweaks = Read-CrapFixerIni $tweakIni.FullName | Where-Object {
            ($AllTweaks -or $_.DefaultSelected -eq 'true' -or $_.Name -eq 'Enable Dark Theme') -and
            (-not $_.Contains('OsVersion') -or ($_.OsVersion -eq '11') -eq $script:IsWin11)
        }
        $restartExplorer = $false
        $errors = 0
        foreach ($t in $tweaks) {
            try {
                Invoke-Tweak $t
                Write-Ok $t.Name
                if ($t.Restart -eq 'explorer') { $restartExplorer = $true }
            } catch {
                $errors++
                Write-Fail "$($t.Name) : $($_.Exception.Message)"
            }
        }

        # Applications préinstallées cochées par défaut dans CrapFixer
        foreach ($app in Read-CrapFixerIni $appIni.FullName | Where-Object { $_.DefaultSelected -eq 'true' }) {
            $packages = @(Get-AppxPackage -Name $app.PackageName -ErrorAction SilentlyContinue)
            if (-not $packages) { continue }
            try {
                $packages | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
                Write-Ok "Appli supprimée : $($app.Name)"
            } catch {
                $errors++
                Write-Fail "Suppression de $($app.Name) : $($_.Exception.Message)"
            }
        }

        if ($restartExplorer) { Write-Info "Redémarrage de l'explorateur..."; Restart-Explorer }
        if ($errors) { throw "$errors élément(s) CrapFixer en échec (voir ci-dessus)" }
    }

    Edge = {
        # Même méthode que WinUtil (Chris Titus) : un faux MicrosoftEdge.exe dans l'ancien dossier
        # d'Edge débloque le désinstalleur officiel. WebView2 (utilisé par Office, Teams...) est conservé.
        $edgeDir = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application"
        $setup = Get-ChildItem "$edgeDir\*\Installer\setup.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName | Select-Object -Last 1
        if (-not $setup) { Write-Ok "Edge n'est pas installé"; return }

        Get-Process msedge -ErrorAction SilentlyContinue | Stop-Process -Force
        New-Item -ItemType File -Force -Path "$env:SystemRoot\SystemApps\Microsoft.MicrosoftEdge_8wekyb3d8bbwe\MicrosoftEdge.exe" | Out-Null
        Write-Info 'Désinstallation de Microsoft Edge...'
        Start-Process -FilePath $setup.FullName -ArgumentList '--uninstall --system-level --force-uninstall --delete-profile' -Wait

        if (Test-Path "$edgeDir\msedge.exe") { throw "Edge est toujours présent après la désinstallation" }
        Write-Ok 'Edge désinstallé'
    }

    Office = {
        $word = @(
            "$env:ProgramFiles\Microsoft Office\root\Office16\WINWORD.EXE",
            "${env:ProgramFiles(x86)}\Microsoft Office\root\Office16\WINWORD.EXE"
        )
        if ($word | Where-Object { Test-Path $_ }) { Write-Ok 'Office est déjà installé'; return }

        Write-Info "Installation d'Office (cela peut prendre un moment)..."
        Start-Process -FilePath (Join-Path $WorkDir $Downloads.Office.File) -Wait
        # Le programme d'installation peut rendre la main avant la fin réelle
        $deadline = (Get-Date).AddMinutes(60)
        while (-not ($word | Where-Object { Test-Path $_ }) -or (Get-Process OfficeSetup -ErrorAction SilentlyContinue)) {
            if ((Get-Date) -gt $deadline) { throw "Office ne semble pas installé après 60 minutes" }
            Start-Sleep -Seconds 15
        }
        Write-Ok 'Office installé'
    }

    Activation = {
        $windowsAppId = '55c92734-d682-4d71-983e-d6ec3f16059f'
        $getLicense = {
            Get-CimInstance SoftwareLicensingProduct -Filter "ApplicationID='$windowsAppId' AND PartialProductKey IS NOT NULL" |
                Sort-Object LicenseStatus | Select-Object -First 1
        }
        $slmgr = Join-Path $env:SystemRoot 'System32\slmgr.vbs'

        if ((& $getLicense).LicenseStatus -ne 1 -and $WindowsKey) {
            Write-Info 'Installation de la clé Windows fournie...'
            cscript.exe //nologo $slmgr /ipk $WindowsKey
            cscript.exe //nologo $slmgr /ato
        }
        if ((& $getLicense).LicenseStatus -eq 1) {
            Write-Ok 'Windows est activé'
        } else {
            Write-Warn "Windows n'est pas activé : ouverture des paramètres d'activation."
            Start-Process 'ms-settings:activation'
        }
        Write-Info 'Office : ouvre Word et connecte-toi avec le compte Microsoft lié à ta licence.'
    }
}

# ---------------------------------------------------------------------------
# Programme principal
# ---------------------------------------------------------------------------
Write-Host "Robo's Ultimate Windows Configurator" -ForegroundColor Magenta
Write-Host "Windows $(if ($script:IsWin11) { '11' } else { '10' }) - dossier de travail : $WorkDir"

# Configuration : reprise de la précédente si le script a été interrompu, sinon assistant
$config = $null
if (Test-Path $ConfigFile) {
    try {
        $config = Read-Config
        Write-Title 'Configuration du lancement précédent'
        Show-Config $config
        Write-Host ''
        if ((Read-Host 'Reprendre avec cette configuration ? (O/N)') -notmatch '^[oOyY]') { $config = Edit-Config $config }
    } catch {
        Write-Warn "Configuration précédente illisible : $($_.Exception.Message)"
        $config = $null
    }
}
if (-not $config) {
    $config = Edit-Config ([ordered]@{
        NewName     = ''
        Steps       = @($StepNames | Where-Object { $Skip -notcontains $_ })
        AllTweaks   = [bool]$AllTweaks
        WindowsKey  = [string]$WindowsKey
        AutoRestart = $true
    })
}
Save-Config $config
$Skip       = @($StepNames | Where-Object { $config.Steps -notcontains $_ })
$AllTweaks  = $config.AllTweaks
$WindowsKey = $config.WindowsKey

$failed = New-Object System.Collections.Generic.List[string]
$todo = @($StepNames | Where-Object { $Skip -notcontains $_ -and -not (Test-StepDone $_) })

Write-Title 'Téléchargements'
foreach ($name in $todo | Where-Object { $Downloads.Contains($_) }) {
    try { Get-Payload $name | Out-Null }
    catch { Write-Fail $_.Exception.Message; $failed.Add($name) }
}

foreach ($name in $StepNames) {
    Write-Title $name
    if ($Skip -contains $name)   { Write-Info 'Non cochée dans la configuration'; continue }
    if (Test-StepDone $name)     { Write-Ok "Déjà faite lors d'un lancement précédent"; continue }
    if ($failed.Contains($name)) { Write-Warn 'Ignorée : le téléchargement a échoué'; continue }
    try {
        & $Steps[$name]
        Set-StepDone $name
        Write-Ok "$name terminé"
    } catch {
        Write-Fail $_.Exception.Message
        $failed.Add($name)
    }
}

Write-Title 'Résumé'
if ($failed.Count) {
    Write-Warn "Étapes en échec : $($failed -join ', '). Relance le script pour les réessayer."
} else {
    Write-Ok 'Tout est terminé.'
}
Write-Info "Journal : $(Join-Path $WorkDir 'journal.txt')"
$pendingName = $null
if ($config.NewName -and $config.NewName -ne $env:COMPUTERNAME) {
    Write-Title 'Nom du PC'
    try {
        Rename-Computer -NewName $config.NewName -Force -ErrorAction Stop -WarningAction SilentlyContinue
        Write-Ok "Le PC s'appellera '$($config.NewName)' après le redémarrage"
        $pendingName = $config.NewName
    } catch {
        Write-Fail $_.Exception.Message
    }
}

Write-Title 'Rapport du poste'
try {
    $stepStatus = foreach ($name in $StepNames) {
        $state = if ($Skip -contains $name) { 'ignorée' }
                 elseif ($failed.Contains($name)) { 'EN ÉCHEC' }
                 elseif (Test-StepDone $name) { 'OK' }
                 else { 'non faite' }
        '{0,-15} : {1}' -f $name, $state
    }
    $report = New-Report -PendingName $pendingName -StepStatus $stepStatus
    $fileName = "Rapport-$(if ($pendingName) { $pendingName } else { $env:COMPUTERNAME })-$(Get-Date -Format 'yyyy-MM-dd').txt"
    $reportPath = Join-Path $WorkDir $fileName
    $report | Set-Content -Path $reportPath -Encoding UTF8
    Copy-Item $reportPath -Destination ([Environment]::GetFolderPath('Desktop')) -Force
    Write-Ok "Rapport enregistré sur le Bureau et dans $reportPath"
} catch {
    Write-Fail "Impossible de générer le rapport : $($_.Exception.Message)"
}

# Tout est fait : la configuration n'est plus utile (elle peut contenir la clé Windows)
if (-not $failed.Count) { Remove-Item $ConfigFile -Force -ErrorAction SilentlyContinue }

if ($config.AutoRestart) {
    Write-Title 'Redémarrage'
    Write-Info 'Le PC redémarre dans 60 secondes. Pour annuler : shutdown /a'
    Stop-Transcript | Out-Null
    shutdown.exe /r /t 60 /c "Robo's Ultimate Windows Configurator : redémarrage pour finaliser la configuration."
    Start-Sleep -Seconds 10
} else {
    Stop-Transcript | Out-Null
    Read-Host 'Terminé. Pense à redémarrer le PC pour finaliser. Entrée pour fermer' | Out-Null
}
