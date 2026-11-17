using System.Text.Json;

namespace VibeKit.StudioControls.Core;

public static class ScreenFraming
{
    public static ScreenPreset? Read(JsonElement transform)
    {
        int width = Dimension(transform, "sourceWidth"), height = Dimension(transform, "sourceHeight");
        if (width <= 0 || height <= 0 || JsonRead.String(transform, "boundsType") == "OBS_BOUNDS_NONE") return null;
        double left = JsonRead.Number(transform, "cropLeft"), right = JsonRead.Number(transform, "cropRight");
        double top = JsonRead.Number(transform, "cropTop"), bottom = JsonRead.Number(transform, "cropBottom");
        double visibleWidth = width - left - right, visibleHeight = height - top - bottom;
        if (left < 0 || right < 0 || top < 0 || bottom < 0 || visibleWidth <= 0 || visibleHeight <= 0) return null;
        return new ScreenPreset(Math.Min(width / visibleWidth, height / visibleHeight),
            left + right > 0 ? (2 * left / (left + right) - 1) * 100 : 0,
            top + bottom > 0 ? (2 * top / (top + bottom) - 1) * 100 : 0);
    }

    public static IReadOnlyDictionary<string, int> CalculateCrop(JsonElement transform, double zoom, double x, double y)
    {
        if (Read(transform) is null) throw new ArgumentException("The captured display is not ready or has no fixed viewport.");
        zoom = Bounded(zoom, 1, 4); x = Bounded(x, -100, 100); y = Bounded(y, -100, 100);
        int width = Dimension(transform, "sourceWidth"), height = Dimension(transform, "sourceHeight");
        int horizontal = width - Math.Max(1, (int)Math.Round(width / zoom));
        int vertical = height - Math.Max(1, (int)Math.Round(height / zoom));
        int left = (int)Math.Round(horizontal * (x + 100) / 200);
        int top = (int)Math.Round(vertical * (y + 100) / 200);
        // The fixed scene viewport, scaling and position are intentionally untouched.
        return new Dictionary<string, int>
        {
            ["cropLeft"] = left, ["cropRight"] = horizontal - left,
            ["cropTop"] = top, ["cropBottom"] = vertical - top
        };
    }

    internal static int Dimension(JsonElement element, string key) => checked((int)Math.Round(JsonRead.Number(element, key)));
    internal static double Bounded(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("Framing values must be finite.");
        return Math.Clamp(value, minimum, maximum);
    }
}

internal static class JsonRead
{
    public static double Number(JsonElement element, string key, double fallback = 0)
    {
        double value = element.TryGetProperty(key, out JsonElement item) ? item.GetDouble() : fallback;
        if (!double.IsFinite(value)) throw new InvalidDataException("OBS returned a non-finite value.");
        return value;
    }
    public static bool Boolean(JsonElement element, string key, bool fallback = false) =>
        element.TryGetProperty(key, out JsonElement item) ? item.GetBoolean() : fallback;
    public static string String(JsonElement element, string key, string fallback = "") =>
        element.TryGetProperty(key, out JsonElement item) ? item.GetString() ?? fallback : fallback;
}
