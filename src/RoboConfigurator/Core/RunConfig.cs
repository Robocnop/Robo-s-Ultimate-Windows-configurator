using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RoboConfigurator.Core
{
    public static class StepNames
    {
        public const string Ninite = "Ninite";
        public const string CrapFixer = "CrapFixer";
        public const string Edge = "Edge";
        public const string Taskbar = "Taskbar";
        public const string Office = "Office";
        public const string Activation = "Activation";

        public static readonly string[] All = { Ninite, CrapFixer, Edge, Taskbar, Office, Activation };
    }

    /// <summary>Choix faits dans l'assistant, enregistrés pour reprendre après une interruption.</summary>
    [DataContract]
    public sealed class RunConfig
    {
        [DataMember] public string NewName { get; set; } = "";
        [DataMember] public string[] Steps { get; set; } = StepNames.All.ToArray();
        [DataMember] public bool AllTweaks { get; set; }
        /// <summary>Jamais écrite en clair : le fichier contient seulement <see cref="ProtectedWindowsKey"/>.</summary>
        public string WindowsKey { get; set; } = "";
        /// <summary>Clé chiffrée avec DPAPI (portée machine : lisible après un redémarrage, par n'importe quel compte administrateur).</summary>
        [DataMember(EmitDefaultValue = false)] private string ProtectedWindowsKey { get; set; }
        /// <summary>Clé en clair des fichiers écrits par les versions précédentes : lue, jamais réécrite.</summary>
        [DataMember(Name = "WindowsKey", EmitDefaultValue = false)] private string LegacyWindowsKey { get; set; }
        [DataMember] public bool AutoRestart { get; set; } = true;
        [DataMember] public bool CleanUp { get; set; } = true;
        /// <summary>Nettoyer même si une étape a échoué (les étapes en échec ne pourront plus être reprises).</summary>
        [DataMember] public bool CleanUpOnError { get; set; }
        /// <summary>Édition voulue pour l'activation : "" = garder l'actuelle, "Pro" ou "Home" (Famille).</summary>
        [DataMember] public string Edition { get; set; } = "";
        [DataMember] public string[] NiniteApps { get; set; } = Core.NiniteApps.Default.ToArray();

        public bool Has(string step) => Steps.Contains(step);

        public static bool IsValidPcName(string name) =>
            Regex.IsMatch(name, "^[A-Za-z0-9-]{1,15}$") && !Regex.IsMatch(name, @"^\d+$");

        public static bool IsValidKey(string key) => Regex.IsMatch(key, "^([A-Z0-9]{5}-){4}[A-Z0-9]{5}$");

        public static RunConfig Load(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var config = (RunConfig)new DataContractJsonSerializer(typeof(RunConfig)).ReadObject(stream);
                config.NewName = config.NewName ?? "";
                config.WindowsKey = UnprotectKey(config.ProtectedWindowsKey) ?? config.LegacyWindowsKey ?? "";
                config.ProtectedWindowsKey = config.LegacyWindowsKey = null;
                // Le fichier peut avoir été modifié à la main : mêmes contrôles que dans l'interface avant d'appeler WMI ou slmgr
                if (config.NewName.Length > 0 && !IsValidPcName(config.NewName))
                {
                    Log.Warn(Lang.T($"Invalid PC name in the saved settings, ignored: “{config.NewName}”", $"Nom du PC invalide dans la configuration enregistrée, ignoré : « {config.NewName} »"));
                    config.NewName = "";
                }
                if (config.WindowsKey.Length > 0 && !IsValidKey(config.WindowsKey))
                {
                    Log.Warn(Lang.T("Invalid Windows key in the saved settings, ignored", "Clé Windows invalide dans la configuration enregistrée, ignorée"));
                    config.WindowsKey = "";
                }
                config.Edition = Editions.Normalize(config.Edition) ?? "";
                config.Steps = StepNames.All.Where(s => (config.Steps ?? new string[0]).Contains(s)).ToArray();
                // Absent des configurations antérieures à la v2.6.0
                config.NiniteApps = config.NiniteApps == null ? Core.NiniteApps.Default.ToArray() : Core.NiniteApps.Normalize(config.NiniteApps);
                return config;
            }
        }

        /// <summary>
        /// Écrit d'abord un fichier temporaire, puis le met à la place de l'ancien : une coupure pendant l'écriture
        /// laisse l'ancienne configuration intacte au lieu d'un fichier tronqué.
        /// </summary>
        public void Save(string path)
        {
            string temp = path + ".tmp";
            ProtectedWindowsKey = string.IsNullOrEmpty(WindowsKey) ? null : ProtectKey(WindowsKey);
            LegacyWindowsKey = null;
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(RunConfig)).WriteObject(stream, this);
                ProtectedWindowsKey = null;
                using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Position = 0;
                    stream.CopyTo(file);
                    file.Flush(true);
                }
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("RoboConfigurator.WindowsKey");

        private static string ProtectKey(string key) =>
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), KeyEntropy, DataProtectionScope.LocalMachine));

        /// <summary>null si absente ; vide si elle ne peut pas être déchiffrée (fichier copié depuis un autre PC...).</summary>
        private static string UnprotectKey(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), KeyEntropy, DataProtectionScope.LocalMachine)); }
            catch (Exception ex)
            {
                Log.Warn(Lang.T($"The saved Windows key cannot be decrypted ({ex.Message}): enter it again", $"La clé Windows enregistrée ne peut pas être déchiffrée ({ex.Message}) : la saisir à nouveau"));
                return "";
            }
        }

        /// <summary>Met de côté un fichier de configuration illisible (gardé pour comprendre) ; renvoie son nouveau nom.</summary>
        public static string SetAsideUnreadable(string path)
        {
            string aside = path + Lang.T(".unreadable", ".illisible");
            if (File.Exists(aside)) File.Delete(aside);
            File.Move(path, aside);
            return aside;
        }
    }

    public static class Editions
    {
        public const string Keep = "";
        public const string Pro = "Pro";
        public const string Home = "Home";

        /// <summary>"pro", "home", "famille"... -> constante, "" -> Keep, null si inconnu.</summary>
        public static string Normalize(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "": case "keep": return Keep;
                case "pro": case "professional": return Pro;
                case "home": case "famille": case "core": return Home;
                default: return null;
            }
        }
    }

    /// <summary>Étapes réussies, mémorisées dans etapes-terminees.txt (une par ligne).</summary>
    public sealed class StepState
    {
        private readonly string _file;
        public StepState(string file) { _file = file; }

        // Trim : une ligne écrite à moitié lors d'une coupure ne correspond à aucune étape et est ignorée
        public bool IsDone(string step) => File.Exists(_file) && File.ReadAllLines(_file).Select(l => l.Trim()).Contains(step);
        public void MarkDone(string step)
        {
            // Après une ligne coupée, l'étape doit commencer une nouvelle ligne pour être reconnue
            bool newLine = File.Exists(_file) && new FileInfo(_file).Length > 0 && !File.ReadAllText(_file).EndsWith("\n");
            File.AppendAllText(_file, (newLine ? Environment.NewLine : "") + step + Environment.NewLine);
        }
    }

    public sealed class Options
    {
        public List<string> Skip { get; } = new List<string>();
        public bool AllTweaks { get; private set; }
        public string WindowsKey { get; private set; } = "";
        public bool Reset { get; private set; }
        public bool Simulation { get; private set; }
        public bool ForceCleanUp { get; private set; }
        public string Edition { get; private set; } = Editions.Keep;
        public string[] Apps { get; private set; } = NiniteApps.Default.ToArray();
        public List<string> Errors { get; } = new List<string>();

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i].TrimStart('-', '/').ToLowerInvariant();
                string Next() => i + 1 < args.Length ? args[++i] : "";
                switch (name)
                {
                    case "skip":
                        foreach (var s in Next().Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
                        {
                            var step = StepNames.All.FirstOrDefault(n => n.Equals(s, StringComparison.OrdinalIgnoreCase));
                            if (step != null) o.Skip.Add(step);
                            else o.Errors.Add(Lang.T($"Unknown step for -Skip: “{s}”. Possible values: {string.Join(", ", StepNames.All)}",
                                                     $"Étape inconnue pour -Skip : « {s} ». Valeurs possibles : {string.Join(", ", StepNames.All)}"));
                        }
                        break;
                    case "apps":
                        var apps = Next().Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                        foreach (var unknown in apps.Where(s => NiniteApps.Find(s) == null))
                            o.Errors.Add(Lang.T($"Unknown app for -Apps: “{unknown}”. Use the app identifiers of ninite.com (7zip, chrome, vlc...)",
                                                $"Appli inconnue pour -Apps : « {unknown} ». Utiliser les identifiants de ninite.com (7zip, chrome, vlc...)"));
                        o.Apps = NiniteApps.Normalize(apps);
                        break;
                    case "alltweaks": o.AllTweaks = true; break;
                    case "windowskey":
                        string key = Next().Trim().ToUpperInvariant();
                        if (key.Length == 0 || RunConfig.IsValidKey(key)) o.WindowsKey = key;
                        else o.Errors.Add(Lang.T("Invalid key for -WindowsKey. Expected format: XXXXX-XXXXX-XXXXX-XXXXX-XXXXX",
                                                 "Clé invalide pour -WindowsKey. Format attendu : XXXXX-XXXXX-XXXXX-XXXXX-XXXXX"));
                        break;
                    case "reset": o.Reset = true; break;
                    case "simulation": o.Simulation = true; break;
                    case "forcecleanup": o.ForceCleanUp = true; break;
                    case "edition":
                        string edition = Next();
                        o.Edition = Editions.Normalize(edition) ?? Editions.Keep;
                        if (Editions.Normalize(edition) == null)
                            o.Errors.Add(Lang.T($"Unknown edition for -Edition: “{edition}”. Possible values: Pro, Home",
                                                $"Édition inconnue pour -Edition : « {edition} ». Valeurs possibles : Pro, Home"));
                        break;
                    case "lang":
                        string code = Next();
                        if (!Lang.Set(code)) o.Errors.Add(Lang.T($"Unknown language for -Lang: “{code}”. Possible values: en, fr",
                                                                 $"Langue inconnue pour -Lang : « {code} ». Valeurs possibles : en, fr"));
                        break;
                    default: o.Errors.Add(Lang.T($"Unknown option: “{args[i]}”", $"Option inconnue : « {args[i]} »")); break;
                }
            }
            return o;
        }
    }
}
