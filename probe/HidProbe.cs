using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AjazzProbe
{
    static class Native
    {
        public const uint DIGCF_PRESENT = 0x00000002;
        public const uint DIGCF_DEVICEINTERFACE = 0x00000010;
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        public const int ERROR_IO_PENDING = 997;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
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

        [DllImport("hid.dll", SetLastError = true)]
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetFeature(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(IntPtr hFile, ref NativeOverlapped lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            const ushort TARGET_VID = 0x25A7;
            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);

            IntPtr devInfo = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (devInfo == Native.INVALID_HANDLE_VALUE)
            {
                Console.WriteLine("SetupDiGetClassDevs failed: " + Marshal.GetLastWin32Error());
                return;
            }

            try
            {
                var featByPid = new Dictionary<ushort, string>();
                var inByPid = new Dictionary<ushort, string>();

                var iface = new Native.SP_DEVICE_INTERFACE_DATA();
                iface.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));

                for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
                {
                    int reqSize;
                    Native.SetupDiGetDeviceInterfaceDetail(devInfo, ref iface, IntPtr.Zero, 0, out reqSize, IntPtr.Zero);
                    IntPtr detailBuf = Marshal.AllocHGlobal(reqSize);
                    try
                    {
                        Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(devInfo, ref iface, detailBuf, reqSize, out reqSize, IntPtr.Zero))
                            continue;

                        string path = Marshal.PtrToStringUni(new IntPtr(detailBuf.ToInt64() + 4));
                        if (path.IndexOf("vid_25a7", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        IntPtr metaHandle = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                        if (metaHandle != Native.INVALID_HANDLE_VALUE)
                        {
                            var attrs = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES)) };
                            if (Native.HidD_GetAttributes(metaHandle, ref attrs))
                            {
                                IntPtr pp;
                                if (Native.HidD_GetPreparsedData(metaHandle, out pp))
                                {
                                    Native.HIDP_CAPS caps;
                                    Native.HidP_GetCaps(pp, out caps);
                                    Native.HidD_FreePreparsedData(pp);

                                    if (attrs.VendorID == TARGET_VID && caps.UsagePage == 0xFF02 && caps.FeatureReportByteLength == 17)
                                    {
                                        Console.WriteLine(string.Format("Found Feature endpoint: PID=0x{0:X4} Path={1}", attrs.ProductID, path));
                                        featByPid[attrs.ProductID] = path;
                                    }
                                    else if (attrs.VendorID == TARGET_VID && caps.UsagePage == 0xFF01 && caps.InputReportByteLength == 17)
                                    {
                                        Console.WriteLine(string.Format("Found Input endpoint:   PID=0x{0:X4} Path={1}", attrs.ProductID, path));
                                        inByPid[attrs.ProductID] = path;
                                    }
                                }
                            }
                            Native.CloseHandle(metaHandle);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailBuf);
                    }
                }

                foreach (var kv in featByPid)
                {
                    ushort pid = kv.Key;
                    if (!inByPid.ContainsKey(pid)) continue;
                    Console.WriteLine(string.Format("\n=== Probing PID=0x{0:X4} ===", pid));
                    ProbeGetPower(kv.Value, inByPid[pid]);
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(devInfo);
            }
        }

        static void ProbeGetPower(string featPath, string inPath)
        {
            IntPtr hFeat = Native.CreateFile(featPath, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (hFeat == Native.INVALID_HANDLE_VALUE)
            {
                Console.WriteLine("  Feature open failed: Win32 Error " + Marshal.GetLastWin32Error());
                return;
            }
            IntPtr hIn = Native.CreateFile(inPath, Native.GENERIC_READ,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (hIn == Native.INVALID_HANDLE_VALUE)
            {
                Console.WriteLine("  Input open failed: Win32 Error " + Marshal.GetLastWin32Error());
                Native.CloseHandle(hFeat);
                return;
            }
            try
            {
                Console.WriteLine("  Both Feature & Input open SUCCESS (coexists with OemDrv.exe!)");

                // Build GetPower Feature Report (17 bytes):
                // [0] = 0x08, [1] = 0x04, [2..15] = 0, [16] = (0x55 - sum(0..15)) & 0xFF = 0x49
                byte[] cmd = new byte[17];
                cmd[0] = 0x08;
                cmd[1] = 0x04;
                int sum = 0;
                for (int i = 0; i < 16; i++) sum += cmd[i];
                cmd[16] = (byte)((0x55 - sum) & 0xFF);

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    using (var evt = new ManualResetEvent(false))
                    {
                        var ov = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
                        byte[] inBuf = new byte[17];
                        inBuf[0] = 0x09;
                        uint bytesRead;
                        bool okRead = Native.ReadFile(hIn, inBuf, 17, out bytesRead, ref ov);
                        int readErr = Marshal.GetLastWin32Error();

                        bool okFeat = Native.HidD_SetFeature(hFeat, cmd, 17);
                        int featErr = Marshal.GetLastWin32Error();
                        Console.WriteLine(string.Format("  Attempt {0}: SetFeature={1} (err={2})", attempt + 1, okFeat, featErr));

                        if (!okRead && readErr == Native.ERROR_IO_PENDING)
                        {
                            if (evt.WaitOne(1000))
                            {
                                okRead = Native.GetOverlappedResult(hIn, ref ov, out bytesRead, false);
                            }
                            else
                            {
                                Native.CancelIoEx(hIn, IntPtr.Zero);
                                uint dummy;
                                Native.GetOverlappedResult(hIn, ref ov, out dummy, true);
                                Console.WriteLine("  ReadFile timed out (1000ms)");
                                continue;
                            }
                        }

                        if (okRead)
                        {
                            Console.WriteLine("  ReadFile (" + bytesRead + " bytes): " + BitConverter.ToString(inBuf));
                            if (inBuf[0] == 0x09 && inBuf[1] == 0x04 && inBuf[2] == 0x00)
                            {
                                byte nPower = inBuf[6];
                                byte bCharging = inBuf[7];
                                Console.WriteLine(string.Format("  >>> DECODED: nPower={0}% (0x{0:X2}), bCharging={1} (0x{1:X2}) <<<", nPower, bCharging));
                                break;
                            }
                        }
                        else
                        {
                            Console.WriteLine("  ReadFile failed: err=" + readErr);
                        }
                    }
                }
            }
            finally
            {
                Native.CloseHandle(hIn);
                Native.CloseHandle(hFeat);
            }
        }
    }
}
