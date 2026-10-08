using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Varlatch;

/// <summary>
/// How Windows tells the app that a notification or one of its buttons was
/// clicked: a COM class the app registers under its own user, which Windows
/// calls in the running app, or starts the app for when it is not running.
/// This is what any desktop app that is not packaged does; it needs nothing
/// from the Windows App SDK runtime, so the app stays self-contained.
/// </summary>
internal static partial class ToastActivator
{
    /// <summary>The app's identity for its notifications.</summary>
    public const string AppUserModelId = "Robotsson.Varlatch";

    public const string Clsid = "6E3B0E59-3C3F-4C2B-9C1E-1F0A6D2B7A41";

    /// <summary>On the command line of the app when Windows starts it for a click.</summary>
    public const string Argument = "-ToastActivated";

    private const string AumidKey = @"Software\Classes\AppUserModelId\" + AppUserModelId;
    private const string ClsidKey = @"Software\Classes\CLSID\{" + Clsid + "}";

    private static uint cookie;
    private static Action<string>? handler;

    /// <summary>Registers the app's identity and activator for the current user, and listens for clicks.</summary>
    public static void Install(string displayName, string iconPath, Action<string> onActivated)
    {
        handler = onActivated;
        SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        using (var aumid = Registry.CurrentUser.CreateSubKey(AumidKey))
        {
            aumid.SetValue("DisplayName", displayName);
            aumid.SetValue("IconUri", iconPath);
            aumid.SetValue("CustomActivator", "{" + Clsid + "}");
        }
        using (var server = Registry.CurrentUser.CreateSubKey(ClsidKey + @"\LocalServer32"))
        {
            server.SetValue("", $"\"{Environment.ProcessPath}\" {Argument}");
        }
        var clsid = new Guid(Clsid);
        var hr = CoRegisterClassObject(ref clsid, new Factory(), CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out cookie);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>For the uninstaller: Windows forgets the app's identity and activator.</summary>
    public static void Uninstall()
    {
        Registry.CurrentUser.DeleteSubKeyTree(AumidKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ClsidKey, throwOnMissingSubKey: false);
    }

    public static void Stop()
    {
        if (cookie != 0) CoRevokeClassObject(cookie);
        cookie = 0;
    }

    [ComImport]
    [Guid("53E31837-6600-4A81-9395-75CFFE746F94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INotificationActivationCallback
    {
        void Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                      [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
                      IntPtr data,
                      uint count);
    }

    [ComImport]
    [Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Callback : INotificationActivationCallback
    {
        public void Activate(string appUserModelId, string invokedArgs, IntPtr data, uint count)
        {
            try
            {
                handler?.Invoke(invokedArgs ?? "");
            }
            catch (Exception e)
            {
                Log.Write("notification click: " + e);
            }
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Factory : IClassFactory
    {
        private static readonly Guid Unknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid CallbackInterface = typeof(INotificationActivationCallback).GUID;

        public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance)
        {
            instance = IntPtr.Zero;
            if (outer != IntPtr.Zero) return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
            var callback = new Callback();
            if (iid == CallbackInterface)
                instance = Marshal.GetComInterfaceForObject<Callback, INotificationActivationCallback>(callback);
            else if (iid == Unknown)
                instance = Marshal.GetIUnknownForObject(callback);
            else
                return unchecked((int)0x80004002); // E_NOINTERFACE
            return 0;
        }

        public int LockServer(bool lockServer) => 0;
    }

    private const uint CLSCTX_LOCAL_SERVER = 4;
    private const uint REGCLS_MULTIPLEUSE = 1;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object factory,
                                                    uint context, uint flags, out uint cookie);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint cookie);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}
