using ExpeditionsMacro.Core.Imaging;

namespace ExpeditionsMacro.Vision.Camera;

public readonly record struct CameraRegisteredMatch(
    double Score,
    double Scale,
    int OffsetX,
    int OffsetY);

/// <summary>
/// Scores camera evidence after compensating for the small projection shifts
/// produced by Roblox camera state, UI scale rounding, and client rendering.
/// </summary>
public static class CameraRegisteredScorer
{
    public const int ThumbnailMaximumTranslation = 5;
    public const int FullCompositeMaximumHorizontalTranslation = 6;
    public const int FullCompositeMaximumVerticalTranslation = 12;

    private static readonly double[] ScaleCandidates = [0.975, 1.0, 1.025];

    public static CameraRegisteredMatch Score(
        ImageFrame reference,
        ImageFrame current,
        int maximumTranslation = ThumbnailMaximumTranslation) =>
        Score(reference, current, maximumTranslation, maximumTranslation);

    public static CameraRegisteredMatch Score(
        ImageFrame reference,
        ImageFrame current,
        int maximumHorizontalTranslation,
        int maximumVerticalTranslation)
    {
        if (reference.Format != PixelFormat.Gray8 || current.Format != PixelFormat.Gray8)
        {
            throw new ArgumentException("Registered camera inputs must be prepared grayscale images.");
        }
        if (reference.Width != current.Width || reference.Height != current.Height)
        {
            throw new ArgumentException("Registered camera inputs must have identical dimensions.");
        }
        if (maximumHorizontalTranslation is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(maximumHorizontalTranslation));
        if (maximumVerticalTranslation is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(maximumVerticalTranslation));

        CameraRegisteredMatch best = new(double.NegativeInfinity, 1, 0, 0);
        ImageFrame? bestAligned = null;
        foreach (double scale in ScaleCandidates)
        {
            ImageFrame scaled = ScaleGray(current, scale);
            (int bestX, int bestY) = FindBestTranslation(
                reference,
                scaled,
                maximumHorizontalTranslation,
                maximumVerticalTranslation);
            ImageFrame alignedFrame = TranslateGray(scaled, bestX, bestY);
            double score = ManagedStructuralSimilarity(reference, alignedFrame);
            if (score <= best.Score) continue;
            best = new CameraRegisteredMatch(
                Math.Clamp(score, 0, 1),
                scale,
                bestX,
                bestY);
            bestAligned = alignedFrame;
        }
        if (bestAligned is null) return best;
        double consensus = RegionConsensus(reference, bestAligned);
        double combined = Math.Max(best.Score, 0.88 * best.Score + 0.12 * consensus);
        return best with { Score = Math.Clamp(combined, 0, 1) };
    }

    public static CameraRegisteredMatch ScoreComposite(ImageFrame reference, ImageFrame current)
    {
        if (reference.Width != current.Width || reference.Height != current.Height)
        {
            throw new ArgumentException("Registered camera composites must have identical dimensions.");
        }
        if (reference.Width % CameraRegionAnalyzer.CompositeTileWidth != 0
            || reference.Height % CameraRegionAnalyzer.CompositeTileHeight != 0)
        {
            throw new ArgumentException("Registered camera composites do not use the expected tile geometry.");
        }

        int columns = reference.Width / CameraRegionAnalyzer.CompositeTileWidth;
        int rows = reference.Height / CameraRegionAnalyzer.CompositeTileHeight;
        int tileCount = columns * rows;
        if (columns != 2 || tileCount is < 2 or > 8)
        {
            throw new ArgumentException("Registered camera composites must contain two through eight tiles in two columns.");
        }

        CameraRegisteredMatch tight = Score(reference, current);
        List<CameraRegisteredMatch> matches = [];
        for (int index = 0; index < tileCount; index++)
        {
            var tile = new Core.Geometry.ScreenRegion(
                index % columns * CameraRegionAnalyzer.CompositeTileWidth,
                index / columns * CameraRegionAnalyzer.CompositeTileHeight,
                CameraRegionAnalyzer.CompositeTileWidth,
                CameraRegionAnalyzer.CompositeTileHeight);
            matches.Add(Score(
                reference.Crop(tile),
                current.Crop(tile),
                FullCompositeMaximumHorizontalTranslation,
                FullCompositeMaximumVerticalTranslation));
        }

        double medianX = Median(matches.Select(match => (double)match.OffsetX));
        double medianY = Median(matches.Select(match => (double)match.OffsetY));
        if (Math.Abs(medianY) <= ThumbnailMaximumTranslation) return tight;

        CameraRegisteredMatch[] inliers = matches
            .Where(match => Math.Abs(match.OffsetX - medianX) <= 2.5 && Math.Abs(match.OffsetY - medianY) <= 2.5)
            .ToArray();
        int requiredInliers = (int)Math.Ceiling(tileCount * 0.75);
        if (inliers.Length < requiredInliers) return tight;

        double registeredTiles = Median(inliers.Select(match => match.Score));
        double score = Math.Max(tight.Score, 0.75 * registeredTiles + 0.25 * tight.Score);
        return new CameraRegisteredMatch(
            Math.Clamp(score, 0, 1),
            Median(inliers.Select(match => match.Scale)),
            (int)Math.Round(medianX),
            (int)Math.Round(medianY));
    }

    private static (int X, int Y) FindBestTranslation(
        ImageFrame reference,
        ImageFrame current,
        int maximumHorizontalTranslation,
        int maximumVerticalTranslation)
    {
        int searchStep = Math.Max(maximumHorizontalTranslation, maximumVerticalTranslation) > ThumbnailMaximumTranslation ? 2 : 1;
        int sampleStride = searchStep == 1 ? 2 : 4;
        int bestX = 0;
        int bestY = 0;
        double bestCorrelation = double.NegativeInfinity;
        for (int y = -maximumVerticalTranslation; y <= maximumVerticalTranslation; y += searchStep)
        {
            for (int x = -maximumHorizontalTranslation; x <= maximumHorizontalTranslation; x += searchStep)
            {
                double correlation = CorrelationAt(reference, current, x, y, sampleStride);
                if (correlation <= bestCorrelation) continue;
                bestCorrelation = correlation;
                bestX = x;
                bestY = y;
            }
        }

        if (searchStep == 1) return (bestX, bestY);

        int coarseX = bestX;
        int coarseY = bestY;
        bestCorrelation = double.NegativeInfinity;
        for (int y = Math.Max(-maximumVerticalTranslation, coarseY - searchStep); y <= Math.Min(maximumVerticalTranslation, coarseY + searchStep); y++)
        {
            for (int x = Math.Max(-maximumHorizontalTranslation, coarseX - searchStep); x <= Math.Min(maximumHorizontalTranslation, coarseX + searchStep); x++)
            {
                double correlation = CorrelationAt(reference, current, x, y, sampleStride: 2);
                if (correlation <= bestCorrelation) continue;
                bestCorrelation = correlation;
                bestX = x;
                bestY = y;
            }
        }
        return (bestX, bestY);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new ArgumentException("Median input is empty.", nameof(values));
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    public static double HueSimilarity(ImageFrame reference, ImageFrame current)
    {
        if (reference.Format != PixelFormat.Rgb24 || current.Format != PixelFormat.Rgb24)
        {
            throw new ArgumentException("Hue inputs must be RGB images.");
        }
        if (reference.Width != current.Width || reference.Height != current.Height)
        {
            throw new ArgumentException("Hue inputs must have identical dimensions.");
        }

        double weightedScore = 0;
        double totalWeight = 0;
        int qualifying = 0;
        for (int y = 0; y < reference.Height; y++)
        {
            for (int x = 0; x < reference.Width; x++)
            {
                int offset = (y * reference.Width + x) * 3;
                (double leftHue, double leftSaturation, double leftValue) = RgbToHsv(
                    reference.Pixels[offset], reference.Pixels[offset + 1], reference.Pixels[offset + 2]);
                (double rightHue, double rightSaturation, double rightValue) = RgbToHsv(
                    current.Pixels[offset], current.Pixels[offset + 1], current.Pixels[offset + 2]);
                double saturation = Math.Min(leftSaturation, rightSaturation);
                double value = Math.Min(leftValue, rightValue);
                if (saturation < 0.14 || value < 0.10) continue;
                double hueDistance = Math.Abs(leftHue - rightHue);
                hueDistance = Math.Min(hueDistance, 1 - hueDistance);
                double weight = saturation * value;
                weightedScore += Math.Exp(-hueDistance / 0.10) * weight;
                totalWeight += weight;
                qualifying++;
            }
        }

        int minimum = Math.Max(32, reference.Width * reference.Height / 35);
        return qualifying < minimum || totalWeight < 1e-6
            ? 0.5
            : Math.Clamp(weightedScore / totalWeight, 0, 1);
    }

    private static ImageFrame ScaleGray(ImageFrame source, double scale)
    {
        byte[] output = new byte[source.Pixels.Length];
        double centerX = (source.Width - 1) / 2d;
        double centerY = (source.Height - 1) / 2d;
        for (int y = 0; y < source.Height; y++)
        {
            double sourceY = (y - centerY) / scale + centerY;
            int y0 = Math.Clamp((int)Math.Floor(sourceY), 0, source.Height - 1);
            int y1 = Math.Clamp(y0 + 1, 0, source.Height - 1);
            double fy = Math.Clamp(sourceY - Math.Floor(sourceY), 0, 1);
            for (int x = 0; x < source.Width; x++)
            {
                double sourceX = (x - centerX) / scale + centerX;
                int x0 = Math.Clamp((int)Math.Floor(sourceX), 0, source.Width - 1);
                int x1 = Math.Clamp(x0 + 1, 0, source.Width - 1);
                double fx = Math.Clamp(sourceX - Math.Floor(sourceX), 0, 1);
                double top = source.Pixels[y0 * source.Width + x0] * (1 - fx) + source.Pixels[y0 * source.Width + x1] * fx;
                double bottom = source.Pixels[y1 * source.Width + x0] * (1 - fx) + source.Pixels[y1 * source.Width + x1] * fx;
                output[y * source.Width + x] = (byte)Math.Clamp((int)Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
            }
        }
        return new ImageFrame(source.Width, source.Height, PixelFormat.Gray8, output, takeOwnership: true);
    }

    private static double CorrelationAt(
        ImageFrame reference,
        ImageFrame current,
        int offsetX,
        int offsetY,
        int sampleStride)
    {
        int left = Math.Max(0, -offsetX);
        int right = Math.Min(reference.Width, current.Width - offsetX);
        int top = Math.Max(0, -offsetY);
        int bottom = Math.Min(reference.Height, current.Height - offsetY);
        double sumReference = 0;
        double sumCurrent = 0;
        double sumReferenceSquared = 0;
        double sumCurrentSquared = 0;
        double sumProduct = 0;
        int count = 0;
        for (int y = top; y < bottom; y += sampleStride)
        {
            int referenceRow = y * reference.Width;
            int currentRow = (y + offsetY) * current.Width;
            for (int x = left; x < right; x += sampleStride)
            {
                double a = reference.Pixels[referenceRow + x];
                double b = current.Pixels[currentRow + x + offsetX];
                sumReference += a;
                sumCurrent += b;
                sumReferenceSquared += a * a;
                sumCurrentSquared += b * b;
                sumProduct += a * b;
                count++;
            }
        }
        if (count == 0) return 0;
        double covariance = sumProduct - sumReference * sumCurrent / count;
        double varianceReference = sumReferenceSquared - sumReference * sumReference / count;
        double varianceCurrent = sumCurrentSquared - sumCurrent * sumCurrent / count;
        double denominator = Math.Sqrt(Math.Max(0, varianceReference) * Math.Max(0, varianceCurrent));
        return denominator < 1e-6 ? 0 : covariance / denominator;
    }

    private static ImageFrame TranslateGray(ImageFrame source, int offsetX, int offsetY)
    {
        byte[] output = new byte[source.Pixels.Length];
        for (int y = 0; y < source.Height; y++)
        {
            int sourceY = Math.Clamp(y + offsetY, 0, source.Height - 1);
            for (int x = 0; x < source.Width; x++)
            {
                int sourceX = Math.Clamp(x + offsetX, 0, source.Width - 1);
                output[y * source.Width + x] = source.Pixels[sourceY * source.Width + sourceX];
            }
        }
        return new ImageFrame(source.Width, source.Height, PixelFormat.Gray8, output, takeOwnership: true);
    }

    private static double RegionConsensus(ImageFrame reference, ImageFrame current)
    {
        List<double> scores = [];
        for (int row = 0; row < 2; row++)
        {
            int y0 = row * reference.Height / 2;
            int y1 = (row + 1) * reference.Height / 2;
            for (int column = 0; column < 2; column++)
            {
                int x0 = column * reference.Width / 2;
                int x1 = (column + 1) * reference.Width / 2;
                var region = new Core.Geometry.ScreenRegion(x0, y0, x1 - x0, y1 - y0);
                scores.Add(ManagedStructuralSimilarity(reference.Crop(region), current.Crop(region)));
            }
        }
        scores.Sort();
        return scores.Skip(1).Average();
    }

    private static double ManagedStructuralSimilarity(ImageFrame reference, ImageFrame current)
    {
        const int columns = 8;
        const int rows = 6;
        List<double> scores = [];
        for (int row = 0; row < rows; row++)
        {
            int y0 = row * reference.Height / rows;
            int y1 = (row + 1) * reference.Height / rows;
            for (int column = 0; column < columns; column++)
            {
                int x0 = column * reference.Width / columns;
                int x1 = (column + 1) * reference.Width / columns;
                double sumA = 0;
                double sumB = 0;
                double sumAA = 0;
                double sumBB = 0;
                double sumAB = 0;
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    int offset = y * reference.Width;
                    for (int x = x0; x < x1; x++)
                    {
                        double a = reference.Pixels[offset + x];
                        double b = current.Pixels[offset + x];
                        sumA += a;
                        sumB += b;
                        sumAA += a * a;
                        sumBB += b * b;
                        sumAB += a * b;
                        count++;
                    }
                }
                if (count == 0) continue;
                double varianceA = sumAA - sumA * sumA / count;
                double varianceB = sumBB - sumB * sumB / count;
                if (varianceA / count < 9) continue;
                double denominator = Math.Sqrt(Math.Max(0, varianceA) * Math.Max(0, varianceB));
                double luminance = denominator < 1e-6
                    ? 0
                    : Math.Clamp((sumAB - sumA * sumB / count) / denominator, 0, 1);

                double gradientDot = 0;
                double gradientA = 0;
                double gradientB = 0;
                double minimumMagnitude = 0;
                double maximumMagnitude = 0;
                for (int y = y0; y < Math.Max(y0, y1 - 1); y++)
                {
                    int offset = y * reference.Width;
                    int next = (y + 1) * reference.Width;
                    for (int x = x0; x < Math.Max(x0, x1 - 1); x++)
                    {
                        double ax = reference.Pixels[offset + x + 1] - reference.Pixels[offset + x];
                        double ay = reference.Pixels[next + x] - reference.Pixels[offset + x];
                        double bx = current.Pixels[offset + x + 1] - current.Pixels[offset + x];
                        double by = current.Pixels[next + x] - current.Pixels[offset + x];
                        gradientDot += ax * bx + ay * by;
                        gradientA += ax * ax + ay * ay;
                        gradientB += bx * bx + by * by;
                        double magnitudeA = Math.Sqrt(ax * ax + ay * ay);
                        double magnitudeB = Math.Sqrt(bx * bx + by * by);
                        minimumMagnitude += Math.Min(magnitudeA, magnitudeB);
                        maximumMagnitude += Math.Max(magnitudeA, magnitudeB);
                    }
                }
                if (gradientA < 1e-6) continue;
                double gradient = gradientB < 1e-6
                    ? 0
                    : Math.Clamp(gradientDot / Math.Sqrt(gradientA * gradientB), 0, 1);
                double magnitude = maximumMagnitude < 1e-6 ? 0 : minimumMagnitude / maximumMagnitude;
                // Defocus and windowed-mode downscaling shrink gradient magnitudes far more
                // than they disturb gradient direction or normalized luminance. Weight the
                // blur-tolerant direction and luminance terms and keep the magnitude ratio as
                // a weak cue only, so a sharp reference still matches a softer runtime frame.
                scores.Add(0.55 * gradient + 0.38 * luminance + 0.07 * magnitude);
            }
        }
        if (scores.Count == 0) return 0;
        scores.Sort();
        int low = (int)(scores.Count * 0.20);
        int high = Math.Max(low + 1, (int)Math.Ceiling(scores.Count * 0.95));
        return Math.Clamp(scores.Skip(low).Take(high - low).Average(), 0, 1);
    }

    private static (double Hue, double Saturation, double Value) RgbToHsv(byte red, byte green, byte blue)
    {
        double r = red / 255d;
        double g = green / 255d;
        double b = blue / 255d;
        double maximum = Math.Max(r, Math.Max(g, b));
        double minimum = Math.Min(r, Math.Min(g, b));
        double delta = maximum - minimum;
        double hue = 0;
        if (delta > 1e-9)
        {
            if (maximum == r) hue = ((g - b) / delta) % 6;
            else if (maximum == g) hue = (b - r) / delta + 2;
            else hue = (r - g) / delta + 4;
            hue /= 6;
            if (hue < 0) hue += 1;
        }
        double saturation = maximum <= 1e-9 ? 0 : delta / maximum;
        return (hue, saturation, maximum);
    }
}
