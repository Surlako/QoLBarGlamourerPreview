using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;

namespace QoLBarGlamourPreview;

internal sealed class ImGuiHookManager : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ButtonDelegate(IntPtr label, Vector2 size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ButtonExDelegate(IntPtr label, Vector2 size, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginDelegate(IntPtr name, IntPtr open, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndDelegate();

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    private readonly Plugin plugin;
    private Hook<ButtonDelegate>? buttonHook;
    private Hook<ButtonExDelegate>? buttonExHook;
    private Hook<BeginDelegate>? beginHook;
    private Hook<EndDelegate>? endHook;

    public ImGuiHookManager(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Initialize()
    {
        try
        {
            var moduleHandle = GetModuleHandle("cimgui.dll");
            if (moduleHandle == IntPtr.Zero)
            {
                var module = Process.GetCurrentProcess().Modules
                    .Cast<ProcessModule>()
                    .FirstOrDefault(m => m.ModuleName.Contains("cimgui", StringComparison.OrdinalIgnoreCase));
                moduleHandle = module?.BaseAddress ?? IntPtr.Zero;
            }

            if (moduleHandle == IntPtr.Zero)
                throw new InvalidOperationException("Could not find cimgui.dll.");

            var buttonAddress = GetProcAddress(moduleHandle, "igButton");
            var buttonExAddress = GetProcAddress(moduleHandle, "igButtonEx");
            var beginAddress = GetProcAddress(moduleHandle, "igBegin");
            var endAddress = GetProcAddress(moduleHandle, "igEnd");

            if (buttonAddress == IntPtr.Zero || beginAddress == IntPtr.Zero || endAddress == IntPtr.Zero)
                throw new InvalidOperationException("Could not resolve the required cimgui functions.");

            buttonHook = Plugin.GameInteropProvider.HookFromAddress<ButtonDelegate>(buttonAddress, ButtonDetour);
            buttonHook.Enable();

            if (buttonExAddress != IntPtr.Zero)
            {
                buttonExHook = Plugin.GameInteropProvider.HookFromAddress<ButtonExDelegate>(buttonExAddress, ButtonExDetour);
                buttonExHook.Enable();
            }

            beginHook = Plugin.GameInteropProvider.HookFromAddress<BeginDelegate>(beginAddress, BeginDetour);
            beginHook.Enable();

            endHook = Plugin.GameInteropProvider.HookFromAddress<EndDelegate>(endAddress, EndDetour);
            endHook.Enable();

            plugin.SetHookError(string.Empty);
            Plugin.Log.Information("QoLBar Glamour Preview native hooks initialized.");
        }
        catch (Exception ex)
        {
            DisposeHooks();
            Plugin.Log.Error(ex, "Failed to initialize QoLBar Glamour Preview native hooks.");
            plugin.SetHookError(ex.Message);
        }
    }

    private void DisposeHooks()
    {
        TryDisposeHook(endHook, "igEnd");
        endHook = null;
        TryDisposeHook(beginHook, "igBegin");
        beginHook = null;
        TryDisposeHook(buttonExHook, "igButtonEx");
        buttonExHook = null;
        TryDisposeHook(buttonHook, "igButton");
        buttonHook = null;
    }

    private static void TryDisposeHook(IDisposable? hook, string name)
    {
        try
        {
            hook?.Dispose();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not dispose the {HookName} hook.", name);
        }
    }

    public void Dispose() => DisposeHooks();

    private byte ButtonDetour(IntPtr labelPointer, Vector2 size)
    {
        var result = buttonHook is not null ? buttonHook.Original(labelPointer, size) : (byte)0;
        ReportButton(labelPointer);
        return result;
    }

    private byte ButtonExDetour(IntPtr labelPointer, Vector2 size, int flags)
    {
        var result = buttonExHook is not null ? buttonExHook.Original(labelPointer, size, flags) : (byte)0;
        ReportButton(labelPointer);
        return result;
    }

    private void ReportButton(IntPtr labelPointer)
    {
        try
        {
            if (!plugin.ShouldInspectButton ||
                !ImGui.IsItemHovered(ImGuiHoveredFlags.RectOnly))
                return;

            var label = Marshal.PtrToStringUTF8(labelPointer);
            if (!string.IsNullOrWhiteSpace(label))
                plugin.OnButtonDrawn(label);
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Error while inspecting an ImGui button.");
        }
    }

    private byte BeginDetour(IntPtr namePointer, IntPtr openPointer, int flags)
    {
        try
        {
            plugin.OnBeginWindow(IsQoLBarWindow(namePointer));
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Error while tracking an ImGui window.");
        }

        return beginHook is not null ? beginHook.Original(namePointer, openPointer, flags) : (byte)0;
    }

    private static bool IsQoLBarWindow(IntPtr namePointer)
    {
        if (namePointer == IntPtr.Zero)
            return false;

        ReadOnlySpan<byte> prefix = "QoLBar"u8;
        for (var index = 0; index < prefix.Length; index++)
        {
            if (Marshal.ReadByte(namePointer, index) != prefix[index])
                return false;
        }

        var suffix = Marshal.ReadByte(namePointer, prefix.Length);
        return suffix == 0 || suffix == (byte)'#';
    }

    private void EndDetour()
    {
        endHook?.Original();
        try
        {
            plugin.OnEndWindow();
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Error while leaving an ImGui window.");
        }
    }
}
