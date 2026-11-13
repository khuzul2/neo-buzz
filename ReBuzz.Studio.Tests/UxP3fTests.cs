// Fork (docs/specs/ux-overhaul.md P3f and the P3e polish): the device view gives its list the room; Preferences in one window.

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Device;

namespace ReBuzz.Studio.Tests
{
    public class UxP3fTests
    {
        static void Settle(Window window = null)
        {
            Dispatcher.UIThread.RunJobs();
            window?.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        sealed class DeviceRig : IDisposable
        {
            public SessionRig Song { get; }
            public MainWindow Window { get; }
            public MachineCore Fm;

            public DeviceRig()
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Song = new SessionRig("Lead");
                Fm = Song.Add("Neo FM6", "Fm", track: false);
                StudioSelection.For(Song.Core).Clear(this);
                Window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
                Window.Show();
                Window.ShowView("Machines");
                Settle();
            }

            public DeviceDock Dock => (DeviceDock)Window.Detail.DevicePage;

            public void Select(IMachine m)
            {
                StudioSelection.For(Song.Core).SelectMachine(m, this);
                Settle();
            }

            public void Dispose()
            {
                Window.Close();
                Song.Dispose();
            }
        }

        [AvaloniaTest]
        public void ShotDeviceFm6()
        {
            using var restore = Studio.AfterTest(Studio.ResetPanels);
            using var rig = new DeviceRig();
            rig.Window.Detail.ShowDevice();
            rig.Select(rig.Fm);
            Studio.Screenshot(rig.Window, Environment.GetEnvironmentVariable("P3F_SHOT") ?? "p3f-device-fm6");
        }
    }
}
