using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RoboConfigurator.Core
{
    /// <summary>Section d'un fichier .ini de CrapFixer : [Nom] suivi de clé=valeur.</summary>
    public sealed class IniSection
    {
        public IniSection(string name) { Name = name; }
        public string Name { get; }
        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string this[string key] => Values.TryGetValue(key, out var v) ? v : null;
        public bool Has(string key) => Values.ContainsKey(key);
        public bool DefaultSelected => string.Equals(this["DefaultSelected"], "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Lit Wintweak2.ini et Winappx.ini de CrapFixer et applique les entrées cochées par défaut dans l'appli.
    /// Mettre à jour l'archive CrapFixer sur le Nextcloud suffit donc pour mettre à jour les tweaks.
    /// </summary>
    public static class CrapFixer
    {
        public static List<IniSection> ReadIni(string path)
        {
            var sections = new List<IniSection>();
            IniSection current = null;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue;
                var header = Regex.Match(line, @"^\[(.+)\]$");
                if (header.Success)
                {
                    current = new IniSection(header.Groups[1].Value);
                    sections.Add(current);
                    continue;
                }
                var pair = Regex.Match(line, "^([^=]+)=(.*)$");
                if (current != null && pair.Success)
                {
                    // "DefaultSelected=true   ; recommended" -> commentaire en fin de ligne
                    current.Values[pair.Groups[1].Value.Trim()] = Regex.Replace(pair.Groups[2].Value, @"\s+;.*$", "").Trim();
                }
            }
            return sections;
        }

        /// <summary>Tweaks à appliquer : cochés par défaut (ou tous), + thème sombre, pour la bonne version de Windows.</summary>
        public static IEnumerable<IniSection> SelectTweaks(IEnumerable<IniSection> all, bool allTweaks) =>
            all.Where(t => (allTweaks || t.DefaultSelected || t.Name == "Enable Dark Theme") &&
                           (!t.Has("OsVersion") || (t["OsVersion"] == "11") == SystemInfo.IsWin11));

        /// <summary>Une entrée peut modifier plusieurs valeurs : Path, Path2, Path3...</summary>
        public static void ApplyTweak(IniSection tweak)
        {
            for (int i = 1; ; i++)
            {
                string n = i == 1 ? "" : i.ToString(CultureInfo.InvariantCulture);
                if (!tweak.Has("Path" + n)) break;
                SetRegistry(tweak["Path" + n], tweak["ValueName" + n], tweak["ValueType" + n] ?? "DWORD", tweak["RecommendedValue" + n]);
            }
        }

        private static void SetRegistry(string path, string name, string type, string value)
        {
            int sep = path.IndexOf('\\');
            string hive = sep < 0 ? path : path.Substring(0, sep);
            string subKey = sep < 0 ? "" : path.Substring(sep + 1);
            RegistryKey root;
            switch (hive.ToUpperInvariant())
            {
                case "HKEY_CURRENT_USER": case "HKCU": root = Registry.CurrentUser; break;
                case "HKEY_LOCAL_MACHINE": case "HKLM": root = Registry.LocalMachine; break;
                case "HKEY_CLASSES_ROOT": case "HKCR": root = Registry.ClassesRoot; break;
                case "HKEY_USERS": case "HKU": root = Registry.Users; break;
                default: throw new Exception(Lang.T($"Unknown registry hive: {hive}", $"Ruche de registre inconnue : {hive}"));
            }
            if (name == "(Default)") name = "";

            if (value == "<delete>") { root.DeleteSubKeyTree(subKey, false); return; }
            if (value == "<deletevalue>")
            {
                using (var key = root.OpenSubKey(subKey, true)) key?.DeleteValue(name ?? "", false);
                return;
            }

            using (var key = root.CreateSubKey(subKey))
            {
                switch (type.ToUpperInvariant())
                {
                    case "DWORD": key.SetValue(name, unchecked((int)(uint)ParseNumber(value)), RegistryValueKind.DWord); break;
                    case "QWORD": key.SetValue(name, unchecked((long)ParseNumber(value)), RegistryValueKind.QWord); break;
                    case "STRING": key.SetValue(name, value ?? "", RegistryValueKind.String); break;
                    case "BINARY":
                        var bytes = Regex.Split(value ?? "", @"[,\s]+").Where(b => b.Length > 0)
                                         .Select(b => Convert.ToByte(b, 16)).ToArray();
                        key.SetValue(name, bytes, RegistryValueKind.Binary);
                        break;
                    default: throw new Exception(Lang.T($"Unknown value type: {type}", $"Type de valeur inconnu : {type}"));
                }
            }
        }

        private static ulong ParseNumber(string value)
        {
            value = (value ?? "").Trim();
            return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.Parse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : ulong.Parse(value, CultureInfo.InvariantCulture);
        }

        /// <summary>Noms des paquets Appx installés pour l'utilisateur courant.</summary>
        public static List<string> InstalledAppxNames()
        {
            var names = new List<string>();
            int code = SystemInfo.PowerShell("Get-AppxPackage | ForEach-Object { $_.Name }", line => names.Add(line));
            if (code != 0) throw new Exception(Lang.T("Could not list the installed apps", "Impossible de lister les applications installées"));
            return names;
        }

        /// <summary>Même logique que Get-AppxPackage -Name : jokers * et ? autorisés.</summary>
        public static bool MatchesPackage(string pattern, string name) =>
            Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);

        public static void RemoveAppx(string packageName)
        {
            string quoted = packageName.Replace("'", "''");
            var errors = new List<string>();
            int code = SystemInfo.PowerShell($"Get-AppxPackage -Name '{quoted}' | Remove-AppxPackage -ErrorAction Stop", errors.Add);
            if (code != 0) throw new Exception(errors.FirstOrDefault() ?? $"code {code}");
        }
    }
}
