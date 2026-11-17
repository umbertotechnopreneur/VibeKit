using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using VibeKit.StudioControls.Core;

namespace VibeKit.StudioControls.Windows;

/// <summary>Synthetic Windows CI checks. No window, camera, OBS connection or device enumeration.</summary>
internal static class WindowsVerification
{
    public static int Run()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "StudioControls-verify-" + Guid.NewGuid().ToString("N"));
        LocalSettings.VerificationDirectory = temporary;
        try
        {
            Directory.CreateDirectory(temporary);
            var settings = new PanelSettings { Theme = "Dark", ProtectedPassword = LocalSettings.ProtectPassword("synthetic-test-password") };
            LocalSettings.Save(settings);
            var loaded = LocalSettings.Load(out string? warning);
            Require(warning is null && loaded.Theme == "Dark", "Settings round trip failed.");
            Require(LocalSettings.ReadPassword(loaded) == "synthetic-test-password", "DPAPI round trip failed.");
            Require(!File.ReadAllText(Path.Combine(temporary, "panel.json")).Contains("synthetic-test-password", StringComparison.Ordinal), "Plain password was persisted.");
            string logo = Path.Combine(temporary, "logo.png");
            using (var bitmap = new Bitmap(100, 100))
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.CornflowerBlue);
                bitmap.Save(logo, ImageFormat.Png);
            }
            var project = new ProjectSettings { Name = "Synthetic verification", Description = "A local, synthetic renderer test.",
                Links = ["https://example.invalid", "", ""], Logo = logo, VibeWare = true };
            ProjectSettings saved = CardRenderer.Prepare(project, CancellationToken.None);
            Require(saved.Logo == "Project-logo.png", "Logo was not copied into the project assets.");
            var hashes = new Dictionary<string, byte[]>();
            foreach (var card in StudioCatalog.Default.CardSources)
            {
                string path = Path.Combine(LocalSettings.AssetsPath, card.FileName);
                using var bitmap = new Bitmap(path);
                Require(bitmap.Width == 2560 && bitmap.Height == 1440, "Invalid artwork dimensions.");
                if (card.FileName == "Thank-you-card.png") Require(bitmap.GetPixel(500, 500).A == 0, "Outro webcam opening is opaque.");
                if (card.FileName == "Compare-frame.png") Require(bitmap.GetPixel(500, 500).A == 0 && bitmap.GetPixel(1800, 500).A == 0, "Comparison openings are opaque.");
                hashes[path] = SHA256.HashData(File.ReadAllBytes(path));
            }
            string brokenLogo = Path.Combine(temporary, "broken.png");
            File.WriteAllText(brokenLogo, "synthetic malformed image");
            bool rejected = false;
            try { CardRenderer.Prepare(project with { Logo = brokenLogo }, CancellationToken.None); }
            catch (Exception error) when (error is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { rejected = true; }
            Require(rejected, "Malformed image was not rejected.");
            foreach (var (path, hash) in hashes)
                Require(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Render failure changed an existing card.");
            Diagnostics.Record("synthetic.verification", new InvalidOperationException("synthetic private detail",
                new IOException("synthetic private inner detail")));
            string exported = Path.Combine(temporary, "logs.zip");
            Diagnostics.Export(exported);
            using (var archive = System.IO.Compression.ZipFile.OpenRead(exported))
            {
                Require(archive.Entries.All(entry => entry.Name == "environment.txt" || entry.Name.StartsWith("studio.log", StringComparison.Ordinal)), "Diagnostics exported configuration.");
                using var log = new StreamReader(archive.GetEntry("studio.log")!.Open());
                string text = log.ReadToEnd();
                Require(text.Contains("caused-by System.IO.IOException", StringComparison.Ordinal), "Diagnostic cause type was lost.");
                Require(!text.Contains("synthetic private", StringComparison.Ordinal), "Diagnostic messages exposed private details.");
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "windows-verification.txt"), "PASS: isolated DPAPI, settings, six GDI+ cards, transparency, failed-render preservation and diagnostic archive. No devices or OBS contacted.\n");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "windows-verification.txt"), "FAIL: " + error + "\n");
            return 1;
        }
        finally
        {
            LocalSettings.VerificationDirectory = null;
            try { Directory.Delete(temporary, true); }
            catch (IOException) { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
