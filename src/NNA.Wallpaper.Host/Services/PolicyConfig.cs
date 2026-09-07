using System.Runtime.InteropServices;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// Undocumented COM interface used by the Windows Sound control panel itself to change the
/// default playback/recording device. Not part of any public SDK header, so it is declared by
/// hand here; the vtable order below (GetMixFormat .. SetEndpointVisibility) is fixed by the
/// interface's real (reverse-engineered) layout and must not be reordered. Parameter types on the
/// methods we never call (everything before SetDefaultEndpoint) only need to preserve the right
/// argument count/size for the vtable slot to line up, so they use IntPtr placeholders.
/// </summary>
[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(string deviceId, out IntPtr format);
    [PreserveSig] int GetDeviceFormat(string deviceId, bool bDefault, out IntPtr format);
    [PreserveSig] int ResetDeviceFormat(string deviceId);
    [PreserveSig] int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod(string deviceId, bool bDefault, out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int SetProcessingPeriod(string deviceId, IntPtr period);
    [PreserveSig] int GetShareMode(string deviceId, out IntPtr mode);
    [PreserveSig] int SetShareMode(string deviceId, IntPtr mode);
    [PreserveSig] int GetPropertyValue(string deviceId, IntPtr key, out IntPtr value);
    [PreserveSig] int SetPropertyValue(string deviceId, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint(string deviceId, ERole role);
    [PreserveSig] int SetEndpointVisibility(string deviceId, bool visible);
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

/// <summary>
/// Creates the CPolicyConfigClient COM object (CLSID {870af99c-171d-4f9e-af0d-e63df40c2bc9}) and
/// calls SetDefaultEndpoint for all three roles, matching what the Sound control panel's "Set as
/// Default Device" does. Never throws: every failure (COM class missing on this build, access
/// denied, bad device id) comes back as <c>ok:false</c> with a short reason so the caller can
/// answer <c>{ok:false,error:"unsupported"}</c> instead of taking the process down.
/// </summary>
internal static class PolicyConfig
{
    private static readonly Guid ClsidPolicyConfigClient = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    /// <summary>Only proves the COM class can be created (used for diagnostics/reporting); makes no changes.</summary>
    public static bool TryCreate(out string? error)
    {
        object? obj = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidPolicyConfigClient, throwOnError: true);
            obj = Activator.CreateInstance(type!);
            _ = (IPolicyConfig)obj!;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    public static bool TrySetDefaultEndpoint(string deviceId, out string? error)
    {
        object? obj = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidPolicyConfigClient, throwOnError: true);
            obj = Activator.CreateInstance(type!);
            var policy = (IPolicyConfig)obj!;
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
            {
                int hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr < 0)
                {
                    error = "hresult 0x" + hr.ToString("x8") + " (role " + role + ")";
                    return false;
                }
            }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }
}
