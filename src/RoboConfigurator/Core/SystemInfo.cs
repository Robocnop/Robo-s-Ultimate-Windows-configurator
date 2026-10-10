using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace RoboConfigurator.Core
{
    public sealed class OemKey
    {
        public OemKey(string key, string description) { Key = key; Description = description; }
        public string Key { get; }
        public string Description { get; }
    }

    /// <summary>Lecture d'informations Windows (WMI, registre) et lancement de programmes.</summary>
    public static class SystemInfo
    {
        private const string WindowsAppId = "55c92734-d682-4d71-983e-d6ec3f16059f";

        public static IEnumerable<ManagementObject> Wmi(string query, string scope = @"root\cimv2")
        {
            using (var searcher = new ManagementObjectSearcher(scope, query))
                foreach (ManagementObject o in searcher.Get()) yield return o;
        }

        public static string Str(object value) => value?.ToString().Trim() ?? "";

        public static int BuildNumber => Environment.OSVersion.Version.Build;
        public static bool IsWin11 => BuildNumber >= 22000;

        public static string WindowsCaption()
        {
            string caption = Str(Wmi("SELECT Caption FROM Win32_OperatingSystem").First()["Caption"]);
            string display = Str(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", ""));
            return $"{caption.Replace("Microsoft ", "")} {display}".Trim();
        }

        public static string Model()
        {
            var cs = Wmi("SELECT Manufacturer, Model FROM Win32_ComputerSystem").First();
            return $"{Str(cs["Manufacturer"])} {Str(cs["Model"])}".Trim();
        }

        /// <summary>Clé Windows enregistrée dans le BIOS par le fabricant (PC de marque), ou null.</summary>
        public static OemKey GetOemKey()
        {
            try
            {
                var svc = Wmi("SELECT OA3xOriginalProductKey, OA3xOriginalProductKeyDescription FROM SoftwareLicensingService").First();
                string key = Str(svc["OA3xOriginalProductKey"]);
                return key.Length > 0 ? new OemKey(key, Str(svc["OA3xOriginalProductKeyDescription"])) : null;
            }
            catch (Exception) { return null; }
        }

        public static bool IsWindowsActivated() =>
            Wmi($"SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID='{WindowsAppId}' AND PartialProductKey IS NOT NULL")
                .Any(p => Convert.ToUInt32(p["LicenseStatus"]) == 1);

        public static void RenameComputer(string newName)
        {
            var scope = new ManagementScope(@"root\cimv2", new ConnectionOptions { EnablePrivileges = true });
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_ComputerSystem")))
            {
                var cs = searcher.Get().Cast<ManagementObject>().First();
                var input = cs.GetMethodParameters("Rename");
                input["Name"] = newName;
                uint code = Convert.ToUInt32(cs.InvokeMethod("Rename", input, null)["ReturnValue"]);
                if (code != 0) throw new Exception(Lang.T($"Windows refused the rename (code {code})", $"Windows a refusé le renommage (code {code})"));
            }
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetFirmwareType(out uint firmwareType);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        /// <summary>Supprime un fichier ou un dossier vide au prochain démarrage (s'il est en cours d'utilisation).</summary>
        public static void DeleteAtReboot(string path)
        {
            const int MOVEFILE_DELAY_UNTIL_REBOOT = 4;
            MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        }

        public static string SecureBoot()
        {
            if (GetFirmwareType(out uint type) && type == 1) return Lang.T("not supported (legacy BIOS)", "non pris en charge (BIOS hérité)");
            object value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null);
            if (value == null) return Lang.T("unknown", "inconnu");
            return Convert.ToInt32(value) == 1 ? Lang.T("enabled", "activé") : Lang.T("disabled", "désactivé");
        }

        /// <summary>Lance un programme sans fenêtre et transmet chaque ligne de sortie au journal.</summary>
        public static int Run(string file, string arguments, Action<string> onLine = null)
        {
            var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            var psi = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem,
            };
            using (var p = new Process { StartInfo = psi })
            {
                DataReceivedEventHandler handler = (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) (onLine ?? Log.Info)(e.Data.Trim());
                };
                p.OutputDataReceived += handler;
                p.ErrorDataReceived += handler;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        /// <summary>Lance un programme avec son interface et attend qu'il se ferme.</summary>
        public static int RunVisible(string file, string arguments = "")
        {
            using (var p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true }))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        /// <summary>Exécute une commande Windows PowerShell 5.1 (toujours présent) sans fenêtre.</summary>
        public static int PowerShell(string command, Action<string> onLine = null)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'; " + command));
            return Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", onLine);
        }
    }
}
