using Microsoft.Win32;

namespace ReBuzz.Core
{
    public class WindowsRegistryKey(RegistryKey registryKey) : IRegistryKey
    {
        public void SetValue(string name, object value)
        {
            registryKey.SetValue(name, value);
        }
    }
}