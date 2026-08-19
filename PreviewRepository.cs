using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QoLBarGlamourPreview;

internal sealed class PreviewRepository : IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new(
        [".png", ".jpg", ".jpeg", ".webp", ".bmp"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly Regex CopySuffixRegex = new(@"\s*\(\d+\)$", RegexOptions.Compiled);
    private static readonly string CacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "QoLBarGlamourPreviewCache");

    private readonly Configuration configuration;
    private readonly string pluginConfigRoot;
    private readonly object reloadLock = new();
    private readonly Dictionary<string, CacheEntry> cacheBustedPaths = new(StringComparer.OrdinalIgnoreCase);

    private PreviewSnapshot snapshot = PreviewSnapshot.Empty;
    private Task? reloadTask;
    private DateTime lastReloadUtc = DateTime.MinValue;
    private bool forceReloadPending;
    private volatile bool disposed;

    public string PluginConfigRoot => Volatile.Read(ref snapshot).PluginConfigRoot;
    public string GpmConfigurationFile => Volatile.Read(ref snapshot).GpmConfigurationFile;
    public string AllocationFile => Volatile.Read(ref snapshot).AllocationFile;
    public string DesignsDirectory => Volatile.Read(ref snapshot).DesignsDirectory;
    public string PreviewsDirectory => Volatile.Read(ref snapshot).PreviewsDirectory;
    public string LastError => Volatile.Read(ref snapshot).LastError;
    public int PreviewCount => Volatile.Read(ref snapshot).PreviewsByDesign.Count;

    public PreviewRepository(Configuration configuration)
    {
        this.configuration = configuration;
        pluginConfigRoot = ResolvePluginConfigRoot();
        CleanupCacheDirectory();
    }

    public void ForceReload()
    {
        lock (reloadLock)
            lastReloadUtc = DateTime.MinValue;

        ReloadIfStale(force: true);
    }

    public bool TryGetPreview(string designName, out string texturePath)
    {
        ReloadIfStale(force: false);
        texturePath = string.Empty;

        var current = Volatile.Read(ref snapshot);
        if (!current.PreviewsByDesign.TryGetValue(designName, out var originalPath))
            return false;

        texturePath = GetCacheBustedPath(originalPath);
        return !string.IsNullOrWhiteSpace(texturePath);
    }

    private void ReloadIfStale(bool force)
    {
        var now = DateTime.UtcNow;

        lock (reloadLock)
        {
            if (disposed)
                return;

            if (reloadTask is { IsCompleted: false })
            {
                forceReloadPending |= force;
                return;
            }

            var interval = TimeSpan.FromSeconds(Math.Clamp(configuration.RefreshIntervalSeconds, 1, 60));
            if (!force && now - lastReloadUtc < interval)
                return;

            lastReloadUtc = now;
            StartReloadUnsafe();
        }
    }

    private void StartReloadUnsafe()
    {
        reloadTask = Task.Run(() =>
        {
            var updated = BuildSnapshot();
            if (!disposed)
                Volatile.Write(ref snapshot, updated);

            lock (reloadLock)
            {
                if (disposed || !forceReloadPending)
                    return;

                forceReloadPending = false;
                lastReloadUtc = DateTime.UtcNow;
                StartReloadUnsafe();
            }
        });
    }

    private PreviewSnapshot BuildSnapshot()
    {
        var previous = Volatile.Read(ref snapshot);

        try
        {
            var gpmConfigurationFile = ResolveGpmConfigurationFile(pluginConfigRoot);
            var designsDirectory = Path.Combine(pluginConfigRoot, "Glamourer", "designs");
            var previewsDirectory = ResolvePreviewsDirectory(gpmConfigurationFile);
            var allocationFile = ResolveAllocationFile(pluginConfigRoot, previewsDirectory);
            var previewsByDesign = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(previewsDirectory) || !Directory.Exists(previewsDirectory))
            {
                return new PreviewSnapshot(
                    pluginConfigRoot,
                    gpmConfigurationFile,
                    allocationFile,
                    designsDirectory,
                    previewsDirectory,
                    previewsByDesign,
                    "GPM previews folder was not found. Configure it in GPM or set a manual folder here.");
            }

            var allocations = LoadAllocations(allocationFile);
            LoadAllocatedDesigns(
                allocations,
                designsDirectory,
                previewsDirectory,
                previewsByDesign);
            LoadFilenameFallbacks(previewsDirectory, previewsByDesign);

            return new PreviewSnapshot(
                pluginConfigRoot,
                gpmConfigurationFile,
                allocationFile,
                designsDirectory,
                previewsDirectory,
                previewsByDesign,
                string.Empty);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to reload Glamourer preview mappings.");
            return previous with { LastError = ex.Message };
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
        return TryGetStringProperty(document.RootElement, "PreviewsFolderPath", out var path)
            ? Environment.ExpandEnvironmentVariables(path)
            : string.Empty;
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
            if (!Guid.TryParse(property.Name, out var id) || property.Value.ValueKind != JsonValueKind.String)
                continue;

            var value = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                result[id] = value;
        }

        return result;
    }

    private static void LoadAllocatedDesigns(
        IReadOnlyDictionary<Guid, string> allocations,
        string designsDirectory,
        string previewsDirectory,
        IDictionary<string, string> previewsByDesign)
    {
        if (!Directory.Exists(designsDirectory))
            return;

        var previewsRoot = Path.GetFullPath(previewsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(designsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id) ||
                !allocations.TryGetValue(id, out var relativeImage))
                continue;

            var imagePath = Path.GetFullPath(Path.Combine(previewsDirectory, relativeImage));
            if (!imagePath.StartsWith(previewsRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(imagePath))
                continue;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (TryGetStringProperty(document.RootElement, "Name", out var name) &&
                    !string.IsNullOrWhiteSpace(name))
                    previewsByDesign[name.Trim()] = imagePath;
            }
            catch (Exception ex)
            {
                Plugin.Log.Verbose(ex, $"Could not parse Glamourer design file {file}.");
            }
        }
    }

    private static void LoadFilenameFallbacks(
        string previewsDirectory,
        IDictionary<string, string> previewsByDesign)
    {
        foreach (var image in Directory.EnumerateFiles(previewsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!ImageExtensions.Contains(Path.GetExtension(image)))
                continue;

            var name = CopySuffixRegex.Replace(Path.GetFileNameWithoutExtension(image), string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(name) && !previewsByDesign.ContainsKey(name))
                previewsByDesign[name] = image;
        }
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.String)
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
            var now = DateTime.UtcNow;
            if (cacheBustedPaths.TryGetValue(originalPath, out var cached) && now < cached.NextCheckUtc)
                return cached.Exists ? cached.CachePath : string.Empty;

            var nextCheck = now.AddSeconds(Math.Clamp(configuration.RefreshIntervalSeconds, 1, 60));
            if (!File.Exists(originalPath))
            {
                cacheBustedPaths[originalPath] = new CacheEntry(0, string.Empty, nextCheck, false);
                return string.Empty;
            }

            var lastWriteTicks = File.GetLastWriteTimeUtc(originalPath).Ticks;
            if (cached.Exists &&
                cached.LastWriteTicks == lastWriteTicks &&
                File.Exists(cached.CachePath))
            {
                cacheBustedPaths[originalPath] = cached with { NextCheckUtc = nextCheck };
                return cached.CachePath;
            }

            Directory.CreateDirectory(CacheDirectory);

            var fileName = Path.GetFileNameWithoutExtension(originalPath);
            var extension = Path.GetExtension(originalPath);
            var pathHash = StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetDirectoryName(originalPath) ?? string.Empty);
            var cachePath = Path.Combine(CacheDirectory, $"qgp_{pathHash:X8}_{fileName}_{lastWriteTicks}{extension}");

            if (!File.Exists(cachePath))
                File.Copy(originalPath, cachePath, true);

            if (cached.Exists && !cached.CachePath.Equals(cachePath, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(cached.CachePath); } catch { }
            }

            cacheBustedPaths[originalPath] = new CacheEntry(lastWriteTicks, cachePath, nextCheck, true);
            return cachePath;
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Could not cache-bust a preview image.");
            return originalPath;
        }
    }

    private static void CleanupCacheDirectory()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory))
                return;

            foreach (var file in Directory.EnumerateFiles(CacheDirectory, "qgp_*", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Could not clean the QoLBar preview cache.");
        }
    }

    public void Dispose()
        => disposed = true;

    private readonly record struct CacheEntry(
        long LastWriteTicks,
        string CachePath,
        DateTime NextCheckUtc,
        bool Exists);

    private sealed record PreviewSnapshot(
        string PluginConfigRoot,
        string GpmConfigurationFile,
        string AllocationFile,
        string DesignsDirectory,
        string PreviewsDirectory,
        IReadOnlyDictionary<string, string> PreviewsByDesign,
        string LastError)
    {
        public static PreviewSnapshot Empty { get; } = new(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            string.Empty);
    }
}
