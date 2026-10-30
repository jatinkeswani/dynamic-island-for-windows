using System.Runtime.InteropServices;

namespace DynamicIsland;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Lists playback devices through Windows Core Audio and switches the default one.
/// Listing uses the documented MMDevice API. Switching uses IPolicyConfig, which is undocumented
/// (the same interface EarTrumpet and SoundSwitch use); Windows has no public API for it.
/// </summary>
public static class AudioOutputs
{
    private const uint DeviceStateActive = 1;
    private static PropertyKey FriendlyName = new() { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };

    public static List<AudioDevice> GetOutputs()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        string? defaultId = null;
        if (enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var defaultDevice) == 0)
            defaultDevice.GetId(out defaultId);

        var result = new List<AudioDevice>();
        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(DataFlow.Render, DeviceStateActive, out var devices));
        devices.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            devices.Item(i, out var device);
            device.GetId(out string id);
            result.Add(new AudioDevice(id, ReadName(device) ?? "Unknown device", id == defaultId));
        }
        return result;
    }

    public static void SetDefault(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigCom();
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role));
    }

    private static string? ReadName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return null;
        if (store.GetValue(ref FriendlyName, out var value) != 0) return null;
        try
        {
            const ushort VtLpwstr = 31;
            return value.Type == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    private enum DataFlow { Render = 0 }
    private enum Role { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(16)] private IntPtr _padding;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    // Only SetDefaultEndpoint is called; the methods before it exist to keep the vtable order
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
    }
}
