// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeWare.Vignette;

internal sealed record Appearance(string Color, double Intensity, double Radius, double Softness, double Aspect)
{
    internal static readonly Appearance Default = new("#000000", .39, .43, .43, 1);
    internal bool Valid => Color is { Length: 7 } && Color[0] == '#' &&
        uint.TryParse(Color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _) &&
        Intensity is >= 0 and <= .85 && Radius is >= .1 and <= .9 &&
        Softness is >= .08 and <= .9 && Aspect is >= .65 and <= 1.55;

    internal uint[] Palette()
    {
        uint tint = uint.Parse(Color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        uint r = tint >> 16, g = (tint >> 8) & 255, b = tint & 255;
        var premultiplied = new uint[256];
        for (uint a = 0; a < premultiplied.Length; a++)
            premultiplied[a] = a << 24 | ((r * a + 127) / 255) << 16 |
                ((g * a + 127) / 255) << 8 | (b * a + 127) / 255;
        return premultiplied;
    }

    internal double Alpha(double x, double y)
    {
        double distance = Math.Sqrt((x * x / (Aspect * Aspect) + y * y * Aspect * Aspect) / 2);
        double t = Math.Clamp((distance - Radius) / Softness, 0, 1);
        return Intensity * t * t * (3 - 2 * t);
    }

    internal uint[] Texture(int size)
    {
        var pixels = new uint[size * size];
        var palette = Palette();
        int half = (size + 1) / 2;
        for (int y = 0; y < half; y++)
            for (int x = 0; x < half; x++)
            {
                uint color = palette[(int)Math.Round(Alpha(2d * x / (size - 1) - 1, 2d * y / (size - 1) - 1) * 255,
                    MidpointRounding.AwayFromZero)];
                int right = size - 1 - x, bottom = size - 1 - y;
                pixels[y * size + x] = pixels[y * size + right] =
                    pixels[bottom * size + x] = pixels[bottom * size + right] = color;
            }
        return pixels;
    }
}

internal sealed record Pipeline(string Id, string Name, string? Description, string Color,
    double Intensity, double Radius, double Softness, double Aspect)
{
    [JsonIgnore] internal Appearance Appearance => new(Color, Intensity, Radius, Softness, Aspect);
    internal bool Valid => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Name) &&
        Name.Length <= 80 && Appearance.Valid;

    internal static Pipeline Personal(string? id, string name, Appearance value)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80) throw new InvalidOperationException("Enter a name between 1 and 80 characters.");
        return new(id ?? Guid.NewGuid().ToString(), name, null, value.Color, value.Intensity, value.Radius, value.Softness, value.Aspect);
    }
}

internal enum ThemeMode { System, Light, Dark }

internal sealed record Preferences(Appearance Appearance, string[]? Screens, Pipeline[] Personal,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Dark = false,
    bool UseAcrylic = false,
    [property: JsonPropertyName("theme"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull),
        JsonConverter(typeof(JsonStringEnumConverter<ThemeMode>))] ThemeMode? ThemeOverride = null)
{
    internal static Preferences Default => new(Appearance.Default, null, []);

    internal ThemeMode Theme => ThemeOverride is { } mode && Enum.IsDefined(mode)
        ? mode
        : Dark ? ThemeMode.Dark : ThemeMode.System;

    internal Preferences WithTheme(ThemeMode mode) => this with { ThemeOverride = mode, Dark = false };
}

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, MaxDepth = 12 };
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VibeWare", "Vignette", "settings.json");
    internal string FilePath => path;
    internal bool Writable { get; private set; } = true;
    internal string? Warning { get; private set; }

    internal static IReadOnlyList<Pipeline> Builtins()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Vignette.Pipelines")
            ?? throw new InvalidOperationException("The built-in pipeline catalog is missing.");
        var values = JsonSerializer.Deserialize<Pipeline[]>(stream, Json);
        if (values is not { Length: 15 } || values.Any(value => !value.Valid) || values.Select(value => value.Id).Distinct().Count() != values.Length)
            throw new InvalidOperationException("The built-in pipeline catalog is invalid.");
        return values;
    }

    internal Preferences Load()
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 128 * 1024) throw new InvalidDataException("The settings file is too large.");
            var value = JsonSerializer.Deserialize<Preferences>(stream, Json);
            if (value is null || value.Appearance?.Valid != true || value.Personal is not { Length: <= 60 } ||
                value.Personal.Any(item => item?.Valid != true) || value.Personal.Select(item => item.Id).Distinct().Count() != value.Personal.Length ||
                value.Screens?.Any(string.IsNullOrWhiteSpace) == true)
                throw new InvalidDataException("Invalid vignette settings.");
            return value;
        }
        catch (FileNotFoundException) { return Preferences.Default; }
        catch (DirectoryNotFoundException) { return Preferences.Default; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Writable = false;
            Warning = "Settings could not be read. The original file was preserved.";
            Trace.TraceWarning(error.Message);
            return Preferences.Default;
        }
    }

    internal void Save(Preferences value)
    {
        if (!Writable) return;
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, Guid.NewGuid() + ".tmp");
        try
        {
            using (var stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(stream, value, Json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
