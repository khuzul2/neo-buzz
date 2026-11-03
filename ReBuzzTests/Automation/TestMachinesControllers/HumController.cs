using ReBuzzTests.Automation.TestMachines;
using System;

namespace ReBuzzTests.Automation.TestMachinesControllers
{
    /// <summary>Fork addition (0.4.1): controller for the <see cref="Hum"/> test machine.</summary>
    public class HumController(string instanceName) : DynamicMachineController(MachineName, instanceName)
    {
        private const string MachineName = "Hum";

        public static HumController NewInstance(string instanceName = MachineName) => new(instanceName);

        public static IDynamicTestMachineInfo Info => HumInfo.Instance;

        /// <summary>Has the machine call <paramref name="listener"/> (parameter index, track or -1, value) for each value its host sets.</summary>
        public ITestMachineInstanceCommand Listen(Action<int, int, int> listener)
            => new TestManagedMachineInstanceCommand(this, "Listen", listener);
    }
}
