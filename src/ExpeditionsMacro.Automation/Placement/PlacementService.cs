using ExpeditionsMacro.Core.Abstractions;
using ExpeditionsMacro.Core.Geometry;
using ExpeditionsMacro.Core.Models;
using ExpeditionsMacro.Core.Persistence;
using ExpeditionsMacro.Core.Runtime;
using ExpeditionsMacro.Automation.Camera;

namespace ExpeditionsMacro.Automation.Placement;

public sealed class PlacementService
{
    private readonly IRobloxAutomation _automation;
    private readonly IPlacementCaptureService _capture;
    private readonly PlacementModelRepository _models;

    public PlacementService(
        IRobloxAutomation automation,
        IPlacementCaptureService capture,
        PlacementModelRepository models)
    {
        _automation = automation;
        _capture = capture;
        _models = models;
    }

    public async Task<PlacementModel> RecordAsync(
        string name,
        int defaultDelayMilliseconds,
        bool useRecordedDelays,
        Action<PlacementCapture>? captured = null,
        Action<string>? status = null,
        CancellationToken cancellationToken = default,
        CameraAlignmentEngine? camera = null,
        CameraModel? cameraModel = null,
        IProgress<MacroProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a placement model name.", nameof(name));
        RobloxWindow window = _automation.FindWindow() ?? throw new InvalidOperationException("No visible Roblox window was found.");
        ClientBounds initial = _automation.GetClientBounds(window);
        bool resized = initial.Width != RobloxClientProfile.Width || initial.Height != RobloxClientProfile.Height;
        (int width, int height, IReadOnlyList<PlacementCapture> captures) recording;
        EnsureFocus(window);
        if (resized)
        {
            status?.Invoke($"Resizing Roblox to {RobloxClientProfile.Width} × {RobloxClientProfile.Height}.");
            await _automation.ResizeClientAsync(window, RobloxClientProfile.Width, RobloxClientProfile.Height, cancellationToken).ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        ClientBounds client = _automation.GetClientBounds(window);
        if (client.Width != RobloxClientProfile.Width || client.Height != RobloxClientProfile.Height)
        {
            throw new InvalidOperationException($"Roblox did not accept the standard {RobloxClientProfile.Width} × {RobloxClientProfile.Height} client size.");
        }
        if (cameraModel is not null)
        {
            if (camera is null) throw new ArgumentNullException(nameof(camera), "A camera alignment engine is required when a camera model is supplied.");
            status?.Invoke($"Aligning the camera with '{cameraModel.Manifest.Name}' before recording.");
            try
            {
                await camera.AlignAsync(cameraModel, window, manageShiftLock: true, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Placements recorded from an unaligned camera would not match the aligned view the
                // macro uses at runtime, so abort rather than save a misleading model.
                throw new InvalidOperationException($"Camera alignment failed before placement recording: {error.Message}", error);
            }
            EnsureFocus(window);
        }
        recording = await _capture.RecordAsync(window, captured, status, cancellationToken).ConfigureAwait(false);
        (int width, int height, IReadOnlyList<PlacementCapture> captures) = recording;
        if (captures.Count == 0) throw new InvalidOperationException("Record at least one unit placement before saving.");
        string id = ModelId.FromName(name);
        PlacementModel model = new()
        {
            Id = id,
            Name = name.Trim(),
            ClientWidth = width,
            ClientHeight = height,
            Steps = PlacementModel.FromCaptures(captures, defaultDelayMilliseconds, useRecordedDelays),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        // The global macro hotkey ends a recording by cancelling the observation token. Saving the
        // completed captures must therefore use an independent token.
        await _models.SaveAsync(model, CancellationToken.None).ConfigureAwait(false);
        return model;
    }

    public async Task PlayAsync(
        PlacementModel model,
        bool useDefaultInterval,
        int defaultIntervalMilliseconds,
        int keyHoldMilliseconds = 110,
        int afterKeyMilliseconds = 250,
        Action<int, int, PlacementStep>? stepSent = null,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        model.Validate();
        RobloxWindow window = _automation.FindWindow() ?? throw new InvalidOperationException("No visible Roblox window was found.");
        await PlayStepsAsync(window, model, model.Steps, useDefaultInterval, defaultIntervalMilliseconds, keyHoldMilliseconds, afterKeyMilliseconds, stepSent, status, cancellationToken).ConfigureAwait(false);
    }

    public async Task PlayStepsAsync(
        RobloxWindow window,
        PlacementModel model,
        IReadOnlyList<PlacementStep> steps,
        bool useDefaultInterval,
        int defaultIntervalMilliseconds,
        int keyHoldMilliseconds,
        int afterKeyMilliseconds,
        Action<int, int, PlacementStep>? stepSent,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        if (defaultIntervalMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(defaultIntervalMilliseconds));
        EnsureFocus(window);
        await EnsureSizeAsync(window, model.ClientWidth, model.ClientHeight, cancellationToken).ConfigureAwait(false);
        for (int index = 0; index < steps.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlacementStep step = steps[index];
            EnsureFocus(window);
            status?.Invoke($"Step {index + 1}/{steps.Count}: pressing top-row {step.UnitKey} for {keyHoldMilliseconds} ms.");
            await _automation.TapUnitKeyAsync(window, step.UnitKey, keyHoldMilliseconds, cancellationToken).ConfigureAwait(false);
            await Task.Delay(afterKeyMilliseconds, cancellationToken).ConfigureAwait(false);
            await EnsureSizeAsync(window, model.ClientWidth, model.ClientHeight, cancellationToken).ConfigureAwait(false);
            status?.Invoke($"Step {index + 1}/{steps.Count}: clicking relative ({step.X}, {step.Y}).");
            await _automation.ClickClientAsync(window, step.X, step.Y, cancellationToken).ConfigureAwait(false);
            stepSent?.Invoke(index + 1, steps.Count, step);
            int delay = useDefaultInterval ? defaultIntervalMilliseconds : step.DelayAfterMilliseconds;
            status?.Invoke($"Step {index + 1}/{steps.Count}: waiting {delay} ms after click.");
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureSizeAsync(RobloxWindow window, int width, int height, CancellationToken cancellationToken)
    {
        ClientBounds bounds = _automation.GetClientBounds(window);
        if (bounds.Width != width || bounds.Height != height)
        {
            await _automation.ResizeClientAsync(window, width, height, cancellationToken).ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        ClientBounds actual = _automation.GetClientBounds(window);
        if (actual.Width != width || actual.Height != height) throw new InvalidOperationException("Roblox did not accept the placement model's client size.");
    }

    private void EnsureFocus(RobloxWindow window)
    {
        if (!_automation.Focus(window)) throw new InvalidOperationException("Windows could not focus Roblox.");
    }
}
