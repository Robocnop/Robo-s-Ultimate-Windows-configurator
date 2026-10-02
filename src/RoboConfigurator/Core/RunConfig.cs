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
    /// <summary>Étapes de la configuration, dans l'ordre d'exécution.</summary>
    public static class StepNames
    {
        public const string Ninite = "Ninite";
        public const string CrapFixer = "CrapFixer";
        public const string Edge = "Edge";
        public const string Office = "Office";
        public const string Activation = "Activation";

        public static readonly string[] All = { Ninite, CrapFixer, Edge, Office, Activation };
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

    /// <summary>Étapes réussies, mémorisées dans etapes-terminees.txt (une par ligne).</summary>
    public sealed class StepState
    {
        private readonly string _file;
        public StepState(string file) { _file = file; }

        public bool IsDone(string step) => File.Exists(_file) && File.ReadAllLines(_file).Contains(step);
        public void MarkDone(string step) => File.AppendAllLines(_file, new[] { step });
    }

    /// <summary>Arguments de la ligne de commande (mêmes options que l'ancien Lancer.bat).</summary>
    public sealed class Options
    {
        public List<string> Skip { get; } = new List<string>();
        public bool AllTweaks { get; private set; }
        public string WindowsKey { get; private set; } = "";
        public bool Reset { get; private set; }
        public bool Simulation { get; private set; }
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
                            else o.Errors.Add($"Étape inconnue pour -Skip : « {s} ». Valeurs possibles : {string.Join(", ", StepNames.All)}");
                        }
                        break;
                    case "alltweaks": o.AllTweaks = true; break;
                    case "windowskey": o.WindowsKey = Next().Trim().ToUpperInvariant(); break;
                    case "reset": o.Reset = true; break;
                    case "simulation": o.Simulation = true; break;
                    default: o.Errors.Add($"Option inconnue : « {args[i]} »"); break;
                }
            }
            return o;
        }
    }
}
