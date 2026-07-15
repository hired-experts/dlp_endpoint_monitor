using System.Runtime.InteropServices;
using System.Text;

namespace DlpEndpointMonitor.Win32;

// ── Delegates ────────────────────────────────────────────────────────────────

delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

// ── Structs ───────────────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
struct WNDCLASSEX
{
    public uint    cbSize;
    public uint    style;
    public WndProc lpfnWndProc;
    public int     cbClsExtra;
    public int     cbWndExtra;
    public IntPtr  hInstance;
    public IntPtr  hIcon;
    public IntPtr  hCursor;
    public IntPtr  hbrBackground;
    public string? lpszMenuName;
    public string  lpszClassName;
    public IntPtr  hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
struct MSG
{
    public IntPtr hWnd;
    public uint   message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint   time;
    public int    ptX;
    public int    ptY;
}

[StructLayout(LayoutKind.Sequential)]
struct KBDLLHOOKSTRUCT
{
    public uint   vkCode;
    public uint   scanCode;
    public uint   flags;
    public uint   time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
struct DEV_BROADCAST_HDR
{
    public uint dbch_size;
    public uint dbch_devicetype;
    public uint dbch_reserved;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct DEV_BROADCAST_DEVICEINTERFACE
{
    public uint   dbcc_size;
    public uint   dbcc_devicetype;
    public uint   dbcc_reserved;
    public Guid   dbcc_classguid;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string dbcc_name;
}

[StructLayout(LayoutKind.Sequential)]
struct SP_DEVICE_INTERFACE_DATA
{
    public uint   cbSize;
    public Guid   InterfaceClassGuid;
    public uint   Flags;
    public IntPtr Reserved;
}

// The owning devnode of an interface. Filled by SetupDiGetDeviceInterfaceDetail so the caller
// can read the true device instance ID (SetupDiGetDeviceInstanceId) instead of string-munging
// the interface path.
[StructLayout(LayoutKind.Sequential)]
struct SP_DEVINFO_DATA
{
    public uint   cbSize;
    public Guid   ClassGuid;
    public uint   DevInst;
    public IntPtr Reserved;
}

// Fixed-size buffer large enough for any device path (MAX_PATH = 260 chars).
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct SP_DEVICE_INTERFACE_DETAIL_DATA_W
{
    public uint cbSize;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
    public string DevicePath;
}

[StructLayout(LayoutKind.Sequential)]
struct DEV_BROADCAST_VOLUME
{
    public uint   dbcv_size;
    public uint   dbcv_devicetype;
    public uint   dbcv_reserved;
    public uint   dbcv_unitmask;   // bitmask: bit 0 = A:\, bit 1 = B:\, etc.
    public ushort dbcv_flags;
}

// ── P/Invoke ──────────────────────────────────────────────────────────────────

static class NativeMethods
{
    // user32 — window management
    [DllImport("user32.dll", SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int X, int Y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool   DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void   PostQuitMessage(int nExitCode);
    [DllImport("user32.dll")] public static extern bool   PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int    GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMin, uint wMax);
    [DllImport("user32.dll")] public static extern bool   TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    // user32 — clipboard
    [DllImport("user32.dll")] public static extern bool   AddClipboardFormatListener(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool   RemoveClipboardFormatListener(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool   OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool   CloseClipboard();
    [DllImport("user32.dll")] public static extern bool   EmptyClipboard();
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] public static extern bool   IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] public static extern uint   EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] public static extern uint   RegisterClipboardFormat(string lpszFormat);

    // user32 — hooks
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool   UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern short  GetAsyncKeyState(int vKey);

    // user32 — device notifications
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr filter, uint flags);
    [DllImport("user32.dll")] public static extern bool UnregisterDeviceNotification(IntPtr handle);

    // kernel32
    [DllImport("kernel32.dll")] public static extern IntPtr  GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern IntPtr  GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] public static extern IntPtr  GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] public static extern bool    GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] public static extern IntPtr  GlobalFree(IntPtr hMem);

    // shell32
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    // setupapi — device enumeration
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(
        ref Guid ClassGuid,
        IntPtr   Enumerator,
        IntPtr   hwndParent,
        uint     Flags);

    // ClassGuid=NULL + a PnP enumerator branch name ("BTHENUM"/"BTHLE"), used with
    // DIGCF_ALLCLASSES to walk every device node under that branch directly - no interface
    // GUID involved. Distinct from the overload above (always called with Enumerator=IntPtr.Zero
    // today, so it never needed a real string or the W-suffixed entry point).
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsByEnumerator(
        IntPtr ClassGuid,
        string Enumerator,
        IntPtr hwndParent,
        uint   Flags);

    // Enumerates DEVICE NODES in a device-info set - SetupDiEnumDeviceInterfaces (below) is
    // interface-only and a different call.
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(
        IntPtr              DeviceInfoSet,
        uint                MemberIndex,
        ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr                       DeviceInfoSet,
        IntPtr                       DeviceInfoData,
        ref Guid                     InterfaceClassGuid,
        uint                         MemberIndex,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr                                DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA          DeviceInterfaceData,
        ref SP_DEVICE_INTERFACE_DETAIL_DATA_W DeviceInterfaceDetailData,
        uint                                  DeviceInterfaceDetailDataSize,
        out uint                              RequiredSize,
        IntPtr                                DeviceInfoData);

    // Overload that also fills the owning devnode, so the caller can read the true device
    // instance ID via SetupDiGetDeviceInstanceId.
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr                                DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA          DeviceInterfaceData,
        ref SP_DEVICE_INTERFACE_DETAIL_DATA_W DeviceInterfaceDetailData,
        uint                                  DeviceInterfaceDetailDataSize,
        out uint                              RequiredSize,
        ref SP_DEVINFO_DATA                   DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInstanceId(
        IntPtr              DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        StringBuilder       DeviceInstanceId,
        uint                DeviceInstanceIdSize,
        out uint            RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    // cfgmgr32 — device node management
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Locate_DevNodeW(
        out uint pdnDevInst,
        string   pDeviceID,
        uint     ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Enable_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    // First-child / next-sibling pair used together to enumerate all children of a devnode
    // (CM_Get_Child once, then CM_Get_Sibling repeatedly) - confirmed signature/usage pattern
    // via learn.microsoft.com's CM_Get_Child/CM_Get_Sibling reference pages. ulFlags is
    // documented "Not used, must be zero" for both, same as CM_Get_Parent above.
    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_IDW(
        uint          dnDevInst,
        StringBuilder Buffer,
        uint          BufferLen,
        uint          ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Request_Device_EjectW(
        uint     dnDevInst,
        out uint pVetoType,
        IntPtr   pszVetoName,
        uint     ulNameLength,
        uint     ulFlags);

    // Reads a devnode registry property (used for the REG_DWORD removal policy). Buffer is a
    // single uint for the DWORD-typed properties we read.
    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_DevNode_Registry_PropertyW(
        uint     dnDevInst,
        uint     ulProperty,
        out uint pulRegDataType,
        out uint Buffer,
        ref uint pulLength,
        uint     ulFlags);

    // Same native entry point as above, re-declared with a byte[] buffer instead of a single
    // uint out-param - needed for Compatible IDs (CM_DRP_COMPATIBLEIDS), a REG_MULTI_SZ (a
    // variable-length sequence of null-terminated strings), which cannot be marshaled through
    // the DWORD-typed overload IsRemovable depends on. pulLength is the buffer's byte capacity
    // on input, the actual byte length written on output - same in/out convention as the DWORD
    // overload's pulLength. A fixed 4KB caller buffer (see UsbActions.IsMassStorageDevice) is
    // used rather than the classic two-call growable-buffer idiom, matching this file's existing
    // "generously-sized fixed buffer" convention (see SP_DEVICE_INTERFACE_DETAIL_DATA_W).
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    public static extern uint CM_Get_DevNode_Registry_PropertyW_MultiSz(
        uint     dnDevInst,
        uint     ulProperty,
        out uint pulRegDataType,
        byte[]   Buffer,
        ref uint pulLength,
        uint     ulFlags);

    // ── Constants ─────────────────────────────────────────────────────────────

    // Messages
    public const uint WM_DESTROY         = 0x0002;
    public const uint WM_CLIPBOARDUPDATE = 0x031D;
    public const uint WM_DEVICECHANGE    = 0x0219;
    public const uint WM_DISPLAYCHANGE   = 0x007E;
    public const uint WM_WTSSESSION_CHANGE = 0x02B1;

    // Clipboard formats
    public const uint CF_UNICODETEXT  = 13;
    public const uint CF_HDROP        = 15;
    public const uint CF_DIB          = 8;
    public const uint GMEM_MOVEABLE   = 0x0002;
    public const uint DRAGQUERY_COUNT = 0xFFFFFFFF;

    // Keyboard hook
    public const int  WH_KEYBOARD_LL  = 13;
    public const int  HC_ACTION       = 0;
    public const uint WM_KEYDOWN      = 0x0100;
    public const uint WM_KEYUP        = 0x0101;
    public const uint WM_SYSKEYDOWN   = 0x0104;
    public const uint WM_SYSKEYUP     = 0x0105;
    public const uint VK_SHIFT        = 0x10;
    public const uint VK_CONTROL      = 0x11;
    public const uint VK_MENU         = 0x12; // Alt
    public const uint VK_INSERT       = 0x2D;
    public const uint VK_C            = 0x43;
    public const uint VK_S            = 0x53;
    public const uint VK_X            = 0x58;
    public const uint VK_V            = 0x56;
    public const uint VK_Z            = 0x5A;
    public const uint VK_LWIN         = 0x5B;
    public const uint VK_RWIN         = 0x5C;
    public const uint VK_SNAPSHOT     = 0x2C; // PrintScreen

    // Device change
    public const int  DBT_DEVICEARRIVAL          = 0x8000;
    public const int  DBT_DEVICEREMOVECOMPLETE   = 0x8004;
    public const uint DBT_DEVTYP_VOLUME          = 0x00000002;
    public const uint DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;

    // RegisterDeviceNotification flags
    public const uint DEVICE_NOTIFY_WINDOW_HANDLE         = 0x00000000;
    public const uint DEVICE_NOTIFY_ALL_INTERFACE_CLASSES = 0x00000004;

    // cfgmgr32
    public const uint CM_LOCATE_DEVNODE_NORMAL  = 0x00000000;
    public const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001; // includes recently removed devices
    public const uint CM_DISABLE_UI_NOT_OK      = 0x00000001;
    public const uint CR_SUCCESS                = 0x00000000;
    public const uint MAX_DEVICE_ID_LEN         = 200;

    // Removal policy (CM_Get_DevNode_Registry_PropertyW): a built-in / soldered device reports
    // EXPECT_NO_REMOVAL. Used to keep the laptop's own keyboard/touchpad unblockable.
    public const uint CM_DRP_REMOVAL_POLICY                   = 0x00000021;
    public const uint CM_REMOVAL_POLICY_EXPECT_NO_REMOVAL     = 1;

    // Compatible IDs (CM_Get_DevNode_Registry_PropertyW, REG_MULTI_SZ): decreasing-specificity
    // class/subclass/protocol strings (e.g. "USB\Class_08&SubClass_06&Prot_50"), used to detect
    // USB mass-storage class without needing a bound driver/interface GUID (see
    // UsbActions.IsMassStorageDevice - USBSTOR-disabled devices never expose GUID_DEVINTERFACE_DISK).
    // Confirmed 0x00000003 against Microsoft's own Windows SDK cfgmgr32.h (10.0.10240.0, mirrored at
    // github.com/tpn/winsdk-10), cross-checked against ReactOS/Wine/mingw-w64's independently
    // maintained cfgmgr32.h mirrors (all agree). Sanity-checked against this file's own
    // CM_DRP_REMOVAL_POLICY=0x21 - the same header lists CM_DRP_REMOVAL_POLICY_HW_DEFAULT (not
    // CM_DRP_REMOVAL_POLICY itself, which is 0x20) at 0x21; IsRemovable's live-hardware behavior
    // confirms 0x21 is the one that actually returns a populated policy, so this is the same
    // enumeration/header this file has always used.
    public const uint CM_DRP_COMPATIBLEIDS                    = 0x00000003;

    // setupapi
    public const uint DIGCF_PRESENT         = 0x00000002;
    public const uint DIGCF_ALLCLASSES      = 0x00000004;
    public const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // Window creation
    public static readonly IntPtr HWND_MESSAGE  = new(-3);
    public const uint WS_POPUP                  = 0x80000000;
    public const uint WS_EX_TOOLWINDOW          = 0x00000080; // excluded from Alt+Tab / taskbar
    public const uint WS_EX_NOACTIVATE          = 0x08000000; // never steals focus

    // user32 — display configuration (Windows CCD API)

    // Topology-only overload (numPaths/numModes = 0, arrays = null)
    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(
        uint   numPathElements,
        IntPtr pathArray,
        uint   numModeInfoElements,
        IntPtr modeInfoArray,
        uint   flags);

    // Path-array overload — used with SDC_USE_SUPPLIED_DISPLAY_CONFIG
    [DllImport("user32.dll", EntryPoint = "SetDisplayConfig")]
    public static extern int SetDisplayConfigPaths(
        uint                        numPathElements,
        [In] DisplayConfigPathInfo[] pathArray,
        uint                        numModeInfoElements,
        [In] DisplayConfigModeInfo[] modeInfoArray,
        uint                        flags);

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(
        uint     flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        uint                        flags,
        ref uint                    numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint                    numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr                      currentTopologyId);

    // Same native QueryDisplayConfig entry point, but with a real out-param instead of IntPtr.Zero
    // for the last argument - only meaningful (and only populated by Windows) when flags is
    // QDC_DATABASE_CURRENT, which is the one case DisplayActions.GetCurrentTopology needs.
    [DllImport("user32.dll", EntryPoint = "QueryDisplayConfig")]
    public static extern int QueryDisplayConfigTopology(
        uint                        flags,
        ref uint                    numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint                    numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        out uint                    currentTopologyId);

    public const uint SDC_TOPOLOGY_INTERNAL          = 0x00000001;
    public const uint SDC_TOPOLOGY_EXTEND            = 0x00000004;
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    public const uint SDC_APPLY                      = 0x00000080;
    public const uint SDC_SAVE_TO_DATABASE           = 0x00000200;
    public const uint SDC_ALLOW_CHANGES              = 0x00000400;

    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

    // Flags param for GetDisplayConfigBufferSizes/QueryDisplayConfig - the CCD database's current
    // topology entry, rather than a live path snapshot. Required (and the only flag that populates
    // the out currentTopologyId param above) for reading which Win+P mode is currently active.
    public const uint QDC_DATABASE_CURRENT = 0x00000004;

    // DISPLAYCONFIG_TOPOLOGY_ID values returned via QueryDisplayConfigTopology's out param -
    // exactly the four Win+P projection modes (PC screen only / Duplicate / Extend / Second
    // screen only). Bit-identical to the SDC_TOPOLOGY_* Set flags above by coincidence of the
    // underlying Win32 API, but named separately since they come from a different call (Query,
    // not Set) and mixing the two constant sets would be confusing to read.
    public const uint DISPLAYCONFIG_TOPOLOGY_INTERNAL = 0x00000001;
    public const uint DISPLAYCONFIG_TOPOLOGY_CLONE    = 0x00000002;
    public const uint DISPLAYCONFIG_TOPOLOGY_EXTEND   = 0x00000004;
    public const uint DISPLAYCONFIG_TOPOLOGY_EXTERNAL = 0x00000008;

    public const uint DISPLAYCONFIG_PATH_ACTIVE             = 0x00000001;
    public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID   = 0xFFFFFFFF;
    public const int  DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = unchecked((int)0x80000000);

    // BluetoothAPIs — device enumeration and management
    [DllImport("BluetoothAPIs.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("BluetoothAPIs.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("BluetoothAPIs.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    // pAddress is BLUETOOTH_ADDRESS (union of ULONGLONG + byte[6]); passing as ref ulong (ullLong field).
    [DllImport("BluetoothAPIs.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true)]
    public static extern uint BluetoothRemoveDevice(ref ulong pAddress);

    // wtsapi32/advapi32/userenv/kernel32 — cross-session process launch (AlertHost.exe).
    // Launching a GUI process into a different logged-on user's interactive session from a
    // Session-0 service requires duplicating that user's token and creating the process under
    // it with an explicit "winsta0\default" desktop, or the window is created in a
    // non-interactive window station and is never visible - this is the standard, documented
    // Win32 pattern for this, not an invented alternative.
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQueryUserToken(uint SessionId, out IntPtr phToken);

    // Session-change notifications (WM_WTSSESSION_CHANGE) so the primary can re-derive "what's the
    // active console session now" itself (via WTSGetActiveConsoleSessionId, above) whenever ANY
    // session connects/disconnects/logs on/off - not just this process's own session, which in the
    // real Session-0 deployment never changes. NOTIFY_FOR_ALL_SESSIONS is required for that reason.
    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    // Resolves who owns a session (SessionActions.GetCurrentSessionUser) - hServer is always
    // IntPtr.Zero (WTS_CURRENT_SERVER_HANDLE, the local machine); the returned buffer is a
    // WTSFreeMemory-owned native string, never freed by the caller directly. CharSet.Unicode is
    // required - wtsapi32 exports no undecorated symbol, so an unmarked DllImport defaults to
    // CharSet.Ansi and silently binds to WTSQuerySessionInformationA, whose ANSI buffer
    // SessionActions.QuerySessionInfoString's Marshal.PtrToStringUni (UTF-16) would then misread,
    // corrupting every username - matches every other string-returning P/Invoke in this file
    // (SetupDiGetDeviceInstanceId etc.), which already sets this explicitly.
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool WTSQuerySessionInformation(
        IntPtr hServer, uint sessionId, int wtsInfoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr pMemory);

    // WTS_INFO_CLASS values this binary actually uses (the enum has many more members we don't
    // need) - named to match the Win32 enum member names exactly for grep-ability.
    public const int WTSUserName   = 5;
    public const int WTSDomainName = 7;

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(
        IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, StringBuilder? lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    // DuplicateTokenEx / CreateProcessAsUser parameters (named per their Win32 enum values).
    public const int  SECURITY_IMPERSONATION_LEVEL = 2; // SecurityImpersonation
    public const int  TOKEN_TYPE_PRIMARY           = 1; // TokenPrimary
    public const uint MAXIMUM_ALLOWED              = 0x02000000;
    public const uint CREATE_UNICODE_ENVIRONMENT   = 0x00000400;

    // WTSRegisterSessionNotification dwFlags — NOTIFY_FOR_ALL_SESSIONS (not
    // NOTIFY_FOR_THIS_SESSION=0), since this process's OWN session never changes in the real
    // deployment; we need to hear about any OTHER session's transitions too.
    public const uint NOTIFY_FOR_ALL_SESSIONS = 1;
}

// ── Bluetooth structs ─────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
struct SYSTEMTIME
{
    public ushort wYear, wMonth, wDayOfWeek, wDay;
    public ushort wHour, wMinute, wSecond, wMilliseconds;
}

[StructLayout(LayoutKind.Sequential)]
struct BLUETOOTH_DEVICE_SEARCH_PARAMS
{
    public uint   dwSize;
    [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
    [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
    [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
    [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
    [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
    public byte   cTimeoutMultiplier;
    public IntPtr hRadio; // null = all radios
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct BLUETOOTH_DEVICE_INFO
{
    public uint   dwSize;
    public ulong  Address;         // BLUETOOTH_ADDRESS.ullLong — LSB = rgBytes[0]
    public uint   ulClassofDevice;
    [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
    [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
    [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
    public SYSTEMTIME stLastSeen;
    public SYSTEMTIME stLastUsed;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
    public string szName;
}

// ── Display config structs (Windows CCD API) ─────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
struct LUID { public uint LowPart; public int HighPart; }

[StructLayout(LayoutKind.Sequential)]
struct DisplayConfigRational { public uint Numerator; public uint Denominator; }

// sizeof = 20 bytes
[StructLayout(LayoutKind.Sequential)]
struct DisplayConfigPathSourceInfo
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;   // DISPLAYCONFIG_PATH_MODE_IDX_INVALID when path inactive
    public uint statusFlags;
}

// sizeof = 48 bytes
[StructLayout(LayoutKind.Sequential)]
struct DisplayConfigPathTargetInfo
{
    public LUID                   adapterId;
    public uint                   id;
    public uint                   modeInfoIdx;   // DISPLAYCONFIG_PATH_MODE_IDX_INVALID when path inactive
    public int                    outputTechnology; // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000
    public int                    rotation;
    public int                    scaling;
    public DisplayConfigRational  refreshRate;
    public int                    scanLineOrdering;
    public int                    targetAvailable; // BOOL
    public uint                   statusFlags;
}

// sizeof = 72 bytes
[StructLayout(LayoutKind.Sequential)]
struct DisplayConfigPathInfo
{
    public DisplayConfigPathSourceInfo sourceInfo;
    public DisplayConfigPathTargetInfo targetInfo;
    public uint                        flags; // DISPLAYCONFIG_PATH_ACTIVE = 0x1
}

// sizeof = 64 bytes exactly — fields are opaque, kept as ulongs so the struct is
// blittable and the CLR copies all 64 bytes correctly on array element assignment.
[StructLayout(LayoutKind.Sequential)]
struct DisplayConfigModeInfo
{
    private ulong _0, _1, _2, _3, _4, _5, _6, _7;
}

// ── Alert host launch structs (CreateProcessAsUser) ──────────────────────────

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct STARTUPINFO
{
    public int     cb;
    public string? lpReserved;
    public string? lpDesktop;      // must be "winsta0\default" or the launched window is invisible
    public string? lpTitle;
    public int     dwX;
    public int     dwY;
    public int     dwXSize;
    public int     dwYSize;
    public int     dwXCountChars;
    public int     dwYCountChars;
    public int     dwFillAttribute;
    public int     dwFlags;
    public short   wShowWindow;
    public short   cbReserved2;
    public IntPtr  lpReserved2;
    public IntPtr  hStdInput;
    public IntPtr  hStdOutput;
    public IntPtr  hStdError;
}

[StructLayout(LayoutKind.Sequential)]
struct PROCESS_INFORMATION
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public int    dwProcessId;
    public int    dwThreadId;
}
