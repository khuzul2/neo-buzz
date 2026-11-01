#!/usr/bin/env python3
"""Engine census for docs/specs/engine-split.md (P1 step 0).

For each stage of EngineCensus.csproj (0-5, see its header) it measures:
  * csc errors: what "dotnet build" reports. csc stops after declaration errors, so these
    hide every error inside method bodies;
  * all errors: the same files compiled through Roslyn's API (tool/), declaration and body;
  * CA1416: calls to Windows-only APIs that compile but fail at run time off Windows;
  * DllImport declarations by library.
Each error gets one cause (WPF, WinForms, Win32/GDI+, a library, a type declared outside the
engine set, cascade) and one seam (engine-split.md section 3).

    python3 experiments/engine-census/census.py [stages...] [--out DIR]   all stages by default
    python3 experiments/engine-census/census.py closure                   rewrite CommonClosure.props
    python3 experiments/engine-census/census.py machines                  scan RunTime/Gear machine DLLs
    python3 experiments/engine-census/census.py --check [--update]        the ratchet (Linux CI)

Writes DIR/stage<N>.tsv and .json (raw and classified diagnostics), DIR/report.md and
DIR/machines.md. DIR defaults to experiments/engine-census/out (git-ignored).

--check compiles the ratchet stage (CHECK_STAGE) through Roslyn only and compares its error
count with baseline.json: more errors than the baseline fails (exit 1) and lists the files that
gained errors; fewer passes and asks for the baseline to be lowered. --update rewrites
baseline.json with the current counts (each P1 PR lowers it, engine-split.md §5).
"""
import json, os, re, subprocess, sys
from collections import defaultdict, Counter

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
PROJ = os.path.join(HERE, 'EngineCensus.csproj')
TOOL = os.path.join(HERE, 'tool')

# ---------------------------------------------------------------- classification tables

# WPF type names (PresentationFramework, PresentationCore, WindowsBase).
WPF_TYPES = set('''
Window UserControl Control ContentControl FrameworkElement UIElement Visual DependencyObject
DependencyProperty DependencyPropertyChangedEventArgs PropertyMetadata FrameworkPropertyMetadata
Dispatcher DispatcherPriority DispatcherTimer DispatcherOperation DispatcherObject Application
MessageBox MessageBoxButton MessageBoxImage MessageBoxResult MessageBoxOptions
Color Colors Brush Brushes SolidColorBrush LinearGradientBrush ImageSource BitmapImage BitmapSource
BitmapFrame WriteableBitmap RenderTargetBitmap PngBitmapEncoder BitmapCacheOption BitmapCreateOptions
ImageBrush DrawingVisual DrawingContext Pen FormattedText Typeface Geometry StreamGeometry
PathGeometry Transform ScaleTransform TranslateTransform RotateTransform MatrixTransform Matrix
Keyboard ModifierKeys Key KeyEventArgs KeyEventHandler KeyBinding KeyGesture Mouse MouseEventArgs
MouseButtonEventArgs MouseWheelEventArgs MouseButton MouseButtonState Cursor Cursors InputBinding
Point Size Rect Vector Visibility Thickness CornerRadius HorizontalAlignment VerticalAlignment
ResourceDictionary Style Setter Trigger DataTemplate ControlTemplate ContextMenu MenuItem Separator
Canvas Grid StackPanel DockPanel WrapPanel Panel Border TextBlock TextBox Button ToggleButton
CheckBox RadioButton ListBox ListView ComboBox ComboBoxItem Slider ScrollViewer ScrollBar Label
Image ItemsControl Popup ToolTip TabControl TabItem GroupBox Expander ProgressBar StatusBar
HwndSource HwndHost HwndSourceParameters WindowInteropHelper ComponentDispatcher MSG
Clipboard DataObject DataFormats DragDrop DragDropEffects DragEventArgs IDataObject
RoutedEventArgs RoutedEventHandler RoutedEvent EventManager RoutedCommand RoutedUICommand
CommandManager CommandBinding ExecutedRoutedEventArgs CanExecuteRoutedEventArgs ApplicationCommands
Binding BindingOperations BindingMode IValueConverter IMultiValueConverter MultiBinding
RelativeSource PropertyPath BindingExpression XamlReader XamlWriter ParserContext
SizeToContent WindowStartupLocation WindowState WindowStyle ResizeMode ShutdownMode
SystemParameters SystemColors SystemFonts FontWeights FontStyles FontFamily TextAlignment
VisualTreeHelper LogicalTreeHelper AdornerLayer Adorner PresentationSource CompositionTarget
RenderOptions BitmapScalingMode EdgeMode TextOptions Freezable Animatable
FlowDocument Paragraph Run Hyperlink InlineCollection TextRange
DoubleAnimation Storyboard Duration IInputElement FocusManager KeyboardNavigation
GridLength GridUnitType RowDefinition ColumnDefinition Orientation SelectionMode
ExitEventArgs StartupEventArgs DispatcherUnhandledExceptionEventArgs SessionEndingCancelEventArgs
DispatcherUnhandledExceptionFilterEventArgs DispatcherFrame DispatcherHookEventArgs
CollectionViewSource ICollectionView ListCollectionView CollectionView
WindowCollection PixelFormat PixelFormats BitmapPalette Int32Rect DpiScale
MenuBase Menu HeaderedItemsControl ItemContainerGenerator FrameworkContentElement
OpenFileDialog SaveFileDialog RenderMode TemplatePart TemplatePartAttribute
SizeChangedEventArgs TextChangedEventArgs SelectionChangedEventArgs RoutedPropertyChangedEventArgs
'''.split())
WINFORMS_TYPES = set('''
Form Screen SendKeys Keys OpenFileDialog SaveFileDialog FolderBrowserDialog DialogResult
NativeWindow Message IWin32Window CommonDialog Control ColorDialog
'''.split())
# Third-party or WinRT namespaces (first segment(s)) -> library bucket.
LIB_PREFIXES = [
    ('NAudio', 'NAudio'),
    ('Serilog', 'Serilog'),
    ('Serilog.', 'Serilog'),
    ('Microsoft.Windows.Devices', 'WinRT'),
    ('Windows.', 'WinRT'),
    ('Windows', 'WinRT'),
    ('ICSharpCode', 'SharpZipLib'),
    ('Box2dNet', 'Box2dNet'),
]
NAUDIO_TYPES = set('''
WaveFormat IWaveProvider ISampleProvider WaveBuffer WasapiOut AsioOut MMDevice MMDeviceEnumerator
DataFlow DeviceState Role AudioClientShareMode AsioDriver AsioAudioAvailableEventArgs
AsioDevice StoppedEventArgs PlaybackState WaveFormatEncoding WaveFormatExtensible WasapiCapture
MidiIn MidiOut MidiInMessageEventArgs MidiEvent MidiCommandCode NoteEvent NoteOnEvent
ControlChangeEvent PitchWheelChangeEvent MidiController MidiInCapabilities MidiOutCapabilities
PatchChangeEvent ChannelAfterTouchEvent SysexEvent MidiMessage BufferedWaveProvider
WaveFileWriter WaveFileReader AudioFileReader MmException MmResult WaveOutEvent
'''.split())
WINRT_TYPES = set('''
MidiSession MidiEndpointConnection MidiEndpointDeviceInformation MidiVirtualDevice
MidiVirtualDeviceManager MidiVirtualDeviceCreationConfig MidiMessageReceivedEventArgs
MidiDesktopAppSdkInitializer DeviceInformation DeviceWatcher DeviceInformationCollection
IAsyncOperation TypedEventHandler IMidiUniversalPacket MidiMessage32 MidiMessage64
MidiFunctionBlock MidiEndpointDeviceInformationFilters MidiDeclaredEndpointInfo
MidiDeclaredDeviceIdentity MidiEndpointUserSuppliedInfo MidiEndpointNativeDataFormat
MidiMessageBuilder MidiMessageHelper MidiClock MidiGroup MidiChannel MidiSendMessageResults
DeviceInformationUpdate DeviceWatcherStatus MidiEndpointDeviceWatcher
'''.split())
SERILOG_TYPES = set('Log LoggerConfiguration ILogger LogEventLevel'.split())

TYPE_DECL = re.compile(r'\b(?:class|struct|interface|enum|record|delegate\s+[\w<>\[\],\s.?]+?)\s+([A-Z_]\w*)')
EXT_METHOD = re.compile(r'\bstatic\s+[\w<>\[\],.?\s]+?\s+(\w+)\s*(?:<[^>]*>)?\s*\(\s*this\s')
UI_USING = re.compile(r'^\s*using\s+(System\.Windows(?:\.(?!Input\.ICommand)[\w.]+)?|System\.Windows\.Forms)\s*;', re.M)


def rel(p):
    p = os.path.abspath(p)
    return os.path.relpath(p, REPO).replace('\\', '/')


def run(cmd, **kw):
    return subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, **kw)


def msbuild_items(stage, target, item):
    args = ['dotnet', 'msbuild', PROJ, f'-p:CensusStage={stage}', f'-getItem:{item}', '-nologo']
    if target:
        args.insert(3, f'-t:{target}')
    r = run(args)
    data = json.loads(r.stdout[r.stdout.index('{'):])
    return data['Items'].get(item, [])


# ---------------------------------------------------------------- repo index

NS_DECL = re.compile(r'^\s*namespace\s+([\w.]+)', re.M)


def index_repo():
    """name -> [(file, namespace)], ext method -> [files], file -> is UI, namespaces declared."""
    types, exts, ui, namespaces = defaultdict(list), defaultdict(set), {}, defaultdict(set)
    for dp, dn, fn in os.walk(REPO):
        dn[:] = [d for d in dn if d not in ('bin', 'obj', '.git', 'build', 'out', 'node_modules', 'ExternalMachines', '3rdParty')]
        for f in fn:
            if not f.endswith('.cs'):
                continue
            p = os.path.join(dp, f)
            r = rel(p)
            try:
                s = open(p, encoding='utf-8-sig', errors='replace').read()
            except OSError:
                continue
            s_nc = re.sub(r'//.*', '', s)
            nss = NS_DECL.findall(s_nc)
            ns = nss[0] if nss else ''
            for n in nss:
                parts = n.split('.')
                for i in range(1, len(parts)):
                    namespaces['.'.join(parts[:i])]
                namespaces[n].add(r)
            for m in TYPE_DECL.finditer(s_nc):
                types[m.group(1)].append((r, ns))
            for m in EXT_METHOD.finditer(s_nc):
                exts[m.group(1)].add(r)
            ui[r] = (f.endswith('.xaml.cs') or bool(UI_USING.search(s))
                     or 'InitializeComponent' in s)
    return types, exts, ui, namespaces


# ---------------------------------------------------------------- classification

Q = re.compile(r"'([^']*)'")
WIN32 = 'Win32: registry, GDI+'
WINRT = 'Library: WinRT (Windows MIDI Services, Windows.Devices)'
OUTSIDE = 'Type outside the engine set'


def file_usings(path, cache={}):
    if path not in cache:
        try:
            s = open(os.path.join(REPO, path), encoding='utf-8-sig', errors='replace').read()
        except OSError:
            s = ''
        us = re.findall(r'^\s*using\s+(?:static\s+)?([\w.]+)\s*;', s, re.M)
        for n in NS_DECL.findall(s):  # own namespace and its parents
            parts = n.split('.')
            us += ['.'.join(parts[:i]) for i in range(1, len(parts) + 1)]
        cache[path] = us
    return cache[path]


def lib_of_namespace(ns):
    for pre, name in LIB_PREFIXES:
        if ns == pre.rstrip('.') or ns.startswith(pre):
            return 'Library: ' + name if not name.startswith('WinRT') and 'Midi2' not in pre else WINRT
    return None


def outside_decl(base, path, types, compiled):
    """Declarations of `base` visible from `path` (by namespace) that are not compiled."""
    us = set(file_usings(path))
    decls = [(f, ns) for f, ns in types.get(base, []) if ns in us or ns == '']
    if decls and not any(f in compiled for f, _ in decls):
        return sorted({f for f, _ in decls})
    return None


def classify(d, types, exts, ui, namespaces, compiled):
    """Return (bucket, symbol, detail)."""
    code, msg, path = d['id'], d['msg'], d['file']
    q = Q.findall(msg)
    if code == 'CA1416':
        api = q[0] if q else '?'
        return WIN32 + ' (run time)', api.split('(')[0], ''
    if code == 'CS1069':  # forwarded to System.Drawing.Common
        return WIN32, f'{q[1]}.{q[0]} (System.Drawing.Common)', ''

    if code == 'CS0234':  # 'X' does not exist in the namespace 'Y'
        sym, ns = q[0], q[1]
        full = f'{ns}.{sym}'
        if full.startswith('System.Windows.Forms') or (ns == 'System.Windows' and sym == 'Forms'):
            return 'WinForms', full, ''
        if (ns.startswith('System.Windows') or (ns == 'System' and sym == 'Windows')
                or full.startswith('System.Security.RightsManagement') or full in ('Microsoft.Win32.SaveFileDialog', 'Microsoft.Win32.OpenFileDialog')
                or full == 'System.ComponentModel.DesignerProperties'):
            return 'WPF', full, ''
        lib = lib_of_namespace(full) or lib_of_namespace(ns)
        if lib:
            return lib, full, ''
        decls = [f for f, dns in types.get(sym, []) + types.get(sym + 'Attribute', []) if dns == ns]
        if decls and not any(f in compiled for f in decls):
            return OUTSIDE, sym, ', '.join(sorted(set(decls)))
        if full in namespaces:
            return OUTSIDE, full + ' (namespace)', ''
        return 'Other', full, ''

    if code in ('CS0246', 'CS0103', 'CS0122', 'CS0308', 'CS0118'):
        sym = q[0]
        base = sym.split('<')[0].split('.')[-1]
        if code == 'CS0118':  # 'X' is a namespace but is used like a type
            decls = [f for f, dns in types.get(base, []) if dns.split('.')[-1] == base]
            if decls and not any(f in compiled for f in decls):
                return OUTSIDE, base, ', '.join(sorted(set(decls)))
            return 'Cascade', code, msg[:120]
        if base.endswith('Attribute') and base not in types and base[:-9] in types:
            base = base[:-9]
        # A namespace in a using directive
        if sym in namespaces and code == 'CS0246':
            return OUTSIDE, sym + ' (namespace)', ''
        # A namespace named relative to the file's own (SevenZip's RangeCoder.BitEncoder)
        own = [u for u in file_usings(path) if '.' in u or u in namespaces]
        rel_ns = sorted({f'{u}.{sym}' for u in own if f'{u}.{sym}' in namespaces and namespaces[f'{u}.{sym}']})
        if rel_ns and not types.get(base):
            files = sorted({f for n in rel_ns for f in namespaces[n]})
            if not any(f in compiled for f in files):
                return OUTSIDE, rel_ns[0] + ' (namespace)', ', '.join(files)
        lib = lib_of_namespace(sym)
        if lib and code in ('CS0246', 'CS0103'):
            return lib, sym, ''
        where = outside_decl(base, path, types, compiled)
        if where:
            return OUTSIDE, base, ', '.join(where)
        if code == 'CS0103' and path.endswith('.xaml.cs'):
            return 'WPF', 'XAML-generated member (' + base + ')', ''
        if base in WINFORMS_TYPES and any(u.startswith('System.Windows.Forms') for u in file_usings(path)):
            return 'WinForms', base, ''
        if base in WPF_TYPES or base == 'InitializeComponent':
            return 'WPF', base, ''
        if base in NAUDIO_TYPES:
            return 'Library: NAudio', base, ''
        if base in WINRT_TYPES:
            return WINRT, base, ''
        if base in SERILOG_TYPES:
            return 'Library: Serilog', base, ''
        us = file_usings(path)
        libs = {lib_of_namespace(u) for u in us} - {None}
        if len(libs) == 1:
            return libs.pop(), base, 'by using'
        if any(u.startswith('System.Windows') and u != 'System.Windows.Input' for u in us) and not libs:
            return 'WPF', base, 'by using'
        return 'Other', base, ''

    if code in ('CS1061', 'CS0117'):  # 'T' does not contain a definition for 'M'
        owner, sym = q[0], (q[1] if len(q) > 1 else '?')
        if sym in exts and not any(f in compiled for f in exts[sym]):
            return OUTSIDE, sym + '()', ', '.join(sorted(exts[sym]))
        obase = owner.split('<')[0].split('.')[-1]
        if obase in WPF_TYPES or owner.startswith('System.Windows'):
            return 'WPF', f'{obase}.{sym}', ''
        if obase in NAUDIO_TYPES:
            return 'Library: NAudio', f'{obase}.{sym}', ''
        decl = [f for f, _ in types.get(obase, [])]
        if decl and not any(f in compiled for f in decl):
            return OUTSIDE, f'{obase}.{sym}', ', '.join(sorted(decl))
        if decl and all(ui.get(f) for f in decl):
            return 'WPF', f'{obase}.{sym} (inherited from a WPF base)', ''
        return 'Other', f'{obase}.{sym}', ''

    return 'Cascade', code, msg[:120]


# ---------------------------------------------------------------- seams (engine-split.md §3)

SEAMS = [
    ('S1 IUserMessages (message boxes, questions)',
     'MessageBox MessageBoxButton MessageBoxResult MessageBoxImage System.Windows.MessageBox MessageBoxWindow'),
    ('S2 IUiDispatcher + IUiTimer (dispatcher, timers, priorities)',
     'DispatcherTimer DispatcherPriority System.Windows.Threading Dispatcher DispatcherFrame '
     'DispatcherOperationCallback Application CommandManager'),
    ('S3 IMachineUiHost (parameter window, machine GUI window, pattern editor control, rename dialog)',
     'Window System.Windows.Window UserControl MachineGUIHostWindow ParameterWindow ParameterWindowVM '
     'RenameMachineWindow Viewbox SizeToContent ResizeMode WindowInteropHelper HwndSource Rect System.Windows.Rect '
     'System.Windows.Visibility WindowStartupLocation ParameterValueEditor NativeMachineFrameworkUI (namespace) '
     'System.Windows.Forms System.Windows.Controls System.Windows.Interop Win32Mouse WPFExtensions'),
    ('S4 Portable colours and images (ARGB uint, skin bitmaps as bytes)',
     'Color Colors ColorConverter ImageSource BitmapSource Point Size Imaging Int32Rect BitmapSizeOptions '
     'System.Windows.Media System.Drawing.Bitmap (System.Drawing.Common) System.Drawing.Imaging.PixelFormat (System.Drawing.Common) '
     'System.Drawing.Imaging.BitmapData (System.Drawing.Common) System.Drawing.Imaging.ImageLockMode (System.Drawing.Common)'),
    ('S5 IKeyboard with a portable modifier enum', 'Keyboard ModifierKeys'),
    ('S6 Themes and XAML resources leave the engine',
     'ResourceDictionary XamlReaderEx Fonts FontStyles FontWeights FontStretches RenderOptions RenderMode '
     'FrameworkElement PresentationSource ScaleTransform UIElement AsioConfigWindow WasapiConfigWindow'),
    ('S7 View state the engine reads from UI statics',
     'MachineView BuzzGUI.SequenceEditor (namespace) SessionPalette VUMeter'),
    ('S8 Audio and MIDI backends (IAudioDriverFactory, IMidiBackend)',
     'AsioAudioDriver WasapiAudioDriver AsioOut AsioDevice MidiIn MidiOut MidiInDevice MidiOutDevice Midi2 Midi2.Release '
     'DeviceInformation DeviceInformationUpdate DeviceWatcher DeviceWatcherStatus Windows'),
    ('S9 IFileNameChoice for the remaining dialogs', 'OpenFileDialog Microsoft.Win32.SaveFileDialog'),
    ('S10 Build info and logging (embedded resource, Serilog project reference)', 'Resources Log Serilog'),
]
SEAM_OF = {}
for _name, _syms in SEAMS:
    for _s in re.findall(r'[\w.]+(?: \([^)]*\))?', _syms):
        SEAM_OF[_s] = _name


def seam_of(d):
    if d.get('using'):
        return 'S0 Delete or move `using` lines'
    if d['bucket'] == 'Cascade':
        return 'Cascade (goes with the others)'
    return SEAM_OF.get(d['sym'], 'Unassigned')


# ---------------------------------------------------------------- per stage

DEFINES = ('TRACE', 'DEBUG', 'NET', 'NET10_0', 'NETCOREAPP', 'NET5_0_OR_GREATER', 'NET6_0_OR_GREATER',
           'NET7_0_OR_GREATER', 'NET8_0_OR_GREATER', 'NET9_0_OR_GREATER', 'NET10_0_OR_GREATER')
_ANALYZERS = []


def analyzer_dir():
    if not _ANALYZERS:
        props = json.loads(run(['dotnet', 'msbuild', PROJ, '-getProperty:NETCoreSdkVersion',
                                '-getProperty:NetCoreRoot']).stdout)['Properties']
        _ANALYZERS.append(os.path.join(props['NetCoreRoot'], 'sdk', props['NETCoreSdkVersion'], 'Sdks',
                                       'Microsoft.NET.Sdk', 'analyzers'))
    return _ANALYZERS[0]


def roslyn(srcs, refs, tag, outdir, idx, analyzers=True):
    """Compile through tool/ and return classified, de-duplicated diagnostics."""
    types, exts, ui, namespaces = idx
    args_path = os.path.join(outdir, f'{tag}.args')
    with open(args_path, 'w') as f:
        f.writelines(f'src:{x}\n' for x in srcs)
        f.writelines(f'ref:{x}\n' for x in refs)
        f.writelines(f'def:{x}\n' for x in DEFINES)
        for a in ('Microsoft.CodeAnalysis.NetAnalyzers.dll', 'Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll'):
            if analyzers:
                f.write(f'analyzer:{os.path.join(analyzer_dir(), a)}\n')
    tsv = os.path.join(outdir, f'{tag}.tsv')
    t = run(['dotnet', 'run', '--no-build', '--project', TOOL, '-c', 'Release', '--', args_path, tsv])
    if t.returncode != 0:
        print(t.stdout, t.stderr)
        sys.exit(1)
    compiled = {rel(x) for x in srcs}
    lines = {}
    diags, seen = [], set()
    for line in open(tsv, encoding='utf-8'):
        sev, code, path, ln, col, msg = line.rstrip('\n').split('\t', 5)
        key = (rel(path), int(ln), int(col), code, msg)
        if key in seen:
            continue
        seen.add(key)
        d = {'sev': sev, 'id': code, 'file': rel(path), 'line': int(ln), 'col': int(col), 'msg': msg}
        d['bucket'], d['sym'], d['where'] = classify(d, types, exts, ui, namespaces, compiled)
        if path not in lines:
            lines[path] = open(path, encoding='utf-8-sig', errors='replace').read().split('\n')
        src = lines[path][int(ln) - 1].strip() if int(ln) - 1 < len(lines[path]) else ''
        d['using'] = bool(re.match(r'using\s+(static\s+)?[\w.]+\s*;', src))
        diags.append(d)
    return diags, compiled


def stage_inputs(stage):
    srcs = sorted({os.path.normpath(c['FullPath']) for c in msbuild_items(stage, None, 'Compile')})
    refs = [r['FullPath'] for r in msbuild_items(stage, 'ResolveReferences', 'ReferencePath')]
    return srcs, refs


def pinvokes(srcs):
    """DllImport/LibraryImport declarations by library, in the given files."""
    libs = Counter()
    files = defaultdict(set)
    for x in srcs:
        s = open(x, encoding='utf-8', errors='replace').read()
        for m in re.finditer(r'\[\s*(?:DllImport|LibraryImport)\s*\(\s*(?:"([^"]+)"|([\w.]+))', s):
            lib = (m.group(1) or m.group(2)).lower().replace('.dll', '')
            libs[lib] += 1
            files[lib].add(rel(x))
    return libs, files


def measure(stage, outdir, idx):
    # 1. what dotnet build (csc) reports
    b = run(['dotnet', 'build', PROJ, f'-p:CensusStage={stage}', '-p:TreatWarningsAsErrors=false',
             '-nologo', '-clp:NoSummary', '-v:q'])
    csc = set()
    for line in b.stdout.splitlines():
        m = re.match(r'(.*?)\((\d+),(\d+)\): error (CS\d+): (.*?) \[', line.strip())
        if m:
            csc.add((rel(m.group(1)), int(m.group(2)), int(m.group(3)), m.group(4)))
    # 2. full Roslyn pass
    srcs, refs = stage_inputs(stage)
    diags, compiled = roslyn(srcs, refs, f'stage{stage}', outdir, idx)
    loc = sum(sum(1 for _ in open(x, encoding='utf-8', errors='replace')) for x in srcs)
    libs, lib_files = pinvokes(srcs)
    return {'stage': stage, 'files': len(srcs), 'loc': loc, 'csc': len(csc), 'diags': diags,
            'compiled': compiled, 'pinvoke': dict(libs),
            'pinvoke_files': {k: sorted(v) for k, v in lib_files.items()}}


def closure(outdir, idx):
    """Smallest BuzzGUI.Common subset the stage-5 engine set needs; writes CommonClosure.props.

    Candidates are the stage-3 BuzzGUI.Common files (UI files already removed). Starting from
    none, add every candidate that declares a type the compilation misses, until nothing changes.
    """
    srcs, refs = stage_inputs(3)
    common = [x for x in srcs if rel(x).startswith('ReBuzzGUI/BuzzGUI.Common/')]
    srcs5, refs = stage_inputs(5)
    base = [x for x in srcs5 if not rel(x).startswith('ReBuzzGUI/BuzzGUI.Common/')]
    by_rel = {rel(x): x for x in common}
    chosen = set()
    for it in range(30):
        diags, _ = roslyn(base + sorted(chosen), refs, f'closure{it}', outdir, idx)
        add = set()
        for d in diags:
            if d['bucket'] != OUTSIDE or not d['where']:
                continue
            for f in d['where'].split(', '):
                if f in by_rel and by_rel[f] not in chosen:
                    add.add(by_rel[f])
        errs = sum(1 for d in diags if d['sev'] == 'Error')
        print(f'closure pass {it}: {len(chosen)} Common files, {errs} errors, +{len(add)}')
        if not add:
            break
        chosen |= add
    bs = '\\'
    lines = ['<Project>',
             '  <!-- Generated by "census.py closure": the BuzzGUI.Common files the stage-5 engine set needs. -->',
             '  <ItemGroup>']
    for x in sorted(chosen, key=rel):
        r = rel(x)[len('ReBuzzGUI/BuzzGUI.Common/'):].replace('/', bs)
        lines.append(f'    <Compile Include="$(C){r}" Link="BuzzGUI.Common{bs}{r}" />')
    lines += ['  </ItemGroup>', '</Project>', '']
    with open(os.path.join(HERE, 'CommonClosure.props'), 'w') as f:
        f.write('\n'.join(lines))
    print(f'CommonClosure.props: {len(chosen)} of {len(common)} BuzzGUI.Common files (stage-5 engine set)')


def short(f):
    return f.replace('ReBuzzGUI/', '').replace('ReBuzz/', '')


def report(results, outdir):
    out = []
    w = out.append
    w('# Engine census (generated by census.py)\n')
    w('| Stage | Files | LOC | csc errors (dotnet build) | All errors (Roslyn) | of which `using` lines | Files with errors | CA1416 sites | P/Invoke decls |')
    w('|---|---|---|---|---|---|---|---|---|')
    for r in results:
        errs = [d for d in r['diags'] if d['sev'] == 'Error']
        ca = [d for d in r['diags'] if d['id'] == 'CA1416']
        us = sum(1 for d in errs if d.get('using'))
        w(f"| {r['stage']} | {r['files']} | {r['loc']:,} | {r['csc']} | {len(errs)} | {us} | {len({d['file'] for d in errs})} | {len(ca)} | {sum(r['pinvoke'].values())} |")
    for r in results:
        errs = [d for d in r['diags'] if d['sev'] == 'Error']
        w(f"\n## Stage {r['stage']}: errors by cause\n")
        w('| Cause | Errors | of which `using` lines | Distinct symbols | Files |')
        w('|---|---|---|---|---|')
        by = defaultdict(list)
        for d in errs:
            by[d['bucket']].append(d)
        for b, ds in sorted(by.items(), key=lambda kv: -len({d['file'] for d in kv[1]})):
            w(f"| {b} | {len(ds)} | {sum(1 for d in ds if d.get('using'))} | {len({d['sym'] for d in ds})} | {len({d['file'] for d in ds})} |")
        ca = [d for d in r['diags'] if d['id'] == 'CA1416']
        if ca:
            w(f"| (CA1416: compiles, Windows-only at run time) | {len(ca)} | – | {len({d['sym'] for d in ca})} | {len({d['file'] for d in ca})} |")
        if r['pinvoke']:
            w('\nP/Invoke declarations by library: ' + ', '.join(f"{k} {v} ({len(r['pinvoke_files'][k])} files)" for k, v in sorted(r['pinvoke'].items(), key=lambda kv: -kv[1])))
        w(f"\n### Stage {r['stage']}: errors by seam that removes them\n")
        w('| Seam | Errors | Files | Files (errors) |')
        w('|---|---|---|---|')
        bys = defaultdict(list)
        for d in errs:
            bys[seam_of(d)].append(d)
        for sname, ds in sorted(bys.items()):
            fc = Counter(short(d['file']) for d in ds)
            w(f"| {sname} | {len(ds)} | {len(fc)} | {', '.join(f'{f} ({n})' for f, n in fc.most_common())} |")
        w(f"\n### Stage {r['stage']}: symbols per cause (count, files)\n")
        for b, ds in sorted(by.items(), key=lambda kv: -len(kv[1])):
            c = Counter(d['sym'] for d in ds)
            files_of = defaultdict(set)
            where = {}
            for d in ds:
                files_of[d['sym']].add(short(d['file']))
                if d['where']:
                    where[d['sym']] = d['where']
            w(f"- **{b}**")
            for s, n in c.most_common():
                extra = f" — declared in {where[s]}" if s in where and b == OUTSIDE else ''
                w(f"  - `{s}` ×{n} in {len(files_of[s])} files: {', '.join(sorted(files_of[s])[:8])}{' …' if len(files_of[s]) > 8 else ''}{extra}")
        if ca:
            c = Counter(d['sym'] for d in ca)
            files_of = defaultdict(set)
            for d in ca:
                files_of[d['sym']].add(short(d['file']))
            w('- **CA1416 (run-time)**')
            for s, n in c.most_common():
                w(f"  - `{s}` ×{n}: {', '.join(sorted(files_of[s])[:6])}")
        w(f"\n### Stage {r['stage']}: files by error count\n")
        w('| File | Errors | Causes |')
        w('|---|---|---|')
        per = defaultdict(list)
        for d in errs:
            per[d['file']].append(d)
        for f, ds in sorted(per.items(), key=lambda kv: -len(kv[1])):
            cs = Counter(d['bucket'] for d in ds)
            w(f"| {short(f)} | {len(ds)} | {'; '.join(f'{k} {v}' for k, v in cs.most_common())} |")
    with open(os.path.join(outdir, 'report.md'), 'w') as f:
        f.write('\n'.join(out) + '\n')


# WPF-typed members of the machine API (engine-split.md section 2.5), as they appear in metadata.
WPF_API_MEMBERS = {
    ('BuzzGUI.Interfaces.IMachineDLL', 'get_Skin'), ('BuzzGUI.Interfaces.IMachineDLL', 'get_SkinLED'),
    ('BuzzGUI.Interfaces.IMachineDLL', 'get_SkinLEDSize'), ('BuzzGUI.Interfaces.IMachineDLL', 'get_SkinLEDPosition'),
    ('BuzzGUI.Interfaces.IMachineDLL', 'get_TextColor'), ('BuzzGUI.Interfaces.IMachine', 'get_ParameterWindow'),
    ('BuzzGUI.Interfaces.IBuzz', 'get_ThemeColors'), ('BuzzGUI.Common.Global', 'get_MachineViewHwndSource'),
    ('BuzzGUI.Common.Global', 'set_MachineViewHwndSource'),
}


def machines(outdir):
    """Scan prebuilt managed machine DLLs (RunTime/Gear) for references to the machine API."""
    dlls = []
    for dp, dn, fn in os.walk(os.path.join(REPO, 'RunTime', 'Gear')):
        dlls += [os.path.join(dp, f) for f in fn if f.lower().endswith('.dll')]
    tsv = os.path.join(outdir, 'machines.tsv')
    t = run(['dotnet', 'run', '--no-build', '--project', TOOL, '-c', 'Release', '--', 'refs', tsv] + sorted(dlls))
    if t.returncode != 0:
        print(t.stdout, t.stderr)
        sys.exit(1)
    rows = [l.rstrip('\n').split('\t') for l in open(tsv, encoding='utf-8')]
    by = defaultdict(list)
    for r in rows:
        by[r[0]].append(r)
    closure_types = set()
    props = os.path.join(HERE, 'CommonClosure.props')
    if os.path.exists(props):
        types, exts, ui, namespaces = index_repo()
        files = {('ReBuzzGUI/BuzzGUI.Common/' + m.replace(chr(92), '/')) for m in re.findall(r'Include="\$\(C\)([^"]+)"', open(props).read())}
        for name, decls in types.items():
            for f, ns in decls:
                if f in files:
                    closure_types.add(f'{ns}.{name}')
    out = ['| Machine DLL | Target | Refs BuzzGUI.Interfaces | Refs BuzzGUI.Common | WPF-typed API members used | Common types outside the portable subset | WPF/WinForms refs |',
           '|---|---|---|---|---|---|---|']
    for dll, rs in sorted(by.items()):
        tfm = rs[0][1]
        bi = any(r[2] == 'asmref' and r[3] == 'BuzzGUI.Interfaces' for r in rs)
        bc = any(r[2] == 'asmref' and r[3] == 'BuzzGUI.Common' for r in rs)
        wpf_members = sorted({f"{r[4].split('.')[-1]}.{r[5].replace('get_', '').replace('set_', '')}" for r in rs
                              if r[2] == 'memberref' and (r[4], r[5]) in WPF_API_MEMBERS})
        common_types = sorted({r[4] for r in rs if r[3] == 'BuzzGUI.Common' and r[2] in ('typeref',)
                               and r[4].split('+')[0] not in closure_types})
        ui = sorted({r[3] for r in rs if r[2] == 'asmref' and r[3] in ('PresentationFramework', 'PresentationCore', 'WindowsBase', 'System.Windows.Forms')})
        out.append(f"| {dll} | {tfm} | {'yes' if bi else 'no'} | {'yes' if bc else 'no'} | {', '.join(wpf_members) or '–'} | "
                   f"{len(common_types)}: {', '.join(t.split('.')[-1] for t in common_types[:6])}{' …' if len(common_types) > 6 else ''} | {', '.join(ui) or '–'} |")
    with open(os.path.join(outdir, 'machines.md'), 'w') as f:
        f.write('\n'.join(out) + '\n')
    print('\n'.join(out))


# ---------------------------------------------------------------- ratchet (Linux CI)

CHECK_STAGE = 5
BASELINE = os.path.join(HERE, 'baseline.json')


def gh(kind, msg, title=None, file=None, line=None):
    """A GitHub Actions annotation when running there; plain text otherwise."""
    if not os.environ.get('GITHUB_ACTIONS'):
        where = f'{file}({line}): ' if file else ''
        print(f'{where}{kind}: {msg}')
        return
    props = []
    if file:
        props.append(f'file={file}')
    if line:
        props.append(f'line={line}')
    if title:
        props.append(f'title={title}')
    msg = msg.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
    print(f"::{kind} {','.join(props)}::{msg}")


def check(outdir, idx, update):
    """Compare the ratchet stage's error count with baseline.json (engine-split.md §5)."""
    r = run(['dotnet', 'restore', PROJ, '-nologo', '-v:q'])
    if r.returncode != 0:
        print(r.stdout, r.stderr)
        sys.exit(1)
    srcs, refs = stage_inputs(CHECK_STAGE)
    diags, _ = roslyn(srcs, refs, 'check', outdir, idx, analyzers=False)
    errs = [d for d in diags if d['sev'] == 'Error']
    per_file = Counter(d['file'] for d in errs)
    total = len(errs)
    print(f'census stage {CHECK_STAGE}: {len(srcs)} files, {total} errors in {len(per_file)} files')

    if update:
        with open(BASELINE, 'w', newline='\n') as f:
            json.dump({'stage': CHECK_STAGE, 'errors': total,
                       'files': dict(sorted(per_file.items()))}, f, indent=2)
            f.write('\n')
        print(f'baseline.json: {total} errors')
        return 0

    base = json.load(open(BASELINE))
    if base.get('stage') != CHECK_STAGE:
        gh('error', f"baseline.json is for stage {base.get('stage')}, the check measures stage {CHECK_STAGE}",
           title='Engine census')
        return 1
    limit, base_files = base['errors'], base.get('files', {})
    rose = {f: (base_files.get(f, 0), n) for f, n in per_file.items() if n > base_files.get(f, 0)}
    for f, (was, now) in sorted(rose.items()):
        print(f'  {f}: {was} -> {now}')
    if total > limit:
        shown = 0
        for d in sorted(errs, key=lambda d: (d['file'], d['line'])):
            if d['file'] in rose and shown < 50:
                gh('error', f"{d['id']}: {d['msg']}", file=d['file'], line=d['line'])
                shown += 1
        gh('error', f'{total} errors, baseline {limit}: the engine gained {total - limit} errors that keep it '
           f'from compiling as plain net10.0. Files that gained errors: '
           f"{', '.join(sorted(rose)) or '(none; errors moved)'}. See docs/specs/engine-split.md.",
           title='Engine census')
        return 1
    if total < limit:
        gh('warning', f'{total} errors, baseline {limit}: lower the baseline with '
           f'"python3 experiments/engine-census/census.py --check --update".', title='Engine census')
    else:
        gh('notice', f'{total} errors (baseline {limit})', title='Engine census')
    return 0


def main():
    argv = sys.argv[1:]
    outdir = os.path.join(HERE, 'out')
    if '--out' in argv:
        i = argv.index('--out')
        outdir = argv[i + 1]
        del argv[i:i + 2]
    os.makedirs(outdir, exist_ok=True)
    r = run(['dotnet', 'build', TOOL, '-c', 'Release', '-nologo', '-v:q'])
    if r.returncode != 0:
        print(r.stdout)
        sys.exit(1)
    idx = index_repo()
    if '--check' in argv:
        sys.exit(check(outdir, idx, '--update' in argv))
    if argv and argv[0] == 'closure':
        closure(outdir, idx)
        return
    if argv and argv[0] == 'machines':
        machines(outdir)
        return
    stages = [int(a) for a in argv] or [0, 1, 2, 3, 4, 5]
    results = []
    for st in stages:
        res = measure(st, outdir, idx)
        errs = [d for d in res['diags'] if d['sev'] == 'Error']
        print(f"stage {st}: {res['files']} files, {res['loc']} LOC, csc {res['csc']}, all {len(errs)}, "
              f"CA1416 {sum(1 for d in res['diags'] if d['id'] == 'CA1416')}, P/Invoke {sum(res['pinvoke'].values())}")
        results.append(res)
        with open(os.path.join(outdir, f'stage{st}.json'), 'w') as f:
            json.dump({k: (sorted(v) if isinstance(v, set) else v) for k, v in res.items()}, f, indent=0)
    report(results, outdir)
    print('report:', os.path.join(outdir, 'report.md'))


if __name__ == '__main__':
    main()
