using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using static RoboConfigurator.Core.SystemInfo;

namespace RoboConfigurator.Core
{
    /// <summary>Rapport du poste : matériel, Windows, réseau et résultat de chaque étape.</summary>
    public static class Report
    {
        private static string Gb(object bytes) => $"{Convert.ToDouble(bytes) / (1L << 30):0} {Lang.T("GB", "Go")}";
        private static string WmiDate(object value) => Lang.Date(ManagementDateTimeConverter.ToDateTime(Str(value)));
        private static string Unknown => Lang.T("unknown", "inconnu");

        /// <summary>Résultat du bloc, ou <paramref name="fallback"/> si l'information n'est pas disponible sur ce poste.</summary>
        private static T Safe<T>(Func<T> block, T fallback)
        {
            try { var r = block(); return r == null || r.ToString() == "" ? fallback : r; }
            catch (Exception) { return fallback; }
        }

        /// <summary>Ligne « libellé : valeur » alignée sur 16 caractères.</summary>
        private static string Row(string en, string fr, string value) =>
            $"  {Lang.T(en, fr),-16}: {value}";

        public static List<string> Build(string pendingName, OemKey oemKey, IEnumerable<string> stepStatus)
        {
            var os = Wmi("SELECT * FROM Win32_OperatingSystem").First();
            var cs = Wmi("SELECT * FROM Win32_ComputerSystem").First();
            var bios = Wmi("SELECT * FROM Win32_BIOS").First();
            var board = Safe(() => Wmi("SELECT * FROM Win32_BaseBoard").First(), null);
            const string cv = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

            string tpm = Safe(() =>
            {
                var t = Wmi("SELECT SpecVersion FROM Win32_Tpm", @"root\cimv2\Security\MicrosoftTpm").FirstOrDefault();
                string version = t == null ? null : Str(t["SpecVersion"]).Split(',')[0].Trim();
                return t == null ? Lang.T("none", "absent") : Lang.T($"present, version {version}", $"présent, version {version}");
            }, Unknown);
            string rename = string.IsNullOrEmpty(pendingName) ? ""
                          : Lang.T($" -> {pendingName} (after restart)", $" -> {pendingName} (après redémarrage)");

            var r = new List<string>
            {
                $"{Lang.T("PC REPORT", "RAPPORT DU POSTE")} - {Lang.Date(DateTime.Now)} {DateTime.Now:HH:mm}",
                "",
                Lang.T("PC", "POSTE"),
                Row("Name", "Nom", Environment.MachineName + rename),
                Row("Manufacturer", "Fabricant", $"{Str(cs["Manufacturer"])} {Str(cs["Model"])}"),
                Row("Serial number", "N° de série", Str(bios["SerialNumber"])),
                Row("Motherboard", "Carte mère", board == null ? Lang.T("unknown", "inconnue") : $"{Str(board["Manufacturer"])} {Str(board["Product"])}"),
                Row("BIOS", "BIOS", $"{Str(bios["SMBIOSBIOSVersion"])} {Lang.T("from", "du")} {Safe(() => WmiDate(bios["ReleaseDate"]), Unknown)}"),
                Row("Secure Boot", "Secure Boot", Safe(SystemInfo.SecureBoot, Unknown)),
                Row("TPM", "TPM", tpm),
                "",
                "WINDOWS",
                Row("Edition", "Édition", $"{Str(os["Caption"])} {Str(Registry.GetValue(cv, "DisplayVersion", ""))} (build {Str(os["BuildNumber"])}.{Str(Registry.GetValue(cv, "UBR", ""))})"),
                Row("Activated", "Activé", Safe(() => IsWindowsActivated() ? Lang.T("yes", "oui") : Lang.T("no", "non"), Unknown)),
                Row("OEM key (BIOS)", "Clé OEM (BIOS)", oemKey != null ? Lang.T($"present ({oemKey.Description})", $"présente ({oemKey.Description})") : Lang.T("none", "aucune")),
                Row("Installed on", "Installé le", Safe(() => WmiDate(os["InstallDate"]), Unknown)),
                "",
                Lang.T("PROCESSOR", "PROCESSEUR"),
            };
            foreach (var cpu in Wmi("SELECT * FROM Win32_Processor"))
                r.Add($"  {Str(cpu["Name"])} - {cpu["NumberOfCores"]} {Lang.T("cores", "cœurs")} / {cpu["NumberOfLogicalProcessors"]} threads");

            r.Add("");
            r.Add(Lang.T("MEMORY", "MÉMOIRE"));
            var sticks = Wmi("SELECT * FROM Win32_PhysicalMemory").ToList();
            string slots = Safe(() => Wmi("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => Convert.ToInt32(a["MemoryDevices"])).ToString(), "?");
            r.Add(Row("Total", "Total", Lang.T($"{Gb(cs["TotalPhysicalMemory"])} ({sticks.Count} module(s) in {slots} slot(s))",
                                               $"{Gb(cs["TotalPhysicalMemory"])} ({sticks.Count} barrette(s) sur {slots} emplacement(s))")));
            foreach (var m in sticks)
            {
                object speed = Convert.ToUInt32(m["ConfiguredClockSpeed"] ?? 0) > 0 ? m["ConfiguredClockSpeed"] : m["Speed"];
                r.Add($"  - {Str(m["DeviceLocator"])} : {Gb(m["Capacity"])} {Lang.T("at", "à")} {speed} MHz ({Str(m["Manufacturer"])} {Str(m["PartNumber"])})");
            }

            r.Add("");
            r.Add(Lang.T("STORAGE", "STOCKAGE"));
            foreach (var d in Safe(() => Wmi("SELECT * FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage").OrderBy(x => Str(x["DeviceId"])).ToList(), new List<ManagementObject>()))
                r.Add($"  - {Str(d["FriendlyName"])} : {Gb(d["Size"])}, {MediaType(d["MediaType"])} {BusType(d["BusType"])}, {Lang.T("health", "santé")} {Health(d["HealthStatus"])}");
            foreach (var v in Wmi("SELECT * FROM Win32_LogicalDisk WHERE DriveType = 3").OrderBy(x => Str(x["DeviceID"])))
            {
                string letter = Str(v["DeviceID"]);
                string bitlocker = Safe(() => Wmi($"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{letter}'",
                    @"root\cimv2\Security\MicrosoftVolumeEncryption").Any(e => Convert.ToUInt32(e["ProtectionStatus"]) == 1) ? Lang.T(", BitLocker on", ", BitLocker activé") : "", "");
                r.Add(Lang.T($"  {letter} {Gb(v["FreeSpace"])} free of {Gb(v["Size"])} ({Str(v["FileSystem"])}{bitlocker})",
                             $"  {letter} {Gb(v["FreeSpace"])} libres sur {Gb(v["Size"])} ({Str(v["FileSystem"])}{bitlocker})"));
            }

            r.Add("");
            r.Add(Lang.T("GRAPHICS", "CARTE GRAPHIQUE"));
            foreach (var g in Wmi("SELECT Name, DriverVersion FROM Win32_VideoController"))
                r.Add($"  {Str(g["Name"])} ({Lang.T("driver", "pilote")} {Str(g["DriverVersion"])})");

            r.Add("");
            r.Add(Lang.T("NETWORK", "RÉSEAU"));
            // Équivalent de Get-NetAdapter -Physical
            var adapters = Safe(() => Wmi("SELECT * FROM MSFT_NetAdapter WHERE ConnectorPresent = TRUE", @"root\StandardCimv2").OrderBy(n => Str(n["Name"])).ToList(), new List<ManagementObject>());
            foreach (var n in adapters)
            {
                string state = Lang.T("disconnected", "déconnecté");
                if (Convert.ToUInt32(n["MediaConnectState"]) == 1)
                {
                    string ip = Safe(() => string.Join(", ", Wmi($"SELECT IPAddress FROM MSFT_NetIPAddress WHERE InterfaceIndex = {n["InterfaceIndex"]} AND AddressFamily = 2", @"root\StandardCimv2").Select(a => Str(a["IPAddress"]))), "-");
                    state = Lang.T($"connected {LinkSpeed(n["Speed"])}, IP {ip}", $"connecté {LinkSpeed(n["Speed"])}, IP {ip}");
                }
                r.Add($"  - {Str(n["InterfaceDescription"])} : MAC {Mac(Str(n["PermanentAddress"]))}, {state}");
            }

            r.Add("");
            r.Add(Lang.T("CONFIGURATOR", "CONFIGURATEUR"));
            r.AddRange(stepStatus.Select(s => "  " + s));
            return r;
        }

        private static string MediaType(object v)
        {
            switch (Convert.ToInt32(v)) { case 3: return "HDD"; case 4: return "SSD"; case 5: return "SCM"; default: return Lang.T("unknown type", "type inconnu"); }
        }

        private static string BusType(object v)
        {
            switch (Convert.ToInt32(v))
            {
                case 1: return "SCSI"; case 3: return "ATA"; case 7: return "USB"; case 8: return "RAID";
                case 10: return "SAS"; case 11: return "SATA"; case 12: return "SD"; case 14: return Lang.T("virtual", "virtuel"); case 17: return "NVMe";
                default: return "";
            }
        }

        private static string Health(object v)
        {
            switch (Convert.ToInt32(v))
            {
                case 0: return Lang.T("good", "bonne");
                case 1: return Lang.T("warning", "avertissement");
                case 2: return Lang.T("BAD", "MAUVAISE");
                default: return Lang.T("unknown", "inconnue");
            }
        }

        private static string LinkSpeed(object bitsPerSecond)
        {
            double bps = Convert.ToDouble(bitsPerSecond);
            return bps >= 1e9 ? $"{bps / 1e9:0.#} Gbps" : $"{bps / 1e6:0.#} Mbps";
        }

        private static string Mac(string raw) =>
            raw.Length == 12 ? string.Join("-", Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2))) : raw;
    }
}
