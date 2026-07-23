using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ExpeditionsMacro.App.Models;
using ExpeditionsMacro.App.Services;
using ExpeditionsMacro.Automation.Diagnostics;
using ExpeditionsMacro.Core.Models;
using ExpeditionsMacro.Core.Persistence;
using ExpeditionsMacro.Core.Runtime;

namespace ExpeditionsMacro.App.Pages;

public partial class PlacementModelsPage : UserControl, IAppPage
{
    private readonly AppServices _services;
    private readonly ObservableCollection<PlacementModel> _models = [];
    private readonly ObservableCollection<PlacementStepRow> _steps = [];
    private PlacementModel? _selectedModel;

    private sealed record CameraChoice(string? Id, string Name);

    public PlacementModelsPage(AppServices services)
    {
        _services = services;
        InitializeComponent();
        ModelsList.ItemsSource = _models;
        StepsGrid.ItemsSource = _steps;
        _services.Coordinator.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateBusyState);
        _services.Hotkey.BindingChanged += (_, _) => Dispatcher.BeginInvoke(UpdateHotkeyText);
    }

    public Func<Task>? IdleHotkeyAction => null;

    public async Task OnShownAsync()
    {
        UpdateHotkeyText();
        await RefreshCameraModelsAsync();
        await RefreshModelsAsync(_selectedModel?.Id);
    }

    private async Task RefreshCameraModelsAsync()
    {
        IReadOnlyList<CameraModelManifest> manifests = await _services.CameraModels.ListAsync().ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            object? selected = CameraModelCombo.SelectedItem;
            string? selectedId = (selected as CameraChoice)?.Id;
            var choices = new List<CameraChoice> { new(null, "(none — record without aligning)") };
            choices.AddRange(manifests.Select(manifest => new CameraChoice(manifest.Id, manifest.Name)));
            CameraModelCombo.ItemsSource = choices;
            CameraModelCombo.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selectedId) ?? choices[0];
        });
    }

    internal async Task RefreshModelsAsync(string? selectedId = null)
    {
        IReadOnlyList<PlacementModel> models = await _services.PlacementModels.ListAsync().ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            _models.Clear();
            foreach (PlacementModel model in models) _models.Add(model);
            if (selectedId is not null) ModelsList.SelectedItem = _models.FirstOrDefault(model => model.Id == selectedId);
        });
    }

    private void ModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelsList.SelectedItem is not PlacementModel model) return;
        ApplyModel(model);
    }

    private void ApplyModel(PlacementModel model)
    {
        _selectedModel = model;
        ModelNameText.Text = model.Name;
        _steps.Clear();
        foreach (PlacementStep step in model.Steps) _steps.Add(PlacementStepRow.FromModel(step));
        StatusText.Text = $"Loaded {model.Name}.";
        DetailText.Text = $"{model.Steps.Count} placements · Roblox client {model.ClientWidth} × {model.ClientHeight}";
    }

    private void NewModel_Click(object sender, RoutedEventArgs e)
    {
        ModelsList.SelectedItem = null;
        _selectedModel = null;
        ModelNameText.Text = "Placement model";
        _steps.Clear();
        StatusText.Text = "Ready to record a new model.";
        DetailText.Text = $"Click Record placements, focus Roblox, then press {_services.Hotkey.DisplayName}.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PlacementModel model = BuildModel();
            await _services.PlacementModels.SaveAsync(model);
            _selectedModel = model;
            await RefreshModelsAsync(model.Id);
            StatusText.Text = "Placement model saved.";
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
        }
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        string name = ModelNameText.Text.Trim();
        if (name.Length == 0) { StatusText.Text = "Enter a model name."; return; }
        if (!int.TryParse(DefaultDelayText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay) || delay < 0)
        {
            StatusText.Text = "Default delay must be a non-negative whole number.";
            return;
        }
        bool useRecordedTiming = RecordedTimingCheck.IsChecked == true;

        CameraModel? cameraModel = null;
        if (CameraModelCombo.SelectedItem is CameraChoice { Id: not null } choice)
        {
            try
            {
                cameraModel = await _services.CameraModels.LoadAsync(choice.Id);
                if (cameraModel is null) { StatusText.Text = $"Camera model '{choice.Name}' could not be loaded."; return; }
            }
            catch (Exception error)
            {
                StatusText.Text = error.Message;
                return;
            }
        }

        _steps.Clear();
        CameraModel? alignmentModel = cameraModel;
        IProgress<MacroProgress> alignmentProgress = new InlineProgress<MacroProgress>(value => Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = value.Message;
            OperationProgress.Value = Math.Clamp(value.Percent, 0, 100);
        }));
        _services.Coordinator.Arm("Placement recording", async token =>
        {
            PlacementModel model = await _services.Placement.RecordAsync(
                name,
                delay,
                useRecordedTiming,
                captured: capture =>
                {
                    _services.DeepDebug.RecordEvent("placement", "capture_recorded", capture);
                    Dispatcher.BeginInvoke(() =>
                    {
                        _steps.Add(new PlacementStepRow { UnitKey = capture.UnitKey, X = capture.X, Y = capture.Y, DelayAfterMilliseconds = delay });
                        OperationProgress.Value = Math.Min(95, _steps.Count * 10);
                    });
                },
                status: message =>
                {
                    _services.DeepDebug.RecordEvent("placement", "recording_status", new { Message = message });
                    Dispatcher.BeginInvoke(() =>
                    {
                        StatusText.Text = message;
                        DetailText.Text = message.Contains("Recording", StringComparison.OrdinalIgnoreCase) ? $"Press {_services.Hotkey.DisplayName} again to finish and save." : DetailText.Text;
                    });
                },
                cancellationToken: token,
                camera: alignmentModel is null ? null : _services.Camera,
                cameraModel: alignmentModel,
                progress: alignmentProgress);
            await Dispatcher.InvokeAsync(() =>
            {
                ApplyModel(model);
                OperationProgress.Value = 100;
            });
            await RefreshModelsAsync(model.Id);
        }, new DeepDebugOperationContext
        {
            PlacementModelIds = [ModelId.FromName(name)],
            OperationSettings = new
            {
                Name = name,
                DefaultDelayMilliseconds = delay,
                UseRecordedTiming = useRecordedTiming,
            },
            RefreshReferencedModelsAfterOperation = true,
        });
        StatusText.Text = "Placement recording armed.";
        string hotkey = _services.Hotkey.DisplayName;
        DetailText.Text = $"Focus Roblox and press {hotkey} to begin. Press {hotkey} again after the final placement.";
        UpdateBusyState();
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        PlacementModel model;
        try
        {
            model = BuildModel();
            await _services.PlacementModels.SaveAsync(model);
            _selectedModel = model;
        }
        catch (Exception error)
        {
            StatusText.Text = error.Message;
            return;
        }
        if (!int.TryParse(DefaultDelayText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay) || delay < 0) delay = 900;
        bool overridePlaybackTiming = PlaybackOverrideCheck.IsChecked == true;
        _services.Coordinator.Arm("Placement playback", async token =>
        {
            await _services.Placement.PlayAsync(
                model,
                overridePlaybackTiming,
                delay,
                stepSent: (index, total, step) =>
                {
                    _services.DeepDebug.RecordEvent("placement", "playback_step", new { Index = index, Total = total, Step = step });
                    Dispatcher.BeginInvoke(() => OperationProgress.Value = 100d * index / total);
                },
                status: message =>
                {
                    _services.DeepDebug.RecordEvent("placement", "playback_status", new { Message = message });
                    Dispatcher.BeginInvoke(() => StatusText.Text = message);
                },
                cancellationToken: token);
        }, new DeepDebugOperationContext
        {
            PlacementModelIds = [model.Id],
            OperationSettings = new
            {
                Model = model.Id,
                UseDefaultInterval = overridePlaybackTiming,
                DefaultDelayMilliseconds = delay,
            },
        });
        StatusText.Text = $"Playback armed. Focus Roblox and press {_services.Hotkey.DisplayName} to begin.";
        DetailText.Text = "Roblox will temporarily match the model's recorded client size.";
        UpdateBusyState();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _services.Coordinator.Cancel();

    private void UpdateHotkeyText()
    {
        string hotkey = _services.Hotkey.DisplayName;
        RecordingDescription.Text = $"Recording starts after {hotkey} and ends when {hotkey} is pressed again.";
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        int delay = int.TryParse(DefaultDelayText.Text, out int value) ? Math.Max(0, value) : 900;
        _steps.Add(new PlacementStepRow { UnitKey = 1, X = 0, Y = 0, DelayAfterMilliseconds = delay });
        StepsGrid.SelectedIndex = _steps.Count - 1;
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (StepsGrid.SelectedItem is PlacementStepRow row) _steps.Remove(row);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int offset)
    {
        if (StepsGrid.SelectedItem is not PlacementStepRow row) return;
        int current = _steps.IndexOf(row);
        int target = current + offset;
        if (target < 0 || target >= _steps.Count) return;
        _steps.Move(current, target);
        StepsGrid.SelectedItem = row;
    }

    private async void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (ModelsList.SelectedItem is not PlacementModel model) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Delete placement model '{model.Name}'?", "Delete model", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _services.PlacementModels.Delete(model.Id);
        NewModel_Click(sender, e);
        await RefreshModelsAsync();
    }

    private PlacementModel BuildModel()
    {
        if (_steps.Count == 0) throw new InvalidOperationException("Add or record at least one placement.");
        string name = ModelNameText.Text.Trim();
        string id = _selectedModel?.Id ?? ModelId.FromName(name);
        int width = _selectedModel?.ClientWidth ?? 808;
        int height = _selectedModel?.ClientHeight ?? 611;
        PlacementModel model = new()
        {
            Id = id,
            Name = name,
            ClientWidth = width,
            ClientHeight = height,
            Steps = _steps.Select(row => row.ToModel()).ToArray(),
            CreatedAt = _selectedModel?.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        model.Validate();
        return model;
    }

    private void UpdateBusyState()
    {
        bool busy = _services.Coordinator.IsBusy;
        SaveButton.IsEnabled = !busy;
        RecordButton.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
    }
}
