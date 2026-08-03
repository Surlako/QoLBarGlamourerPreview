using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QoLBarGlamourPreview;

internal sealed class PreviewRepository
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    private static readonly Regex CopySuffixRegex = new(@"\s*\(\d+\)$", RegexOptions.Compiled);

    private readonly Configuration configuration;
    private readonly Dictionary<string, string> previewsByDesign = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long LastWriteTicks, string CachePath)> cacheBustedPaths = new(StringComparer.OrdinalIgnoreCase);
    private DateTime lastReloadUtc = DateTime.MinValue;

    public string PluginConfigRoot { get; private set; } = string.Empty;
    public string GpmConfigurationFile { get; private set; } = string.Empty;
    public string AllocationFile { get; private set; } = string.Empty;
    public string DesignsDirectory { get; private set; } = string.Empty;
    public string PreviewsDirectory { get; private set; } = string.Empty;
    public string LastError { get; private set; } = string.Empty;
    public int PreviewCount => previewsByDesign.Count;

    public PreviewRepository(Configuration configuration)
    {
        this.configuration = configuration;
    }

    public void ForceReload()
    {
        lastReloadUtc = DateTime.MinValue;
        ReloadIfStale(true);
    }

    public bool TryGetPreview(string designName, out string texturePath)
    {
        ReloadIfStale(false);
        texturePath = string.Empty;

        if (!previewsByDesign.TryGetValue(designName, out var originalPath) || !File.Exists(originalPath))
            return false;

        texturePath = GetCacheBustedPath(originalPath);
        return File.Exists(texturePath);
    }

    private void ReloadIfStale(bool force)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(configuration.RefreshIntervalSeconds, 1, 60));
        if (!force && DateTime.UtcNow - lastReloadUtc < interval)
            return;

        lastReloadUtc = DateTime.UtcNow;
        Reload();
    }

    private void Reload()
    {
        previewsByDesign.Clear();
        LastError = string.Empty;

        try
        {
            PluginConfigRoot = ResolvePluginConfigRoot();
            GpmConfigurationFile = ResolveGpmConfigurationFile(PluginConfigRoot);
            DesignsDirectory = Path.Combine(PluginConfigRoot, "Glamourer", "designs");
            PreviewsDirectory = ResolvePreviewsDirectory(GpmConfigurationFile);
            AllocationFile = ResolveAllocationFile(PluginConfigRoot, PreviewsDirectory);

            if (string.IsNullOrWhiteSpace(PreviewsDirectory) || !Directory.Exists(PreviewsDirectory))
            {
                LastError = "GPM previews folder was not found. Configure it in GPM or set a manual folder here.";
                return;
            }

            var allocations = LoadAllocations(AllocationFile);
            LoadAllocatedDesigns(allocations);
            LoadFilenameFallbacks();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Plugin.Log.Error(ex, "Failed to reload Glamourer preview mappings.");
        }
    }

    private static string ResolvePluginConfigRoot()
    {
        var parent = Plugin.PluginInterface.ConfigDirectory.Parent;
        if (parent is not null && parent.Exists)
            return parent.FullName;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "XIVLauncher", "pluginConfigs");
    }

    private static string ResolveGpmConfigurationFile(string root)
    {
        var directCandidates = new[]
        {
            Path.Combine(root, "GlamourerPreviewManager.json"),
            Path.Combine(root, "GlamourerPreviewManager", "GlamourerPreviewManager.json")
        };

        foreach (var candidate in directCandidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        if (!Directory.Exists(root))
            return string.Empty;

        return Directory.EnumerateFiles(root, "*GlamourerPreviewManager*.json", SearchOption.TopDirectoryOnly)
            .FirstOrDefault() ?? string.Empty;
    }

    private string ResolvePreviewsDirectory(string gpmConfigurationFile)
    {
        if (!string.IsNullOrWhiteSpace(configuration.ManualPreviewsFolder))
            return Environment.ExpandEnvironmentVariables(configuration.ManualPreviewsFolder.Trim().Trim('"'));

        if (string.IsNullOrWhiteSpace(gpmConfigurationFile) || !File.Exists(gpmConfigurationFile))
            return string.Empty;

        using var document = JsonDocument.Parse(File.ReadAllText(gpmConfigurationFile));
        if (TryGetStringProperty(document.RootElement, "PreviewsFolderPath", out var path))
            return Environment.ExpandEnvironmentVariables(path);

        return string.Empty;
    }

    private static string ResolveAllocationFile(string root, string previewsDirectory)
    {
        var direct = Path.Combine(root, "GlamourerPreviewManager", "allocation.json");
        if (File.Exists(direct))
            return direct;

        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*Glamourer*Preview*", SearchOption.TopDirectoryOnly))
            {
                var candidate = Path.Combine(directory, "allocation.json");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        var legacy = string.IsNullOrWhiteSpace(previewsDirectory)
            ? string.Empty
            : Path.Combine(previewsDirectory, "allocation.json");
        return File.Exists(legacy) ? legacy : direct;
    }

    private static Dictionary<Guid, string> LoadAllocations(string allocationFile)
    {
        var result = new Dictionary<Guid, string>();
        if (!File.Exists(allocationFile))
            return result;

        using var document = JsonDocument.Parse(File.ReadAllText(allocationFile));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (Guid.TryParse(property.Name, out var id) && property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    result[id] = value;
            }
        }

        return result;
    }

    private void LoadAllocatedDesigns(Dictionary<Guid, string> allocations)
    {
        if (!Directory.Exists(DesignsDirectory))
            return;

        var previewsRoot = Path.GetFullPath(PreviewsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(DesignsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id))
                continue;
            if (!allocations.TryGetValue(id, out var relativeImage))
                continue;

            var imagePath = Path.GetFullPath(Path.Combine(PreviewsDirectory, relativeImage));
            if (!imagePath.StartsWith(previewsRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(imagePath))
                continue;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (TryGetStringProperty(document.RootElement, "Name", out var name) && !string.IsNullOrWhiteSpace(name))
                    previewsByDesign[name.Trim()] = imagePath;
            }
            catch (Exception ex)
            {
                Plugin.Log.Verbose(ex, $"Could not parse Glamourer design file {file}.");
            }
        }
    }

    private void LoadFilenameFallbacks()
    {
        foreach (var image in Directory.EnumerateFiles(PreviewsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!ImageExtensions.Contains(Path.GetExtension(image), StringComparer.OrdinalIgnoreCase))
                continue;

            var name = CopySuffixRegex.Replace(Path.GetFileNameWithoutExtension(image), string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(name))
                previewsByDesign.TryAdd(name, image);
        }
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.String)
                continue;

            value = property.Value.GetString() ?? string.Empty;
            return true;
        }

        return false;
    }

    private string GetCacheBustedPath(string originalPath)
    {
        try
        {
            var lastWriteTicks = File.GetLastWriteTimeUtc(originalPath).Ticks;
            if (cacheBustedPaths.TryGetValue(originalPath, out var cached) &&
                cached.LastWriteTicks == lastWriteTicks && File.Exists(cached.CachePath))
                return cached.CachePath;

            var cacheDirectory = Path.Combine(Path.GetTempPath(), "QoLBarGlamourPreviewCache");
            Directory.CreateDirectory(cacheDirectory);

            var fileName = Path.GetFileNameWithoutExtension(originalPath);
            var extension = Path.GetExtension(originalPath);
            var pathHash = StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetDirectoryName(originalPath) ?? string.Empty);
            var cachePath = Path.Combine(cacheDirectory, $"qgp_{pathHash:X8}_{fileName}_{lastWriteTicks}{extension}");

            if (!File.Exists(cachePath))
                File.Copy(originalPath, cachePath, true);

            if (cacheBustedPaths.TryGetValue(originalPath, out var old) &&
                !old.CachePath.Equals(cachePath, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(old.CachePath); } catch { }
            }

            cacheBustedPaths[originalPath] = (lastWriteTicks, cachePath);
            return cachePath;
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Could not cache-bust a preview image.");
            return originalPath;
        }
    }
}
