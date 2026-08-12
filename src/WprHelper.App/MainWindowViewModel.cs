using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using WprHelper.Contracts;
using WprHelper.Infrastructure;

namespace WprHelper.App;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The capture cancellation source is disposed after every run and is never retained at shutdown.")]
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions SettingsJsonOptions = new() { WriteIndented = true };
    private readonly ISessionManager _sessions;
    private readonly IProfileRepository _profiles;
    private readonly IWprCommandBuilder _wprCommands;
    private CancellationTokenSource? _captureCts;
    private CancellationTokenSource? _postProcessCts;
    private CaptureState _currentState = CaptureState.Idle;
    private bool _isRunning;
    private string _statusMessage;
    private string _stateText;
    private double _progressPercent;
    private CaptureProfile? _selectedProfile;
    private CaptureProfile? _summaryProfile;
    private readonly string _settingsPath;
    private bool _settingsInitialized;
    private string? _lastUsedProfileName;

    public MainWindowViewModel(ISessionManager sessions, IProfileRepository profiles, IStoragePathResolver paths,
        IWprCommandBuilder wprCommands)
    {
        _sessions = sessions; _profiles = profiles; _wprCommands = wprCommands; DataRoot = paths.DataRoot;
        _settingsPath = Path.Combine(DataRoot, "settings.json");
        OpenEtlFolderCommand = new RelayCommand(OpenEtlFolder, CanOpenEtlFolder);
        LocalDirectory = Path.Combine(paths.DataRoot, "Captures");
        Directory.CreateDirectory(LocalDirectory);
        _statusMessage = LocalizationService.Get("Ready"); _stateText = CaptureState.Idle.ToString();
        InitializeWprProfileOptions(["CPU"]);
        BrowseWprCommand = new RelayCommand(() => WprPath = PickExecutable(WprPath, "wpr.exe") ?? WprPath);
        BrowseTargetCommand = new RelayCommand(() => { var path = PickExecutable(TargetPath, "*.exe"); if (path is not null) { TargetPath = path; WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty; } });
        BrowseLocalCommand = new RelayCommand(() => LocalDirectory = PickFolder(LocalDirectory) ?? LocalDirectory);
        BrowseDestinationCommand = new RelayCommand(() => DestinationDirectory = PickFolder(DestinationDirectory) ?? DestinationDirectory);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning, HandleCommandError);
        StopCommand = new RelayCommand(RequestStop, () => IsRunning);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, onError: HandleCommandError);
        RefreshProfilesCommand = new AsyncRelayCommand(RefreshProfilesAsync, onError: HandleCommandError);
        LoadProfileCommand = new RelayCommand(LoadSelectedProfile, () => SelectedProfile is not null);
        RenameProfileCommand = new AsyncRelayCommand(RenameSelectedProfileAsync, CanRenameSelectedProfile, HandleCommandError);
        ResetSettingsCommand = new RelayCommand(ResetSettings, () => !IsRunning);
        LoadApplicationSettings();
        WprStatus = File.Exists(WprPath) ? "wpr.exe" : LocalizationService.Get("NoWpr");
        _settingsInitialized = true;
    }

    public async Task InitializeAsync()
    {
        await RefreshProfilesAsync();
        if (!LoadLastUsedProfile || string.IsNullOrWhiteSpace(_lastUsedProfileName)) return;
        var profile = Profiles.FirstOrDefault(x => string.Equals(x.Name, _lastUsedProfileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return;
        SelectedProfile = profile;
        ApplyProfile(profile);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string DataRoot { get; }
    public string ApplicationVersion { get; } = Assembly.GetEntryAssembly()?.GetName().Version is { } version
        ? $"{version.Major}.{version.Minor}"
        : "1.0";
    public ObservableCollection<CaptureProfile> Profiles { get; } = [];
    public ObservableCollection<WprProfileOption> WprProfileOptions { get; } = [];
    public ObservableCollection<string> SelectedWprProfileDescriptions { get; } = [];
    public RelayCommand BrowseWprCommand { get; }
    public RelayCommand BrowseTargetCommand { get; }
    public RelayCommand BrowseLocalCommand { get; }
    public RelayCommand BrowseDestinationCommand { get; }
    public RelayCommand OpenEtlFolderCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public AsyncRelayCommand SaveProfileCommand { get; }
    public AsyncRelayCommand RefreshProfilesCommand { get; }
    public RelayCommand LoadProfileCommand { get; }
    public AsyncRelayCommand RenameProfileCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }

    public string WprPath { get => Get(WprExecutableLocator.FindPreferred()); set { Set(value); WprStatus = File.Exists(value) && string.Equals(Path.GetFileName(value), "wpr.exe", StringComparison.OrdinalIgnoreCase) ? $"wpr.exe ({FileVersionInfo.GetVersionInfo(value).FileVersion})" : LocalizationService.Get("NoWpr"); } }
    public string WprStatus { get => Get(LocalizationService.Get("NoWpr")); private set => Set(value); }
    public string CustomWprProfile { get => Get(string.Empty); set { Set(value); RefreshSelectedWprProfileDescriptions(); } }
    public bool FileMode { get => Get(true); set => Set(value); }
    public string WprStartArguments { get => Get(string.Empty); set => Set(value); }
    public string TargetPath { get => Get(string.Empty); set { Set(value); OnPropertyChanged(nameof(TargetExecutableName)); } }
    public string TargetExecutableName => string.IsNullOrWhiteSpace(TargetPath) ? LocalizationService.Get("TargetNotSelected") : Path.GetFileName(TargetPath);
    public string TargetArguments { get => Get(string.Empty); set => Set(value); }
    public string WorkingDirectory { get => Get(string.Empty); set => Set(value); }
    public bool RunTargetElevated { get => Get(false); set => Set(value); }
    public bool StopAfterTargetExit { get => Get(true); set => Set(value); }
    public double? ExitDelaySeconds { get => Get<double?>(null); set => Set(value); }
    public double? MaximumDurationSeconds { get => Get<double?>(null); set => Set(value); }
    public double MinimumFreeGb { get => Get(1d); set => Set(value); }
    public string LocalDirectory { get => Get(string.Empty); set { Set(value); OpenEtlFolderCommand.RaiseCanExecuteChanged(); } }
    public string DestinationDirectory { get => Get(string.Empty); set => Set(value); }
    public string FileNameTemplate { get => Get("{AppName}_{ComputerName}_{DateTime}"); set => Set(value); }
    public bool OverwriteExisting { get => Get(false); set => Set(value); }
    public string ProfileName { get => Get("Default"); set => Set(value); }
    public LanguagePreference Language { get => Get(LanguagePreference.Automatic); set { Set(value); LocalizationService.Apply(value); RefreshLocalizedValues(); SaveApplicationSettings(); } }
    public bool Topmost { get => Get(false); set { Set(value); SaveApplicationSettings(); } }
    public bool ConfirmStop { get => Get(true); set { Set(value); SaveApplicationSettings(); } }
    public bool OpenFolderAfterCompletion { get => Get(false); set { Set(value); SaveApplicationSettings(); } }
    public bool LoadLastUsedProfile { get => Get(true); set { Set(value); SaveApplicationSettings(); } }
    public double UiScalePercent
    {
        get => Get(100d);
        set { Set(value); OnPropertyChanged(nameof(UiScale)); SaveApplicationSettings(); }
    }
    public double UiScale => UiScalePercent / 100d;
    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) { StartCommand.RaiseCanExecuteChanged(); StopCommand.RaiseCanExecuteChanged(); ResetSettingsCommand.RaiseCanExecuteChanged(); } } }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public double ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }
    public bool IsProgressIndeterminate { get => Get(false); private set => Set(value); }
    public string StartTimeText { get => Get("—"); private set => Set(value); }
    public string CaptureTimeText { get => Get("—"); private set => Set(value); }
    public string FinishTimeText { get => Get("—"); private set => Set(value); }
    public string SavePathText { get => Get("—"); private set { Set(value); OpenEtlFolderCommand.RaiseCanExecuteChanged(); } }
    public string StopSummaryText { get => Get("—"); private set => Set(value); }
    public string FilterSummaryText { get => Get("—"); private set => Set(value); }
    public string AppliedWprCommandText { get => Get("—"); private set => Set(value); }
    public CaptureProfile? SelectedProfile { get => _selectedProfile; set { if (Set(ref _selectedProfile, value)) { LoadProfileCommand.RaiseCanExecuteChanged(); RenameProfileCommand.RaiseCanExecuteChanged(); } } }

    private async Task StartAsync()
    {
        IsRunning = true; _captureCts = new CancellationTokenSource(); _postProcessCts = new CancellationTokenSource(); ProgressPercent = 0; IsProgressIndeterminate = false;
        StartTimeText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture); CaptureTimeText = "—"; FinishTimeText = "—";
        try
        {
            var profile = BuildProfile();
            UpdateCaptureSummary(profile);
            var progress = new Progress<CaptureProgress>(p =>
            {
                _currentState = p.State;
                if (p.State == CaptureState.Capturing && CaptureTimeText == "—") CaptureTimeText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
                StateText = $"{p.State} · {p.Elapsed:hh\\:mm\\:ss}";
                StatusMessage = p.Message;
                if (p.TransferPercent is { } transferPercent) { IsProgressIndeterminate = false; ProgressPercent = transferPercent; }
                else if (p.State == CaptureState.Capturing)
                {
                    var limits = new List<double>();
                    if (profile.Stop.MaximumDuration is { } duration) limits.Add(p.Elapsed.TotalMilliseconds * 100d / duration.TotalMilliseconds);
                    IsProgressIndeterminate = limits.Count == 0;
                    if (limits.Count > 0) ProgressPercent = Math.Clamp(limits.Max(), 0, 99);
                }
            });
            var result = await _sessions.CaptureAsync(profile, progress, _captureCts.Token, _postProcessCts.Token);
            StateText = result.Session.State.ToString(); StatusMessage = LocalizationService.Get("CaptureCompleted"); ProgressPercent = 100;
            var savedEtl = result.Files.FirstOrDefault(x => string.Equals(Path.GetExtension(x), ".etl", StringComparison.OrdinalIgnoreCase));
            if (savedEtl is not null) SavePathText = savedEtl;
            var outputDirectory = savedEtl is null ? profile.LocalDirectory : Path.GetDirectoryName(savedEtl);
            if (OpenFolderAfterCompletion && Directory.Exists(outputDirectory))
                Process.Start(new ProcessStartInfo(outputDirectory!) { UseShellExecute = true });
        }
        catch (Exception ex) { StateText = CaptureState.Failed.ToString(); StatusMessage = $"{LocalizationService.Get("CaptureFailed")}: {ex.Message}"; }
        finally { FinishTimeText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture); IsProgressIndeterminate = false; _captureCts.Dispose(); _captureCts = null; _postProcessCts.Dispose(); _postProcessCts = null; _currentState = CaptureState.Idle; IsRunning = false; }
    }

    private CaptureProfile BuildProfile()
    {
        var selectedProfiles = GetSelectedWprProfiles();
        return new CaptureProfile
        {
            Name = ProfileName,
            Language = Language,
            WprPath = WprPath,
            TargetPath = TargetPath,
            WprProfile = selectedProfiles.Count > 0 ? selectedProfiles[0] : string.Empty,
            WprProfiles = selectedProfiles,
            FileMode = FileMode,
            WprStartArguments = WprStartArguments,
            TargetArguments = TargetArguments,
            WorkingDirectory = WorkingDirectory,
            RunTargetElevated = RunTargetElevated,
            Stop = new StopOptions { StopAfterTargetExit = StopAfterTargetExit, TargetExitDelay = TimeSpan.FromSeconds(ExitDelaySeconds ?? 0), MaximumDuration = MaximumDurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, MinimumFreeBytes = checked((long)(MinimumFreeGb * 1024 * 1024 * 1024)) },
            LocalDirectory = SessionManager.NormalizeDestination(LocalDirectory),
            DestinationDirectory = SessionManager.NormalizeDestination(DestinationDirectory),
            FileNameTemplate = FileNameTemplate,
            OverwriteExisting = OverwriteExisting
        };
    }

    private void UpdateCaptureSummary(CaptureProfile profile)
    {
        _summaryProfile = profile;
        SavePathText = profile.LocalDirectory;
        var yes = LocalizationService.Get("Yes");
        var no = LocalizationService.Get("No");
        var off = LocalizationService.Get("Disabled");
        var stopOnClose = profile.Stop.StopAfterTargetExit
            ? $"{yes}, {profile.Stop.TargetExitDelay.TotalSeconds:0.###} {LocalizationService.Get("SecondsShort")}" : no;
        var duration = profile.Stop.MaximumDuration is { } maximum
            ? $"{maximum.TotalSeconds:0.###} {LocalizationService.Get("SecondsShort")}" : off;
        StopSummaryText = $"{LocalizationService.Get("CloseConditionShort")}: {stopOnClose}; " +
            $"{LocalizationService.Get("DurationConditionShort")}: {duration}; " +
            $"{LocalizationService.Get("FreeReserveShort")}: {FormatSize(profile.Stop.MinimumFreeBytes)}";
        var selectedProfiles = profile.WprProfiles.Count > 0 ? profile.WprProfiles : [profile.WprProfile];
        FilterSummaryText = $"{string.Join(", ", selectedProfiles)}; {(profile.FileMode ? "-filemode" : "memory mode")}" +
            (string.IsNullOrWhiteSpace(profile.WprStartArguments) ? string.Empty : $"; {profile.WprStartArguments}");
        AppliedWprCommandText = _wprCommands.FormatStart(profile);
    }

    private async Task SaveProfileAsync()
    {
        var profile = BuildProfile();
        await _profiles.SaveAsync(profile, CancellationToken.None);
        await RefreshProfilesAsync();
        SelectedProfile = Profiles.FirstOrDefault(x => string.Equals(x.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        RememberLastUsedProfile(profile.Name);
        StatusMessage = LocalizationService.Get("ProfileSaved");
    }
    private async Task RefreshProfilesAsync()
    {
        var selectedName = SelectedProfile?.Name;
        Profiles.Clear();
        foreach (var p in await _profiles.LoadAllAsync(CancellationToken.None)) Profiles.Add(p);
        SelectedProfile = Profiles.FirstOrDefault(x => string.Equals(x.Name, selectedName, StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
    }
    private void LoadSelectedProfile()
    {
        if (SelectedProfile is not { } profile) return;
        ApplyProfile(profile);
        RememberLastUsedProfile(profile.Name);
    }
    private bool CanRenameSelectedProfile() => SelectedProfile is not null;
    private async Task RenameSelectedProfileAsync()
    {
        if (SelectedProfile is not { } selected) return;
        var dialog = new ProfileNameDialog(selected.Name) { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        var newName = dialog.ProfileName;
        if (string.Equals(selected.Name, newName, StringComparison.OrdinalIgnoreCase)) return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusMessage = LocalizationService.Get("InvalidProfileName");
            return;
        }
        if (Profiles.Any(x => !ReferenceEquals(x, selected) && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = LocalizationService.Get("ProfileNameExists");
            return;
        }
        try
        {
            var renamed = selected with { Name = newName };
            await _profiles.RenameAsync(selected.Name, renamed, CancellationToken.None);
            if (string.Equals(_lastUsedProfileName, selected.Name, StringComparison.OrdinalIgnoreCase))
                _lastUsedProfileName = newName;
            await RefreshProfilesAsync();
            SelectedProfile = Profiles.FirstOrDefault(x => string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase));
            ProfileName = newName;
            SaveApplicationSettings();
            StatusMessage = LocalizationService.Get("ProfileRenamed");
        }
        catch (Exception ex)
        {
            StatusMessage = $"{LocalizationService.Get("ProfileRenameFailed")}: {ex.Message}";
        }
    }
    private void ApplyProfile(CaptureProfile p)
    {
        ProfileName = p.Name;
        Language = p.Language;
        WprPath = p.WprPath;
        SetSelectedWprProfiles(p.WprProfiles.Count > 0 ? p.WprProfiles : [p.WprProfile]);
        FileMode = p.FileMode;
        WprStartArguments = p.WprStartArguments;
        TargetPath = p.TargetPath;
        TargetArguments = p.TargetArguments;
        WorkingDirectory = p.WorkingDirectory;
        RunTargetElevated = p.RunTargetElevated;
        StopAfterTargetExit = p.Stop.StopAfterTargetExit;
        ExitDelaySeconds = p.Stop.TargetExitDelay.TotalSeconds;
        MaximumDurationSeconds = p.Stop.MaximumDuration?.TotalSeconds;
        MinimumFreeGb = p.Stop.MinimumFreeBytes / (1024d * 1024 * 1024);
        LocalDirectory = p.LocalDirectory;
        DestinationDirectory = p.DestinationDirectory;
        FileNameTemplate = p.FileNameTemplate;
        OverwriteExisting = p.OverwriteExisting;
    }

    private static string? PickExecutable(string current, string filterName) { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = $"Executables ({filterName})|{filterName}|All files|*.*", FileName = current }; return dialog.ShowDialog() == true ? dialog.FileName : null; }
    private static string? PickFolder(string current)
    {
        var normalized = SessionManager.NormalizeDestination(current);
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(normalized) ? normalized : string.Empty,
            FolderName = Directory.Exists(normalized) ? normalized : string.Empty,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void ResetSettings()
    {
        if (System.Windows.MessageBox.Show(LocalizationService.Get("ResetSettingsMessage"),
                LocalizationService.Get("ResetSettingsTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _settingsInitialized = false;
        try
        {
            ProfileName = "Default";
            SelectedProfile = null;
            WprPath = WprExecutableLocator.FindPreferred();
            SetSelectedWprProfiles(["CPU"]);
            FileMode = true;
            WprStartArguments = string.Empty;
            TargetPath = string.Empty;
            TargetArguments = string.Empty;
            WorkingDirectory = string.Empty;
            RunTargetElevated = false;
            StopAfterTargetExit = true;
            ExitDelaySeconds = null;
            MaximumDurationSeconds = null;
            MinimumFreeGb = 1d;
            LocalDirectory = Path.Combine(DataRoot, "Captures");
            Directory.CreateDirectory(LocalDirectory);
            DestinationDirectory = string.Empty;
            FileNameTemplate = "{AppName}_{ComputerName}_{DateTime}";
            OverwriteExisting = false;

            Language = LanguagePreference.Automatic;
            Topmost = false;
            ConfirmStop = true;
            OpenFolderAfterCompletion = false;
            LoadLastUsedProfile = true;
            UiScalePercent = 100d;
            _lastUsedProfileName = null;
        }
        finally
        {
            _settingsInitialized = true;
        }

        _summaryProfile = null;
        SavePathText = "\u2014";
        StopSummaryText = "\u2014";
        FilterSummaryText = "\u2014";
        AppliedWprCommandText = "\u2014";
        SaveApplicationSettings();
        StatusMessage = LocalizationService.Get("SettingsReset");
    }
    private bool CanOpenEtlFolder() => File.Exists(SavePathText) || Directory.Exists(LocalDirectory);
    private void OpenEtlFolder()
    {
        if (File.Exists(SavePathText))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SavePathText}\"") { UseShellExecute = true });
            return;
        }
        if (Directory.Exists(LocalDirectory))
            Process.Start(new ProcessStartInfo(LocalDirectory) { UseShellExecute = true });
    }

    private void InitializeWprProfileOptions(IReadOnlyCollection<string> selectedProfiles)
    {
        WprProfileOptions.Clear();
        foreach (var name in new[] { "CPU", "GeneralProfile", "DiskIO", "FileIO", "Registry", "Network", "Heap", "GPU", "DotNET" })
        {
            var option = new WprProfileOption(name, LocalizationService.Get(GetWprProfileDescriptionKey(name)), OnWprProfileSelectionChanged)
            {
                IsSelected = selectedProfiles.Contains(name, StringComparer.OrdinalIgnoreCase)
            };
            WprProfileOptions.Add(option);
        }
        RefreshSelectedWprProfileDescriptions();
    }

    private void SetSelectedWprProfiles(IReadOnlyCollection<string> profiles)
    {
        foreach (var option in WprProfileOptions)
            option.IsSelected = profiles.Contains(option.Name, StringComparer.OrdinalIgnoreCase);
        CustomWprProfile = profiles.FirstOrDefault(profile => WprProfileOptions.All(option =>
            !string.Equals(option.Name, profile, StringComparison.OrdinalIgnoreCase))) ?? string.Empty;
        RefreshSelectedWprProfileDescriptions();
    }

    private List<string> GetSelectedWprProfiles()
    {
        var profiles = WprProfileOptions.Where(option => option.IsSelected).Select(option => option.Name).ToList();
        if (!string.IsNullOrWhiteSpace(CustomWprProfile)) profiles.Add(CustomWprProfile.Trim());
        return profiles;
    }

    private void OnWprProfileSelectionChanged() => RefreshSelectedWprProfileDescriptions();

    private void RefreshSelectedWprProfileDescriptions()
    {
        SelectedWprProfileDescriptions.Clear();
        foreach (var option in WprProfileOptions.Where(option => option.IsSelected))
            SelectedWprProfileDescriptions.Add($"{option.Name} — {option.Description}");
        if (!string.IsNullOrWhiteSpace(CustomWprProfile))
            SelectedWprProfileDescriptions.Add($"{CustomWprProfile.Trim()} — {LocalizationService.Get("WprProfileDescriptionCustom")}");
        if (SelectedWprProfileDescriptions.Count == 0)
            SelectedWprProfileDescriptions.Add(LocalizationService.Get("SelectAtLeastOneWprProfile"));
    }

    private static string GetWprProfileDescriptionKey(string profile) => profile.ToUpperInvariant() switch
    {
        "CPU" => "WprProfileDescriptionCpu",
        "GENERALPROFILE" => "WprProfileDescriptionGeneral",
        "DISKIO" => "WprProfileDescriptionDiskIo",
        "FILEIO" => "WprProfileDescriptionFileIo",
        "REGISTRY" => "WprProfileDescriptionRegistry",
        "NETWORK" => "WprProfileDescriptionNetwork",
        "HEAP" => "WprProfileDescriptionHeap",
        "GPU" => "WprProfileDescriptionGpu",
        "DOTNET" => "WprProfileDescriptionDotNet",
        _ => "WprProfileDescriptionOther"
    };
    private static string FormatSize(long value) => value >= 1024 * 1024 * 1024 ? $"{value / (1024d * 1024 * 1024):0.00} GB" : value >= 1024 * 1024 ? $"{value / (1024d * 1024):0.0} MB" : $"{value / 1024d:0} KB";
    private void HandleCommandError(Exception ex) => StatusMessage = ex.Message;
    private void RefreshLocalizedValues()
    {
        WprStatus = File.Exists(WprPath) ? "wpr.exe" : LocalizationService.Get("NoWpr");
        var selected = GetSelectedWprProfiles();
        InitializeWprProfileOptions(selected);
        OnPropertyChanged(nameof(TargetExecutableName));
        if (_summaryProfile is not null) UpdateCaptureSummary(_summaryProfile);
        if (!IsRunning) StatusMessage = LocalizationService.Get("Ready");
    }

    private void LoadApplicationSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var settings = JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(_settingsPath));
            if (settings is null) return;
            Language = settings.Language; Topmost = settings.Topmost; ConfirmStop = settings.ConfirmStop;
            OpenFolderAfterCompletion = settings.OpenFolderAfterCompletion; LoadLastUsedProfile = settings.LoadLastUsedProfile;
            UiScalePercent = Math.Clamp(settings.UiScalePercent, 80, 125); _lastUsedProfileName = settings.LastUsedProfileName;
        }
        catch { }
    }

    private void SaveApplicationSettings()
    {
        if (!_settingsInitialized) return;
        try
        {
            var settings = new ApplicationSettings
            {
                Language = Language,
                Topmost = Topmost,
                ConfirmStop = ConfirmStop,
                OpenFolderAfterCompletion = OpenFolderAfterCompletion,
                UiScalePercent = UiScalePercent,
                LoadLastUsedProfile = LoadLastUsedProfile,
                LastUsedProfileName = _lastUsedProfileName
            };
            var temp = _settingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonOptions));
            File.Move(temp, _settingsPath, overwrite: true);
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private void RememberLastUsedProfile(string name)
    {
        _lastUsedProfileName = name;
        SaveApplicationSettings();
    }

    private sealed record ApplicationSettings
    {
        public LanguagePreference Language { get; init; } = LanguagePreference.Automatic;
        public bool Topmost { get; init; }
        public bool ConfirmStop { get; init; } = true;
        public bool OpenFolderAfterCompletion { get; init; }
        public double UiScalePercent { get; init; } = 100;
        public bool LoadLastUsedProfile { get; init; } = true;
        public string? LastUsedProfileName { get; init; }
    }

    private void RequestStop()
    {
        if (ConfirmStop && System.Windows.MessageBox.Show(LocalizationService.Get("ConfirmStopMessage"), LocalizationService.Get("ConfirmStopTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_currentState is CaptureState.Finalizing or CaptureState.Copying) _postProcessCts?.Cancel();
        else _captureCts?.Cancel();
    }

    public async Task<bool> StopAndWaitForShutdownAsync(TimeSpan timeout)
    {
        _captureCts?.Cancel();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (IsRunning && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
        return !IsRunning;
    }

    private readonly Dictionary<string, object?> _values = [];
    private T Get<T>(T defaultValue = default!, [CallerMemberName] string name = "") => _values.TryGetValue(name, out var value) ? (T)value! : defaultValue;
    private void Set<T>(T value, [CallerMemberName] string name = "")
    {
        if (_values.TryGetValue(name, out var current) && current is T typed && EqualityComparer<T>.Default.Equals(typed, value)) return;
        _values[name] = value;
        OnPropertyChanged(name);
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "") { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); return true; }
    private void OnPropertyChanged([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new(name));
}

public sealed class WprProfileOption : INotifyPropertyChanged
{
    private readonly Action _selectionChanged;
    private bool _isSelected;

    public WprProfileOption(string name, string description, Action selectionChanged)
    {
        Name = name;
        Description = description;
        _selectionChanged = selectionChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; }
    public string Description { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
            _selectionChanged();
        }
    }
}
