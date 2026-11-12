# NeoBuzz Live

NeoBuzz Live is a fork of [ReBuzz](https://github.com/wasteddesign/ReBuzz), the modular digital audio workstation built upon the foundation of [Jeskola Buzz](https://jeskola.net/buzz/), by WDE and the ReBuzz contributors (MIT). It adds a clip-launching Session View for playing live, the Neo machines, an MCP server through which AI agents work in your songs, and a studio for macOS; [FORK.md](FORK.md) lists everything it adds and how it is built.

Songs (`.bmx`, `.bmxml`), settings and machines from ReBuzz carry over: NeoBuzz Live keeps ReBuzz's file formats, the machine names songs save and its settings folders.

## Download

- **Mac** (Apple silicon, macOS 12 or later): [NeoBuzz Live for Mac (latest main)](https://github.com/khuzul2/neo-buzz/releases/tag/mac-latest), `NeoBuzz-Live-macos-arm64.dmg`. Open it and drag NeoBuzz Live onto Applications. The app isn't notarized, so the first time you open it macOS asks you to allow it: System Settings → Privacy & Security → Open Anyway, or once in Terminal `xattr -dr com.apple.quarantine "/Applications/NeoBuzz Live.app"`. The disk image's *Read me first.txt* has the steps.
- **Windows** (x64): `NeoBuzz-Live-<version>-x64.zip`, from the **Fork package** workflow (Actions tab) or [`build.ps1`](FORK.md#building-the-package). Unzip anywhere and run `NeoBuzzLive.exe`; *README-FIRST.txt* lists what it needs: the [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-10.0.0-windows-x64-installer), the [latest Visual C++ Redistributable (x86 and x64)](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170) and, optionally, [Windows MIDI Services](https://github.com/microsoft/MIDI/releases/latest).

## AI agents

NeoBuzz Live is an MCP server ([docs/specs/mcp-server.md](docs/specs/mcp-server.md)), named `neobuzz-live`. With the app open, allow agents (Settings → Agent on Windows, Preferences ▸ Agent on a Mac; Ctrl+Shift+A or ⇧⌘A) and paste the line it shows into a terminal:

```
claude mcp add --transport http neobuzz-live http://127.0.0.1:7481/mcp --header "Authorization: Bearer <token>"
```

Without a window, Claude Code starts it on stdio: `claude mcp add neobuzz-live -- "C:\path\to\NeoBuzzLive.exe" --agent` on Windows, or `rebuzz-headless --agent` (built from `ReBuzz.Headless`) on macOS and Linux.

## Building

[FORK.md](FORK.md#building-the-package): `.\build.ps1` on Windows builds the solution, every machine and the zip; on a Mac, `tools/mac-bundle.sh` and `tools/mac-dmg.sh` make `NeoBuzz Live.app` and its disk image. The solution, projects and namespaces keep ReBuzz's names (`ReBuzz.sln`, `ReBuzz.*`), so upstream merges stay clean.

## Based on ReBuzz

NeoBuzz Live is based on ReBuzz, by WDE (wasteddesign) and the ReBuzz contributors: https://github.com/wasteddesign/ReBuzz. ReBuzz's MIT licence ([LICENSE](LICENSE)) covers it and is kept as it is. Upstream's README follows, for its notes on building, debugging and its machines.

## ReBuzz (upstream's README)
ReBuzz is a modular digital audio workstation (DAW) built upon the foundation of [Jeskola Buzz](https://jeskola.net/buzz/) software. Written in C#, ReBuzz combines modern features with the beloved workflow of its predecessor. While it’s still in development, users should exercise some caution regarding stability and other potential uncertainties. The primary focus is on providing a stable experience and robust VST support.

![ReBuzz](images/topanimation.gif)

### Features
* 32 and 64 bit VST2/3 support
* 32 and 64 bit buzz machine support
* MIDI 2.0
* Modern Pattern Editor, Modern Sequence Editor, AudioBlock, EnvelopeBlock, CMC, TrackScript...
* Multi-process architecture
* Multi-io for native and managed machines
* Multi-channel recording
* Includes [NWaves](https://github.com/ar1st0crat/NWaves) .NET DSP library for audio processing
* bmx and bmxml file support
* ...

### Download
[ReBuzz Installer](https://github.com/wasteddesign/ReBuzz/releases/latest)

Requires:
1. [.NET 10.0 Desktop Runtime - Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-10.0.0-windows-x64-installer)
2. [Latest Microsoft Visual C++ Redistributable (X86 and X64)](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170)

Optional:
1. [Install Windows.MIDI.Services.Tools](https://github.com/microsoft/MIDI/releases/latest)

### How to build?
1. Clone this repo. **IMPORTANT: Clone with --recurse-submodules** <br>

2. Load the ReBuzz.sln (located at \ReBuzz\ReBuzz.sln) **In Visual Studio 2022** (Community version will suffice) 

3. Ensure ReBuzz is the Startup project. It should be highlighted bold in the Solution Explorer.<br><br>
    If it is not, then right click the ReBuzz project in Solution Explorer, and select "Set as Startup Project"

4. Build all.

5. The result should be output to (depending on if Debug or Release is selected):<br>
    - <repo dir>\bin\Debug\x64\net9.0-windows\ <br>
    - <repo dir>\bin\Release\x64\net9.0-windows\ <br>

6. You should be able to run ReBuzz directly from there.

In order to prevent pollution of the source tree(s), **all** intermediate files are located in: <br>
    - <repo dir>\build

    This has been achieved by (*This is information only. This is only useful when adding new projects to ReBuzz*): <br>
      - Placing ``Directory.build.props`` in each project (same level as each .csproj file) <br>
      - *Manually* setting the following in the .csproj: <br>
        ```xml
        <IntermediateOutputPath>$(SolutionDir)build\$(Configuration)\$(Platform)\Rebuzz\obj</IntermediateOutputPath>
        ```
      - Output directory set to ``root\ReBuzz\bin``, also set in the csproj files using the following: <br>
        ```xml
        <OutputPath>$(SolutionDir)bin\$(Configuration)</OutputPath>
        ```
     - *Adding instructions into the csproj files to manaully* generate ``project.assets.json``, because for some reason, MSBuild is incapable of detecting that this file is missing and does not peform a restore by itself:<br>
     ```xml
     <Target Name="ForcePerformRestore" AfterTargets="BeforeBuild">
         <Exec Command="IF NOT EXIST  &quot;$(IntermediateOutputPath)..\project.assets.json&quot;  echo restore to $(IntermediateOutputPath)..\project.assets.json &amp;&amp;  dotnet restore &quot;$(ProjectName).csproj&quot;  --no-dependencies &amp;&amp; move &quot;build\$(ProjectName)\obj\*&quot; &quot;$(IntermediateOutputPath)..&quot; &amp;&amp; rmdir /Q /S build	 " />
    </Target>

   <Target Name="RemoveEmptyObj" AfterTargets="AfterBuild">
         <Exec Command="IF EXIST obj rmdir obj" />
   </Target>

### Notes

* To speed up debugging in Visual Studio, set Data Binding Off under Tools -> Options -> Debugging -> Output Window -> WPF Trace Settings
* Set an exception for ReBuzz.exe process in Windows Defender if starting up ReBuzz takes too long.

ReBuzz uses multi-process architecture. To debug a native machine that is loaded by ReBuzzEngine in Visual Studio you can for example:
1. Add a break point in MachineManager.CreateNativeMachine
https://github.com/wasteddesign/ReBuzz/blob/1243cc9ef40e4c25804c462d290d511c02b37054/ReBuzz/MachineManagement/MachineManager.cs#L236
3. Add a break point to ReBuzzEngine.cpp IPC::UILoadLibrary
https://github.com/wasteddesign/ReBuzz/blob/1243cc9ef40e4c25804c462d290d511c02b37054/ReBuzzEngine/rebe/ReBuzzEngine.cpp#L144
4. Start ReBuzz debug session
5. When break point is hit, do _Attach to Process..._ and find _ReBuzzEngine_
6. Continue debugging

### Machine Source Code
Dive into the machine source code
* https://github.com/Buzztrax/buzzmachines
* https://github.com/thepedal

### How can I help?
All the basic functionality is implemented but there many areas to improve. In general, contributions are needed in every part of the software, but here are few items to look into:

- [ ] Pick an [issue](https://github.com/wasteddesign/ReBuzz/issues) and start contributing to Rebuzz development today!
- [ ] Improve stability, fix bugs and issues
- [ ] Cleanup code and architecture
- [ ] Add comments and documentation
- [x] Improve Audio wave handling (Wavetable)
- [ ] Improve file handling to support older songs
- [ ] Reduce latency, optimize code

#### Join the discussion on Discord:  
https://discord.gg/vPE4THRBNT

Let's make this a good one.
