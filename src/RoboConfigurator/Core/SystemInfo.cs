using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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

        /// <summary>Édition installée (registre EditionID) : Core = Famille, Professional = Pro, etc.</summary>
        public static string EditionId() =>
            Str(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", ""));

        public static bool IsAdministrator() =>
            new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        public static bool IsHomeEdition(string editionId) => editionId.StartsWith("Core", StringComparison.OrdinalIgnoreCase);
        public static bool IsProEdition(string editionId) => editionId.StartsWith("Professional", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Lance un programme sans fenêtre et transmet chaque ligne de sortie au journal. Un code de retour non nul est
        /// journalisé avec la commande (ou <paramref name="display"/>) et la durée, pour comprendre un échec.
        /// </summary>
        public static int Run(string file, string arguments, Action<string> onLine = null, string display = null)
        {
            int code = RunCore(file, arguments, onLine, out TimeSpan elapsed);
            if (code != 0)
                Log.Info(Lang.T($"{display ?? $"{Path.GetFileName(file)} {arguments}"} returned code {code} (0x{code:X8}) after {elapsed.TotalSeconds:0.#} s",
                                $"{display ?? $"{Path.GetFileName(file)} {arguments}"} a renvoyé le code {code} (0x{code:X8}) après {elapsed.TotalSeconds:0.#} s"));
            return code;
        }

        private static int RunCore(string file, string arguments, Action<string> onLine, out TimeSpan elapsed)
        {
            var start = DateTime.Now;
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
                elapsed = DateTime.Now - start;
                return p.ExitCode;
            }
        }

        public static int RunVisible(string file, string arguments = "")
        {
            var start = DateTime.Now;
            using (var p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true }))
            {
                p.WaitForExit();
                string name = Path.GetFileName(file);
                Log.Info(Lang.T($"{name} closed with code {p.ExitCode} (0x{p.ExitCode:X8}) after {(DateTime.Now - start).TotalSeconds:0} s",
                                $"{name} s'est fermé avec le code {p.ExitCode} (0x{p.ExitCode:X8}) après {(DateTime.Now - start).TotalSeconds:0} s"));
                return p.ExitCode;
            }
        }

        /// <summary>Exécute une commande Windows PowerShell 5.1 (toujours présent) sans fenêtre.</summary>
        public static int PowerShell(string command, Action<string> onLine = null)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'; " + command));
            return Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", onLine,
                       display: $"PowerShell « {command} »");
        }
    }
}
