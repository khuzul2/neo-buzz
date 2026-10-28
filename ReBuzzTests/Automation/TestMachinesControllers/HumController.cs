using ReBuzzTests.Automation.TestMachines;

namespace ReBuzzTests.Automation.TestMachinesControllers
{
    /// <summary>Fork addition (0.4.1): controller for the <see cref="Hum"/> test machine.</summary>
    public class HumController(string instanceName) : DynamicMachineController(MachineName, instanceName)
    {
        private const string MachineName = "Hum";

        public static HumController NewInstance(string instanceName = MachineName) => new(instanceName);

        public static IDynamicTestMachineInfo Info => HumInfo.Instance;
    }
}
