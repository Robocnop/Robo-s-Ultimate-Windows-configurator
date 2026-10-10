using System.ComponentModel;
using System.Runtime.CompilerServices;
using RoboConfigurator.Core;
using Wpf.Ui.Controls;

namespace RoboConfigurator
{
    public enum StepStatus { Pending, Skipped, AlreadyDone, Downloading, Running, Done, Warning, Failed }

    public sealed class StepItem : INotifyPropertyChanged
    {
        public StepItem(string key, string title, string description, SymbolRegular icon)
        {
            Key = key; Title = title; Description = description; Icon = icon;
        }

        public string Key { get; }
        public string Title { get; }
        public string Description { get; }
        public SymbolRegular Icon { get; }

        private bool _isSelected = true;
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

        private bool _alreadyDone;
        public bool AlreadyDone { get => _alreadyDone; set => Set(ref _alreadyDone, value); }

        private string _detail = "";
        public string Detail { get => _detail; set => Set(ref _detail, value); }

        private StepStatus _status;
        public StepStatus Status
        {
            get => _status;
            set
            {
                if (!Set(ref _status, value)) return;
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusSymbol));
                OnPropertyChanged(nameof(IsBusy));
            }
        }

        public bool IsBusy => Status == StepStatus.Running || Status == StepStatus.Downloading;

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case StepStatus.Skipped: return Lang.T("Skipped", "Ignorée");
                    case StepStatus.AlreadyDone: return Lang.T("Already done", "Déjà faite");
                    case StepStatus.Downloading: return Lang.T("Downloading", "Téléchargement");
                    case StepStatus.Running: return Lang.T("In progress", "En cours");
                    case StepStatus.Done: return Lang.T("Done", "Terminée");
                    case StepStatus.Warning: return Lang.T("Check", "À vérifier");
                    case StepStatus.Failed: return Lang.T("Failed", "Échec");
                    default: return Lang.T("Waiting", "En attente");
                }
            }
        }

        public SymbolRegular StatusSymbol
        {
            get
            {
                switch (Status)
                {
                    case StepStatus.Skipped: return SymbolRegular.SubtractCircle24;
                    case StepStatus.AlreadyDone:
                    case StepStatus.Done: return SymbolRegular.CheckmarkCircle24;
                    case StepStatus.Warning: return SymbolRegular.Warning24;
                    case StepStatus.Failed: return SymbolRegular.ErrorCircle24;
                    default: return SymbolRegular.Circle24;
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
