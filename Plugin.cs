using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace QoLBarGlamourPreview;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/qgp";
    private const string LegacyCommandName = "/qolglampreview";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly Configuration configuration;
    private readonly PreviewRepository repository;
    private readonly ImGuiHookManager hooks;
    private readonly Stack<bool> windowStack = new();

    private int windowStackFrame = -1;
    private int qolBarWindowDepth;
    private string hoveredDesignName = string.Empty;
    private int hoveredFrame = -100;
    private DateTime hoveredSinceUtc = DateTime.MinValue;
    private bool configWindowOpen;
    private string hookError = string.Empty;

    public Plugin()
    {
        configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        repository = new PreviewRepository(configuration);
        repository.ForceReload();

        hooks = new ImGuiHookManager(this);
        hooks.Initialize();

        var commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open QoLBar Glamour Preview settings."
        };

        CommandManager.AddHandler(CommandName, commandInfo);
        CommandManager.AddHandler(LegacyCommandName, commandInfo);

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(LegacyCommandName);
        hooks.Dispose();
        repository.Dispose();
    }

    internal void SetHookError(string message) => hookError = message;

    internal bool ShouldInspectButton
    {
        get
        {
            EnsureWindowFrame();
            return configuration.Enabled && qolBarWindowDepth > 0;
        }
    }

    internal void OnBeginWindow(bool isQoLBarWindow)
    {
        EnsureWindowFrame();

        windowStack.Push(isQoLBarWindow);
        if (isQoLBarWindow)
            qolBarWindowDepth++;
    }

    internal void OnEndWindow()
    {
        EnsureWindowFrame();

        if (windowStack.TryPop(out var wasQoLBarWindow) && wasQoLBarWindow && qolBarWindowDepth > 0)
            qolBarWindowDepth--;
    }

    internal void OnButtonDrawn(string label)
    {
        if (!ShouldInspectButton)
            return;

        var cleanName = CleanButtonLabel(label);
        if (string.IsNullOrWhiteSpace(cleanName) || cleanName == "+")
            return;

        var currentFrame = (int)ImGui.GetFrameCount();
        if (!cleanName.Equals(hoveredDesignName, StringComparison.OrdinalIgnoreCase))
        {
            hoveredDesignName = cleanName;
            hoveredSinceUtc = DateTime.UtcNow;
        }

        hoveredFrame = currentFrame;
    }

    private void EnsureWindowFrame()
    {
        var currentFrame = (int)ImGui.GetFrameCount();
        if (currentFrame == windowStackFrame)
            return;

        windowStackFrame = currentFrame;
        windowStack.Clear();
        qolBarWindowDepth = 0;
    }

    private static string CleanButtonLabel(string label)
    {
        var separator = label.IndexOf("##", StringComparison.Ordinal);
        if (separator >= 0)
            label = label[..separator];
        return label.Trim();
    }

    private void Draw()
    {
        if (configWindowOpen)
            DrawConfigurationWindow();

        if (!configuration.Enabled || string.IsNullOrWhiteSpace(hoveredDesignName))
            return;

        var currentFrame = (int)ImGui.GetFrameCount();
        if (currentFrame - hoveredFrame > 1)
            return;

        if ((DateTime.UtcNow - hoveredSinceUtc).TotalMilliseconds < configuration.HoverDelayMilliseconds)
            return;

        if (!repository.TryGetPreview(hoveredDesignName, out var imagePath))
        {
            if (configuration.ShowMissingPreviewNotice)
                DrawMissingPreviewTooltip();
            return;
        }

        var texture = TextureProvider.GetFromFile(imagePath).GetWrapOrDefault();
        if (texture is null || texture.Width <= 0 || texture.Height <= 0)
            return;

        ImGui.BeginTooltip();

        if (configuration.ShowDesignName)
        {
            ImGui.TextUnformatted(hoveredDesignName);
            ImGui.Separator();
        }

        var width = Math.Clamp(configuration.PreviewWidth, 100f, 1000f) * ImGuiHelpers.GlobalScale;
        var height = width * texture.Height / texture.Width;
        var maxHeight = ImGuiHelpers.MainViewport.WorkSize.Y * 0.75f;
        if (height > maxHeight)
        {
            height = maxHeight;
            width = height * texture.Width / texture.Height;
        }

        ImGui.Image(texture.Handle, new Vector2(width, height));
        ImGui.EndTooltip();
    }

    private void DrawMissingPreviewTooltip()
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(hoveredDesignName);
        ImGui.TextDisabled("No GPM preview assigned.");
        ImGui.EndTooltip();
    }

    private void DrawConfigurationWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(560, 430), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("QoLBar Glamour Preview Settings", ref configWindowOpen))
        {
            ImGui.End();
            return;
        }

        var changed = false;

        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Enable previews", ref enabled))
        {
            configuration.Enabled = enabled;
            changed = true;
        }

        var showName = configuration.ShowDesignName;
        if (ImGui.Checkbox("Show design name", ref showName))
        {
            configuration.ShowDesignName = showName;
            changed = true;
        }

        var showMissing = configuration.ShowMissingPreviewNotice;
        if (ImGui.Checkbox("Show a notice when no preview exists", ref showMissing))
        {
            configuration.ShowMissingPreviewNotice = showMissing;
            changed = true;
        }

        var previewWidth = configuration.PreviewWidth;
        if (ImGui.SliderFloat("Preview width", ref previewWidth, 100f, 800f, "%.0f px"))
        {
            configuration.PreviewWidth = previewWidth;
            changed = true;
        }

        var delay = configuration.HoverDelayMilliseconds;
        if (ImGui.SliderInt("Hover delay", ref delay, 0, 1000, "%d ms"))
        {
            configuration.HoverDelayMilliseconds = delay;
            changed = true;
        }

        var refresh = configuration.RefreshIntervalSeconds;
        if (ImGui.SliderInt("Refresh interval", ref refresh, 1, 30, "%d seconds"))
        {
            configuration.RefreshIntervalSeconds = refresh;
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Optional manual previews folder");
        ImGui.SetNextItemWidth(-1);
        var manualFolder = configuration.ManualPreviewsFolder;
        if (ImGui.InputText("##ManualPreviewsFolder", ref manualFolder, 1024))
        {
            configuration.ManualPreviewsFolder = manualFolder;
            changed = true;
        }
        ImGui.TextDisabled("Leave this empty to read the folder from Glamourer Preview Manager automatically.");

        if (changed)
            configuration.Save();

        if (ImGui.Button("Reload previews"))
            repository.ForceReload();

        ImGui.SameLine();
        if (ImGui.Button("Save settings"))
        {
            configuration.Save();
            repository.ForceReload();
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Mapped previews: {repository.PreviewCount}");
        ImGui.TextWrapped($"GPM config: {DisplayPath(repository.GpmConfigurationFile)}");
        ImGui.TextWrapped($"Allocation file: {DisplayPath(repository.AllocationFile)}");
        ImGui.TextWrapped($"Glamourer designs: {DisplayPath(repository.DesignsDirectory)}");
        ImGui.TextWrapped($"Previews folder: {DisplayPath(repository.PreviewsDirectory)}");

        if (!string.IsNullOrWhiteSpace(repository.LastError))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), repository.LastError);
        }

        if (!string.IsNullOrWhiteSpace(hookError))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.25f, 0.25f, 1f), $"Hook error: {hookError}");
        }

        ImGui.End();
    }

    private static string DisplayPath(string path) => string.IsNullOrWhiteSpace(path) ? "Not found" : path;

    private void OnCommand(string command, string arguments) => configWindowOpen = !configWindowOpen;
    private void OpenConfig() => configWindowOpen = true;
}
