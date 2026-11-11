using System;

namespace AjazzBatteryMonitor
{
    /// <summary>
    /// Immutable snapshot of a decoded Ajazz AJ139 Pro battery query.
    /// </summary>
    public sealed class BatteryStatus
    {
        public bool Connected { get; private set; }
        public int Percent { get; private set; }
        public bool IsCharging { get; private set; }
        public string ConnectionMode { get; private set; }
        public string RawHex { get; private set; }
        public DateTime Timestamp { get; private set; }

        public BatteryStatus(bool connected, int percent, bool isCharging, string connectionMode, string rawHex)
        {
            Connected = connected;
            Percent = percent;
            IsCharging = isCharging;
            ConnectionMode = connectionMode ?? "Desconhecido";
            RawHex = rawHex ?? string.Empty;
            Timestamp = DateTime.Now;
        }

        public static BatteryStatus Disconnected(string reason)
        {
            return new BatteryStatus(false, -1, false, reason ?? "Desconectado", string.Empty);
        }
    }

    /// <summary>
    /// Implements the reverse-engineered Compx/Ajazz 17-byte USB HID battery protocol.
    /// </summary>
    public static class AjazzProtocol
    {
        public const ushort VendorId = 0x25A7;
        public const ushort WiredProductId = 0xFA7B;
        public const ushort DongleProductId = 0xFA7C;

        public const ushort FeatureUsagePage = 0xFF02;
        public const ushort InputUsagePage = 0xFF01;
        public const int ReportLength = 17;

        public const byte FeatureReportId = 0x08;
        public const byte InputReportId = 0x09;
        public const byte CmdGetConnectStatus = 0x03;
        public const byte CmdGetPower = 0x04;
        public const byte ChecksumSeed = 0x55;

        /// <summary>
        /// Computes the Compx/Ajazz checksum byte: (0x55 - sum(bytes[0..length-1])) &amp; 0xFF.
        /// </summary>
        public static byte ComputeChecksum(byte[] buffer, int length)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (length < 0 || length > buffer.Length) throw new ArgumentOutOfRangeException("length");

            int sum = 0;
            for (int i = 0; i < length; i++)
            {
                sum += buffer[i];
            }
            return (byte)((ChecksumSeed - sum) & 0xFF);
        }

        /// <summary>
        /// Builds a 17-byte HID Feature Report for the given Compx/Ajazz opcode.
        /// </summary>
        public static byte[] BuildCommand(byte opcode)
        {
            var packet = new byte[ReportLength];
            packet[0] = FeatureReportId;
            packet[1] = opcode;
            packet[ReportLength - 1] = ComputeChecksum(packet, ReportLength - 1);
            return packet;
        }

        /// <summary>
        /// Builds the 17-byte HID Feature Report for GetPower (command 0x04).
        /// </summary>
        public static byte[] BuildGetPowerCommand()
        {
            return BuildCommand(CmdGetPower);
        }

        /// <summary>
        /// Builds the 17-byte HID Feature Report for GetConnectStatus (command 0x03).
        /// </summary>
        public static byte[] BuildGetConnectStatusCommand()
        {
            return BuildCommand(CmdGetConnectStatus);
        }

        /// <summary>
        /// Parses and validates a 17-byte HID Input Report response to GetConnectStatus (command 0x03).
        /// </summary>
        public static bool TryParseGetConnectStatusResponse(byte[] report, out bool isConnected)
        {
            isConnected = false;
            if (!IsValidReplyHeader(report, CmdGetConnectStatus))
                return false;

            isConnected = report[6] == 0x01;
            return true;
        }

        /// <summary>
        /// Parses and validates a 17-byte HID Input Report response to GetPower.
        /// </summary>
        public static bool TryParseGetPowerResponse(byte[] report, string connectionMode, out BatteryStatus status)
        {
            status = null;
            if (!IsValidReplyHeader(report, CmdGetPower))
                return false;

            int percent = report[6];
            if (percent < 0 || percent > 100)
                return false;

            bool isCharging = report[7] != 0x00;
            status = new BatteryStatus(true, percent, isCharging, connectionMode, BitConverter.ToString(report, 0, ReportLength));
            return true;
        }

        private static bool IsValidReplyHeader(byte[] report, byte expectedOpcode)
        {
            if (report == null || report.Length < ReportLength)
                return false;

            if (report[0] != InputReportId || report[1] != expectedOpcode || report[2] != 0x00)
                return false;

            byte expectedChecksum = ComputeChecksum(report, ReportLength - 1);
            return report[ReportLength - 1] == expectedChecksum;
        }
    }
}
