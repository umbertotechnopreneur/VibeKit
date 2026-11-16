using OpenAIUsage;
using System.Text.Json;

// One request per invocation, capped before parsing; stdout contains only public widget data.
try
{
    var storage = new Storage();
    string command = args.FirstOrDefault() ?? "snapshot";
    if (command == "settings")
        Console.WriteLine(JsonSerializer.Serialize(storage.LoadSettings(), JsonOptions.Compact));
    else if (command == "settings-status")
        Console.WriteLine(JsonSerializer.Serialize(storage.LoadSettingsForEditing(), JsonOptions.Compact));
    else if (command == "save-settings")
    {
        var request = await BoundedTextReader.ReadToEndAsync(Console.In, Storage.MaximumSettingsBytes);
        var value = JsonSerializer.Deserialize<WidgetSettings>(request, JsonOptions.Default)
            ?? throw new ArgumentException("Settings are required.");
        storage.SaveSettings(value);
        Console.WriteLine("{\"ok\":true}");
    }
    else if (command == "snapshot")
    {
        var state = await new UsageService(storage).RefreshAsync(storage.LoadSettings());
        Console.WriteLine(JsonSerializer.Serialize(state, JsonOptions.Compact));
        if (!state.Connected) Environment.ExitCode = 2;
    }
    else throw new ArgumentException("Commands: snapshot, settings, settings-status, save-settings");
}
catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or JsonException or UnauthorizedAccessException or TimeZoneNotFoundException or InvalidTimeZoneException)
{
    Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Compact));
    Environment.ExitCode = 1;
}
