using System.Collections.Generic;
using BuzzGUI.Interfaces;

namespace Buzz.MachineInterface
{
    // Fork (docs/specs/plugin-host.md §3.1, phase H1): what a managed machine that hosts other things (plugins)
    // needs of the engine, as optional interfaces. A machine implements the ones it uses; the engine checks
    // for them, so every other managed machine is untouched.

    /// <summary>
    /// A managed machine type that stands for many things (one host DLL, every scanned plugin), as a native
    /// "loader" machine does. The machine class also declares
    /// <c>public static IEnumerable&lt;string&gt; GetInstrumentList()</c>: the names the Gear menu lists
    /// ("Vendor/Name (VST3)": a '/' makes submenus). A song stores the chosen name as the machine's instrument.
    /// </summary>
    public interface IBuzzMachineInstrument
    {
        /// <summary>
        /// The instrument the machine was created for, or has in a song. Called once, right after the engine has made the
        /// machine's parameters and before its state is set, and not at all when the Gear menu entry is the machine itself.
        /// </summary>
        void SetInstrument(string instrument);
    }

    /// <summary>
    /// The engine side of a machine's parameters changing after it was made (a bank of slots that takes the names of
    /// the plugin parameters bound to it). The host object a machine is constructed with implements this too:
    /// <c>host as IBuzzMachineHostRuntime</c>. Global parameters only; call from the main thread.
    /// </summary>
    public interface IBuzzMachineHostRuntime
    {
        /// <summary>
        /// Renames a global parameter, gives it another range and default, and shows or hides it. A hidden parameter stays in the
        /// machine and its song data but is not listed by the agent server or the parameter windows that honour
        /// <see cref="IParameterMetadata.IsVisible"/>. The machine sets the values the new range needs itself
        /// (<c>IParameter.SetValue</c>). Does nothing for an index the machine doesn't have.
        /// </summary>
        void SetGlobalParameterInfo(int index, string name, string description, int min, int max, int def, bool visible);

        /// <summary>
        /// True while a song file is loading (its machines are made, their parameter values and states set). A machine that mirrors an
        /// outside state (a hosted plug-in) holds the engine's writes meanwhile, since its own state follows.
        /// </summary>
        bool SongLoading => false;
    }

    /// <summary>
    /// A machine that describes its parameters itself, per instance, in the agent server's manifest format
    /// (<c>rebuzz-machine-manifest/1</c>, as the .machine.json files beside machine DLLs): the plugin host builds it from
    /// the loaded plugin, with units. The engine asks again whenever <see cref="ManifestRevision"/> changes.
    /// </summary>
    public interface IBuzzMachineManifest
    {
        /// <summary>The manifest as JSON, or null for none (the manifest beside the DLL, if any, applies).</summary>
        string ManifestJson { get; }

        /// <summary>Changes whenever <see cref="ManifestJson"/> does.</summary>
        int ManifestRevision { get; }
    }

    /// <summary>
    /// A machine whose own GUI is a native window made by something other than WPF or Avalonia (a hosted plug-in's editor), which a host
    /// shows inside one of its own windows (an HWND in Avalonia's NativeControlHost, an NSView) or, when that does not work, in a floating
    /// window of the library's. The studio asks for it on the machine's double click. Every member is called on the host's UI thread.
    /// </summary>
    public interface IBuzzMachineNativeEditor
    {
        /// <summary>0: no editor for this window API. Bits: 1 = can be embedded in a host window, 2 = can float in a window of its own.</summary>
        int EditorSupport(string api);

        /// <summary>
        /// Opens the editor. <paramref name="api"/> is "hwnd" or "nsview". Embedded (<paramref name="floating"/> false): the session's
        /// <see cref="INativeEditorSession.Handle"/> is a native container the host puts in its window (on Windows a child of
        /// <paramref name="parent"/>). Floating: a window of the library's, titled <paramref name="title"/>. <paramref name="scale"/> is
        /// the display scale (1.0 = 96 dpi; Windows only). Null when it could not be opened; <paramref name="error"/> says why.
        /// </summary>
        INativeEditorSession OpenEditor(string api, bool floating, IntPtr parent, double scale, string title, out string error);
    }

    /// <summary>One open editor of an <see cref="IBuzzMachineNativeEditor"/>: closed (disposed) by the host.</summary>
    public interface INativeEditorSession : IDisposable
    {
        /// <summary>The native container (HWND or NSView) the plug-in's view is in.</summary>
        IntPtr Handle { get; }
        bool Floating { get; }

        /// <summary>The view's size now, in physical pixels on Windows and points on macOS.</summary>
        int Width { get; }
        int Height { get; }

        /// <summary>Whether the user may resize the view (the host then lets its window follow the user).</summary>
        bool CanResize { get; }

        /// <summary>The host's window changed size: the plug-in takes what it can (<see cref="Width"/> and <see cref="Height"/> say what it took).</summary>
        void SetSize(int width, int height);

        /// <summary>The display scale changed.</summary>
        void SetScale(double scale);

        /// <summary>The plug-in asked for another size: the container has been sized already, the host sizes its window to match.</summary>
        event Action<int, int> Resized;

        /// <summary>The plug-in asked for its window to close, the floating window's close button was used, or the plug-in went away.</summary>
        event Action CloseRequested;

        /// <summary>A check of what the view draws (colour statistics as JSON); null when the platform cannot say. For tests.</summary>
        string Snapshot(string imagePath);
    }
}
