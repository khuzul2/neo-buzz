namespace NeoBuzz.MachineTests
{
    /// <summary>The measuring helpers themselves: AllocationCheck, which every machine's zero-allocation test reads with.</summary>
    [TestFixture]
    public class MeasureTests
    {
        static object kept;

        static int Throws(int b)
        {
            try
            {
                throw new InvalidOperationException("a probe in block " + b);
            }
            catch (InvalidOperationException e)
            {
                return e.Message.Length;
            }
        }

        [Test]
        public void AllocationCheckNamesTheBlocksThatAllocatedAndWhatTheThreadDidInThem()
        {
            // An array in block 123 and an exception thrown and caught in block 200: both blocks named with their bytes, and
            // the exception by the runtime's own event, heard as this thread's (its OS id matches the events', on every
            // platform the tests run on), so a machine's one-off allocation on a CI runner will say what it was.
            var (bytes, why) = AllocationCheck.Run(300, b =>
            {
                if (b == 123) kept = new float[1000];
                if (b == 200) Throws(b);
            });
            TestContext.Out.WriteLine(why);
            Assert.That(bytes, Is.GreaterThan(4000));
            Assert.That(why, Does.Contain("block 123: 40"), "the array's block, with its bytes");
            Assert.That(why, Does.Contain("block 200: "), "the exception's block");
            Assert.That(why, Does.Match(@"\n    ExceptionThrown"), "the runtime's event for the exception, as this thread's (no [thread N])");
            Assert.That(why, Does.Not.Contain("none carried this thread's id"));
            var (none, nothing) = AllocationCheck.Run(300, _ => { });
            Assert.That(none, Is.EqualTo(0));
            Assert.That(nothing, Is.Empty);
        }
    }
}
