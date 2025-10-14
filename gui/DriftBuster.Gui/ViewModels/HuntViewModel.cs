using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DriftBuster.Gui.Models;
using DriftBuster.Gui.Services;

namespace DriftBuster.Gui.ViewModels
{
    public partial class HuntViewModel : ObservableObject
    {
        private readonly IDriftbusterService _service;

        public ObservableCollection<HuntHitView> Hits { get; } = new();

        [ObservableProperty]
        private string? _directoryPath;

        [ObservableProperty]
        private string? _pattern;

        [ObservableProperty]
        private string? _directoryError;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private string? _statusMessage;

        [ObservableProperty]
        private string _rawJson = string.Empty;

        [ObservableProperty]
        private int _resultCount;

        public IAsyncRelayCommand RunHuntCommand { get; }

        public bool IsBusy => RunHuntCommand.IsRunning;

        public bool HasHits => Hits.Count > 0;

        public bool HasDirectoryError => !string.IsNullOrEmpty(DirectoryError);

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

        public bool HasRawJson => !string.IsNullOrEmpty(RawJson);

        public bool HasNoHits => !HasHits;

        public HuntViewModel(IDriftbusterService service, string? initial = null)
        {
            _service = service;
            StatusMessage = initial;

            Hits.CollectionChanged += OnHitsChanged;

            RunHuntCommand = new AsyncRelayCommand(RunHuntAsync, CanRunHunt);
            RunHuntCommand.PropertyChanged += (_, args) =>
            {
                if (string.Equals(args.PropertyName, nameof(AsyncRelayCommand.IsRunning), StringComparison.Ordinal))
                {
                    OnPropertyChanged(nameof(IsBusy));
                    RunHuntCommand.NotifyCanExecuteChanged();
                }
            };

            UpdateValidation();
        }

        partial void OnDirectoryPathChanged(string? value) => UpdateValidation();

        private async Task RunHuntAsync()
        {
            ErrorMessage = null;
            StatusMessage = null;

            try
            {
                var result = await _service.HuntAsync(DirectoryPath, Pattern).ConfigureAwait(false);
                ApplyResult(result);
            }
            catch (System.Exception ex)
            {
                ErrorMessage = ex.Message;
                StatusMessage = null;
                Hits.Clear();
                ResultCount = 0;
                RawJson = string.Empty;
            }
        }

        private void ApplyResult(HuntResult result)
        {
            Hits.Clear();
            foreach (var hit in result.Hits)
            {
                Hits.Add(new HuntHitView(
                    hit.Rule.Name,
                    hit.Rule.Description,
                    hit.Rule.TokenName,
                    hit.RelativePath,
                    hit.Path,
                    hit.LineNumber,
                    hit.Excerpt));
            }

            ResultCount = result.Count;
            RawJson = result.RawJson;
            StatusMessage = result.Count == 0
                ? "No matches found"
                : $"Found {result.Count} hit{(result.Count == 1 ? string.Empty : "s")}.";
            ErrorMessage = null;
        }

        private bool CanRunHunt()
        {
            return string.IsNullOrEmpty(DirectoryError) && !RunHuntCommand.IsRunning;
        }

        private void UpdateValidation()
        {
            DirectoryError = ValidateDirectoryPath(DirectoryPath);
            RunHuntCommand.NotifyCanExecuteChanged();
        }

        private void OnHitsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasHits));
            OnPropertyChanged(nameof(HasNoHits));
        }

        partial void OnDirectoryErrorChanged(string? value)
        {
            OnPropertyChanged(nameof(HasDirectoryError));
        }

        partial void OnErrorMessageChanged(string? value)
        {
            OnPropertyChanged(nameof(HasError));
        }

        partial void OnStatusMessageChanged(string? value)
        {
            OnPropertyChanged(nameof(HasStatus));
        }

        partial void OnRawJsonChanged(string value)
        {
            OnPropertyChanged(nameof(HasRawJson));
        }

        private static string? ValidateDirectoryPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "Select a directory or file";
            }

            if (Directory.Exists(path) || File.Exists(path))
            {
                return null;
            }

            return "Path not found";
        }

        public sealed class HuntHitView
        {
            public HuntHitView(string ruleName, string description, string? tokenName, string relativePath, string fullPath, int lineNumber, string excerpt)
            {
                RuleName = ruleName;
                Description = description;
                TokenName = string.IsNullOrWhiteSpace(tokenName) ? "—" : tokenName;
                RelativePath = relativePath;
                FullPath = fullPath;
                LineNumber = lineNumber;
                Excerpt = TrimExcerpt(excerpt);
            }

            public string RuleName { get; }

            public string Description { get; }

            public string TokenName { get; }

            public bool HasToken => TokenName != "—";

            public string RelativePath { get; }

            public string FullPath { get; }

            public int LineNumber { get; }

            public string Excerpt { get; }

            private static string TrimExcerpt(string text)
            {
                const int limit = 160;
                if (string.IsNullOrEmpty(text) || text.Length <= limit)
                {
                    return text;
                }

                return text[..limit] + "…";
            }
        }
    }
}
