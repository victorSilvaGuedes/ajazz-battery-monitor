using System;
using System.Diagnostics;
using System.Security;

namespace AjazzBatteryMonitor
{
    /// <summary>
    /// Dispatches native Windows 10/11 Toast notifications via PowerShell 5.1 WinRT, with BalloonTip fallback.
    /// </summary>
    public static class ToastNotifier
    {
        private const string PowerShellAumid = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";

        public static bool ShowToast(string title, string message, Action<string, string> fallbackBalloon)
        {
            try
            {
                string safeTitle = SecurityElement.Escape(title ?? "Ajazz AJ139 Pro");
                string safeMsg = SecurityElement.Escape(message ?? "");

                string psScript =
                    "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; " +
                    "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null; " +
                    "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument; " +
                    "$xml.LoadXml('<toast duration=\"long\"><visual><binding template=\"ToastGeneric\"><text>" +
                    safeTitle.Replace("'", "''") + "</text><text>" + safeMsg.Replace("'", "''") +
                    "</text></binding></visual><audio src=\"ms-winsoundevent:Notification.Default\"/></toast>'); " +
                    "$toast = [Windows.UI.Notifications.ToastNotification]::new($xml); " +
                    "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('" + PowerShellAumid + "').Show($toast);";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + psScript.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc != null && proc.WaitForExit(5000) && proc.ExitCode == 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Fall back to tray balloon below
            }

            if (fallbackBalloon != null)
            {
                fallbackBalloon(title, message);
            }
            return false;
        }
    }
}
