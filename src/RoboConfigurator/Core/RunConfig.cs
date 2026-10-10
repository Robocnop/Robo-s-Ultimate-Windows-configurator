using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
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
        [DataMember] public string WindowsKey { get; set; } = "";
        [DataMember] public bool AutoRestart { get; set; } = true;
        [DataMember] public bool CleanUp { get; set; } = true;
        /// <summary>Nettoyer même si une étape a échoué (les étapes en échec ne pourront plus être reprises).</summary>
        [DataMember] public bool CleanUpOnError { get; set; }
        /// <summary>Édition voulue pour l'activation : "" = garder l'actuelle, "Pro" ou "Home" (Famille).</summary>
        [DataMember] public string Edition { get; set; } = "";

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
                config.WindowsKey = config.WindowsKey ?? "";
                config.Edition = Editions.Normalize(config.Edition) ?? "";
                config.Steps = StepNames.All.Where(s => (config.Steps ?? new string[0]).Contains(s)).ToArray();
                return config;
            }
        }

        public void Save(string path)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(RunConfig)).WriteObject(stream, this);
                File.WriteAllText(path, Encoding.UTF8.GetString(stream.ToArray()), new UTF8Encoding(false));
            }
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

        public bool IsDone(string step) => File.Exists(_file) && File.ReadAllLines(_file).Contains(step);
        public void MarkDone(string step) => File.AppendAllLines(_file, new[] { step });
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
                    case "alltweaks": o.AllTweaks = true; break;
                    case "windowskey": o.WindowsKey = Next().Trim().ToUpperInvariant(); break;
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
