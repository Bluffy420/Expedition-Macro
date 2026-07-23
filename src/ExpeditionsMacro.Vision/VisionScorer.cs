using ExpeditionsMacro.Core.Imaging;
using ExpeditionsMacro.Vision.Infrastructure;
using OpenCvSharp;

namespace ExpeditionsMacro.Vision;

public static class VisionScorer
{
    public const int MaximumReferenceWidth = 720;
    private const int TileColumns = 8;
    private const int TileRows = 6;

    public static ImageFrame PrepareGray(ImageFrame image, int? targetWidth = null, int? targetHeight = null, int maximumWidth = MaximumReferenceWidth)
    {
        OpenCvRuntime.Initialize();
        using Mat source = ImageCodec.ToMat(image);
        using Mat gray = new();
        if (source.Channels() == 3) Cv2.CvtColor(source, gray, ColorConversionCodes.RGB2GRAY);
        else source.CopyTo(gray);

        using Mat sized = new();
        if (targetWidth is not null && targetHeight is not null && (gray.Width != targetWidth || gray.Height != targetHeight))
        {
            Cv2.Resize(gray, sized, new Size(targetWidth.Value, targetHeight.Value), 0, 0, InterpolationFlags.Area);
        }
        else if (targetWidth is null && gray.Width > maximumWidth)
        {
            double scale = (double)maximumWidth / gray.Width;
            Cv2.Resize(gray, sized, new Size(maximumWidth, Math.Max(32, (int)Math.Round(gray.Height * scale))), 0, 0, InterpolationFlags.Area);
        }
        else
        {
            gray.CopyTo(sized);
        }

        using Mat blurred = new();
        Cv2.GaussianBlur(sized, blurred, new Size(3, 3), 0);
        using CLAHE clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));
        using Mat normalized = new();
        clahe.Apply(blurred, normalized);
        return ImageCodec.FromMat(normalized, PixelFormat.Gray8);
    }

    public static (IReadOnlyList<ImageFrame> Prepared, ImageFrame Reference, double Baseline) BuildReference(IReadOnlyList<ImageFrame> frames)
    {
        if (frames.Count == 0) throw new ArgumentException("At least one reference frame is required.", nameof(frames));
        ImageFrame first = PrepareGray(frames[0]);
        List<ImageFrame> prepared = [first];
        for (int index = 1; index < frames.Count; index++) prepared.Add(PrepareGray(frames[index], first.Width, first.Height));
        ImageFrame reference = Median(prepared);
        double baseline = Median(prepared.Select(frame => RobustSimilarity(reference, frame)).ToArray());
        return (prepared, reference, baseline);
    }

    public static double ScoreFrame(ImageFrame reference, ImageFrame current) =>
        RobustSimilarity(reference, PrepareGray(current, reference.Width, reference.Height));

    public static ImageFrame MakeThumbnail(ImageFrame gray, int width = 160)
    {
        if (gray.Format != PixelFormat.Gray8) throw new ArgumentException("Thumbnail input must be grayscale.", nameof(gray));
        int height = Math.Max(24, (int)Math.Round((double)gray.Height * width / gray.Width));
        using Mat source = ImageCodec.ToMat(gray);
        using Mat resized = new();
        Cv2.Resize(source, resized, new Size(width, height), 0, 0, InterpolationFlags.Area);
        return ImageCodec.FromMat(resized, PixelFormat.Gray8);
    }

    public static double RobustSimilarity(ImageFrame reference, ImageFrame current)
    {
        if (reference.Format != PixelFormat.Gray8 || current.Format != PixelFormat.Gray8) throw new ArgumentException("Similarity inputs must be prepared grayscale images.");
        if (reference.Width != current.Width || reference.Height != current.Height) throw new ArgumentException("Similarity inputs must have identical dimensions.");
        using Mat referenceMat = ImageCodec.ToMat(reference);
        using Mat currentMat = ImageCodec.ToMat(current);
        using Mat refGx = new();
        using Mat refGy = new();
        using Mat curGx = new();
        using Mat curGy = new();
        Cv2.Sobel(referenceMat, refGx, MatType.CV_32F, 1, 0, 3);
        Cv2.Sobel(referenceMat, refGy, MatType.CV_32F, 0, 1, 3);
        Cv2.Sobel(currentMat, curGx, MatType.CV_32F, 1, 0, 3);
        Cv2.Sobel(currentMat, curGy, MatType.CV_32F, 0, 1, 3);
        using Mat referenceEdges = EdgeMap(referenceMat);
        using Mat currentEdges = EdgeMap(currentMat);

        List<double> scores = [];
        for (int row = 0; row < TileRows; row++)
        {
            int y0 = row * reference.Height / TileRows;
            int y1 = (row + 1) * reference.Height / TileRows;
            for (int column = 0; column < TileColumns; column++)
            {
                int x0 = column * reference.Width / TileColumns;
                int x1 = (column + 1) * reference.Width / TileColumns;
                Rect region = new(x0, y0, x1 - x0, y1 - y0);
                using Mat refTile = new(referenceMat, region);
                using Mat curTile = new(currentMat, region);
                using Mat refEdgeTile = new(referenceEdges, region);
                using Mat curEdgeTile = new(currentEdges, region);
                Cv2.MeanStdDev(refTile, out _, out Scalar deviation);
                int minimumEdges = Math.Max(8, region.Width * region.Height / 450);
                if (Cv2.CountNonZero(refEdgeTile) < minimumEdges || deviation.Val0 < 3) continue;

                using Mat refGxTile = new(refGx, region);
                using Mat refGyTile = new(refGy, region);
                using Mat curGxTile = new(curGx, region);
                using Mat curGyTile = new(curGy, region);
                double gradient = GradientCosine(refGxTile, refGyTile, curGxTile, curGyTile);
                double chamfer = ChamferSimilarity(refEdgeTile, curEdgeTile);
                double luminance = NormalizedCorrelation(refTile, curTile);
                // Defocus fragments Canny edges and inflates chamfer distance, while gradient
                // direction and normalized luminance stay comparatively stable. Favor the
                // blur-tolerant terms so a sharp reference still matches a softer runtime frame.
                scores.Add(0.50 * gradient + 0.22 * chamfer + 0.28 * luminance);
            }
        }

        if (scores.Count == 0) return 0;
        scores.Sort();
        int low = (int)(scores.Count * 0.20);
        int high = Math.Max(low + 1, (int)Math.Ceiling(scores.Count * 0.95));
        return Math.Clamp(scores.Skip(low).Take(high - low).Average(), 0, 1);
    }

    public static double ChooseSuccessThreshold(double baseline, IReadOnlyList<double> scanScores)
    {
        int edge = Math.Max(2, scanScores.Count / 20);
        IReadOnlyList<double> interior = scanScores.Count > edge * 2 ? scanScores.Skip(edge).Take(scanScores.Count - edge * 2).ToArray() : scanScores;
        double threshold;
        if (interior.Count > 0)
        {
            double wrongCeiling = Percentile(interior, 98);
            threshold = wrongCeiling + 0.58 * (baseline - wrongCeiling);
        }
        else
        {
            threshold = baseline - 0.06;
        }
        double upper = Math.Max(0.45, Math.Min(0.96, baseline - 0.015));
        return Math.Clamp(Math.Min(threshold, upper), 0.45, upper);
    }

    public static ImageFrame Median(IReadOnlyList<ImageFrame> frames)
    {
        if (frames.Count == 0) throw new ArgumentException("At least one image is required.", nameof(frames));
        ImageFrame first = frames[0];
        if (frames.Any(frame => frame.Width != first.Width || frame.Height != first.Height || frame.Format != first.Format)) throw new ArgumentException("Median images must have identical formats and dimensions.", nameof(frames));
        byte[] output = new byte[first.Pixels.Length];
        byte[] values = new byte[frames.Count];
        for (int pixel = 0; pixel < output.Length; pixel++)
        {
            for (int frame = 0; frame < frames.Count; frame++) values[frame] = frames[frame].Pixels[pixel];
            Array.Sort(values);
            output[pixel] = values.Length % 2 == 1
                ? values[values.Length / 2]
                : (byte)((values[values.Length / 2 - 1] + values[values.Length / 2]) / 2);
        }
        return new ImageFrame(first.Width, first.Height, first.Format, output, takeOwnership: true);
    }

    private static Mat EdgeMap(Mat gray)
    {
        double[] values = new double[gray.Rows * gray.Cols];
        byte[] pixels = new byte[values.Length];
        System.Runtime.InteropServices.Marshal.Copy(gray.Data, pixels, 0, pixels.Length);
        for (int index = 0; index < pixels.Length; index++) values[index] = pixels[index];
        Array.Sort(values);
        double median = values[values.Length / 2];
        int lower = (int)Math.Max(20, 0.55 * median);
        int upper = (int)Math.Min(245, Math.Max(lower + 20, 1.35 * median));
        Mat edges = new();
        Cv2.Canny(gray, edges, lower, upper);
        return edges;
    }

    private static double GradientCosine(Mat refGx, Mat refGy, Mat curGx, Mat curGy)
    {
        using Mat dotX = refGx.Mul(curGx);
        using Mat dotY = refGy.Mul(curGy);
        using Mat refSquareX = refGx.Mul(refGx);
        using Mat refSquareY = refGy.Mul(refGy);
        using Mat curSquareX = curGx.Mul(curGx);
        using Mat curSquareY = curGy.Mul(curGy);
        double dot = Cv2.Sum(dotX).Val0 + Cv2.Sum(dotY).Val0;
        double refNorm = Math.Sqrt(Cv2.Sum(refSquareX).Val0 + Cv2.Sum(refSquareY).Val0);
        double curNorm = Math.Sqrt(Cv2.Sum(curSquareX).Val0 + Cv2.Sum(curSquareY).Val0);
        return refNorm < 1e-6 || curNorm < 1e-6 ? 0 : Math.Clamp(dot / (refNorm * curNorm), 0, 1);
    }

    private static double NormalizedCorrelation(Mat reference, Mat current)
    {
        using Mat result = new();
        Cv2.MatchTemplate(current, reference, result, TemplateMatchModes.CCoeffNormed);
        return Math.Clamp(result.At<float>(0, 0), 0, 1);
    }

    private static double ChamferSimilarity(Mat referenceEdges, Mat currentEdges)
    {
        if (Cv2.CountNonZero(referenceEdges) < 4 || Cv2.CountNonZero(currentEdges) < 4) return 0;
        using Mat invertedReference = new();
        using Mat invertedCurrent = new();
        Cv2.BitwiseNot(referenceEdges, invertedReference);
        Cv2.BitwiseNot(currentEdges, invertedCurrent);
        using Mat referenceDistance = new();
        using Mat currentDistance = new();
        Cv2.DistanceTransform(invertedReference, referenceDistance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
        Cv2.DistanceTransform(invertedCurrent, currentDistance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
        List<float> distances = [];
        int rows = referenceEdges.Rows;
        int columns = referenceEdges.Cols;
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                if (referenceEdges.At<byte>(y, x) != 0) distances.Add(currentDistance.At<float>(y, x));
                if (currentEdges.At<byte>(y, x) != 0) distances.Add(referenceDistance.At<float>(y, x));
            }
        }
        distances.Sort();
        int cutoffIndex = Math.Clamp((int)Math.Round((distances.Count - 1) * 0.80), 0, distances.Count - 1);
        float cutoff = distances[cutoffIndex];
        float[] trimmed = distances.Where(value => value <= cutoff).ToArray();
        double mean = trimmed.Length == 0 ? cutoff : trimmed.Average(value => (double)value);
        return Math.Exp(-mean / 3.5);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 1) return sorted[0];
        double rank = percentile / 100 * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }
}
