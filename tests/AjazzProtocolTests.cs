using System;

namespace AjazzBatteryMonitor.Tests
{
    public static class TestRunner
    {
        private static int _passed;
        private static int _failed;

        private static void AssertTrue(bool cond, string name)
        {
            if (cond)
            {
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine("[FAIL] " + name);
            }
        }

        private static void AssertEqual(int expected, int actual, string name)
        {
            if (expected == actual)
            {
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine(string.Format("[FAIL] {0} (expected {1}, got {2})", name, expected, actual));
            }
        }

        private static byte[] MakeResponse(byte reportId, byte cmd, byte status, byte percent, byte charging, bool validChecksum = true)
        {
            var buf = new byte[17];
            buf[0] = reportId;
            buf[1] = cmd;
            buf[2] = status;
            buf[5] = 0x02;
            buf[6] = percent;
            buf[7] = charging;
            buf[16] = AjazzProtocol.ComputeChecksum(buf, 16);
            if (!validChecksum) buf[16] ^= 0xFF;
            return buf;
        }

        public static int RunAll()
        {
            _passed = 0;
            _failed = 0;

            // 1. BuildGetPowerCommand exact bytes: 08 04 00 ... 49
            byte[] cmd = AjazzProtocol.BuildGetPowerCommand();
            AssertEqual(17, cmd.Length, "BuildGetPowerCommand length == 17");
            AssertEqual(0x08, cmd[0], "BuildGetPowerCommand ReportId == 0x08");
            AssertEqual(0x04, cmd[1], "BuildGetPowerCommand Opcode == 0x04");
            AssertEqual(0x49, cmd[16], "BuildGetPowerCommand Checksum == 0x49");

            // 1b. BuildGetConnectStatusCommand exact bytes: 08 03 00 ... 4A
            byte[] statusCmd = AjazzProtocol.BuildGetConnectStatusCommand();
            AssertEqual(0x03, statusCmd[1], "BuildGetConnectStatusCommand Opcode == 0x03");
            AssertEqual(0x4A, statusCmd[16], "BuildGetConnectStatusCommand Checksum == 0x4A");

            // 1c. Parse GetConnectStatus real captures (0x01 = connected, 0x00 = disconnected/asleep)
            byte[] disconnectedCapture = new byte[] { 0x09, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x48 };
            bool isConnected;
            AssertTrue(AjazzProtocol.TryParseGetConnectStatusResponse(disconnectedCapture, out isConnected), "TryParse disconnected 0x03 capture succeeds");
            AssertTrue(!isConnected, "Disconnected 0x03 capture decoded as isConnected == false");

            byte[] connectedCapture = new byte[] { 0x09, 0x03, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x47 };
            AssertTrue(AjazzProtocol.TryParseGetConnectStatusResponse(connectedCapture, out isConnected), "TryParse connected 0x03 capture succeeds");
            AssertTrue(isConnected, "Connected 0x03 capture decoded as isConnected == true");

            // 2. Parse live hardware capture: 09-04-00-00-00-02-64-00-00-00-00-00-00-00-00-00-E2
            byte[] liveCapture = new byte[] { 0x09, 0x04, 0x00, 0x00, 0x00, 0x02, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE2 };
            BatteryStatus s;
            AssertTrue(AjazzProtocol.TryParseGetPowerResponse(liveCapture, "USB", out s), "TryParse live capture succeeds");
            AssertEqual(100, s.Percent, "Live capture percent == 100");
            AssertTrue(!s.IsCharging, "Live capture charging == false");

            // 3. Parse active charging packet
            byte[] chargingPkt = MakeResponse(0x09, 0x04, 0x00, 85, 1);
            AssertTrue(AjazzProtocol.TryParseGetPowerResponse(chargingPkt, "USB", out s), "TryParse charging packet succeeds");
            AssertEqual(85, s.Percent, "Charging packet percent == 85");
            AssertTrue(s.IsCharging, "Charging packet IsCharging == true");

            // 4. Reject invalid packets
            AssertTrue(!AjazzProtocol.TryParseGetPowerResponse(MakeResponse(0x09, 0x04, 0x00, 85, 1, false), "USB", out s), "Reject bad checksum");
            AssertTrue(!AjazzProtocol.TryParseGetPowerResponse(MakeResponse(0x0A, 0x04, 0x00, 85, 1), "USB", out s), "Reject non-0x09 ReportId");
            AssertTrue(!AjazzProtocol.TryParseGetPowerResponse(MakeResponse(0x09, 0x03, 0x00, 85, 1), "USB", out s), "Reject non-0x04 Opcode");
            AssertTrue(!AjazzProtocol.TryParseGetPowerResponse(MakeResponse(0x09, 0x04, 0x01, 85, 1), "USB", out s), "Reject non-zero status");
            AssertTrue(!AjazzProtocol.TryParseGetPowerResponse(MakeResponse(0x09, 0x04, 0x00, 101, 1), "USB", out s), "Reject percent > 100");

            // 5. State machine: startup already 100% idle -> no notification
            var mon1 = new BatteryMonitor(2);
            int hits1 = 0;
            mon1.ChargeCompleted += _ => hits1++;
            mon1.Update(new BatteryStatus(true, 100, false, "USB", ""));
            mon1.Update(new BatteryStatus(true, 100, false, "USB", ""));
            AssertEqual(0, hits1, "No spurious notification when already 100% idle on startup");

            // 6. State machine: Charging -> 100% (bCharging stays true) with debounce=2 -> fires once
            var mon2 = new BatteryMonitor(2);
            int hits2 = 0;
            mon2.ChargeCompleted += _ => hits2++;
            mon2.Update(new BatteryStatus(true, 92, true, "USB", ""));
            mon2.Update(new BatteryStatus(true, 100, true, "USB", ""));
            AssertEqual(0, hits2, "Debounce suppresses first 100% reading");
            mon2.Update(new BatteryStatus(true, 100, true, "USB", ""));
            AssertEqual(1, hits2, "Fires once on second 100% reading");
            mon2.Update(new BatteryStatus(true, 100, true, "USB", ""));
            AssertEqual(1, hits2, "Does not re-fire while staying at 100%");

            // 7. State machine: Charging -> 100% where charger cuts bCharging=false upon hitting 100%
            var mon3 = new BatteryMonitor(2);
            int hits3 = 0;
            mon3.ChargeCompleted += _ => hits3++;
            mon3.Update(new BatteryStatus(true, 99, true, "USB", ""));
            mon3.Update(new BatteryStatus(true, 100, false, "USB", ""));
            mon3.Update(new BatteryStatus(true, 100, false, "USB", ""));
            AssertEqual(1, hits3, "Fires once when bCharging drops to false at 100% after charging");

            // 8. State machine: Reset after unplugging/discharging and re-charging
            mon3.Update(new BatteryStatus(true, 95, false, "2.4G", ""));
            mon3.Update(new BatteryStatus(true, 96, true, "USB", ""));
            mon3.Update(new BatteryStatus(true, 100, true, "USB", ""));
            mon3.Update(new BatteryStatus(true, 100, true, "USB", ""));
            AssertEqual(2, hits3, "Fires again on next charge cycle");

            // 9. State machine: Single 100% glitch does not fire
            var mon4 = new BatteryMonitor(2);
            int hits4 = 0;
            mon4.ChargeCompleted += _ => hits4++;
            mon4.Update(new BatteryStatus(true, 98, true, "USB", ""));
            mon4.Update(new BatteryStatus(true, 100, true, "USB", ""));
            mon4.Update(new BatteryStatus(true, 99, true, "USB", ""));
            mon4.Update(new BatteryStatus(true, 100, true, "USB", ""));
            AssertEqual(0, hits4, "Single 100% glitch reset by 99% reading");

            // 10. Low battery alerts: 20%, 15%, 10%, 5%, 0% (with debounce=2)
            var mon5 = new BatteryMonitor(2);
            var lowAlerts = new System.Collections.Generic.List<int>();
            mon5.LowBatteryAlert += st => lowAlerts.Add(st.Percent);

            mon5.Update(new BatteryStatus(true, 25, false, "2.4G", ""));
            mon5.Update(new BatteryStatus(true, 25, false, "2.4G", ""));
            AssertEqual(0, lowAlerts.Count, "No low alert at 25%");

            int[] steps = new int[] { 20, 15, 10, 5, 0 };
            foreach (int pct in steps)
            {
                mon5.Update(new BatteryStatus(true, pct, false, "2.4G", ""));
                mon5.Update(new BatteryStatus(true, pct, false, "2.4G", ""));
            }
            AssertEqual(5, lowAlerts.Count, "Low alerts fired for 20, 15, 10, 5, 0");
            AssertEqual(20, lowAlerts[0], "First low alert at 20%");
            AssertEqual(15, lowAlerts[1], "Second low alert at 15%");
            AssertEqual(0, lowAlerts[4], "Last low alert at 0%");

            // 11. Low battery alerts do not fire while charging/USB or repeat at same level after brief sleep
            mon5.Update(new BatteryStatus(false, -1, false, "Offline", ""));
            mon5.Update(new BatteryStatus(true, 0, false, "2.4G", ""));
            mon5.Update(new BatteryStatus(true, 0, false, "2.4G", ""));
            AssertEqual(5, lowAlerts.Count, "No duplicate 0% alert after brief sleep/wake");

            var mon6 = new BatteryMonitor(2);
            int lowHits6 = 0;
            mon6.LowBatteryAlert += _ => lowHits6++;
            mon6.Update(new BatteryStatus(true, 15, true, "USB", ""));
            mon6.Update(new BatteryStatus(true, 15, true, "USB", ""));
            AssertEqual(0, lowHits6, "No low alert while plugged in / charging");

            Console.WriteLine(string.Format("\nSummary: {0} passed, {1} failed", _passed, _failed));
            return _failed == 0 ? 0 : 1;
        }
    }
}
