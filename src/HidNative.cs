using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace AjazzBatteryMonitor
{
    /// <summary>
    /// Win32 SetupAPI + HID.DLL reader that coexists with OemDrv.exe using shared R/W handles.
    /// </summary>
    public static class HidBatteryReader
    {
        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int ERROR_IO_PENDING = 997;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        private sealed class EndpointPair
        {
            public ushort ProductId;
            public string FeaturePath;
            public string InputPath;
        }

        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr hFile, ref NativeOverlapped lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Queries the connected Ajazz AJ139 Pro (preferring wired USB 0xFA7B over 2.4GHz dongle 0xFA7C).
        /// </summary>
        public static BatteryStatus ReadCurrentStatus(int timeoutMs)
        {
            var pairs = DiscoverEndpointPairs();
            if (pairs.Count == 0)
            {
                return BatteryStatus.Disconnected("Mouse não encontrado");
            }

            // Prefer wired USB mouse (0xFA7B) when plugged in, then fall back to 2.4G dongle (0xFA7C)
            pairs.Sort((a, b) =>
            {
                int pa = a.ProductId == AjazzProtocol.WiredProductId ? 0 : 1;
                int pb = b.ProductId == AjazzProtocol.WiredProductId ? 0 : 1;
                return pa.CompareTo(pb);
            });

            foreach (var pair in pairs)
            {
                string mode = pair.ProductId == AjazzProtocol.WiredProductId ? "USB" : "2.4G";
                BatteryStatus status = QueryEndpointPair(pair, mode, timeoutMs);
                if (status != null && status.Connected)
                {
                    return status;
                }
            }

            return BatteryStatus.Disconnected("Mouse em repouso ou desconectado");
        }

        private static List<EndpointPair> DiscoverEndpointPairs()
        {
            var map = new Dictionary<ushort, EndpointPair>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);

            IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devInfo == INVALID_HANDLE_VALUE)
                return new List<EndpointPair>();

            try
            {
                var iface = new SP_DEVICE_INTERFACE_DATA();
                iface.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));

                for (uint i = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
                {
                    int reqSize;
                    SetupDiGetDeviceInterfaceDetail(devInfo, ref iface, IntPtr.Zero, 0, out reqSize, IntPtr.Zero);
                    IntPtr detailBuf = Marshal.AllocHGlobal(reqSize);
                    try
                    {
                        Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref iface, detailBuf, reqSize, out reqSize, IntPtr.Zero))
                            continue;

                        string path = Marshal.PtrToStringUni(new IntPtr(detailBuf.ToInt64() + 4));
                        if (path.IndexOf("vid_25a7", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        InspectPath(path, map);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailBuf);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfo);
            }

            var complete = new List<EndpointPair>();
            foreach (var pair in map.Values)
            {
                if (!string.IsNullOrEmpty(pair.FeaturePath) && !string.IsNullOrEmpty(pair.InputPath))
                    complete.Add(pair);
            }
            return complete;
        }

        private static void InspectPath(string path, Dictionary<ushort, EndpointPair> map)
        {
            IntPtr meta = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (meta == INVALID_HANDLE_VALUE) return;

            try
            {
                var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
                if (!HidD_GetAttributes(meta, ref attrs) || attrs.VendorID != AjazzProtocol.VendorId)
                    return;

                if (attrs.ProductID != AjazzProtocol.WiredProductId && attrs.ProductID != AjazzProtocol.DongleProductId)
                    return;

                IntPtr pp;
                if (!HidD_GetPreparsedData(meta, out pp)) return;

                HIDP_CAPS caps;
                try
                {
                    HidP_GetCaps(pp, out caps);
                }
                finally
                {
                    HidD_FreePreparsedData(pp);
                }

                EndpointPair pair;
                if (!map.TryGetValue(attrs.ProductID, out pair))
                {
                    pair = new EndpointPair { ProductId = attrs.ProductID };
                    map[attrs.ProductID] = pair;
                }

                if (caps.UsagePage == AjazzProtocol.FeatureUsagePage && caps.FeatureReportByteLength == AjazzProtocol.ReportLength)
                {
                    pair.FeaturePath = path;
                }
                else if (caps.UsagePage == AjazzProtocol.InputUsagePage && caps.InputReportByteLength == AjazzProtocol.ReportLength)
                {
                    pair.InputPath = path;
                }
            }
            finally
            {
                CloseHandle(meta);
            }
        }

        private static BatteryStatus QueryEndpointPair(EndpointPair pair, string mode, int timeoutMs)
        {
            IntPtr hFeat = CreateFile(pair.FeaturePath, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (hFeat == INVALID_HANDLE_VALUE) return null;

            IntPtr hIn = CreateFile(pair.InputPath, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (hIn == INVALID_HANDLE_VALUE)
            {
                CloseHandle(hFeat);
                return null;
            }

            try
            {
                // For the 2.4G dongle, check GetConnectStatus (cmd 0x03) before reading battery
                if (pair.ProductId == AjazzProtocol.DongleProductId)
                {
                    bool isConnected = false;
                    byte[] statusCmd = AjazzProtocol.BuildGetConnectStatusCommand();
                    bool gotStatusReply = false;
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        byte[] raw = SendAndReadRaw(hFeat, hIn, statusCmd, timeoutMs);
                        if (raw != null && AjazzProtocol.TryParseGetConnectStatusResponse(raw, out isConnected))
                        {
                            gotStatusReply = true;
                            break;
                        }
                        Thread.Sleep(50);
                    }
                    if (!gotStatusReply || !isConnected)
                    {
                        return null;
                    }
                }

                byte[] cmd = AjazzProtocol.BuildGetPowerCommand();
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    byte[] raw = SendAndReadRaw(hFeat, hIn, cmd, timeoutMs);
                    BatteryStatus parsed;
                    if (raw != null && AjazzProtocol.TryParseGetPowerResponse(raw, mode, out parsed))
                        return parsed;
                    Thread.Sleep(50);
                }
                return null;
            }
            finally
            {
                CloseHandle(hIn);
                CloseHandle(hFeat);
            }
        }

        private static byte[] SendAndReadRaw(IntPtr hFeat, IntPtr hIn, byte[] cmd, int timeoutMs)
        {
            byte expectedOpcode = cmd[1];
            using (var evt = new ManualResetEvent(false))
            {
                var ov = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
                var inBuf = new byte[AjazzProtocol.ReportLength];
                inBuf[0] = AjazzProtocol.InputReportId;

                uint bytesRead;
                bool okRead = ReadFile(hIn, inBuf, (uint)inBuf.Length, out bytesRead, ref ov);
                int readErr = Marshal.GetLastWin32Error();

                if (!HidD_SetFeature(hFeat, cmd, cmd.Length))
                {
                    if (!okRead && readErr == ERROR_IO_PENDING)
                    {
                        CancelIoEx(hIn, IntPtr.Zero);
                        uint dummy;
                        GetOverlappedResult(hIn, ref ov, out dummy, true);
                    }
                    return null;
                }

                // Read up to 4 reports in case an unrelated 0x0A notification arrives first
                for (int reads = 0; reads < 4; reads++)
                {
                    if (!okRead && readErr == ERROR_IO_PENDING)
                    {
                        if (!evt.WaitOne(timeoutMs))
                        {
                            CancelIoEx(hIn, IntPtr.Zero);
                            uint dummy;
                            GetOverlappedResult(hIn, ref ov, out dummy, true);
                            return null;
                        }
                        okRead = GetOverlappedResult(hIn, ref ov, out bytesRead, false);
                    }

                    if (!okRead) return null;

                    if (inBuf[0] == AjazzProtocol.InputReportId && inBuf[1] == expectedOpcode)
                        return inBuf;

                    evt.Reset();
                    Array.Clear(inBuf, 0, inBuf.Length);
                    inBuf[0] = AjazzProtocol.InputReportId;
                    okRead = ReadFile(hIn, inBuf, (uint)inBuf.Length, out bytesRead, ref ov);
                    readErr = Marshal.GetLastWin32Error();
                }
                return null;
            }
        }
    }
}
