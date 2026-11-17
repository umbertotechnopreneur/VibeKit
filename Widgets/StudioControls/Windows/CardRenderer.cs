using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.Json;
using VibeKit.StudioControls.Core;

namespace VibeKit.StudioControls.Windows;

/// <summary>Windows GDI+ artwork; no browser, Python process or live video renderer.</summary>
public static class CardRenderer
{
    private static readonly Color Ink = ColorTranslator.FromHtml("#e8e3dc");
    private static readonly Color Muted = ColorTranslator.FromHtml("#b9b3ad");
    private static readonly Color Accent = ColorTranslator.FromHtml("#eba295");
    private static readonly Color Back = ColorTranslator.FromHtml("#171a1e");
    private static readonly StudioCatalog Catalog = StudioCatalog.Default;

    public static void EnsureAssets(CancellationToken token)
    {
        Directory.CreateDirectory(LocalSettings.AssetsPath);
        string bundled = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (!Directory.Exists(bundled)) throw new DirectoryNotFoundException("The application Assets folder is missing. Extract the complete release archive.");
        foreach (string source in Directory.EnumerateFiles(bundled, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string destination = Path.Combine(LocalSettings.AssetsPath, Path.GetRelativePath(bundled, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination)) continue;
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public static ProjectSettings Prepare(ProjectSettings project, CancellationToken token)
    {
        project.Validate();
        EnsureAssets(token);
        var updates = new Dictionary<string, byte[]>();
        using Bitmap? logo = LoadProjectLogo(project.Logo);
        if (logo is not null)
        {
            updates["Project-logo.png"] = Png(logo);
            project = project with { Logo = "Project-logo.png" };
        }
        foreach (var card in Catalog.CardSources)
        {
            token.ThrowIfCancellationRequested();
            using var bitmap = Render(card.FileName, project, logo);
            updates[card.FileName] = Png(bitmap);
        }
        updates["project.json"] = JsonSerializer.SerializeToUtf8Bytes(project, SettingsStore.JsonOptions);
        Commit(updates, token);
        return project;
    }

    private static Bitmap? LoadProjectLogo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Path.IsPathRooted(path) ? path : Path.Combine(LocalSettings.AssetsPath, path);
        if (!new[] { ".png", ".jpg", ".jpeg", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new ArgumentException("Choose a PNG, JPG or BMP logo.");
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new ArgumentException("Use a logo smaller than 16 MB.");
        using var image = Image.FromFile(path);
        if ((long)image.Width * image.Height > 16_000_000) throw new ArgumentException("Use a logo smaller than 16 megapixels.");
        double scale = Math.Min(1, 1600.0 / Math.Max(image.Width, image.Height));
        var output = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)), PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(output);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(image, 0, 0, output.Width, output.Height);
        return output;
    }

    private static Bitmap Render(string filename, ProjectSettings project, Bitmap? logo)
    {
        var bitmap = new Bitmap(Catalog.CanvasWidth, Catalog.CanvasHeight, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(filename == "CRT-overlay.png" ? Color.Transparent : Back);
            switch (filename)
            {
                case "Intro-card.png": Intro(graphics, project, logo); break;
                case "Thank-you-card.png": Outro(graphics, project, logo); break;
                case "Chapter-card.png":
                    Intro(graphics, project with { Name = string.IsNullOrWhiteSpace(project.Chapter) ? "Next chapter" : project.Chapter,
                        Eyebrow = project.Name, Subtitle = project.ChapterDescription, Description = "", Links = [], Logo = "", VibeWare = false }, null);
                    break;
                case "Privacy-card.png":
                    Intro(graphics, project with { Name = "A short break.", Eyebrow = "STANDBY", Subtitle = "Setting up the next part.",
                        Description = project.Name, Links = [], Logo = "", VibeWare = false }, null);
                    break;
                case "Compare-frame.png": Comparison(graphics, project); break;
                case "CRT-overlay.png": Crt(graphics, project); break;
                default: throw new ArgumentException("Unknown card artwork.");
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static void Intro(Graphics graphics, ProjectSettings project, Bitmap? logo)
    {
        Header(graphics, "Umberto’s Lab");
        Text(graphics, project.Eyebrow, new(112, 260, 1800, 80), 34, 26, Accent, true);
        Text(graphics, project.Name, new(112, 400, 1900, 310), 134, 64, Ink, true);
        Text(graphics, project.Subtitle, new(112, 750, 2100, 130), 54, 32, Ink);
        Text(graphics, project.Description, new(112, 930, 2000, 200), 38, 24, Muted);
        for (int index = 0; index < project.Links.Length; index++)
            Text(graphics, DisplayLink(project.Links[index]), new(112 + index * 790, 1170, 735, 110), 31, 20, Accent);
        if (logo is not null) Logo(graphics, logo, new(2120, 235, 330, 300));
        if (project.VibeWare)
        {
            Logo(graphics, "VibeWare-logo.png", new(2220, 575, 220, 210));
            Text(graphics, "This is VibeWare", new(2080, 825, 370, 70), 30, 24, Accent);
        }
        Footer(graphics);
    }

    private static void Outro(Graphics graphics, ProjectSettings project, Bitmap? logo)
    {
        Header(graphics, "Thank you!");
        Opening(graphics, Catalog.OutroCameraBounds);
        Text(graphics, project.Eyebrow, new(1660, 220, 788, 65), 30, 22, Accent);
        Text(graphics, project.Name, new(1660, 310, 788, 200), 70, 34, Ink, true);
        Text(graphics, project.Subtitle, new(1660, 545, 788, 125), 40, 24, Ink);
        Text(graphics, project.Description, new(1660, 700, 788, 245), 32, 22, Muted);
        for (int index = 0; index < project.Links.Length; index++)
            Text(graphics, DisplayLink(project.Links[index]), new(1660, 985 + index * 82, 788, 72), 30, 19, Accent);
        if (logo is not null) Logo(graphics, logo, new(2198, 60, 250, 135));
        if (project.VibeWare)
        {
            Logo(graphics, "VibeWare-logo.png", new(112, 1200, 125, 112));
            Text(graphics, "This is VibeWare", new(270, 1215, 1500, 52), 28, 24, Ink, true);
            Text(graphics, "umbertogiacobbi.biz/vibeware/manifesto", new(270, 1270, 1500, 48), 26, 22, Accent);
        }
        Footer(graphics);
        Text(graphics, "© 2026 Umberto Giacobbi", new(820, 1340, 900, 60), 27, 23, Muted);
        Logo(graphics, "OBS-logo.png", new(1920, 1310, 68, 68));
        Text(graphics, "Made with OBS Studio", new(2010, 1330, 470, 65), 29, 22, Muted);
    }

    private static void Comparison(Graphics graphics, ProjectSettings project)
    {
        Text(graphics, "Side by side", new(80, 70, 2300, 130), 88, 60, Ink, true);
        string[] captions = [project.CompareA, project.CompareB];
        for (int index = 0; index < Catalog.ComparisonBounds.Length; index++)
        {
            var rect = Catalog.ComparisonBounds[index];
            Opening(graphics, rect);
            Text(graphics, captions[index], new(rect.X, 235, rect.Width, 70), 44, 25, Ink);
        }
        Text(graphics, "umbertogiacobbi.biz", new(80, 1320, 2000, 60), 32, 24, Ink);
    }

    private static void Crt(Graphics graphics, ProjectSettings project)
    {
        using var dark = new SolidBrush(Color.FromArgb(235, 15, 18, 21));
        graphics.FillRectangle(dark, 112, 1010, 2336, 315);
        Text(graphics, project.Name, new(155, 1045, 2220, 100), 70, 35, Ink, true);
        string description = string.IsNullOrWhiteSpace(project.Description) ? project.Subtitle : project.Description;
        Text(graphics, string.IsNullOrWhiteSpace(description) ? "Please stand by. A demo is tuning in." : description,
            new(155, 1165, 2220, 125), 36, 23, Muted);
        graphics.FillRectangle(dark, 0, 1376, 2560, 64);
        Text(graphics, "CRT footage: Ivan Bridgewater / Internet Archive · CC BY 3.0 · excerpt, audio removed", new(112, 1382, 2336, 28), 18, 16, Muted);
        Text(graphics, "archive.org/details/IMB_SF_R30_C8 · creativecommons.org/licenses/by/3.0/", new(112, 1410, 2336, 28), 16, 14, Muted);
    }

    private static void Header(Graphics graphics, string title)
    {
        using var brush = new SolidBrush(Accent);
        graphics.FillRectangle(brush, 112, 82, 8, 68);
        Text(graphics, title, new(150, 70, 2000, 150), title == "Thank you!" ? 100 : 64, 50, Ink, true);
    }

    private static void Footer(Graphics graphics)
    {
        using var line = new Pen(ColorTranslator.FromHtml("#39434a"), 2);
        graphics.DrawLine(line, 112, 1305, 2448, 1305);
        Text(graphics, "umbertogiacobbi.biz", new(112, 1340, 700, 60), 32, 24, Ink);
    }

    private static void Opening(Graphics graphics, LayoutRectangle rectangle)
    {
        var area = new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using (var transparent = new SolidBrush(Color.Transparent)) graphics.FillRectangle(transparent, area);
        graphics.CompositingMode = CompositingMode.SourceOver;
        using var border = new Pen(Accent, 4);
        graphics.DrawRectangle(border, area.X - 3, area.Y - 3, area.Width + 5, area.Height + 5);
    }

    private static void Logo(Graphics graphics, string name, RectangleF bounds)
    {
        using var image = Image.FromFile(Path.Combine(LocalSettings.AssetsPath, name));
        Logo(graphics, image, bounds);
    }

    private static void Logo(Graphics graphics, Image image, RectangleF bounds)
    {
        float scale = Math.Min(bounds.Width / image.Width, bounds.Height / image.Height);
        float width = image.Width * scale, height = image.Height * scale;
        graphics.DrawImage(image, bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
    }

    private static void Text(Graphics graphics, string? value, RectangleF bounds, int size, int minimum, Color color, bool bold = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        using var format = new StringFormat { FormatFlags = StringFormatFlags.LineLimit, Trimming = StringTrimming.None };
        using var brush = new SolidBrush(color);
        for (int pixels = size; pixels >= minimum; pixels--)
        {
            using var font = new Font("Segoe UI", pixels, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            graphics.MeasureString(value, font, bounds.Size, format, out int fitted, out _);
            if (fitted < value.Length) continue;
            graphics.DrawString(value, font, brush, bounds, format);
            return;
        }
        throw new ArgumentException("Text does not fit a card. Shorten the title, description or displayed links.");
    }

    private static string DisplayLink(string value) => value.Replace("https://", "", StringComparison.OrdinalIgnoreCase).Replace("http://", "", StringComparison.OrdinalIgnoreCase);
    private static byte[] Png(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static void Commit(Dictionary<string, byte[]> updates, CancellationToken token)
    {
        string transaction = Path.Combine(LocalSettings.AssetsPath, ".prepare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        var promoted = new List<string>();
        bool rollbackFailed = false;
        try
        {
            foreach (var (name, bytes) in updates)
            {
                token.ThrowIfCancellationRequested();
                LocalSettings.WriteAtomic(Path.Combine(transaction, name), bytes);
                string target = Path.Combine(LocalSettings.AssetsPath, name);
                if (File.Exists(target)) File.Copy(target, Path.Combine(transaction, name + ".previous"));
            }
            foreach (string name in updates.Keys)
            {
                token.ThrowIfCancellationRequested();
                File.Move(Path.Combine(transaction, name), Path.Combine(LocalSettings.AssetsPath, name), true);
                promoted.Add(name);
            }
        }
        catch
        {
            foreach (string name in promoted.AsEnumerable().Reverse())
            {
                try
                {
                    string backup = Path.Combine(transaction, name + ".previous");
                    string target = Path.Combine(LocalSettings.AssetsPath, name);
                    if (File.Exists(backup)) File.Move(backup, target, true); else File.Delete(target);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { rollbackFailed = true; Diagnostics.Record("cards.rollback", error); }
            }
            throw;
        }
        finally
        {
            if (!rollbackFailed)
                try { Directory.Delete(transaction, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Diagnostics.Record("cards.cleanup", error); }
        }
    }
}
