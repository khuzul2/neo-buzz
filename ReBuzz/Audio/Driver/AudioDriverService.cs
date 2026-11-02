using ReBuzz.Audio.Engine;
using ReBuzz.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Audio
{
    public sealed class AudioDeviceService
    {
        private readonly IUserMessages userMessages;
        private readonly IAudioDriverFactory drivers;

        /// <param name="drivers">The platform's drivers (engine-split.md S8); null: no ASIO drivers.</param>
        internal AudioDeviceService(IUserMessages userMessages, IAudioDriverFactory drivers)
        {
            this.userMessages = userMessages;
            this.drivers = drivers;
        }

        public List<AudioEngine.AudioOutDevice> GetOutputDevices()
        {
            var devices = new List<AudioEngine.AudioOutDevice>();

            try
            {
                if (drivers != null)
                {
                    devices.AddRange(drivers.OutputDevices());
                }
                else
                {
                    devices.Add(new AudioEngine.AudioOutDevice
                    {
                        Name = "WASAPI",
                        Type = AudioEngine.AudioOutType.Wasapi
                    });
                }
            }
            catch (Exception e)
            {
                userMessages.Error("AudioDevices Error: " + e.Message, "Audio Devices Error", e);
            }

            return devices;
        }

        public AudioEngine.AudioOutDevice ResolveDevice(string audioDriverName)
        {
            var devices = GetOutputDevices();
            var device = devices.FirstOrDefault(x => x.Name == audioDriverName);
            return device ?? devices.FirstOrDefault();
        }
    }
}
