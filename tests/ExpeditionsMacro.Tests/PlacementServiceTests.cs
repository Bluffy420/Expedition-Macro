using ExpeditionsMacro.Automation.Camera;
using ExpeditionsMacro.Automation.Placement;
using ExpeditionsMacro.Core.Abstractions;
using ExpeditionsMacro.Core.Geometry;
using ExpeditionsMacro.Core.Imaging;
using ExpeditionsMacro.Core.Models;
using ExpeditionsMacro.Core.Persistence;

namespace ExpeditionsMacro.Tests;

public sealed class PlacementServiceTests
{
    private static CameraModel CreateCameraModel()
    {
        ScreenRegion[] regions = [new(96, 110, 166, 108), new(546, 110, 166, 108), new(96, 250, 166, 108), new(321, 250, 166, 108)];
        ImageFrame Tile() => new(4, 4, PixelFormat.Gray8, new byte[16], takeOwnership: true);
        int[] fineOffsets = [-1, 0, 1];
        const int fullYawSteps = 3;
        CameraModelManifest manifest = new()
        {
            Id = "placement-camera",
            Name = "Placement camera",
            Regions = regions,
            ClientWidth = 808,
            ClientHeight = 611,
            BaselineScore = 1,
            SuccessThreshold = 0.5,
            ArrowHoldMilliseconds = 20,
            FineStepPixels = 1,
            FineSearchPixels = 4,
            FineYawOffsets = fineOffsets,
            FullYawSteps = fullYawSteps,
            SettleMilliseconds = 25,
            AtlasSampleCount = fullYawSteps + 1,
            ScanScores = [1, 0.2, 0.2, 0.2],
            CreatedAt = DateTimeOffset.UtcNow,
        };
        return new CameraModel(
            manifest,
            Tile(),
            new ImageFrame(808, 611, PixelFormat.Rgb24, new byte[808 * 611 * 3], takeOwnership: true),
            fineOffsets.Select(_ => Tile()).ToArray(),
            Enumerable.Range(0, fullYawSteps + 1).Select(_ => Tile()).ToArray());
    }

    [Fact]
    public async Task Record_UsesStandardClientSizeAndKeepsIt()
    {
        string root = Path.Combine(Path.GetTempPath(), $"expeditions-placement-{Guid.NewGuid():N}");
        try
        {
            FakeAutomation automation = new();
            FakeCaptureService capture = new(automation);
            PlacementService service = new(automation, capture, new PlacementModelRepository(new AppPaths(root)));

            PlacementModel model = await service.RecordAsync("Canonical placement", 900, useRecordedDelays: false);

            Assert.Equal((808, 611), automation.ResizeRequest);
            Assert.Equal((808, 611), capture.ClientSizeAtCapture);
            Assert.Equal(808, model.ClientWidth);
            Assert.Equal(611, model.ClientHeight);
            Assert.Null(automation.RestoredBounds);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Record_WhenCameraAlignmentFails_AbortsBeforeCapturing()
    {
        string root = Path.Combine(Path.GetTempPath(), $"expeditions-placement-{Guid.NewGuid():N}");
        try
        {
            FakeAutomation automation = new();
            FakeCaptureService capture = new(automation);
            PlacementService service = new(automation, capture, new PlacementModelRepository(new AppPaths(root)));
            CameraAlignmentEngine camera = new(automation, new NullCameraRepository());
            CameraModel cameraModel = CreateCameraModel();

            // The fake throws on CaptureClient, so alignment cannot succeed; recording must abort
            // before the capture service is ever invoked.
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RecordAsync("Aligned placement", 900, useRecordedDelays: false, camera: camera, cameraModel: cameraModel));

            Assert.Contains("Camera alignment failed before placement recording", error.Message, StringComparison.Ordinal);
            Assert.False(capture.RecordInvoked);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Record_WithoutCameraModel_SkipsAlignment()
    {
        string root = Path.Combine(Path.GetTempPath(), $"expeditions-placement-{Guid.NewGuid():N}");
        try
        {
            FakeAutomation automation = new();
            FakeCaptureService capture = new(automation);
            PlacementService service = new(automation, capture, new PlacementModelRepository(new AppPaths(root)));

            PlacementModel model = await service.RecordAsync("Plain placement", 900, useRecordedDelays: false);

            Assert.True(capture.RecordInvoked);
            Assert.Equal("Plain placement", model.Name);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NullCameraRepository : ICameraModelRepository
    {
        public Task<IReadOnlyList<CameraModelManifest>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CameraModelManifest>>([]);
        public Task<CameraModel?> LoadAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<CameraModel?>(null);
        public Task SaveAsync(CameraModel model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeCaptureService(FakeAutomation automation) : IPlacementCaptureService
    {
        public (int Width, int Height)? ClientSizeAtCapture { get; private set; }

        public bool RecordInvoked { get; private set; }

        public Task<(int ClientWidth, int ClientHeight, IReadOnlyList<PlacementCapture> Captures)> RecordAsync(
            RobloxWindow window,
            Action<PlacementCapture>? captured,
            Action<string>? status,
            CancellationToken cancellationToken)
        {
            RecordInvoked = true;
            ClientBounds client = automation.GetClientBounds(window);
            ClientSizeAtCapture = (client.Width, client.Height);
            PlacementCapture capture = new(1, 320, 280, 0, 100);
            captured?.Invoke(capture);
            return Task.FromResult<(int, int, IReadOnlyList<PlacementCapture>)>((client.Width, client.Height, [capture]));
        }
    }

    private sealed class FakeAutomation : IRobloxAutomation
    {
        private readonly RobloxWindow _window = new((nint)42, "Roblox");
        private ClientBounds _client = new(100, 120, 800, 599);

        public (int Width, int Height)? ResizeRequest { get; private set; }

        public WindowBounds? RestoredBounds { get; private set; }

        public RobloxWindow? FindWindow(string titleFragment = "Roblox") => _window;

        public RobloxWindow? ForegroundWindow() => _window;

        public ClientBounds GetClientBounds(RobloxWindow window) => _client;

        public WindowBounds GetWindowBounds(RobloxWindow window) => new(40, 50, 920, 720);

        public bool Focus(RobloxWindow window) => true;

        public Task ResizeClientAsync(RobloxWindow window, int width, int height, CancellationToken cancellationToken)
        {
            ResizeRequest = (width, height);
            _client = _client with { Width = width, Height = height };
            return Task.CompletedTask;
        }

        public void RestoreWindowBounds(RobloxWindow window, WindowBounds bounds) => RestoredBounds = bounds;

        public ImageFrame CaptureScreen(ScreenRegion region) => throw new NotSupportedException();

        public ImageFrame CaptureClient(RobloxWindow window) => throw new NotSupportedException();

        public Task MoveCursorToClientCenterAsync(RobloxWindow window, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ParkCursorAsync(RobloxWindow window, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ClickClientAsync(RobloxWindow window, int x, int y, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DragClientAsync(
            RobloxWindow window,
            int startX,
            int startY,
            int endX,
            int endY,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ScrollClientAsync(RobloxWindow window, int notches, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DragCameraAsync(RobloxWindow window, int deltaX, int deltaY, int chunkPixels, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PulseCameraYawAsync(RobloxWindow window, CameraYawDirection direction, int holdMilliseconds, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ZoomOutFullyAsync(RobloxWindow window, int ticks, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task TapShiftLockKeyAsync(RobloxWindow window, int virtualKey, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task TapLetterKeyAsync(RobloxWindow window, char key, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task TapUnitKeyAsync(RobloxWindow window, int unitKey, int holdMilliseconds, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
