using ReBuzzTests.Automation.TestMachines;

namespace ReBuzzTests.Automation.TestMachinesControllers
{
    /// <summary>Fork addition: controller for the <see cref="Tone"/> test machine.</summary>
    public class ToneController(string instanceName) : DynamicMachineController(MachineName, instanceName)
    {
        private const string MachineName = "Tone";

        public static ToneController NewInstance(string instanceName = MachineName) => new(instanceName);

        public static IDynamicTestMachineInfo Info => ToneInfo.Instance;
    }
}
