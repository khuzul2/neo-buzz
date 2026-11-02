using BuzzDotNet.Audio;
using NAudio.Wave;
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

        public AudioDeviceService(IUserMessages userMessages)
        {
            this.userMessages = userMessages;
        }

        public List<AudioEngine.AudioOutDevice> GetOutputDevices()
        {
            var devices = new List<AudioEngine.AudioOutDevice>();

            try
            {
                if (AsioOut.isSupported())
                {
                    foreach (var name in AsioOut.GetDriverNames())
                    {
                        devices.Add(new AudioEngine.AudioOutDevice
                        {
                            Name = name,
                            Type = AudioEngine.AudioOutType.ASIO
                        });
                    }
                }

                devices.Add(new AudioEngine.AudioOutDevice
                {
                    Name = "WASAPI",
                    Type = AudioEngine.AudioOutType.Wasapi
                });
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
