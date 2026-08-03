using System;
using Dalamud.Configuration;

namespace QoLBarGlamourPreview;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public float PreviewWidth { get; set; } = 320f;
    public int HoverDelayMilliseconds { get; set; } = 100;
    public bool ShowDesignName { get; set; } = true;
    public bool ShowMissingPreviewNotice { get; set; } = false;
    public int RefreshIntervalSeconds { get; set; } = 3;
    public string ManualPreviewsFolder { get; set; } = string.Empty;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
