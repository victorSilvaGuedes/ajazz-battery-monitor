using System;

namespace AjazzBatteryMonitor
{
    public enum ChargePhase
    {
        Disconnected,
        Idle,
        Charging,
        FullNotified
    }

    /// <summary>
    /// Tracks charging progress and emits a single ChargeCompleted event per charge cycle when hitting 100%.
    /// </summary>
    public sealed class BatteryMonitor
    {
        private readonly int _requiredConfirmations;
        private int _fullConfirmCount;
        private bool _sawActiveCharge;

        public ChargePhase Phase { get; private set; }
        public BatteryStatus LastStatus { get; private set; }

        public event Action<BatteryStatus> ChargeCompleted;

        public BatteryMonitor(int requiredConfirmations)
        {
            if (requiredConfirmations < 1) throw new ArgumentOutOfRangeException("requiredConfirmations");
            _requiredConfirmations = requiredConfirmations;
            Phase = ChargePhase.Disconnected;
            LastStatus = BatteryStatus.Disconnected("Inicializando");
        }

        /// <summary>
        /// Processes a new battery reading and triggers ChargeCompleted once charging finishes at 100%.
        /// </summary>
        public void Update(BatteryStatus status)
        {
            if (status == null) throw new ArgumentNullException("status");
            LastStatus = status;

            if (!status.Connected)
            {
                _fullConfirmCount = 0;
                _sawActiveCharge = false;
                Phase = ChargePhase.Disconnected;
                return;
            }

            bool isWired = string.Equals(status.ConnectionMode, "USB", StringComparison.OrdinalIgnoreCase);

            // Reset notification latch once the mouse is unplugged/discharging below 98%
            if (Phase == ChargePhase.FullNotified)
            {
                if (status.Percent < 98 || (!isWired && !status.IsCharging))
                {
                    _fullConfirmCount = 0;
                    _sawActiveCharge = status.IsCharging || (isWired && status.Percent < 100);
                    Phase = _sawActiveCharge ? ChargePhase.Charging : ChargePhase.Idle;
                }
                return;
            }

            if (status.IsCharging || (isWired && status.Percent < 100))
            {
                _sawActiveCharge = true;
                Phase = ChargePhase.Charging;
            }
            else if (Phase == ChargePhase.Disconnected)
            {
                Phase = ChargePhase.Idle;
            }

            // Detect 100% completion after active charging (either bCharging stays 1 at 100%, or drops to 0 when full)
            bool fullWhileCharging = status.Percent == 100 && (_sawActiveCharge || status.IsCharging);
            if (fullWhileCharging)
            {
                _fullConfirmCount++;
                if (_fullConfirmCount >= _requiredConfirmations)
                {
                    Phase = ChargePhase.FullNotified;
                    var handler = ChargeCompleted;
                    if (handler != null) handler(status);
                }
            }
            else
            {
                _fullConfirmCount = 0;
                if (!status.IsCharging && !(isWired && status.Percent < 100))
                {
                    _sawActiveCharge = false;
                    Phase = ChargePhase.Idle;
                }
            }
        }
    }
}
