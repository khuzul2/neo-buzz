using AtmaFileSystem;
using System.IO;

namespace ReBuzzTests.Automation
{
    /// <summary>
    /// Fork addition: the directory of the test assembly, found from a type in it.
    /// AbsoluteDirectoryPath.OfExecutingAssembly() asks for the calling assembly, which depends
    /// on JIT inlining and was the .NET runtime directory in some test orders.
    /// </summary>
    internal static class TestAssemblyDirectory
    {
        public static AbsoluteDirectoryPath Value { get; } =
            AbsoluteDirectoryPath.Value(Path.GetDirectoryName(typeof(TestAssemblyDirectory).Assembly.Location)!);
    }
}
