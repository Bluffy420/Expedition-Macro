using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ExpeditionsMacro.App.Models;
using ExpeditionsMacro.App.Services;
using ExpeditionsMacro.Automation.Camera;
using ExpeditionsMacro.Automation.Challenges;
using ExpeditionsMacro.Automation.Diagnostics;
using ExpeditionsMacro.Automation.Discord;
using ExpeditionsMacro.Automation.Navigation;
using ExpeditionsMacro.Automation.Scheduling;
using ExpeditionsMacro.Automation.Stages;
using ExpeditionsMacro.Core.Abstractions;
using ExpeditionsMacro.Core.Models;
using ExpeditionsMacro.Core.Persistence;
using ExpeditionsMacro.Core.Runtime;
using ExpeditionsMacro.Vision.Packs;

namespace ExpeditionsMacro.App.Pages;

public partial class MacroPage : UserControl, IAppPage
{
    private static readonly TimeSpan SafeSkipDelay = TimeSpan.FromMinutes(5);

    private readonly AppServices _services;
    private readonly ObservableCollection<MacroPlan> _plans = [];
    private readonly ObservableCollection<MacroPresetChoice> _allPresets = [];
    private readonly ObservableCollection<MacroPresetChoice> _visiblePresets = [];
    private readonly Dictionary<string, StoryPreset> _storyPresets = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _runtimeTimer;
    private DateTimeOffset? _runStarted;
    private string? _editingTaskId;
    private bool _loading;
    private bool _macroOwned;
    private bool _testingWebhook;

    public MacroPage(AppServices services)
    {
        _services = services;
        InitializeComponent();
        DataContext = this;
        PlanCombo.ItemsSource = _plans;
        TaskKindCombo.ItemsSource = Enum.GetValues<MacroTaskKind>()
            .Select(kind => new NamedChoice<MacroTaskKind>(kind, Label(kind)))
            .ToArray();
        TaskPresetCombo.ItemsSource = _visiblePresets;
        _runtimeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateRuntime(), Dispatcher);
        _services.Coordinator.StateChanged += (_, _) => Dispatcher.BeginInvoke(CoordinatorStateChanged);
        _services.Hotkey.BindingChanged += (_, _) => Dispatcher.BeginInvoke(UpdateHotkeyText);
    }

    public ObservableCollection<MacroTaskRow> TaskRows { get; } = [];

    public Func<Task>? IdleHotkeyAction => StartFromHotkeyAsync;

    public async Task OnShownAsync()
    {
        _loading = true;
        try
        {
            await RefreshPresetCatalogAsync();
            await RefreshPlansAsync();
            MacroPlan? selected = _plans.FirstOrDefault(plan => plan.Id == _services.Settings.SelectedMacroPlanId) ?? _plans.FirstOrDefault();
            PlanCombo.SelectedItem = selected;
            if (selected is null) ApplyNewPlan();
            else ApplyPlan(selected);

            string webhook = string.Empty;
            try { webhook = _services.SecretProtector.Unprotect(_services.Settings.EncryptedWebhook); } catch { }
            WebhookPassword.Password = webhook;
            WebhookVisible.Text = webhook;
            DiscordUserIdText.Text = _services.Settings.DiscordErrorUserId;
            LoadPrivateServerRecoverySettings();
            TaskKindCombo.SelectedIndex = 0;
            RefreshVisiblePresets();
            UpdateTaskTargetEditor();
        }
        finally
        {
            _loading = false;
        }
        UpdateHotkeyText();
        CoordinatorStateChanged();
    }

    internal void SetSnapshotScroll(bool showEnd)
    {
        // Snapshot artifacts are uploaded by CI. Never render locally protected
        // reporting values into those images, even when the normal controls mask
        // part of a webhook on screen.
        WebhookPassword.Password = string.Empty;
        WebhookVisible.Text = string.Empty;
        DiscordUserIdText.Text = string.Empty;
        ClearPrivateServerRecoverySnapshot();
        PopulateSnapshotTasks();
        UpdateLayout();
        if (showEnd) PageScroll.ScrollToEnd();
        else PageScroll.ScrollToTop();
    }

    private void PopulateSnapshotTasks()
    {
        TaskRows.Clear();
        TaskRows.Add(new MacroTaskRow
        {
            Definition = new MacroTaskDefinition
            {
                Id = "snapshot-challenge",
                Kind = MacroTaskKind.Challenge,
                PresetId = "snapshot-challenge-preset",
                Name = "Challenge rotation",
                Priority = 1,
            },
            Progress = new MacroTaskProgress { TaskId = "snapshot-challenge" },
        });
        TaskRows.Add(new MacroTaskRow
        {
            Definition = new MacroTaskDefinition
            {
                Id = "snapshot-story",
                Kind = MacroTaskKind.Story,
                PresetId = "snapshot-story-preset",
                Name = "School Grounds infinite",
                Priority = 2,
                CompleteOnRuntimeDefeat = true,
                TargetRuntimeMinutes = 180,
            },
            Progress = new MacroTaskProgress
            {
                TaskId = "snapshot-story",
                Victories = 12,
                Defeats = 1,
                RuntimeSeconds = 8450,
            },
        });
        EmptyTasksText.Visibility = Visibility.Collapsed;
        ApplyTotals();
    }

    public Task StartFromHotkeyAsync() => StartMacroAsync();

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartMacroAsync();

    private void Stop_Click(object sender, RoutedEventArgs e) => _services.Coordinator.Cancel();

    private async Task StartMacroAsync()
    {
        if (_services.Coordinator.IsBusy) return;

        MacroPlan plan;
        char playMenuKey;
        char? unitMenuKey = null;
        string webhook = CurrentWebhook();
        string discordUserId = DiscordUserIdText.Text.Trim();
        PrivateServerRecoverySelection privateServerRecovery;
        try
        {
            plan = await SavePlanInternalAsync();
            if (!plan.Tasks.Any(task => task.Enabled)) throw new InvalidOperationException("Enable at least one task before starting the plan.");
            playMenuKey = AppSettings.ParsePlayMenuKey(_services.Settings.PlayMenuKey, _services.Settings.MacroHotkeyVirtualKey);
            if (!string.IsNullOrWhiteSpace(_services.Settings.UnitMenuKey))
            {
                unitMenuKey = AppSettings.ParseUnitMenuKey(
                    _services.Settings.UnitMenuKey,
                    _services.Settings.MacroHotkeyVirtualKey,
                    _services.Settings.PlayMenuKey);
            }
            ValidateDiscord(webhook, discordUserId);
            privateServerRecovery = ReadPrivateServerRecoverySelection();
            await SaveReportingSettingsAsync(webhook, discordUserId);
            await SavePrivateServerRecoverySettingsAsync(privateServerRecovery);
        }
        catch (Exception error)
        {
            PhaseText.Text = error.Message;
            AppendLog($"ERROR: {error.Message}");
            return;
        }

        LogText.Clear();
        _runStarted = DateTimeOffset.Now;
        _macroOwned = true;
        _runtimeTimer.Start();
        MacroProgress.Value = 0;
        VictoriesText.Text = "0";
        DefeatsText.Text = "0";
        AppendLog($"Starting macro plan '{plan.Name}'.");

        IProgress<MacroProgress> progress = new InlineProgress<MacroProgress>(value =>
        {
            _services.DeepDebug.RecordProgress(value);
            _services.DiagnosticCapture.RecordActionState($"{value.Phase}: {value.Message}");
            Dispatcher.BeginInvoke(() =>
            {
                PhaseText.Text = value.Message;
                MacroProgress.Value = Math.Clamp(value.Percent, 0, 100);
            });
        });

        await _services.Coordinator.RunNowAsync(
            "Macro plan",
            token => RunPlanWithFailureHandlingAsync(
                plan,
                webhook,
                discordUserId,
                playMenuKey,
                unitMenuKey,
                privateServerRecovery.Target,
                progress,
                token),
            new DeepDebugOperationContext { MacroPlanId = plan.Id });
    }

    private Task<ScheduledTaskResult> ExecuteTaskAsync(
        MacroTaskDefinition task,
        Func<ScheduledTaskResult, CancellationToken, Task<ScheduledTaskContinuation>> recordResult,
        string webhook,
        string discordUserId,
        char playMenuKey,
        char? unitMenuKey,
        IProgress<MacroProgress> progress,
        CancellationToken cancellationToken)
    {
        Dispatcher.BeginInvoke(() => CurrentTaskText.Text = $"Current task: {Label(task.Kind)} - {task.Name}");
        return task.Kind switch
        {
            MacroTaskKind.Challenge => ExecuteChallengeAsync(task, webhook, discordUserId, playMenuKey, unitMenuKey, progress, cancellationToken),
            MacroTaskKind.Expedition => ExecuteExpeditionAsync(task, recordResult, webhook, discordUserId, playMenuKey, unitMenuKey, progress, cancellationToken),
            MacroTaskKind.Story => ExecuteStoryAsync(task, recordResult, webhook, discordUserId, playMenuKey, unitMenuKey, progress, cancellationToken),
            MacroTaskKind.Raid => ExecuteRaidAsync(task, recordResult, webhook, discordUserId, playMenuKey, unitMenuKey, progress, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(task), task.Kind, "Unknown macro task type."),
        };
    }

    private async Task<ScheduledTaskResult> ExecuteChallengeAsync(
        MacroTaskDefinition task,
        string webhook,
        string discordUserId,
        char playMenuKey,
        char? unitMenuKey,
        IProgress<MacroProgress> progress,
        CancellationToken cancellationToken)
    {
        ChallengePreset preset = await _services.ChallengePresets.LoadAsync(task.PresetId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Challenge preset '{task.PresetId}' could not be loaded.");
        preset.ValidateReady();
        IDetectorPack detector = await LoadDetectorAsync(preset.DetectorPackId, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<ChallengeMapId, ChallengeMapRuntimeModels> models = await LoadChallengeModelsAsync(preset, cancellationToken).ConfigureAwait(false);
        ChallengeRunSummary? summary = null;
        await _services.Challenges.RunAsync(
            preset,
            models,
            detector,
            webhook,
            playMenuKey,
            progress,
            entry => DispatchLog(entry),
            value => summary = value,
            cancellationToken,
            (error, token) => HandleRecoverableFailureAsync("Challenge Macro", webhook, discordUserId, error, token),
            maximumCompletedRuns: 1,
            returnWhenUnavailable: true,
            unitMenuKey).ConfigureAwait(false);

        ChallengeRunSummary result = summary ?? throw new InvalidOperationException("Challenge task returned without a run summary.");
        return result.Completed > 0
            ? new ScheduledTaskResult(result.Victories, result.Defeats, result.Runtime)
            : new ScheduledTaskResult(0, 0, result.Runtime, result.WaitingUntilUtc ?? DateTimeOffset.UtcNow + SafeSkipDelay, Skipped: true);
    }

    private async Task<ScheduledTaskResult> ExecuteExpeditionAsync(
        MacroTaskDefinition task,
        Func<ScheduledTaskResult, CancellationToken, Task<ScheduledTaskContinuation>> recordResult,
        string webhook,
        string discordUserId,
        char playMenuKey,
        char? unitMenuKey,
        IProgress<MacroProgress> progress,
        CancellationToken cancellationToken)
    {
        ExpeditionPreset preset = await _services.Presets.LoadAsync(task.PresetId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Expedition preset '{task.PresetId}' could not be loaded.");
        CameraModel camera = await _services.CameraModels.LoadAsync(preset.CameraModelId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected Expedition camera model could not be loaded.");
        PlacementModel placement = await _services.PlacementModels.LoadAsync(preset.PlacementModelId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected Expedition placement model could not be loaded.");
        IDetectorPack detector = await LoadDetectorAsync(preset.DetectorPackId, cancellationToken).ConfigureAwait(false);
        ExpeditionRunSummary? summary = null;
        await _services.Expeditions.RunAsync(
            preset,
            camera,
            placement,
            detector,
            webhook,
            playMenuKey,
            progress,
            entry => DispatchLog(entry),
            value => summary = value,
            cancellationToken,
            stopAfterCurrentRunUtc: null,
            recoverableFailure: (error, token) => HandleRecoverableFailureAsync("Expeditions Macro", webhook, discordUserId, error, token),
            // A cycling Expedition runs exactly one run, then returns so the scheduler can rotate
            // to the next enabled Expedition preset. A non-cycling Expedition keeps repeating.
            maximumRuns: task.Cycle ? 1 : null,
            unitMenuKey,
            continueScheduledRoute: async (victories, defeats, runtime, token) =>
                await recordResult(new ScheduledTaskResult(victories, defeats, runtime), token).ConfigureAwait(false) == ScheduledTaskContinuation.RepeatStage).ConfigureAwait(false);

        ExpeditionRunSummary result = summary ?? throw new InvalidOperationException("Expedition task returned without a run summary.");
        return result.Repeats > 0
            ? new ScheduledTaskResult(result.Victories, result.Defeats, result.Runtime)
            : new ScheduledTaskResult(0, 0, result.Runtime, DateTimeOffset.UtcNow + SafeSkipDelay, Skipped: true);
    }

    private async Task<ScheduledTaskResult> ExecuteStoryAsync(
        MacroTaskDefinition task,
        Func<ScheduledTaskResult, CancellationToken, Task<ScheduledTaskContinuation>> recordResult,
        string webhook,
        string discordUserId,
        char playMenuKey,
        char? unitMenuKey,
        IProgress<MacroProgress> progress,
        CancellationToken cancellationToken)
    {
        StoryPreset preset = await _services.StoryPresets.LoadAsync(task.PresetId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Story preset '{task.PresetId}' could not be loaded.");
        StageRuntimeModels models = await LoadStageModelsAsync(
            preset.CameraModelId,
            preset.PrestartPlacementModelId,
            preset.DelayedPlacementModelId,
            cancellationToken).ConfigureAwait(false);
        IDetectorPack detector = await LoadDetectorAsync(AnimeExpeditionsDetectorSpec.PackId, cancellationToken).ConfigureAwait(false);
        try
        {
            StageRunResult result = await _services.Stages.RunStoryAsync(
                preset,
                models,
                detector,
                webhook,
                playMenuKey,
                unitMenuKey,
                progress,
                entry => DispatchLog(entry),
                cancellationToken,
                continueScheduledRoute: async (victories, defeats, runtime, token) =>
                    await recordResult(new ScheduledTaskResult(victories, defeats, runtime), token).ConfigureAwait(false) == ScheduledTaskContinuation.RepeatStage).ConfigureAwait(false);
            return ToScheduledResult(result);
        }
        catch (CameraAlignmentException error)
        {
            await HandleRecoverableFailureAsync("Story Macro", webhook, discordUserId, error, cancellationToken).ConfigureAwait(false);
            DispatchLog(new MacroEvent(DateTimeOffset.Now, MacroEventLevel.Warning, $"Story task skipped for {SafeSkipDelay.TotalMinutes:0} minutes after camera alignment failed.", "camera_alignment_skipped", error.BestConfidence));
            return new ScheduledTaskResult(0, 0, TimeSpan.Zero, DateTimeOffset.UtcNow + SafeSkipDelay, Skipped: true);
        }
    }

    private async Task<ScheduledTaskResult> ExecuteRaidAsync(
        MacroTaskDefinition task,
        Func<ScheduledTaskResult, CancellationToken, Task<ScheduledTaskContinuation>> recordResult,
        string webhook,
        string discordUserId,
        char playMenuKey,
        char? unitMenuKey,
        IProgress<MacroProgress> progress,
        CancellationToken cancellationToken)
    {
        RaidPreset preset = await _services.RaidPresets.LoadAsync(task.PresetId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Raid preset '{task.PresetId}' could not be loaded.");
        StageRuntimeModels models = await LoadStageModelsAsync(
            preset.CameraModelId,
            preset.PrestartPlacementModelId,
            preset.DelayedPlacementModelId,
            cancellationToken).ConfigureAwait(false);
        IDetectorPack detector = await LoadDetectorAsync(AnimeExpeditionsDetectorSpec.PackId, cancellationToken).ConfigureAwait(false);
        try
        {
            StageRunResult result = await _services.Stages.RunRaidAsync(
                preset,
                models,
                detector,
                webhook,
                playMenuKey,
                unitMenuKey,
                progress,
                entry => DispatchLog(entry),
                cancellationToken,
                continueScheduledRoute: async (victories, defeats, runtime, token) =>
                    await recordResult(new ScheduledTaskResult(victories, defeats, runtime), token).ConfigureAwait(false) == ScheduledTaskContinuation.RepeatStage).ConfigureAwait(false);
            return ToScheduledResult(result);
        }
        catch (CameraAlignmentException error)
        {
            await HandleRecoverableFailureAsync("Raid Macro", webhook, discordUserId, error, cancellationToken).ConfigureAwait(false);
            DispatchLog(new MacroEvent(DateTimeOffset.Now, MacroEventLevel.Warning, $"Raid task skipped for {SafeSkipDelay.TotalMinutes:0} minutes after camera alignment failed.", "camera_alignment_skipped", error.BestConfidence));
            return new ScheduledTaskResult(0, 0, TimeSpan.Zero, DateTimeOffset.UtcNow + SafeSkipDelay, Skipped: true);
        }
    }

    private async Task<IReadOnlyDictionary<ChallengeMapId, ChallengeMapRuntimeModels>> LoadChallengeModelsAsync(
        ChallengePreset preset,
        CancellationToken cancellationToken)
    {
        Dictionary<ChallengeMapId, ChallengeMapRuntimeModels> result = [];
        foreach (ChallengeMapProfile profile in preset.Maps)
        {
            CameraModel camera = await _services.CameraModels.LoadAsync(profile.CameraModelId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The {Label(profile.Map)} camera model could not be loaded.");
            PlacementModel? prestart = await LoadOptionalPlacementAsync(profile.PrestartPlacementModelId, cancellationToken).ConfigureAwait(false);
            PlacementModel? delayed = await LoadOptionalPlacementAsync(profile.DelayedPlacementModelId, cancellationToken).ConfigureAwait(false);
            result[profile.Map] = new ChallengeMapRuntimeModels(camera, prestart, delayed);
        }
        return result;
    }

    private async Task<StageRuntimeModels> LoadStageModelsAsync(
        string cameraId,
        string prestartId,
        string delayedId,
        CancellationToken cancellationToken)
    {
        CameraModel camera = await _services.CameraModels.LoadAsync(cameraId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The selected camera model could not be loaded.");
        PlacementModel? prestart = await LoadOptionalPlacementAsync(prestartId, cancellationToken).ConfigureAwait(false);
        PlacementModel? delayed = await LoadOptionalPlacementAsync(delayedId, cancellationToken).ConfigureAwait(false);
        return new StageRuntimeModels(camera, prestart, delayed);
    }

    private Task<PlacementModel?> LoadOptionalPlacementAsync(string id, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(id)
            ? Task.FromResult<PlacementModel?>(null)
            : LoadRequiredPlacementAsync(id, cancellationToken);

    private async Task<PlacementModel?> LoadRequiredPlacementAsync(string id, CancellationToken cancellationToken) =>
        await _services.PlacementModels.LoadAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Placement model '{id}' could not be loaded.");

    private async Task<IDetectorPack> LoadDetectorAsync(string id, CancellationToken cancellationToken) =>
        _services.TraceDetector(
            await _services.DetectorPacks.LoadAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Detector pack '{id}' could not be loaded."));

    private async Task HandleRecoverableFailureAsync(
        string macroName,
        string webhook,
        string discordUserId,
        Exception error,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.InvokeAsync(() => AppendLog($"RECOVERABLE: {error.Message}"));
        MacroFailureHandlingResult result = await _services.HandleMacroFailureAsync(macroName, webhook, discordUserId, error).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.InvokeAsync(() => AppendFailureHandlingResult(result));
    }

    private static ScheduledTaskResult ToScheduledResult(StageRunResult result) =>
        new(result.Victories, result.Defeats, result.Runtime);

    private async void SavePlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string webhook = CurrentWebhook();
            string discordUserId = DiscordUserIdText.Text.Trim();
            ValidateDiscord(webhook, discordUserId);
            PrivateServerRecoverySelection privateServerRecovery = ReadPrivateServerRecoverySelection();
            MacroPlan plan = await SavePlanInternalAsync();
            await SaveReportingSettingsAsync(webhook, discordUserId);
            await SavePrivateServerRecoverySettingsAsync(privateServerRecovery);
            PhaseText.Text = $"Plan '{plan.Name}' saved locally.";
        }
        catch (Exception error)
        {
            PhaseText.Text = error.Message;
        }
    }

    private async Task<MacroPlan> SavePlanInternalAsync()
    {
        MacroPlan plan = BuildPlan();
        await _services.MacroPlans.SaveAsync(plan);
        await _services.UpdateSettingsAsync(settings => settings with { SelectedMacroPlanId = plan.Id });
        await RefreshPlansAsync();
        PlanCombo.SelectedItem = _plans.FirstOrDefault(value => value.Id == plan.Id);
        return plan;
    }

    private MacroPlan BuildPlan()
    {
        ReindexRows();
        string name = PlanNameText.Text.Trim();
        MacroPlan plan = new()
        {
            Id = ModelId.FromName(name),
            Name = name,
            Tasks = TaskRows.Select(row => row.Definition).ToArray(),
            Progress = TaskRows.Select(row => row.Progress).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        plan.Validate();
        return plan;
    }

    private async void PlanCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PlanCombo.SelectedItem is not MacroPlan plan) return;
        ApplyPlan(plan);
        await _services.UpdateSettingsAsync(settings => settings with { SelectedMacroPlanId = plan.Id });
    }

    private void NewPlan_Click(object sender, RoutedEventArgs e)
    {
        PlanCombo.SelectedItem = null;
        ApplyNewPlan();
    }

    private void ApplyNewPlan()
    {
        PlanNameText.Text = "Daily rotation";
        TaskRows.Clear();
        EmptyTasksText.Visibility = Visibility.Visible;
        ResetTaskEditor();
        ApplyTotals();
    }

    private void ApplyPlan(MacroPlan plan)
    {
        PlanNameText.Text = plan.Name;
        TaskRows.Clear();
        foreach (MacroTaskDefinition definition in plan.Tasks.OrderBy(task => task.Priority))
        {
            TaskRows.Add(new MacroTaskRow { Definition = definition, Progress = plan.ProgressFor(definition.Id) });
        }
        ReindexRows();
        ResetTaskEditor();
        ApplyTotals();
    }

    private void ApplyPlanProgress(MacroPlan plan)
    {
        Dictionary<string, MacroTaskProgress> progress = plan.Progress.ToDictionary(value => value.TaskId, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < TaskRows.Count; index++)
        {
            MacroTaskRow row = TaskRows[index];
            TaskRows[index] = new MacroTaskRow
            {
                Definition = row.Definition,
                Progress = progress.GetValueOrDefault(row.Definition.Id) ?? row.Progress,
            };
        }
        ApplyTotals();
    }

    private async void ResetProgress_Click(object sender, RoutedEventArgs e)
    {
        if (TaskRows.Count == 0) return;
        MessageBoxResult answer = MessageBox.Show(
            Window.GetWindow(this),
            "Reset victories, defeats, runtime, cooldowns, and completion for every task in this plan?",
            "Reset plan progress",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            MacroPlan reset = await _services.Scheduler.ResetProgressAsync(BuildPlan());
            ApplyPlan(reset);
            PhaseText.Text = "Plan progress reset.";
        }
        catch (Exception error)
        {
            PhaseText.Text = error.Message;
        }
    }

    private void TaskKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskPresetCombo is null) return;
        RefreshVisiblePresets();
        UpdateTaskTargetEditor();
    }

    private void TaskPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTaskTargetEditor();

    private void AddOrUpdateTask_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TaskKindCombo.SelectedItem is not NamedChoice<MacroTaskKind> kind) throw new InvalidOperationException("Choose a task mode.");
            if (TaskPresetCombo.SelectedItem is not MacroPresetChoice preset) throw new InvalidOperationException($"Create and select a {Label(kind.Value)} preset first.");
            bool runtimeTarget = IsInfiniteStory(preset);
            int target = kind.Value == MacroTaskKind.Challenge ? 1 : ParsePositiveInt(TaskTargetText, runtimeTarget ? "Runtime minutes" : "Victory target");
            string id = _editingTaskId ?? $"task-{Guid.NewGuid():N}";
            MacroTaskDefinition definition = new()
            {
                Id = id,
                Kind = kind.Value,
                PresetId = preset.Id,
                Name = preset.Name,
                Priority = 1,
                Enabled = TaskEnabledCheck.IsChecked == true,
                TargetVictories = runtimeTarget ? 1 : target,
                TargetRuntimeMinutes = runtimeTarget ? target : 180,
                CompleteOnRuntimeDefeat = runtimeTarget,
                Cycle = kind.Value == MacroTaskKind.Expedition && TaskCycleCheck.IsChecked == true,
            };
            definition.Validate();

            int existingIndex = IndexOfTask(_editingTaskId);
            MacroTaskProgress progress = existingIndex >= 0 && SameWork(TaskRows[existingIndex].Definition, definition)
                ? TaskRows[existingIndex].Progress
                : new MacroTaskProgress { TaskId = id };
            MacroTaskRow row = new() { Definition = definition, Progress = progress };
            if (existingIndex >= 0) TaskRows[existingIndex] = row;
            else TaskRows.Add(row);
            TaskEditorStatusText.Text = existingIndex >= 0 ? "Task updated. Save the plan to persist it." : "Task added. Save the plan to persist it.";
            ReindexRows();
            ResetTaskEditor();
        }
        catch (Exception error)
        {
            TaskEditorStatusText.Text = error.Message;
        }
    }

    private void EditTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MacroTaskRow row) return;
        _editingTaskId = row.Definition.Id;
        TaskKindCombo.SelectedItem = TaskKindCombo.Items.Cast<NamedChoice<MacroTaskKind>>().First(value => value.Value == row.Definition.Kind);
        RefreshVisiblePresets();
        TaskPresetCombo.SelectedItem = _visiblePresets.FirstOrDefault(value => value.Kind == row.Definition.Kind && value.Id == row.Definition.PresetId);
        TaskEnabledCheck.IsChecked = row.Definition.Enabled;
        TaskCycleCheck.IsChecked = row.Definition.Cycle;
        TaskTargetText.Text = row.Definition.CompleteOnRuntimeDefeat
            ? row.Definition.TargetRuntimeMinutes.ToString(CultureInfo.InvariantCulture)
            : row.Definition.TargetVictories.ToString(CultureInfo.InvariantCulture);
        AddTaskButton.Content = "Update task";
        CancelTaskEditButton.Visibility = Visibility.Visible;
        TaskEditorStatusText.Text = "Editing this task. Changing its preset or target resets its saved progress.";
        UpdateTaskTargetEditor();
    }

    private void CancelTaskEdit_Click(object sender, RoutedEventArgs e) => ResetTaskEditor();

    private void RemoveTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MacroTaskRow row) return;
        TaskRows.Remove(row);
        ReindexRows();
        if (_editingTaskId == row.Definition.Id) ResetTaskEditor();
        TaskEditorStatusText.Text = "Task removed. Save the plan to persist the change.";
    }

    private void MoveTaskUp_Click(object sender, RoutedEventArgs e) => MoveTask((sender as FrameworkElement)?.Tag as MacroTaskRow, -1);

    private void MoveTaskDown_Click(object sender, RoutedEventArgs e) => MoveTask((sender as FrameworkElement)?.Tag as MacroTaskRow, 1);

    private void MoveTask(MacroTaskRow? row, int direction)
    {
        if (row is null) return;
        int index = TaskRows.IndexOf(row);
        int target = index + direction;
        if (index < 0 || target < 0 || target >= TaskRows.Count) return;
        TaskRows.Move(index, target);
        ReindexRows();
        TaskEditorStatusText.Text = "Priority changed. Save the plan to persist the order.";
    }

    private void ResetTaskEditor()
    {
        _editingTaskId = null;
        AddTaskButton.Content = "Add task";
        CancelTaskEditButton.Visibility = Visibility.Collapsed;
        TaskEnabledCheck.IsChecked = true;
        TaskCycleCheck.IsChecked = false;
        TaskTargetText.Text = "1";
        UpdateTaskTargetEditor();
    }

    private void ReindexRows()
    {
        MacroTaskRow[] rows = TaskRows
            .Select((row, index) => new MacroTaskRow
            {
                Definition = row.Definition with { Priority = index + 1 },
                Progress = row.Progress,
            })
            .ToArray();
        TaskRows.Clear();
        foreach (MacroTaskRow row in rows) TaskRows.Add(row);
        EmptyTasksText.Visibility = TaskRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshPresetCatalogAsync()
    {
        _allPresets.Clear();
        _storyPresets.Clear();
        foreach (ChallengePreset preset in await _services.ChallengePresets.ListAsync()) _allPresets.Add(new MacroPresetChoice(MacroTaskKind.Challenge, preset.Id, preset.Name));
        foreach (ExpeditionPreset preset in await _services.Presets.ListAsync()) _allPresets.Add(new MacroPresetChoice(MacroTaskKind.Expedition, preset.Id, preset.Name));
        foreach (StoryPreset preset in await _services.StoryPresets.ListAsync())
        {
            _storyPresets[preset.Id] = preset;
            _allPresets.Add(new MacroPresetChoice(MacroTaskKind.Story, preset.Id, preset.Name));
        }
        foreach (RaidPreset preset in await _services.RaidPresets.ListAsync()) _allPresets.Add(new MacroPresetChoice(MacroTaskKind.Raid, preset.Id, preset.Name));
    }

    private async Task RefreshPlansAsync()
    {
        string? selected = (PlanCombo.SelectedItem as MacroPlan)?.Id;
        _plans.Clear();
        foreach (MacroPlan plan in await _services.MacroPlans.ListAsync()) _plans.Add(plan);
        PlanCombo.SelectedItem = _plans.FirstOrDefault(value => value.Id == selected);
    }

    private void RefreshVisiblePresets()
    {
        MacroPresetChoice? selected = TaskPresetCombo.SelectedItem as MacroPresetChoice;
        MacroTaskKind kind = SelectedTaskKind();
        _visiblePresets.Clear();
        foreach (MacroPresetChoice preset in _allPresets.Where(value => value.Kind == kind).OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _visiblePresets.Add(preset);
        }
        TaskPresetCombo.SelectedItem = _visiblePresets.FirstOrDefault(value => value.Id == selected?.Id) ?? _visiblePresets.FirstOrDefault();
        TaskEditorStatusText.Text = _visiblePresets.Count == 0 ? $"Create a {Label(kind)} preset before adding this task." : string.Empty;
    }

    private void UpdateTaskTargetEditor()
    {
        if (TaskTargetLabel is null || TaskTargetText is null) return;
        MacroTaskKind kind = SelectedTaskKind();
        bool challenge = kind == MacroTaskKind.Challenge;
        bool runtime = TaskPresetCombo.SelectedItem is MacroPresetChoice preset && IsInfiniteStory(preset);
        TaskTargetLabel.Text = challenge ? "Schedule" : runtime ? "Runtime, min" : "Victories";
        TaskTargetText.IsEnabled = !challenge && !_services.Coordinator.IsBusy;
        if (challenge) TaskTargetText.Text = "Every reset";
        else if (runtime && !int.TryParse(TaskTargetText.Text, out _)) TaskTargetText.Text = "180";
        else if (!runtime && !int.TryParse(TaskTargetText.Text, out _)) TaskTargetText.Text = "1";
        if (TaskCycleCheck is not null)
        {
            TaskCycleCheck.Visibility = kind == MacroTaskKind.Expedition ? Visibility.Visible : Visibility.Collapsed;
            TaskCycleCheck.IsEnabled = !_services.Coordinator.IsBusy;
            if (kind != MacroTaskKind.Expedition) TaskCycleCheck.IsChecked = false;
        }
    }

    private void ApplyTotals()
    {
        VictoriesText.Text = TaskRows.Sum(row => row.Progress.Victories).ToString(CultureInfo.InvariantCulture);
        DefeatsText.Text = TaskRows.Sum(row => row.Progress.Defeats).ToString(CultureInfo.InvariantCulture);
    }

    private void CoordinatorStateChanged()
    {
        bool busy = _services.Coordinator.IsBusy;
        StartButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        PlanCombo.IsEnabled = !busy;
        PlanNameText.IsEnabled = !busy;
        TaskRowsControl.IsEnabled = !busy;
        TaskKindCombo.IsEnabled = !busy;
        TaskPresetCombo.IsEnabled = !busy;
        TaskEnabledCheck.IsEnabled = !busy;
        AddTaskButton.IsEnabled = !busy;
        CancelTaskEditButton.IsEnabled = !busy;
        ResetProgressButton.IsEnabled = !busy;
        WebhookPassword.IsEnabled = !busy;
        WebhookVisible.IsEnabled = !busy;
        ShowWebhookCheck.IsEnabled = !busy;
        DiscordUserIdText.IsEnabled = !busy;
        TestWebhookButton.IsEnabled = !busy && !_testingWebhook;
        SetPrivateServerRecoveryControlsEnabled(!busy);
        UpdateTaskTargetEditor();

        if (!busy && _macroOwned)
        {
            _macroOwned = false;
            _runtimeTimer.Stop();
            CurrentTaskText.Text = "Current task: none";
            PhaseText.Text = "Plan stopped. Roblox remains at the standard client size.";
            AppendLog("Macro plan stopped.");
        }
    }

    private void UpdateHotkeyText()
    {
        string hotkey = _services.Hotkey.DisplayName;
        StartButton.Content = $"Start plan  {hotkey}";
        StopButton.Content = $"Stop plan  {hotkey}";
    }

    private void UpdateRuntime()
    {
        if (_runStarted is not null) RuntimeText.Text = (DateTimeOffset.Now - _runStarted.Value).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private void ShowWebhook_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowWebhookCheck.IsChecked == true)
        {
            WebhookVisible.Text = WebhookPassword.Password;
            WebhookPassword.Visibility = Visibility.Collapsed;
            WebhookVisible.Visibility = Visibility.Visible;
        }
        else
        {
            WebhookPassword.Password = WebhookVisible.Text;
            WebhookVisible.Visibility = Visibility.Collapsed;
            WebhookPassword.Visibility = Visibility.Visible;
        }
    }

    private async void TestWebhook_Click(object sender, RoutedEventArgs e)
    {
        string webhook = CurrentWebhook();
        string discordUserId = DiscordUserIdText.Text.Trim();
        WebhookStatusText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(webhook))
        {
            WebhookStatusText.Text = "Enter a webhook first.";
            return;
        }
        if (!DiscordWebhookClient.ValidateWebhookUrl(webhook))
        {
            WebhookStatusText.Text = "Enter a valid Discord webhook URL.";
            return;
        }
        try
        {
            ValidateDiscord(webhook, discordUserId);
        }
        catch (Exception error)
        {
            WebhookStatusText.Text = error.Message;
            return;
        }

        _testingWebhook = true;
        TestWebhookButton.IsEnabled = false;
        TestWebhookButton.Content = "Sending...";
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            await _services.TestDiscordWebhookAsync(webhook, timeout.Token);
            await SaveReportingSettingsAsync(webhook, discordUserId);
            WebhookStatusText.Text = "Test message sent.";
        }
        catch (OperationCanceledException)
        {
            WebhookStatusText.Text = "Test timed out.";
        }
        catch (Exception error)
        {
            WebhookStatusText.Text = $"Test failed: {error.Message}";
        }
        finally
        {
            _testingWebhook = false;
            TestWebhookButton.Content = "Test webhook";
            TestWebhookButton.IsEnabled = !_services.Coordinator.IsBusy;
        }
    }

    private Task SaveReportingSettingsAsync(string webhook, string discordUserId) => _services.UpdateSettingsAsync(settings => settings with
    {
        EncryptedWebhook = _services.SecretProtector.Protect(webhook),
        DiscordErrorUserId = discordUserId,
    });

    private void DispatchLog(MacroEvent entry)
    {
        _services.DeepDebug.RecordMacroEvent(entry);
        _services.DiagnosticCapture.RecordActionState($"{entry.State ?? entry.Level.ToString()}: {entry.Message}");
        Dispatcher.BeginInvoke(() => AppendLog(entry.Level == MacroEventLevel.Error ? $"ERROR: {entry.Message}" : entry.Message));
    }

    private void AppendFailureHandlingResult(MacroFailureHandlingResult result)
    {
        if (result.DiagnosticArchivePath is not null) AppendLog($"Automatic error diagnostics saved to {Path.GetFileName(result.DiagnosticArchivePath)}.");
        if (result.DiagnosticError is not null) AppendLog($"ERROR: Automatic error diagnostics: {result.DiagnosticError}");
        if (result.DiscordPingsSent) AppendLog($"Sent {DiscordWebhookClient.ErrorPingCount} Discord error alerts.");
        if (result.DiscordError is not null) AppendLog($"ERROR: Discord error alerts: {result.DiscordError}");
    }

    private void AppendLog(string message)
    {
        if (message.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) _services.Log.Error(message[6..].Trim());
        else _services.Log.Info(message);
        LogText.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        if (LogText.LineCount > 500)
        {
            int removeThrough = LogText.GetCharacterIndexFromLineIndex(LogText.LineCount - 500);
            LogText.Text = LogText.Text[removeThrough..];
            LogText.CaretIndex = LogText.Text.Length;
        }
        LogText.ScrollToEnd();
    }

    private void ValidateDiscord(string webhook, string discordUserId)
    {
        if (!DiscordWebhookClient.ValidateWebhookUrl(webhook)) throw new InvalidOperationException("Enter a valid Discord webhook URL, or leave it blank.");
        if (!DiscordWebhookClient.ValidateDiscordUserId(discordUserId)) throw new InvalidOperationException("Enter a valid Discord user ID, or leave it blank.");
        if (discordUserId.Length > 0 && webhook.Length == 0) throw new InvalidOperationException("A Discord webhook is required when an error-ping user ID is entered.");
    }

    private string CurrentWebhook() => ShowWebhookCheck.IsChecked == true ? WebhookVisible.Text.Trim() : WebhookPassword.Password.Trim();

    private MacroTaskKind SelectedTaskKind() => (TaskKindCombo.SelectedItem as NamedChoice<MacroTaskKind>)?.Value ?? MacroTaskKind.Challenge;

    private bool IsInfiniteStory(MacroPresetChoice preset) =>
        preset.Kind == MacroTaskKind.Story &&
        _storyPresets.TryGetValue(preset.Id, out StoryPreset? story) &&
        story.RunKind == StoryRunKind.Infinite;

    private int IndexOfTask(string? id)
    {
        if (id is null) return -1;
        for (int index = 0; index < TaskRows.Count; index++)
        {
            if (TaskRows[index].Definition.Id == id) return index;
        }
        return -1;
    }

    private static bool SameWork(MacroTaskDefinition left, MacroTaskDefinition right) =>
        left.Kind == right.Kind &&
        left.PresetId == right.PresetId &&
        left.TargetVictories == right.TargetVictories &&
        left.TargetRuntimeMinutes == right.TargetRuntimeMinutes &&
        left.CompleteOnRuntimeDefeat == right.CompleteOnRuntimeDefeat &&
        left.Cycle == right.Cycle;

    private static int ParsePositiveInt(TextBox field, string label) =>
        int.TryParse(field.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : throw new InvalidDataException($"{label} must be a positive whole number.");

    private static string Label(MacroTaskKind kind) => kind switch
    {
        MacroTaskKind.Challenge => "Challenge",
        MacroTaskKind.Expedition => "Expedition",
        MacroTaskKind.Story => "Story",
        MacroTaskKind.Raid => "Raid",
        _ => kind.ToString(),
    };

    private static string Label(ChallengeMapId map) => map switch
    {
        ChallengeMapId.SchoolGrounds => "School Grounds",
        ChallengeMapId.FlowerForest => "Flower Forest",
        ChallengeMapId.RoseKingdom => "Rose Kingdom",
        ChallengeMapId.FairyKingForest => "Fairy King Forest",
        ChallengeMapId.KingsTomb => "King's Tomb",
        _ => map.ToString(),
    };
}
