using ExpeditionsMacro.Core.Imaging;
using ExpeditionsMacro.Vision;
using ExpeditionsMacro.Vision.Infrastructure;
using OpenCvSharp;

namespace ExpeditionsMacro.Tests;

public sealed class VisionScorerTests
{
    [Fact]
    public void Median_RemovesTransientObjects()
    {
        ImageFrame dark = Gray(3, 2, [10, 10, 10, 10, 10, 10]);
        ImageFrame moving = Gray(3, 2, [250, 250, 10, 10, 10, 10]);

        ImageFrame result = VisionScorer.Median([dark, dark, moving]);

        Assert.Equal(dark.Pixels, result.Pixels);
    }

    [Fact]
    public void RobustSimilarity_PrefersTheSameGeometryAcrossLightingChanges()
    {
        ImageFrame goal = Pattern(240, 180);
        ImageFrame brighter = Transform(goal, value => Math.Clamp((int)(value * 0.72 + 62), 0, 255));
        ImageFrame shifted = Shift(goal, 27, 13);
        ImageFrame reference = VisionScorer.PrepareGray(goal);

        double lightingScore = VisionScorer.ScoreFrame(reference, brighter);
        double shiftedScore = VisionScorer.ScoreFrame(reference, shifted);

        Assert.True(lightingScore > 0.72, $"Lighting score was {lightingScore:P1}.");
        Assert.True(lightingScore > shiftedScore + 0.15, $"Lighting {lightingScore:P1}, shifted {shiftedScore:P1}.");
    }

    [Fact]
    public void RobustSimilarity_ToleratesOneMovingOccluder()
    {
        ImageFrame goal = Pattern(240, 180);
        byte[] occludedPixels = goal.Pixels.ToArray();
        for (int y = 55; y < 105; y++)
        {
            for (int x = 70; x < 135; x++)
            {
                int offset = (y * goal.Width + x) * 3;
                occludedPixels[offset] = 235;
                occludedPixels[offset + 1] = 35;
                occludedPixels[offset + 2] = 190;
            }
        }
        ImageFrame occluded = new(goal.Width, goal.Height, PixelFormat.Rgb24, occludedPixels, takeOwnership: true);
        ImageFrame reference = VisionScorer.PrepareGray(goal);

        double score = VisionScorer.ScoreFrame(reference, occluded);

        Assert.True(score > 0.70, $"Occluded score was {score:P1}.");
    }

    [Fact]
    public void SuccessThreshold_StaysBelowBaselineAndAboveWrongViews()
    {
        double threshold = VisionScorer.ChooseSuccessThreshold(0.96, [0.24, 0.30, 0.35, 0.42, 0.50, 0.90]);

        Assert.InRange(threshold, 0.51, 0.945);
    }

    [Fact]
    public void RobustSimilarity_ToleratesWindowedDefocusWithoutAcceptingWrongYaw()
    {
        ImageFrame goal = Pattern(240, 180);
        ImageFrame blurred = Blur(goal, 9);
        ImageFrame blurredWrongYaw = Blur(Shift(goal, 27, 13), 9);
        ImageFrame reference = VisionScorer.PrepareGray(goal);

        double blurredScore = VisionScorer.ScoreFrame(reference, blurred);
        double wrongScore = VisionScorer.ScoreFrame(reference, blurredWrongYaw);

        Assert.True(blurredScore > 0.80, $"Defocused score was {blurredScore:P1}.");
        Assert.True(blurredScore > wrongScore + 0.15, $"Defocused {blurredScore:P1}, wrong-yaw {wrongScore:P1}.");
    }

    internal static ImageFrame Pattern(int width, int height)
    {
        byte[] pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 3;
                bool line = x % 31 < 3 || y % 27 < 3 || Math.Abs((x * 3 / 4) - y) < 3 || Math.Abs(width - x - y / 2) < 4;
                byte baseValue = line ? (byte)225 : (byte)(35 + (x * 53 + y * 29) % 65);
                pixels[offset] = baseValue;
                pixels[offset + 1] = (byte)Math.Clamp(baseValue - 12 + y % 17, 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(baseValue - 25 + x % 19, 0, 255);
            }
        }
        return new ImageFrame(width, height, PixelFormat.Rgb24, pixels, takeOwnership: true);
    }

    private static ImageFrame Transform(ImageFrame source, Func<byte, int> transform) =>
        new(source.Width, source.Height, source.Format, source.Pixels.Select(value => (byte)transform(value)).ToArray(), takeOwnership: true);

    private static ImageFrame Shift(ImageFrame source, int dx, int dy)
    {
        byte[] output = new byte[source.Pixels.Length];
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                int sourceX = (x + dx) % source.Width;
                int sourceY = (y + dy) % source.Height;
                int destination = (y * source.Width + x) * 3;
                int origin = (sourceY * source.Width + sourceX) * 3;
                Buffer.BlockCopy(source.Pixels, origin, output, destination, 3);
            }
        }
        return new ImageFrame(source.Width, source.Height, source.Format, output, takeOwnership: true);
    }

    private static ImageFrame Gray(int width, int height, byte[] pixels) => new(width, height, PixelFormat.Gray8, pixels, takeOwnership: true);

    internal static ImageFrame Blur(ImageFrame source, int kernelSize)
    {
        using Mat mat = ImageCodec.ToMat(source);
        using Mat blurred = new();
        Cv2.GaussianBlur(mat, blurred, new Size(kernelSize, kernelSize), 0);
        return ImageCodec.FromMat(blurred, source.Format);
    }
}
