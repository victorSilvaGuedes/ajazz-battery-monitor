using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AjazzBatteryMonitor
{
    /// <summary>
    /// System Tray application displaying live Ajazz AJ139 Pro battery status and notifying at 100% charge.
    /// </summary>
    public sealed class TrayAppContext : ApplicationContext
    {
        private const string AppName = "AjazzBatteryMonitor";
        private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const int PollIntervalMs = 15000;

        private readonly NotifyIcon _trayIcon;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _startupItem;
        private readonly System.Windows.Forms.Timer _pollTimer;
        private readonly BatteryMonitor _monitor;
        private readonly string _launcherScriptPath;
        private readonly string _logPath;
        private Icon _currentIcon;
        private bool _pollInFlight;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        public TrayAppContext(string launcherScriptPath, string logDir)
        {
            _launcherScriptPath = launcherScriptPath;
            Directory.CreateDirectory(logDir);
            _logPath = Path.Combine(logDir, "monitor.log");

            _monitor = new BatteryMonitor(requiredConfirmations: 2);
            _monitor.ChargeCompleted += OnChargeCompleted;
            _monitor.LowBatteryAlert += OnLowBatteryAlert;

            _statusItem = new ToolStripMenuItem("Ajazz AJ139 Pro: Verificando...") { Enabled = false };
            var refreshItem = new ToolStripMenuItem("Atualizar agora", null, (s, e) => TriggerPoll());
            var testToastItem = new ToolStripMenuItem("Testar notificação", null, (s, e) => SendFullNotification(isTest: true));
            _startupItem = new ToolStripMenuItem("Iniciar com o Windows", null, (s, e) => ToggleStartup())
            {
                Checked = IsStartupEnabled()
            };
            var openLogsItem = new ToolStripMenuItem("Abrir pasta de logs", null, (s, e) => OpenLogsFolder());
            var exitItem = new ToolStripMenuItem("Sair", null, (s, e) => ExitApplication());

            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(refreshItem);
            menu.Items.Add(testToastItem);
            menu.Items.Add(_startupItem);
            menu.Items.Add(openLogsItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _currentIcon = RenderBatteryIcon(null);
            _trayIcon = new NotifyIcon
            {
                Icon = _currentIcon,
                Text = "Ajazz AJ139 Pro: Verificando...",
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => TriggerPoll();

            _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
            _pollTimer.Tick += (s, e) => TriggerPoll();
            _pollTimer.Start();

            var bootTimer = new System.Windows.Forms.Timer { Interval = 150 };
            bootTimer.Tick += (s, e) =>
            {
                bootTimer.Stop();
                bootTimer.Dispose();
                TriggerPoll();
            };
            bootTimer.Start();

            Log("Monitor iniciado.");
        }

        private void TriggerPoll()
        {
            if (_pollInFlight) return;
            _pollInFlight = true;

            Task.Run(() => HidBatteryReader.ReadCurrentStatus(700)).ContinueWith(t =>
            {
                try
                {
                    _pollInFlight = false;
                    BatteryStatus status = t.IsFaulted || t.Result == null
                        ? BatteryStatus.Disconnected("Erro na leitura HID")
                        : t.Result;

                    ChargePhase prevPhase = _monitor.Phase;
                    _monitor.Update(status);
                    UpdateUi(status);

                    if (prevPhase != _monitor.Phase)
                    {
                        Log(string.Format("Estado: {0} -> {1} | {2}% | Charging={3} | Mode={4}",
                            prevPhase, _monitor.Phase, status.Percent, status.IsCharging, status.ConnectionMode));
                    }
                }
                catch (Exception ex)
                {
                    _pollInFlight = false;
                    Log("Erro em TriggerPoll: " + ex.Message);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void UpdateUi(BatteryStatus status)
        {
            string summary;
            if (!status.Connected)
            {
                summary = "Ajazz AJ139 Pro: " + status.ConnectionMode;
            }
            else
            {
                string chargeTag = status.IsCharging
                    ? "Carregando"
                    : (status.Percent == 100 && status.ConnectionMode == "USB" ? "Carga completa" : status.ConnectionMode);
                summary = string.Format("Ajazz AJ139 Pro: {0}% ({1})", status.Percent, chargeTag);
            }

            _statusItem.Text = summary;
            // NotifyIcon.Text limit is 63 chars on older Win32 shells
            _trayIcon.Text = summary.Length > 63 ? summary.Substring(0, 63) : summary;

            Icon nextIcon = RenderBatteryIcon(status);
            Icon oldIcon = _currentIcon;
            _currentIcon = nextIcon;
            _trayIcon.Icon = nextIcon;
            if (oldIcon != null)
            {
                DestroyIcon(oldIcon.Handle);
                oldIcon.Dispose();
            }
        }

        private void OnChargeCompleted(BatteryStatus status)
        {
            Log(string.Format("Carga 100% confirmada ({0}). Enviando notificação.", status.RawHex));
            SendFullNotification(isTest: false);
        }

        private void OnLowBatteryAlert(BatteryStatus status)
        {
            Log(string.Format("Alerta de bateria baixa: {0}% ({1}). Enviando notificação.", status.Percent, status.RawHex));
            string title = string.Format("Ajazz AJ139 Pro — Bateria em {0}%", status.Percent);
            string body = status.Percent <= 5
                ? "Bateria crítica! Conecte o cabo USB para carregar."
                : "Bateria baixa. Conecte o cabo USB quando puder.";
            DispatchNotification(title, body, ToolTipIcon.Warning);
        }

        private void SendFullNotification(bool isTest)
        {
            string title = isTest
                ? "Ajazz AJ139 Pro — Teste de Notificação"
                : "Ajazz AJ139 Pro — Carga completa (100%)";
            string body = isTest
                ? "As notificações do monitor de bateria estão funcionando."
                : "O mouse atingiu 100% de carga. Você já pode desconectar o cabo USB para usar sem fio.";

            DispatchNotification(title, body, ToolTipIcon.Info);
        }

        private void DispatchNotification(string title, string body, ToolTipIcon fallbackIcon)
        {
            Task.Run(() => ToastNotifier.ShowToast(title, body, (t, m) =>
            {
                try
                {
                    _trayIcon.BalloonTipTitle = t;
                    _trayIcon.BalloonTipText = m;
                    _trayIcon.BalloonTipIcon = fallbackIcon;
                    _trayIcon.ShowBalloonTip(6000);
                }
                catch { }
            }));
        }

        private static Icon RenderBatteryIcon(BatteryStatus status)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                Color bg = Color.FromArgb(65, 65, 70);
                Color fg = Color.White;
                string text = "--";

                if (status != null && status.Connected)
                {
                    bool isWired = string.Equals(status.ConnectionMode, "USB", StringComparison.OrdinalIgnoreCase);
                    bool isCharging = status.IsCharging || (isWired && status.Percent < 100);
                    text = status.Percent >= 100 ? "100" : status.Percent.ToString();

                    if (status.Percent == 100 && isWired)
                    {
                        bg = Color.FromArgb(0, 120, 215);    // Azul: 100% conectado no cabo USB
                    }
                    else if (isCharging)
                    {
                        text = "CB";
                        bg = Color.FromArgb(250, 204, 21);   // Amarelo: carregando
                        fg = Color.FromArgb(17, 24, 39);     // Texto escuro para alto contraste no amarelo
                    }
                    else if (status.Percent <= 20)
                    {
                        bg = Color.FromArgb(200, 42, 42);    // Vermelho: bateria baixa (<= 20%)
                    }
                    else
                    {
                        bg = Color.FromArgb(24, 150, 72);    // Verde: fora do cabo (25%..100%)
                    }
                }

                using (var brush = new SolidBrush(bg))
                {
                    g.FillRectangle(brush, 0, 0, 32, 32);
                }

                float fontSize = text.Length >= 3 ? 20.0f : 26.0f;

                using (var font = new Font("Bahnschrift", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var textBrush = new SolidBrush(fg))
                using (var sf = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                })
                {
                    g.DrawString(text, font, textBrush, new RectangleF(0, 1, 32, 31), sf);
                }

                IntPtr hIcon = bmp.GetHicon();
                return Icon.FromHandle(hIcon);
            }
        }

        private bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, false))
                {
                    return key != null && key.GetValue(AppName) != null;
                }
            }
            catch { return false; }
        }

        private void ToggleStartup()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, true))
                {
                    if (key == null) return;
                    if (_startupItem.Checked)
                    {
                        key.DeleteValue(AppName, false);
                        _startupItem.Checked = false;
                        Log("Inicialização automática desativada.");
                    }
                    else
                    {
                        string cmd = string.Format("wscript.exe \"{0}\"", _launcherScriptPath);
                        key.SetValue(AppName, cmd);
                        _startupItem.Checked = true;
                        Log("Inicialização automática ativada.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Erro ao alterar inicialização: " + ex.Message);
            }
        }

        private void OpenLogsFolder()
        {
            try
            {
                Process.Start("explorer.exe", Path.GetDirectoryName(_logPath));
            }
            catch { }
        }

        private void ExitApplication()
        {
            _pollTimer.Stop();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            if (_currentIcon != null)
            {
                DestroyIcon(_currentIcon.Handle);
                _currentIcon.Dispose();
            }
            Log("Monitor encerrado.");
            ExitThread();
        }

        private void Log(string line)
        {
            try
            {
                string entry = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}", DateTime.Now, line, Environment.NewLine);
                File.AppendAllText(_logPath, entry);
            }
            catch { }
        }
    }
}
