using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
    internal enum ControllerNavigationAction
    {
        Previous,
        Next,
        Activate,
        Cancel,
        Close,
        TogglePin,
        PreviousTab,
        NextTab
    }

    internal sealed class XInputNavigationService : IDisposable
    {
        private const int ControllerCount = 4;
        private const int PollIntervalMilliseconds = 16;
        private const int InitialRepeatDelayMilliseconds = 400;
        private const int RepeatDelayMilliseconds = 120;
        private const short ThumbstickThreshold = 16000;

        private const ushort DPadUp = 0x0001;
        private const ushort DPadDown = 0x0002;
        private const ushort DPadLeft = 0x0004;
        private const ushort DPadRight = 0x0008;
        private const ushort ButtonA = 0x1000;
        private const ushort ButtonB = 0x2000;
        private const ushort ButtonX = 0x4000;
        private const ushort ButtonY = 0x8000;

        private readonly Timer pollTimer;
        private readonly ushort[] previousActionButtons =
            new ushort[ControllerCount];
        private readonly ushort[] previousDirections =
            new ushort[ControllerCount];
        private readonly DateTime[] nextRepeatUtc =
            new DateTime[ControllerCount];

        private bool enabled;
        private bool? previouslyConnected;
        private bool disposed;

        public XInputNavigationService()
        {
            pollTimer = new Timer { Interval = PollIntervalMilliseconds };
            pollTimer.Tick += OnPollTimerTick;

            for (int index = 0; index < nextRepeatUtc.Length; index++)
            {
                nextRepeatUtc[index] = DateTime.MaxValue;
            }
        }

        public event Action<ControllerNavigationAction> NavigationRequested;
        public event Action<string> Error;

        public bool Enabled
        {
            get { return enabled; }
            set { enabled = value; }
        }

        public void Start()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("XInputNavigationService");
            }

            pollTimer.Start();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pollTimer.Stop();
            pollTimer.Tick -= OnPollTimerTick;
            pollTimer.Dispose();
        }

        private void OnPollTimerTick(object sender, EventArgs eventArgs)
        {
            try
            {
                PollControllers();
            }
            catch (DllNotFoundException exception)
            {
                pollTimer.Stop();
                RaiseError("XInput is unavailable: " + exception.Message);
            }
            catch (EntryPointNotFoundException exception)
            {
                pollTimer.Stop();
                RaiseError("This Windows XInput version is unsupported: " + exception.Message);
            }
        }

        private void PollControllers()
        {
            bool anyConnected = false;
            DateTime nowUtc = DateTime.UtcNow;

            for (uint index = 0; index < ControllerCount; index++)
            {
                XInputState state;
                bool connected = NativeXInput.XInputGetState(index, out state) == 0;
                anyConnected |= connected;

                if (!connected)
                {
                    ResetController((int)index);
                    continue;
                }

                ProcessControllerState((int)index, state.Gamepad, nowUtc);
            }

            if (!previouslyConnected.HasValue
                || previouslyConnected.Value != anyConnected)
            {
                previouslyConnected = anyConnected;
                DiagnosticLog.Write(
                    anyConnected
                        ? "XInput controller connected."
                        : "No XInput controller is connected.");
            }
        }

        private void ProcessControllerState(
            int index,
            XInputGamepad gamepad,
            DateTime nowUtc)
        {
            ushort actionButtons = (ushort)(
                gamepad.Buttons & (ButtonA | ButtonB | ButtonX | ButtonY));
            ushort newlyPressed = (ushort)(
                actionButtons & ~previousActionButtons[index]);
            previousActionButtons[index] = actionButtons;

            ushort direction = GetDirection(gamepad);
            if (!enabled)
            {
                previousDirections[index] = direction;
                nextRepeatUtc[index] = DateTime.MaxValue;
                return;
            }

            if (direction != previousDirections[index])
            {
                RaiseDirection(direction);

                nextRepeatUtc[index] = direction == 0
                    ? DateTime.MaxValue
                    : nowUtc.AddMilliseconds(InitialRepeatDelayMilliseconds);
            }
            else if (direction != 0 && nowUtc >= nextRepeatUtc[index])
            {
                RaiseDirection(direction);
                nextRepeatUtc[index] =
                    nowUtc.AddMilliseconds(RepeatDelayMilliseconds);
            }

            previousDirections[index] = direction;

            if ((newlyPressed & ButtonA) != 0)
            {
                RaiseNavigation(ControllerNavigationAction.Activate);
            }

            if ((newlyPressed & ButtonB) != 0)
            {
                RaiseNavigation(ControllerNavigationAction.Cancel);
            }

            if ((newlyPressed & ButtonX) != 0)
            {
                RaiseNavigation(ControllerNavigationAction.Close);
            }

            if ((newlyPressed & ButtonY) != 0)
            {
                RaiseNavigation(ControllerNavigationAction.TogglePin);
            }
        }

        private static ushort GetDirection(XInputGamepad gamepad)
        {
            ushort dpad = (ushort)(
                gamepad.Buttons & (DPadUp | DPadDown | DPadLeft | DPadRight));
            if (dpad != 0)
            {
                return dpad == DPadUp || dpad == DPadDown
                    || dpad == DPadLeft || dpad == DPadRight
                    ? dpad
                    : (ushort)0;
            }

            int horizontal = Math.Abs((int)gamepad.ThumbLeftX);
            int vertical = Math.Abs((int)gamepad.ThumbLeftY);
            if (horizontal > ThumbstickThreshold && horizontal > vertical)
            {
                return gamepad.ThumbLeftX < 0 ? DPadLeft : DPadRight;
            }

            if (vertical > ThumbstickThreshold && vertical > horizontal)
            {
                return gamepad.ThumbLeftY > 0 ? DPadUp : DPadDown;
            }

            return 0;
        }

        private void RaiseDirection(ushort direction)
        {
            switch (direction)
            {
                case DPadUp:
                    RaiseNavigation(ControllerNavigationAction.Previous);
                    break;
                case DPadDown:
                    RaiseNavigation(ControllerNavigationAction.Next);
                    break;
                case DPadLeft:
                    RaiseNavigation(ControllerNavigationAction.PreviousTab);
                    break;
                case DPadRight:
                    RaiseNavigation(ControllerNavigationAction.NextTab);
                    break;
            }
        }

        private void ResetController(int index)
        {
            previousActionButtons[index] = 0;
            previousDirections[index] = 0;
            nextRepeatUtc[index] = DateTime.MaxValue;
        }

        private void RaiseNavigation(ControllerNavigationAction action)
        {
            Action<ControllerNavigationAction> handler = NavigationRequested;
            if (handler != null)
            {
                handler(action);
            }
        }

        private void RaiseError(string message)
        {
            Action<string> handler = Error;
            if (handler != null)
            {
                handler(message);
            }
        }
    }

    internal static class NativeXInput
    {
        [DllImport("xinput1_4.dll")]
        internal static extern uint XInputGetState(
            uint userIndex,
            out XInputState state);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLeftX;
        public short ThumbLeftY;
        public short ThumbRightX;
        public short ThumbRightY;
    }
}
