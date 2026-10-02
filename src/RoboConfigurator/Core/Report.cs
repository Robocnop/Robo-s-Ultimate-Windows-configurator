using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using static RoboConfigurator.Core.SystemInfo;

namespace RoboConfigurator.Core
{
    /// <summary>Rapport du poste : matériel, Windows, réseau et résultat de chaque étape.</summary>
    public static class Report
    {
        private static string Gb(object bytes) => $"{Convert.ToDouble(bytes) / (1L << 30):0} Go";
        private static string Date(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        private static string WmiDate(object value) => Date(ManagementDateTimeConverter.ToDateTime(Str(value)));

        /// <summary>Résultat du bloc, ou <paramref name="fallback"/> si l'information n'est pas disponible sur ce poste.</summary>
        private static T Safe<T>(Func<T> block, T fallback)
        {
            try { var r = block(); return r == null || r.ToString() == "" ? fallback : r; }
            catch (Exception) { return fallback; }
        }

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
                return t == null ? "absent" : $"présent, version {Str(t["SpecVersion"]).Split(',')[0].Trim()}";
            }, "inconnu");

            var r = new List<string>
            {
                $"RAPPORT DU POSTE - {Date(DateTime.Now)} {DateTime.Now:HH:mm}",
                "",
                "POSTE",
                $"  Nom             : {Environment.MachineName}{(string.IsNullOrEmpty(pendingName) ? "" : $" -> {pendingName} (après redémarrage)")}",
                $"  Fabricant       : {Str(cs["Manufacturer"])} {Str(cs["Model"])}",
                $"  N° de série     : {Str(bios["SerialNumber"])}",
                $"  Carte mère      : {(board == null ? "inconnue" : $"{Str(board["Manufacturer"])} {Str(board["Product"])}")}",
                $"  BIOS            : {Str(bios["SMBIOSBIOSVersion"])} du {Safe(() => WmiDate(bios["ReleaseDate"]), "inconnu")}",
                $"  Secure Boot     : {Safe(SystemInfo.SecureBoot, "inconnu")}",
                $"  TPM             : {tpm}",
                "",
                "WINDOWS",
                $"  Édition         : {Str(os["Caption"])} {Str(Registry.GetValue(cv, "DisplayVersion", ""))} (build {Str(os["BuildNumber"])}.{Str(Registry.GetValue(cv, "UBR", ""))})",
                $"  Activé          : {Safe(() => IsWindowsActivated() ? "oui" : "non", "inconnu")}",
                $"  Clé OEM (BIOS)  : {(oemKey != null ? $"présente ({oemKey.Description})" : "aucune")}",
                $"  Installé le     : {Safe(() => WmiDate(os["InstallDate"]), "inconnu")}",
                "",
                "PROCESSEUR",
            };
            foreach (var cpu in Wmi("SELECT * FROM Win32_Processor"))
                r.Add($"  {Str(cpu["Name"])} - {cpu["NumberOfCores"]} cœurs / {cpu["NumberOfLogicalProcessors"]} threads");

            r.Add("");
            r.Add("MÉMOIRE");
            var sticks = Wmi("SELECT * FROM Win32_PhysicalMemory").ToList();
            string slots = Safe(() => Wmi("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => Convert.ToInt32(a["MemoryDevices"])).ToString(), "?");
            r.Add($"  Total           : {Gb(cs["TotalPhysicalMemory"])} ({sticks.Count} barrette(s) sur {slots} emplacement(s))");
            foreach (var m in sticks)
            {
                object speed = Convert.ToUInt32(m["ConfiguredClockSpeed"] ?? 0) > 0 ? m["ConfiguredClockSpeed"] : m["Speed"];
                r.Add($"  - {Str(m["DeviceLocator"])} : {Gb(m["Capacity"])} à {speed} MHz ({Str(m["Manufacturer"])} {Str(m["PartNumber"])})");
            }

            r.Add("");
            r.Add("STOCKAGE");
            foreach (var d in Safe(() => Wmi("SELECT * FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage").OrderBy(x => Str(x["DeviceId"])).ToList(), new List<ManagementObject>()))
                r.Add($"  - {Str(d["FriendlyName"])} : {Gb(d["Size"])}, {MediaType(d["MediaType"])} {BusType(d["BusType"])}, santé {Health(d["HealthStatus"])}");
            foreach (var v in Wmi("SELECT * FROM Win32_LogicalDisk WHERE DriveType = 3").OrderBy(x => Str(x["DeviceID"])))
            {
                string letter = Str(v["DeviceID"]);
                string bitlocker = Safe(() => Wmi($"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{letter}'",
                    @"root\cimv2\Security\MicrosoftVolumeEncryption").Any(e => Convert.ToUInt32(e["ProtectionStatus"]) == 1) ? ", BitLocker activé" : "", "");
                r.Add($"  {letter} {Gb(v["FreeSpace"])} libres sur {Gb(v["Size"])} ({Str(v["FileSystem"])}{bitlocker})");
            }

            r.Add("");
            r.Add("CARTE GRAPHIQUE");
            foreach (var g in Wmi("SELECT Name, DriverVersion FROM Win32_VideoController"))
                r.Add($"  {Str(g["Name"])} (pilote {Str(g["DriverVersion"])})");

            r.Add("");
            r.Add("RÉSEAU");
            // Équivalent de Get-NetAdapter -Physical
            var adapters = Safe(() => Wmi("SELECT * FROM MSFT_NetAdapter WHERE ConnectorPresent = TRUE", @"root\StandardCimv2").OrderBy(n => Str(n["Name"])).ToList(), new List<ManagementObject>());
            foreach (var n in adapters)
            {
                string state = "déconnecté";
                if (Convert.ToUInt32(n["MediaConnectState"]) == 1)
                {
                    string ip = Safe(() => string.Join(", ", Wmi($"SELECT IPAddress FROM MSFT_NetIPAddress WHERE InterfaceIndex = {n["InterfaceIndex"]} AND AddressFamily = 2", @"root\StandardCimv2").Select(a => Str(a["IPAddress"]))), "-");
                    state = $"connecté {LinkSpeed(n["Speed"])}, IP {ip}";
                }
                r.Add($"  - {Str(n["InterfaceDescription"])} : MAC {Mac(Str(n["PermanentAddress"]))}, {state}");
            }

            r.Add("");
            r.Add("CONFIGURATEUR");
            r.AddRange(stepStatus.Select(s => "  " + s));
            return r;
        }

        private static string MediaType(object v)
        {
            switch (Convert.ToInt32(v)) { case 3: return "HDD"; case 4: return "SSD"; case 5: return "SCM"; default: return "type inconnu"; }
        }

        private static string BusType(object v)
        {
            switch (Convert.ToInt32(v))
            {
                case 1: return "SCSI"; case 3: return "ATA"; case 7: return "USB"; case 8: return "RAID";
                case 10: return "SAS"; case 11: return "SATA"; case 12: return "SD"; case 14: return "virtuel"; case 17: return "NVMe";
                default: return "";
            }
        }

        private static string Health(object v)
        {
            switch (Convert.ToInt32(v)) { case 0: return "bonne"; case 1: return "avertissement"; case 2: return "MAUVAISE"; default: return "inconnue"; }
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
