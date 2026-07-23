using ExpeditionsMacro.Core.Abstractions;
using ExpeditionsMacro.Core.Geometry;
using ExpeditionsMacro.Core.Imaging;
using ExpeditionsMacro.Vision;
using ExpeditionsMacro.Vision.Camera;
using ExpeditionsMacro.Vision.Infrastructure;
using ExpeditionsMacro.Windows;

// Live goal-evidence probe against a chosen model. Captures the current client and
// reports the exact final-confidence the aligner would compute, plus per-tile detail.
string modelDir = args[0];

var manifestJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDir, "manifest.json")));
var regions = manifestJson.RootElement.GetProperty("regions")
    .EnumerateArray()
    .Select(r => new ScreenRegion(r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(), r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32()))
    .ToArray();
double threshold = manifestJson.RootElement.GetProperty("success_threshold").GetDouble();
double baseline = manifestJson.RootElement.GetProperty("baseline_score").GetDouble();

ImageFrame reference = ImageCodec.Load(Path.Combine(modelDir, "reference.png"), PixelFormat.Gray8);
ImageFrame goalColor = CameraRegionAnalyzer.BuildColorComposite(
    ImageCodec.Load(Path.Combine(modelDir, "goal.png")), regions, inset: 4);

using var automation = new WindowsRobloxAutomation();
RobloxWindow? found = automation.FindWindow();
if (found is null) { Console.WriteLine("No Roblox window found."); return; }
RobloxWindow window = found.Value;

Console.WriteLine($"model baseline={baseline:F4} threshold={threshold:F4}");
Console.WriteLine("Capturing 5 frames; hold your best-aligned yaw...");
var evidences = new List<double>();
for (int i = 0; i < 5; i++)
{
    ImageFrame live = automation.CaptureClient(window);
    ImageFrame composite = CameraRegionAnalyzer.BuildComposite(live, regions);
    double registered = CameraRegisteredScorer.ScoreComposite(reference, composite).Score;
    ImageFrame liveColor = CameraRegionAnalyzer.BuildColorComposite(live, regions, inset: 4);
    double hue = CameraRegisteredScorer.HueSimilarity(goalColor, liveColor);
    double evidence = Math.Clamp(registered + 0.04 * (hue - 0.5), 0, 1);
    evidences.Add(evidence);
    Console.WriteLine($"  frame {i + 1}: registered={registered:F4} hue={hue:F3} goal-evidence={evidence:F4}");
    await Task.Delay(200);
}
double median = evidences.OrderBy(x => x).ElementAt(evidences.Count / 2);
Console.WriteLine();
Console.WriteLine($"MEDIAN goal-evidence at current yaw: {median:F4}  (threshold {threshold:F4})");
Console.WriteLine(median >= threshold
    ? "PASS: current view clears the new threshold. Alignment should succeed."
    : $"SHORT by {threshold - median:F4}. If you are exactly at the goal yaw, the threshold is still too tight for this render.");
