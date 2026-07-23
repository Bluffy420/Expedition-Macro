using ExpeditionsMacro.Automation.Camera;
using ExpeditionsMacro.Core.Abstractions;
using ExpeditionsMacro.Core.Geometry;
using ExpeditionsMacro.Core.Imaging;
using ExpeditionsMacro.Core.Models;
using ExpeditionsMacro.Core.Persistence;
using ExpeditionsMacro.Core.Runtime;
using ExpeditionsMacro.Vision;
using ExpeditionsMacro.Vision.Camera;
using ExpeditionsMacro.Vision.Infrastructure;

namespace ExpeditionsMacro.Tests;

public sealed class CameraAlignmentTests
{
    private static readonly ScreenRegion[] TestRegions =
    [
        new(96, 110, 166, 108),
        new(546, 110, 166, 108),
        new(96, 250, 166, 108),
        new(321, 250, 166, 108),
    ];

    [Fact]
    public void CameraCalibrationSettings_DefaultsToStandardCameraPreparation()
    {
        CameraCalibrationSettings settings = new();

        Assert.Equal(30, settings.ArrowHoldMilliseconds);
        Assert.Equal(200, settings.SettleMilliseconds);
        Assert.Equal(30, settings.ZoomTicks);
        Assert.Equal(1800, settings.PitchDragPixels);
        settings.Validate();
    }

    [Fact]
    public void CameraCalibrationSettings_AllowsOneMillisecondArrowHold()
    {
        CameraCalibrationSettings settings = new() { ArrowHoldMilliseconds = 1 };

        settings.Validate();
    }

    [Fact]
    public void CameraCalibrationSettings_RejectsZeroMillisecondArrowHold()
    {
        CameraCalibrationSettings settings = new() { ArrowHoldMilliseconds = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
    }

    [Fact]
    public async Task SelectRegion_ResizesBeforeSelectionStoresRelativeCoordinatesAndKeepsStandardSize()
    {
        FakeAutomation automation = new(VisionScorerTests.Pattern(96, 72));
        CameraRegionSelectionService service = new(automation);
        ClientBounds clientDuringSelection = default;

        CameraRegionSelection? selection = await service.SelectAsync(client =>
        {
            clientDuringSelection = client;
            return new ScreenRegion(client.X + 15, client.Y + 20, 96, 72);
        });

        Assert.NotNull(selection);
        Assert.Equal(new ClientBounds(300, 200, 808, 611), clientDuringSelection);
        Assert.Equal(new ScreenRegion(15, 20, 96, 72), selection.Region);
        Assert.Equal((808, 611), automation.ResizeRequest);
        Assert.Equal(new ScreenRegion(315, 220, 96, 72), Assert.Single(automation.CapturedRegions));
        Assert.Null(automation.RestoredBounds);
    }

    [Fact]
    public async Task SelectRegion_WhenCanceled_KeepsStandardSize()
    {
        FakeAutomation automation = new(VisionScorerTests.Pattern(96, 72));
        CameraRegionSelectionService service = new(automation);

        CameraRegionSelection? selection = await service.SelectAsync(_ => null);

        Assert.Null(selection);
        Assert.Empty(automation.CapturedRegions);
        Assert.Null(automation.RestoredBounds);
    }

    [Fact]
    public void AutomaticRegions_SelectFourStableAreasAndBuildAnnotatedGoal()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        ImageFrame[] captures = [goal, goal.Clone(), goal.Clone()];

        IReadOnlyList<ScreenRegion> regions = CameraRegionAnalyzer.SelectStableRegions(captures);
        ImageFrame composite = CameraRegionAnalyzer.BuildComposite(goal, regions);
        ImageFrame overlay = CameraRegionAnalyzer.AnnotateGoal(goal, regions);

        Assert.Equal(4, regions.Count);
        Assert.All(regions, region => Assert.True(region.FitsWithin(goal.Width, goal.Height)));
        Assert.Equal(3, regions.Select(region => Math.Clamp((int)(3 * (region.X + region.Width / 2d) / goal.Width), 0, 2)).Distinct().Count());
        Assert.Equal((304, 192, PixelFormat.Gray8), (composite.Width, composite.Height, composite.Format));
        Assert.Equal((goal.Width, goal.Height, PixelFormat.Rgb24), (overlay.Width, overlay.Height, overlay.Format));
        Assert.False(goal.Pixels.SequenceEqual(overlay.Pixels));
    }

    [Fact]
    public void RegisteredCameraScore_RecoversSmallTranslationAndScale()
    {
        ImageFrame source = VisionScorerTests.Pattern(304, 192);
        ImageFrame goal = VisionScorer.PrepareGray(source);
        ImageFrame shifted = VisionScorer.PrepareGray(Transform(source, 1.025, 5), goal.Width, goal.Height);

        CameraRegisteredMatch match = CameraRegisteredScorer.Score(goal, shifted);

        Assert.True(match.Score > 0.75, $"Registered score was {match.Score:P1}.");
        Assert.Equal(0.975, match.Scale, 3);
    }

    [Fact]
    public void RegisteredCameraScore_RecoversRuntimeProjectionDriftOutsideThumbnailWindow()
    {
        ImageFrame source = VisionScorerTests.Pattern(304, 192);
        ImageFrame goal = VisionScorer.PrepareGray(source);
        ImageFrame shifted = VisionScorer.PrepareGray(Transform(source, 1.0, 4, -10), goal.Width, goal.Height);

        CameraRegisteredMatch thumbnailBound = CameraRegisteredScorer.Score(goal, shifted);
        CameraRegisteredMatch fullComposite = CameraRegisteredScorer.ScoreComposite(goal, shifted);

        Assert.True(thumbnailBound.Score < 0.72, $"Tight registration unexpectedly scored {thumbnailBound.Score:P1}.");
        Assert.True(fullComposite.Score > 0.80, $"Full-composite registration scored only {fullComposite.Score:P1}.");
        Assert.InRange(Math.Abs(fullComposite.OffsetX), 3, 5);
        Assert.InRange(Math.Abs(fullComposite.OffsetY), 9, 11);
    }

    [Fact]
    public void RegisteredCameraScore_RealRuntimeProjectionDriftPassesWithoutAcceptingWrongYaw()
    {
        const double modelThreshold = 0.7158913260242528;
        string directory = Path.Combine(TestPaths.CameraRotations, "RuntimeProjectionDrift");
        ImageFrame reference = ImageCodec.Load(Path.Combine(directory, "reference.png"), PixelFormat.Gray8);
        ImageFrame matching = ImageCodec.Load(Path.Combine(directory, "matching-projection-shift.png"), PixelFormat.Gray8);
        ImageFrame wrongYaw = ImageCodec.Load(Path.Combine(directory, "wrong-yaw.png"), PixelFormat.Gray8);

        CameraRegisteredMatch tight = CameraRegisteredScorer.Score(reference, matching);
        CameraRegisteredMatch registered = CameraRegisteredScorer.ScoreComposite(reference, matching);
        CameraRegisteredMatch wrong = CameraRegisteredScorer.ScoreComposite(reference, wrongYaw);
        CameraRegisteredMatch[] tiles = Enumerable.Range(0, 4)
            .Select(index =>
            {
                ScreenRegion tile = new(
                    index % 2 * CameraRegionAnalyzer.CompositeTileWidth,
                    index / 2 * CameraRegionAnalyzer.CompositeTileHeight,
                    CameraRegionAnalyzer.CompositeTileWidth,
                    CameraRegionAnalyzer.CompositeTileHeight);
                return CameraRegisteredScorer.Score(
                    reference.Crop(tile),
                    matching.Crop(tile),
                    CameraRegisteredScorer.FullCompositeMaximumHorizontalTranslation,
                    CameraRegisteredScorer.FullCompositeMaximumVerticalTranslation);
            })
            .ToArray();
        string tileDetails = string.Join(", ", tiles.Select(match => $"{match.Score:P1}@({match.OffsetX},{match.OffsetY})"));

        Assert.True(tight.Score < modelThreshold, $"The legacy bound unexpectedly scored {tight.Score:P1}.");
        Assert.True(registered.Score >= modelThreshold, $"Projection-drift score was {registered.Score:P1} at ({registered.OffsetX}, {registered.OffsetY}); tiles: {tileDetails}.");
        Assert.True(wrong.Score < modelThreshold, $"Wrong-yaw score was {wrong.Score:P1} at ({wrong.OffsetX}, {wrong.OffsetY}).");
    }

    [Fact]
    public void RegisteredCameraScore_ExpandedVerticalProjectionDoesNotAcceptNearbyYaw()
    {
        ImageFrame goalClient = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        ImageFrame nearbyYawClient = Shift(goalClient, 24);
        ImageFrame reference = CameraRegionAnalyzer.BuildComposite(goalClient, TestRegions);
        ImageFrame nearbyYaw = CameraRegionAnalyzer.BuildComposite(nearbyYawClient, TestRegions);

        CameraRegisteredMatch match = CameraRegisteredScorer.ScoreComposite(reference, nearbyYaw);

        Assert.True(match.Score < 0.80, $"Nearby-yaw score was {match.Score:P1} at ({match.OffsetX}, {match.OffsetY}).");
    }

    [Fact]
    public void RegisteredCameraScore_HandlesIdenticalClientThumbnail()
    {
        ImageFrame full = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        ImageFrame goal = VisionScorer.PrepareGray(full, 160, 101);

        CameraRegisteredMatch match = CameraRegisteredScorer.Score(goal, goal);

        Assert.True(match.Score > 0.95, $"Registered score was {match.Score:P1}.");
    }

    [Fact]
    public void RegisteredCameraScore_ToleratesWindowedDefocusWithoutAcceptingWrongYaw()
    {
        ImageFrame goalClient = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        ImageFrame blurredGoalClient = VisionScorerTests.Blur(goalClient, 9);
        ImageFrame blurredWrongClient = VisionScorerTests.Blur(Shift(goalClient, 24), 9);
        ImageFrame reference = CameraRegionAnalyzer.BuildComposite(goalClient, TestRegions);
        ImageFrame blurredGoal = CameraRegionAnalyzer.BuildComposite(blurredGoalClient, TestRegions);
        ImageFrame blurredWrong = CameraRegionAnalyzer.BuildComposite(blurredWrongClient, TestRegions);

        CameraRegisteredMatch goal = CameraRegisteredScorer.ScoreComposite(reference, blurredGoal);
        CameraRegisteredMatch wrong = CameraRegisteredScorer.ScoreComposite(reference, blurredWrong);

        Assert.True(goal.Score > 0.84, $"Defocused goal score was {goal.Score:P1}.");
        Assert.True(wrong.Score < goal.Score - 0.15, $"Defocused goal {goal.Score:P1}, wrong-yaw {wrong.Score:P1}.");
    }

    [Fact]
    public void HueSimilarity_ReranksMatchingMapColorWithoutOverridingGeometry()
    {
        ImageFrame cyan = SolidRgb(48, 32, 20, 190, 210);
        ImageFrame violet = SolidRgb(48, 32, 170, 40, 210);

        double same = CameraRegisteredScorer.HueSimilarity(cyan, cyan);
        double different = CameraRegisteredScorer.HueSimilarity(cyan, violet);

        Assert.True(same > 0.95, $"Matching hue score was {same:P1}.");
        Assert.True(different < 0.50, $"Different hue score was {different:P1}.");
    }

    [Theory]
    [InlineData("Expedition_Map1")]
    [InlineData("Expedition_Map2")]
    [InlineData("Expedition_Map3")]
    [InlineData("Story_Map1")]
    [InlineData("Story_Map2")]
    [InlineData("Story_Map3")]
    [InlineData("Story_Map4")]
    public void AutomaticRegions_RealRotationFixturesSeparateGoalFromWrongYaw(string dataset)
    {
        string directory = Path.Combine(TestPaths.CameraRotations, dataset);
        ImageFrame goal = ImageCodec.Load(Path.Combine(directory, "goal.png"));
        ImageFrame wrongYaw = ImageCodec.Load(Path.Combine(directory, "wrong-yaw.png"));

        IReadOnlyList<ScreenRegion> regions = CameraRegionAnalyzer.SelectStableRegions([goal, goal.Clone(), goal.Clone()]);
        ImageFrame reference = CameraRegionAnalyzer.BuildComposite(goal, regions);
        ImageFrame wrong = CameraRegionAnalyzer.BuildComposite(wrongYaw, regions);
        double wrongScore = VisionScorer.RobustSimilarity(reference, wrong);
        double registeredGoal = CameraRegisteredScorer.Score(reference, reference).Score;
        double registeredWrong = CameraRegisteredScorer.ScoreComposite(reference, wrong).Score;

        Assert.Equal(4, regions.Count);
        Assert.Equal(3, regions.Select(region => Math.Clamp((int)(3 * (region.X + region.Width / 2d) / goal.Width), 0, 2)).Distinct().Count());
        Assert.True(wrongScore < 0.55, $"{dataset} wrong-yaw score was {wrongScore:P1}.");
        Assert.True(registeredGoal > 0.95, $"{dataset} registered goal score was {registeredGoal:P1}.");
        Assert.True(registeredWrong < 0.72, $"{dataset} registered wrong-yaw score was {registeredWrong:P1}.");
    }

    [Fact]
    public async Task CameraModelV3_RoundTripsAutomaticRegionsAndBothYawAtlases()
    {
        string root = TestPaths.NewTemporaryDirectory();
        try
        {
            ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
            CameraModel expected = CreateModel(goal, Enumerable.Repeat(goal, 7).ToArray());
            CameraModelRepository repository = new(new AppPaths(root));

            await repository.SaveAsync(expected);
            CameraModel? actual = await repository.LoadAsync(expected.Manifest.Id);

            Assert.NotNull(actual);
            Assert.Equal(expected.Manifest.Regions, actual.Manifest.Regions);
            Assert.Equal(expected.Manifest.FullYawSteps, actual.Manifest.FullYawSteps);
            Assert.Equal(expected.Manifest.ArrowHoldMilliseconds, actual.Manifest.ArrowHoldMilliseconds);
            Assert.Equal(expected.Manifest.FineYawOffsets, actual.Manifest.FineYawOffsets);
            Assert.Equal(expected.FineYawAtlas.Count, actual.FineYawAtlas.Count);
            Assert.Equal(expected.YawAtlas.Count, actual.YawAtlas.Count);
            Assert.Equal((808, 611), (actual.GoalOverlay.Width, actual.GoalOverlay.Height));
        }
        finally
        {
            TestPaths.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task CameraModelV3_OverwriteWaitsForTemporaryReaderToReleaseTheExistingModel()
    {
        string root = TestPaths.NewTemporaryDirectory();
        FileStream? reader = null;
        Task? releaseReader = null;
        try
        {
            ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
            CameraModel expected = CreateModel(goal, Enumerable.Repeat(goal, 7).ToArray());
            CameraModel replacement = expected with
            {
                Manifest = expected.Manifest with { Name = "Replacement camera" },
            };
            AppPaths paths = new(root);
            CameraModelRepository repository = new(paths);
            await repository.SaveAsync(expected);

            string referencePath = Path.Combine(paths.CameraModels, expected.Manifest.Id, "reference.png");
            reader = new FileStream(referencePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            releaseReader = Task.Run(async () =>
            {
                await Task.Delay(350);
                reader.Dispose();
            });

            await repository.SaveAsync(replacement);
            await releaseReader;
            CameraModel? actual = await repository.LoadAsync(expected.Manifest.Id);

            Assert.NotNull(actual);
            Assert.Equal("Replacement camera", actual.Manifest.Name);
        }
        finally
        {
            reader?.Dispose();
            if (releaseReader is not null) await releaseReader;
            TestPaths.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task CameraModelCatalog_IgnoresHiddenTransactionDirectories()
    {
        string root = TestPaths.NewTemporaryDirectory();
        try
        {
            ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
            CameraModel expected = CreateModel(goal, Enumerable.Repeat(goal, 7).ToArray());
            AppPaths paths = new(root);
            CameraModelRepository repository = new(paths);
            await repository.SaveAsync(expected);

            string transaction = Path.Combine(paths.CameraModels, $".{expected.Manifest.Id}.stale.backup");
            Directory.CreateDirectory(transaction);
            File.Copy(
                Path.Combine(paths.CameraModels, expected.Manifest.Id, "manifest.json"),
                Path.Combine(transaction, "manifest.json"));

            CameraModelManifest model = Assert.Single(await repository.ListAsync());
            Assert.Equal(expected.Manifest.Id, model.Id);
        }
        finally
        {
            TestPaths.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task Align_UsesArrowAtlasForCoarseYawAndMouseOnlyForFineYaw()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        ImageFrame[] yawFrames =
        [
            goal,
            Shift(goal, 8),
            Shift(goal, 18),
            Shift(goal, 30),
            Shift(goal, 18),
            Shift(goal, 8),
            goal,
        ];
        CameraModel model = CreateModel(goal, yawFrames);
        FakeAutomation automation = new(goal)
        {
            FullYawSteps = 6,
            YawStep = 2,
            CaptureAtYaw = (yaw, _) => yawFrames[yaw],
        };
        CameraAlignmentEngine engine = new(automation, new NullCameraRepository());

        List<MacroProgress> updates = [];
        double score = await engine.AlignAsync(model, progress: new InlineProgress<MacroProgress>(updates.Add));

        Assert.True(score > 0.90, $"Alignment score was {score:P1}.");
        Assert.Equal((808, 611), automation.ResizeRequest);
        Assert.Null(automation.RestoredBounds);
        Assert.Contains(CameraYawDirection.Left, automation.ArrowPulses);
        Assert.NotEmpty(automation.Drags);
        Assert.Equal(0, automation.YawStep);
        Assert.Equal(2, automation.MoveToCenterCount);
        Assert.Equal(2, automation.ShiftLockKeys.Count);
        Assert.All(automation.DragShiftLockStates, state => Assert.True(state));
        Assert.Contains(updates, update => update.Message.Contains("Final verification:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Align_EnablesShiftLockBeforeEveryCameraDragAndRestoresIt()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        ImageFrame wrong = Blank(goal.Width, goal.Height);
        CameraModel model = CreateModel(goal, Enumerable.Repeat(goal, 7).ToArray());
        FakeAutomation automation = new(wrong)
        {
            FullYawSteps = 6,
            CaptureAtCameraState = (_, _, shiftLock) => shiftLock ? goal : wrong,
        };
        CameraAlignmentEngine engine = new(automation, new NullCameraRepository(), shiftLockVirtualKey: () => KeyboardKey.RightControl);

        double score = await engine.AlignAsync(model);

        Assert.True(score > 0.90, $"Alignment score was {score:P1}.");
        Assert.Equal([KeyboardKey.RightControl, KeyboardKey.RightControl], automation.ShiftLockKeys);
        Assert.NotEmpty(automation.DragShiftLockStates);
        Assert.All(automation.DragShiftLockStates, state => Assert.True(state));
        Assert.False(automation.ShiftLockState);
    }

    [Fact]
    public async Task Align_WhenPitchDragFails_RestoresShiftLock()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        CameraModel model = CreateModel(goal, Enumerable.Repeat(goal, 7).ToArray());
        FakeAutomation automation = new(goal)
        {
            DragFailure = new InvalidOperationException("Synthetic pitch failure."),
        };
        CameraAlignmentEngine engine = new(automation, new NullCameraRepository());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.AlignAsync(model));

        Assert.Equal("Synthetic pitch failure.", error.Message);
        Assert.Equal(2, automation.ShiftLockKeys.Count);
        Assert.Single(automation.DragShiftLockStates);
        Assert.True(automation.DragShiftLockStates[0]);
        Assert.False(automation.ShiftLockState);
    }

    [Fact]
    public async Task Align_WhenShiftLockIsAlreadyManaged_DoesNotToggleIt()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        CameraModel model = CreateModel(goal, Enumerable.Repeat(goal, 7).ToArray());
        FakeAutomation automation = new(goal) { FullYawSteps = 6 };
        CameraAlignmentEngine engine = new(automation, new NullCameraRepository());

        await engine.AlignAsync(model, manageShiftLock: false);

        Assert.Equal(0, automation.MoveToCenterCount);
        Assert.Empty(automation.ShiftLockKeys);
    }

    [Fact]
    public async Task Align_UsesPersistedFineYawAtlasForSignedMouseCorrection()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        CameraModel model = CreateModel(goal, Enumerable.Repeat(goal, 7).ToArray());
        FakeAutomation automation = new(goal)
        {
            FullYawSteps = 6,
            CaptureAtYaw = (_, mouse) => Shift(goal, 3 + mouse),
        };
        CameraAlignmentEngine engine = new(automation, new NullCameraRepository());

        double score = await engine.AlignAsync(model, manageShiftLock: false);

        Assert.True(score > 0.90, $"Alignment score was {score:P1}.");
        Assert.Equal(-3, automation.MouseOffset);
    }

    [Fact]
    public async Task Calibrate_SelectsRegionsLearnsArrowTurnAndRestoresShiftLock()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        ImageFrame wrong = Blank(goal.Width, goal.Height);
        FakeAutomation automation = new(goal)
        {
            FullYawSteps = 12,
            CaptureAtYaw = (yaw, mouse) => yaw == 0 ? Shift(goal, mouse) : wrong,
        };
        CameraAlignmentEngine engine = new(automation, new NullCameraRepository());

        CameraModel model = await engine.CalibrateAsync(CalibrationSettings("Automatic regions"));

        Assert.Equal(CameraModelManifest.CurrentSchemaVersion, model.Manifest.SchemaVersion);
        Assert.Equal(4, model.Manifest.Regions.Count);
        Assert.Equal(12, model.Manifest.FullYawSteps);
        Assert.Equal(13, model.Manifest.AtlasSampleCount);
        Assert.Equal(9, model.FineYawAtlas.Count);
        Assert.Equal(Enumerable.Range(-4, 9), model.Manifest.FineYawOffsets);
        Assert.Equal(0, automation.YawStep);
        Assert.Equal(0, automation.MouseOffset);
        Assert.Equal(2, automation.ShiftLockKeys.Count);
        Assert.Equal(30, automation.ZoomTicks);
        Assert.Contains((0, 1800), automation.Drags);
        Assert.All(automation.ArrowPulses.Take(12), pulse => Assert.Equal(CameraYawDirection.Right, pulse));
    }

    [Fact]
    public async Task Calibrate_FineGoalNeighborhoodRecognizesBiasedFullTurnAndRefinesToGoal()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        ImageFrame wrong = Blank(goal.Width, goal.Height);
        FakeAutomation? automation = null;
        automation = new FakeAutomation(goal)
        {
            FullYawSteps = 12,
            CaptureAtYaw = (yaw, mouse) =>
            {
                if (yaw != 0) return wrong;
                int wrapBias = automation!.ArrowPulses.Count >= 12 ? 16 : 0;
                return Shift(goal, wrapBias + mouse);
            },
        };
        CameraCalibrationSettings settings = CalibrationSettings("Fine neighborhood") with
        {
            FineSearchPixels = 16,
            MaximumSamples = 16,
        };
        List<MacroProgress> updates = [];
        CameraAlignmentEngine engine = new(automation, new NullCameraRepository());

        CameraModel model = await engine.CalibrateAsync(
            settings,
            new InlineProgress<MacroProgress>(updates.Add));

        Assert.Equal(12, model.Manifest.FullYawSteps);
        Assert.Equal(-16, automation.MouseOffset);
        Assert.Contains(updates, update => update.Message.Contains("Fine goal neighborhood ready with 33 distinct yaw views", StringComparison.Ordinal));
        Assert.Contains(updates, update => update.Message.Contains("neighborhood: 100%", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Calibrate_DoesNotToggleShiftLockWhenPoseCaptureFailsBeforeShiftSetup()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        FakeAutomation automation = new(goal)
        {
            CaptureFailure = new InvalidOperationException("Synthetic capture failure."),
        };
        CameraAlignmentEngine engine = new(automation, new NullCameraRepository());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.CalibrateAsync(CalibrationSettings("Automatic shift lock")));

        Assert.Equal("Synthetic capture failure.", error.Message);
        Assert.Equal((808, 611), automation.ResizeRequest);
        Assert.Equal(0, automation.MoveToCenterCount);
        Assert.Empty(automation.ShiftLockKeys);
    }

    [Fact]
    public async Task Align_WhenFastMatchIsLow_ArrowFullTurnFallbackFindsTheGoal()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        ImageFrame wrong = Blank(goal.Width, goal.Height);
        CameraModel model = CreateModel(goal, Enumerable.Repeat(wrong, 13).ToArray());
        FakeAutomation automation = new(wrong)
        {
            FullYawSteps = 12,
            YawStep = 3,
            CaptureAtYaw = (yaw, _) => yaw == 0 ? goal : wrong,
        };
        List<MacroProgress> updates = [];
        CameraAlignmentEngine engine = new(automation, new NullCameraRepository());

        double score = await engine.AlignAsync(model, progress: new InlineProgress<MacroProgress>(updates.Add));

        Assert.True(score > 0.90, $"Fallback alignment score was {score:P1}.");
        Assert.Contains(updates, update => update.Message.Contains("Scanning one full yaw turn", StringComparison.Ordinal));
        Assert.Contains(updates, update => update.Message.Contains("Stable goal found", StringComparison.Ordinal));
        Assert.Equal(0, automation.YawStep);
    }

    [Fact]
    public async Task Align_WhenFullTurnFindsStrongFineNeighborhood_RefinesBeforeCompletingTurn()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        ImageFrame wrong = Blank(goal.Width, goal.Height);
        ImageFrame nearby = Shift(goal, 24);
        CameraModel model = WithFineNeighborhoodReference(
            CreateModel(goal, Enumerable.Repeat(wrong, 13).ToArray()),
            offset: -4,
            nearby);
        FakeAutomation automation = new(wrong)
        {
            FullYawSteps = 12,
            CaptureAtYaw = (yaw, mouse) => yaw == 4
                ? mouse == 4 ? goal : nearby
                : wrong,
        };
        List<MacroProgress> updates = [];
        CameraAlignmentEngine engine = new(automation, new NullCameraRepository());

        double score = await engine.AlignAsync(
            model,
            manageShiftLock: false,
            progress: new InlineProgress<MacroProgress>(updates.Add));

        Assert.True(score > 0.90, $"Neighborhood alignment score was {score:P1}.");
        Assert.Contains(updates, update => update.Message.Contains("Strong saved fine-yaw neighborhood found", StringComparison.Ordinal));
        Assert.Contains(updates, update => update.Message.Contains("skipped the remaining full-turn scan", StringComparison.Ordinal));
        Assert.DoesNotContain(updates, update => update.Message.Contains("full-turn scan complete", StringComparison.Ordinal));
        Assert.Equal(4, automation.YawStep);
    }

    [Fact]
    public async Task Align_WhenFineNeighborhoodAlreadyPasses_DoesNotRiskCoarseArrowProbeDrift()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        ImageFrame wrong = Blank(goal.Width, goal.Height);
        ImageFrame nearby = Shift(goal, 24);
        CameraModel model = WithFineNeighborhoodReference(
            CreateModel(goal, Enumerable.Repeat(wrong, 13).ToArray()),
            offset: -4,
            nearby);
        FakeAutomation automation = new(wrong)
        {
            FullYawSteps = 12,
            CaptureAtYaw = (yaw, mouse) => yaw == 4
                ? mouse == 4 ? goal : nearby
                : wrong,
            CorruptOnYawPulseAfterMouseMovement = true,
            CorruptedFrame = wrong,
        };
        CameraAlignmentEngine engine = new(automation, new NullCameraRepository());

        double score = await engine.AlignAsync(model, manageShiftLock: false);

        Assert.True(score > 0.90, $"Neighborhood alignment score was {score:P1}.");
        Assert.False(automation.CameraCorrupted);
        Assert.Equal(4, automation.YawStep);
        Assert.Equal(4, automation.MouseOffset);
    }

    [Fact]
    public async Task Align_WhenStrongFineNeighborhoodDoesNotVerify_ContinuesCurrentScan()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        ImageFrame wrong = Blank(goal.Width, goal.Height);
        ImageFrame nearby = Shift(goal, 24);
        CameraModel model = WithFineNeighborhoodReference(
            CreateModel(goal, Enumerable.Repeat(wrong, 13).ToArray()),
            offset: -4,
            nearby);
        FakeAutomation automation = new(wrong)
        {
            FullYawSteps = 12,
            CaptureAtYaw = (yaw, _) => yaw switch
            {
                4 => nearby,
                8 => goal,
                _ => wrong,
            },
        };
        List<MacroProgress> updates = [];
        CameraAlignmentEngine engine = new(automation, new NullCameraRepository());

        double score = await engine.AlignAsync(
            model,
            manageShiftLock: false,
            progress: new InlineProgress<MacroProgress>(updates.Add));

        Assert.True(score > 0.90, $"Fallback alignment score was {score:P1}.");
        Assert.Contains(updates, update => update.Message.Contains("continued the current turn", StringComparison.Ordinal));
        Assert.Contains(updates, update => update.Message.Contains("Stable goal found during full-turn scan", StringComparison.Ordinal));
        Assert.DoesNotContain(updates, update => update.Message.Contains("skipped the remaining full-turn scan", StringComparison.Ordinal));
        Assert.Equal(8, automation.YawStep);
    }

    [Fact]
    public async Task Align_WhenArrowFullTurnFallbackStaysBelowTarget_StopsWithFailure()
    {
        ImageFrame goal = VisionScorerTests.Pattern(RobloxClientProfile.Width, RobloxClientProfile.Height);
        ImageFrame wrong = Blank(goal.Width, goal.Height);
        CameraModel model = CreateModel(goal, Enumerable.Repeat(wrong, 13).ToArray());
        FakeAutomation automation = new(wrong)
        {
            FullYawSteps = 12,
            YawStep = 3,
            CaptureAtYaw = (_, _) => wrong,
        };
        CameraAlignmentEngine engine = new(automation, new NullCameraRepository());
        List<MacroProgress> updates = [];

        CameraAlignmentException error = await Assert.ThrowsAsync<CameraAlignmentException>(() => engine.AlignAsync(
            model,
            progress: new InlineProgress<MacroProgress>(updates.Add)));

        Assert.Contains("Unit placement was not started", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, error.Attempts);
        Assert.True(automation.ArrowPulses.Count(pulse => pulse == CameraYawDirection.Right) >= 12);
        Assert.True(automation.ArrowPulses.Count(pulse => pulse == CameraYawDirection.Left) >= 12);
        Assert.Equal(2, automation.ShiftLockKeys.Count);
        Assert.All(automation.DragShiftLockStates, state => Assert.True(state));
        Assert.Contains(updates, update => update.Message.Contains("Re-observed the returned full-turn candidate", StringComparison.Ordinal));
    }

    private static CameraModel CreateModel(ImageFrame goal, IReadOnlyList<ImageFrame> yawFrames)
    {
        int fullYawSteps = yawFrames.Count - 1;
        ImageFrame reference = CameraRegionAnalyzer.BuildComposite(goal, TestRegions);
        ImageFrame[] atlas = yawFrames
            .Select(frame => VisionScorer.MakeThumbnail(CameraRegionAnalyzer.BuildComposite(frame, TestRegions)))
            .ToArray();
        int[] fineOffsets = Enumerable.Range(-4, 9).ToArray();
        ImageFrame[] fineAtlas = fineOffsets
            .Select(offset => VisionScorer.MakeThumbnail(CameraRegionAnalyzer.BuildComposite(Shift(goal, offset), TestRegions)))
            .ToArray();
        return new CameraModel(
            new CameraModelManifest
            {
                Id = "camera-test",
                Name = "Camera test",
                Regions = TestRegions,
                ClientWidth = RobloxClientProfile.Width,
                ClientHeight = RobloxClientProfile.Height,
                BaselineScore = 1,
                SuccessThreshold = 0.80,
                ArrowHoldMilliseconds = 20,
                FineStepPixels = 1,
                FineSearchPixels = 4,
                FineYawOffsets = fineOffsets,
                FullYawSteps = fullYawSteps,
                SettleMilliseconds = 0,
                AtlasSampleCount = atlas.Length,
                ScanScores = Enumerable.Repeat(0.2, atlas.Length - 1).Prepend(1).ToArray(),
                CreatedAt = DateTimeOffset.UtcNow,
            },
            reference,
            CameraRegionAnalyzer.AnnotateGoal(goal, TestRegions),
            fineAtlas,
            atlas);
    }

    private static CameraModel WithFineNeighborhoodReference(CameraModel model, int offset, ImageFrame frame)
    {
        ImageFrame[] fineAtlas = model.FineYawAtlas.ToArray();
        int index = Array.IndexOf(model.Manifest.FineYawOffsets.ToArray(), offset);
        Assert.True(index >= 0, $"Fine-yaw offset {offset} is not present in the test model.");
        fineAtlas[index] = VisionScorer.MakeThumbnail(CameraRegionAnalyzer.BuildComposite(frame, TestRegions));
        return model with { FineYawAtlas = fineAtlas };
    }

    private static CameraCalibrationSettings CalibrationSettings(string name) => new()
    {
        Name = name,
        CaptureCount = 2,
        CaptureDuration = TimeSpan.Zero,
        ArrowHoldMilliseconds = 20,
        FineStepPixels = 1,
        FineSearchPixels = 4,
        SettleMilliseconds = 25,
        MaximumSamples = 16,
    };

    private static ImageFrame Blank(int width, int height) =>
        new(width, height, PixelFormat.Rgb24, Enumerable.Range(0, width * height * 3).Select(index => (byte)((((index / 3 % width) / 16) + ((index / 3 / width) / 16)) % 2 == 0 ? 0 : 255)).ToArray(), takeOwnership: true);

    private static ImageFrame Shift(ImageFrame source, int dx)
    {
        byte[] output = new byte[source.Pixels.Length];
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                int sourceX = ((x + dx) % source.Width + source.Width) % source.Width;
                int destination = (y * source.Width + x) * 3;
                int origin = (y * source.Width + sourceX) * 3;
                Buffer.BlockCopy(source.Pixels, origin, output, destination, 3);
            }
        }
        return new ImageFrame(source.Width, source.Height, source.Format, output, takeOwnership: true);
    }

    private static ImageFrame Transform(ImageFrame source, double scale, int dx, int dy = 0)
    {
        byte[] output = new byte[source.Pixels.Length];
        double centerX = (source.Width - 1) / 2d;
        double centerY = (source.Height - 1) / 2d;
        for (int y = 0; y < source.Height; y++)
        {
            int sourceY = Math.Clamp((int)Math.Round((y - centerY) / scale + centerY + dy), 0, source.Height - 1);
            for (int x = 0; x < source.Width; x++)
            {
                int sourceX = Math.Clamp((int)Math.Round((x - centerX) / scale + centerX + dx), 0, source.Width - 1);
                int destination = (y * source.Width + x) * 3;
                int origin = (sourceY * source.Width + sourceX) * 3;
                Buffer.BlockCopy(source.Pixels, origin, output, destination, 3);
            }
        }
        return new ImageFrame(source.Width, source.Height, source.Format, output, takeOwnership: true);
    }

    private static ImageFrame SolidRgb(int width, int height, byte red, byte green, byte blue)
    {
        byte[] pixels = new byte[width * height * 3];
        for (int offset = 0; offset < pixels.Length; offset += 3)
        {
            pixels[offset] = red;
            pixels[offset + 1] = green;
            pixels[offset + 2] = blue;
        }
        return new ImageFrame(width, height, PixelFormat.Rgb24, pixels, takeOwnership: true);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

}
