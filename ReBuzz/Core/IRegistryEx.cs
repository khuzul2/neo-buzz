using System.Collections.Generic;

namespace ReBuzz.Core
{
    public interface IRegistryEx
    {
        void Write<T>(string key, T x, string path = "BuzzGUI");
        T Read<T>(string key, T def, string path = "BuzzGUI");
        IEnumerable<T> ReadNumberedList<T>(string key, string path = "BuzzGUI");
        Dictionary<string, object> ReadDictionary(string path = "BuzzGUI");
        void DeleteCurrentUserSubKey(string key);
        IRegistryKey CreateCurrentUserSubKey(string subKey);
        void DeleteCurrentUserValue(string value, string path);
    }
}
