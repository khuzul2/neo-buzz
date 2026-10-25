// SettingsWindow.xaml.cs – Assignment Settings dialog, fully code-only.
// Uses a custom DarkCombo control instead of ComboBox to avoid WPF theme
// colour conflicts entirely.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using BuzzGUI.Interfaces;

namespace BTDSys.PeerCtrl
{
    // =========================================================================
    // DarkCombo – fully custom dark dropdown, no WPF ComboBox involved.
    // Supports per-item colour highlighting and keyboard navigation.
    //
    // Key design: the Popup is NEVER given keyboard focus. DarkCombo itself
    // retains focus so OnPreviewKeyDown receives every keystroke and routes
    // it to the list manually. This avoids the cross-HwndSource focus problem
    // (WPF Popup is a separate Win32 window and swallows keys when focused).
    // =========================================================================
    public class DarkCombo : ContentControl
    {
        static SolidColorBrush Mkb(string h) { h=h.TrimStart('#'); if(h.Length==3)h=$"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}"; var b=new SolidColorBrush(Color.FromRgb(Convert.ToByte(h.Substring(0,2),16),Convert.ToByte(h.Substring(2,2),16),Convert.ToByte(h.Substring(4,2),16))); b.Freeze(); return b; }

        static readonly SolidColorBrush BG           = Mkb("#2D2D2D");
        static readonly SolidColorBrush FG           = Mkb("#DDDDDD");
        static readonly SolidColorBrush FG_HIGHLIGHT = Mkb("#33FF77");
        static readonly SolidColorBrush BORDER       = Mkb("#555555");

        public event Action<int> SelectionChanged;
        public int    SelectedIndex { get; private set; } = -1;
        public string SelectedItem  => SelectedIndex >= 0 && SelectedIndex < _items.Count ? _items[SelectedIndex].text : null;
        public int    Count         => _items.Count;

        readonly List<(string text, bool highlighted)> _items = new List<(string, bool)>();
        readonly TextBlock _label;
        readonly Popup     _popup;
        readonly ListBox   _list;
        bool _navigating;

        public DarkCombo()
        {
            Focusable = true;
            IsTabStop = true;
            Margin    = new Thickness(0, 2, 0, 2);
            Cursor    = Cursors.Hand;

            _label = new TextBlock { Foreground=FG, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(4,0,24,0), TextTrimming=TextTrimming.CharacterEllipsis };
            var arrow = new TextBlock { Text="\u25be", Foreground=FG, VerticalAlignment=VerticalAlignment.Center, HorizontalAlignment=HorizontalAlignment.Right, Margin=new Thickness(0,0,6,0) };
            var inner = new Grid(); inner.Children.Add(_label); inner.Children.Add(arrow);
            var box   = new Border { Background=BG, BorderBrush=BORDER, BorderThickness=new Thickness(1), Child=inner, Height=24 };

            // Highlight border on hover and focus
            box.MouseEnter += (s,e) => { if (!_popup.IsOpen) box.BorderBrush = Mkb("#888888"); };
            box.MouseLeave += (s,e) => { if (!_popup.IsOpen) box.BorderBrush = IsFocused ? Mkb("#44AADD") : BORDER; };
            GotFocus  += (s,e) => box.BorderBrush = Mkb("#44AADD");
            LostFocus += (s,e) => box.BorderBrush = BORDER;

            _list = new ListBox { Background=BG, BorderBrush=BORDER, BorderThickness=new Thickness(1), Foreground=FG };
            _list.ItemContainerStyle = MakeItemStyle();

            // Mouse click on list item commits – but not during keyboard navigation
            _list.SelectionChanged += (s,e) =>
            {
                if (_popup.IsOpen && !_navigating && _list.SelectedIndex >= 0)
                    Commit(_list.SelectedIndex);
            };

            // Prevent popup from stealing focus on mouse click
            _list.PreviewMouseDown += (s,e) => { this.Focus(); };

            _popup = new Popup { Child=_list, Placement=PlacementMode.Bottom, StaysOpen=false };

            // Popup border feedback – these must come AFTER _popup is assigned
            _popup.Opened += (s,e) => box.BorderBrush = Mkb("#44AADD");
            _popup.Closed  += (s,e) => box.BorderBrush = IsFocused ? Mkb("#44AADD") : BORDER;

            _popup.Opened += (s,e) =>
            {
                _navigating = true;
                _list.SelectedIndex = SelectedIndex;
                _navigating = false;
                if (SelectedIndex >= 0 && SelectedIndex < _list.Items.Count)
                    _list.ScrollIntoView(_list.Items[SelectedIndex]);
                // Do NOT focus the list – DarkCombo keeps focus so our
                // OnPreviewKeyDown handles all keyboard events reliably.
            };

            Loaded += (s,e) => { _popup.PlacementTarget = box; };

            box.MouseLeftButtonDown += (s,e) =>
            {
                this.Focus();   // keep focus on DarkCombo, not on popup
                OpenPopup();
                e.Handled = true;
            };

            Content = box;
        }

        void OpenPopup()
        {
            _popup.Width  = Math.Max(ActualWidth, 150);
            _popup.IsOpen = true;
        }

        // All key routing happens here because DarkCombo holds focus
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (_popup.IsOpen)
            {
                int idx = _list.SelectedIndex < 0 ? 0 : _list.SelectedIndex;
                int max = _list.Items.Count - 1;
                switch (e.Key)
                {
                    case Key.Down:      Navigate(Math.Min(idx + 1, max)); e.Handled=true; return;
                    case Key.Up:        Navigate(Math.Max(idx - 1, 0));   e.Handled=true; return;
                    case Key.PageDown:  Navigate(Math.Min(idx + 8, max)); e.Handled=true; return;
                    case Key.PageUp:    Navigate(Math.Max(idx - 8, 0));   e.Handled=true; return;
                    case Key.Home:      Navigate(0);   e.Handled=true; return;
                    case Key.End:       Navigate(max); e.Handled=true; return;
                    case Key.Return:
                    case Key.Space:
                        if (_list.SelectedIndex >= 0) Commit(_list.SelectedIndex);
                        e.Handled=true; return;
                    case Key.Escape:
                        _popup.IsOpen=false; e.Handled=true; return;
                }
            }
            else
            {
                if (e.Key==Key.Down || e.Key==Key.Up || e.Key==Key.F4)
                {
                    OpenPopup(); e.Handled=true; return;
                }
            }
            base.OnPreviewKeyDown(e);
        }

        void Navigate(int newIdx)
        {
            _navigating = true;
            _list.SelectedIndex = newIdx;
            _navigating = false;
            if (newIdx >= 0 && newIdx < _list.Items.Count)
                _list.ScrollIntoView(_list.Items[newIdx]);
        }

        void Commit(int idx)
        {
            _popup.IsOpen = false;
            if (idx == SelectedIndex) return;
            SelectedIndex = idx;
            UpdateLabel();
            SelectionChanged?.Invoke(idx);
        }

        void UpdateLabel()
        {
            if (SelectedIndex >= 0 && SelectedIndex < _items.Count)
            {
                _label.Text       = _items[SelectedIndex].text;
                _label.Foreground = _items[SelectedIndex].highlighted ? FG_HIGHLIGHT : FG;
            }
            else { _label.Text=""; _label.Foreground=FG; }
        }

        public void SetSelectedIndex(int idx)
        {
            SelectedIndex = idx >= 0 && idx < _items.Count ? idx : -1;
            UpdateLabel();
        }

        public void ClearItems()
        {
            _items.Clear(); _list.Items.Clear();
            SelectedIndex=-1; _label.Text=""; _label.Foreground=FG;
        }

        public void AddItem(string s, bool highlighted=false)
        {
            _items.Add((s, highlighted));
            _list.Items.Add(new ListBoxItem { Content=s, Foreground=highlighted?FG_HIGHLIGHT:FG });
        }

        public int IndexOf(string s) => _items.FindIndex(x => x.text == s);

        static Style MakeItemStyle()
        {
            var s=new Style(typeof(ListBoxItem));
            s.Setters.Add(new Setter(ListBoxItem.BackgroundProperty,      Mkb("#2D2D2D")));
            s.Setters.Add(new Setter(ListBoxItem.BorderThicknessProperty, new Thickness(0)));
            s.Setters.Add(new Setter(ListBoxItem.PaddingProperty,         new Thickness(4,2,4,2)));
            var t1=new Trigger{Property=ListBoxItem.IsMouseOverProperty,Value=true};
            t1.Setters.Add(new Setter(ListBoxItem.BackgroundProperty,Mkb("#3A3A3A")));
            s.Triggers.Add(t1);
            var t2=new Trigger{Property=ListBoxItem.IsSelectedProperty,Value=true};
            t2.Setters.Add(new Setter(ListBoxItem.BackgroundProperty,Mkb("#44AADD")));
            t2.Setters.Add(new Setter(ListBoxItem.ForegroundProperty,Mkb("#FFFFFF")));
            s.Triggers.Add(t2);
            return s;
        }
    }

    // =========================================================================
    // Settings window
    // =========================================================================
    public class SettingsWindow : Window
    {
        readonly PeerCtrlMachine _machine;
        PeerCtrlState _snapshot;
        int  _selTrack = 0, _selAssignment = -1;
        bool _suppress = false;

        DarkCombo  _trackCombo, _machineCombo, _paramCombo, _targetTrackCombo;
        DarkCombo  _midiCtrlCombo, _midiChanCombo;
        CheckBox   _midiFeedbackCheck, _midiIncDecCheck, _midiWrapCheck;
        ListBox    _assignmentList;
        TextBlock  _paramInfoText;
        CurveCanvas _curveEditor;
        TextBox    _midiIncDecAmtBox, _sendFreqBox;
        CheckBox   _stopOnMuteCheck;
        Button     _learnBtn;
        bool       _learning;

        public SettingsWindow(PeerCtrlMachine machine)
        {
            _machine = machine;
            Title    = "Pedal Peer Control – Assignment Settings";
            Width    = 860; Height = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Br("#1E1E1E");
            Content    = Build();
            Snapshot();
            Populate();
            _machine.MidiCCReceived += OnMidiLearn;
            Closed += (s, e) => { _machine.MidiCCReceived -= OnMidiLearn; StopLearn(); };
        }

        // ── Layout ────────────────────────────────────────────────────────────

        UIElement Build()
        {
            var root = new Grid { Margin = new Thickness(8) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SetRow(root, 0, BuildMain());
            var cfg = GBox("Machine Configuration", BuildConfig()); cfg.Margin = new Thickness(0,6,0,0);
            SetRow(root, 1, cfg);
            SetRow(root, 2, BuildButtons());
            return root;
        }

        UIElement BuildMain()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            SetCol(grid, 0, BuildLeft());
            SetCol(grid, 1, new GridSplitter { Width=5, HorizontalAlignment=HorizontalAlignment.Stretch, Background=Br("#444") });
            SetCol(grid, 2, new ScrollViewer { VerticalScrollBarVisibility=ScrollBarVisibility.Auto, Content=BuildRight(), Margin=new Thickness(8,0,0,0) });
            return grid;
        }

        UIElement BuildLeft()
        {
            _trackCombo = DC();
            _trackCombo.SelectionChanged += _ => { if (!_suppress) { _selTrack = _trackCombo.SelectedIndex; _selAssignment = -1; RefreshList(); } };

            _assignmentList = new ListBox { Background=Br("#252525"), Foreground=Br("#DDD"), BorderBrush=Br("#555"), Margin=new Thickness(0,0,0,4) };
            _assignmentList.SelectionChanged += (s,e) => { if (!_suppress) { _selAssignment=_assignmentList.SelectedIndex; LoadEditor(Current()); } };

            var btns = new StackPanel { Orientation=Orientation.Horizontal };
            btns.Children.Add(Btn("Add",    (s,e) => { _machine.GetTrack(_selTrack).Assignments.Add(new TrackAssignment()); _selAssignment=_machine.GetTrack(_selTrack).Assignments.Count-1; RefreshTrackCombo(); RefreshList(); }));
            btns.Children.Add(Btn("Delete", (s,e) => { var ts=_machine.GetTrack(_selTrack); if (_selAssignment<0||_selAssignment>=ts.Assignments.Count) return; ts.Assignments.RemoveAt(_selAssignment); _selAssignment=Math.Min(_selAssignment,ts.Assignments.Count-1); RefreshTrackCombo(); RefreshList(); }));
            btns.Children.Add(Btn("Clear",  (s,e) => { _machine.GetTrack(_selTrack).Assignments.Clear(); _selAssignment=-1; RefreshTrackCombo(); RefreshList(); }));

            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            g.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
            g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var hdr = new StackPanel{Margin=new Thickness(0,0,0,4)}; hdr.Children.Add(Lbl("Track:")); hdr.Children.Add(_trackCombo);
            SetRow(g,0,hdr); SetRow(g,1,_assignmentList); SetRow(g,2,btns);
            return g;
        }

        UIElement BuildRight()
        {
            var sp = new StackPanel();

            _machineCombo     = DC(); _machineCombo.SelectionChanged     += _ => MachineChanged();
            _paramCombo       = DC(); _paramCombo.SelectionChanged       += _ => ParamChanged();
            _targetTrackCombo = DC(); _targetTrackCombo.SelectionChanged += _ => { if (!_suppress){var a=Current();if(a!=null)a.TargetTrack=_targetTrackCombo.SelectedIndex-1;} };
            sp.Children.Add(GBox("Target", LblGrid(new[]{("Machine:",(UIElement)_machineCombo),("Parameter:",(UIElement)_paramCombo),("Track:",(UIElement)_targetTrackCombo)})));

            _midiCtrlCombo = DC(); _midiCtrlCombo.SelectionChanged += _ => { if(!_suppress){var a=Current();if(a!=null)a.MidiController=_midiCtrlCombo.SelectedIndex-1;} };
            _midiChanCombo = DC(); _midiChanCombo.SelectionChanged += _ => { if(!_suppress){var a=Current();if(a==null)return;int i=_midiChanCombo.SelectedIndex;a.MidiChannel=i==0?0:i==_midiChanCombo.Count-1?17:i;} };
            _midiFeedbackCheck = Chk("Feedback",        (s,e)=>{if(!_suppress){var a=Current();if(a!=null)a.MidiFeedback=_midiFeedbackCheck.IsChecked==true;}});
            _midiIncDecCheck   = Chk("Inc/Dec (96/97)", (s,e)=>{if(!_suppress){var a=Current();if(a!=null)a.MidiIncDec=_midiIncDecCheck.IsChecked==true;}});
            _midiWrapCheck     = Chk("Wrap",            (s,e)=>{if(!_suppress){var a=Current();if(a!=null)a.MidiIncDecWrap=_midiWrapCheck.IsChecked==true;}});

            _learnBtn = Btn("Learn", (s,e) => { if (_learning) StopLearn(); else StartLearn(); });

            var mRow1=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,2,0,2)};
            mRow1.Children.Add(Lbl("Controller:")); mRow1.Children.Add(_midiCtrlCombo); mRow1.Children.Add(new StackPanel{Width=8}); mRow1.Children.Add(Lbl("Channel:")); mRow1.Children.Add(_midiChanCombo); mRow1.Children.Add(new StackPanel{Width=8}); mRow1.Children.Add(_learnBtn);
            var mRow2=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,2,0,0)};
            mRow2.Children.Add(_midiFeedbackCheck); mRow2.Children.Add(new StackPanel{Width=12}); mRow2.Children.Add(_midiIncDecCheck); mRow2.Children.Add(new StackPanel{Width=12}); mRow2.Children.Add(_midiWrapCheck);
            var mPanel=new StackPanel(); mPanel.Children.Add(mRow1); mPanel.Children.Add(mRow2);
            sp.Children.Add(GBox("MIDI CC", mPanel));

            _curveEditor = new CurveCanvas{Height=160};
            var resetBtn=Btn("Reset to Linear",(s,e)=>{var a=Current();if(a==null)return;a.Mapping.Reset();_curveEditor.SetEnv(a.Mapping);});
            resetBtn.HorizontalAlignment=HorizontalAlignment.Right;
            var hint=new TextBlock{Text="Left-click=add  Right-click=remove  Drag=move",Foreground=Br("#666"),FontSize=10,VerticalAlignment=VerticalAlignment.Center};
            var cBot=new DockPanel{Margin=new Thickness(0,4,0,0)}; DockPanel.SetDock(resetBtn,Dock.Right); cBot.Children.Add(resetBtn); cBot.Children.Add(hint);
            var cPanel=new StackPanel(); cPanel.Children.Add(new Border{BorderBrush=Br("#555"),BorderThickness=new Thickness(1),Background=Br("#1A1A1A"),Child=_curveEditor}); cPanel.Children.Add(cBot);
            sp.Children.Add(GBox("Mapping (non-linear curve)", cPanel));

            _paramInfoText=new TextBlock{Text="(select a parameter)",Foreground=Br("#888"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4),FontFamily=new FontFamily("Consolas"),FontSize=11};
            sp.Children.Add(GBox("Parameter Info", _paramInfoText));

            var upd=Btn("Update Assignment",(s,e)=>{var a=Current();if(a!=null)_machine.ResolveAssignment(a);RefreshList();}); upd.HorizontalAlignment=HorizontalAlignment.Right; upd.Margin=new Thickness(0,4,0,0);
            sp.Children.Add(upd);
            return sp;
        }

        UIElement BuildConfig()
        {
            _midiIncDecAmtBox=new TextBox{Width=64,Background=Br("#2D2D2D"),Foreground=Br("#DDD"),BorderBrush=Br("#555"),CaretBrush=Br("#DDD")};
            _midiIncDecAmtBox.LostFocus+=(s,e)=>{if(int.TryParse(_midiIncDecAmtBox.Text,out int v))_machine.MidiIncDecAmount=Math.Max(0,Math.Min(65534,v));_midiIncDecAmtBox.Text=_machine.MidiIncDecAmount.ToString();};
            _sendFreqBox=new TextBox{Width=40,Background=Br("#2D2D2D"),Foreground=Br("#DDD"),BorderBrush=Br("#555"),CaretBrush=Br("#DDD")};
            _sendFreqBox.LostFocus+=(s,e)=>{if(int.TryParse(_sendFreqBox.Text,out int v))_machine.SendFreq=Math.Max(0,Math.Min(64,v));_sendFreqBox.Text=_machine.SendFreq.ToString();};
            _stopOnMuteCheck=Chk("Stop on mute",(s,e)=>{if(!_suppress)_machine.StopOnMute=_stopOnMuteCheck.IsChecked==true?1:0;});
            var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(4)};
            row.Children.Add(Lbl("MIDI Inc/Dec Amount:")); row.Children.Add(_midiIncDecAmtBox); row.Children.Add(new StackPanel{Width=16});
            row.Children.Add(Lbl("Send Freq (0=tick only):")); row.Children.Add(_sendFreqBox); row.Children.Add(new StackPanel{Width=16});
            row.Children.Add(_stopOnMuteCheck);
            return row;
        }

        UIElement BuildButtons()
        {
            var row=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};
            row.Children.Add(Btn("OK",(s,e)=>{_machine.ResolveAllMachines();DialogResult=true;Close();}));
            row.Children.Add(new StackPanel{Width=6});
            row.Children.Add(Btn("Cancel",(s,e)=>{_machine.MachineState=_snapshot;DialogResult=false;Close();}));
            return row;
        }

        // ── Data ──────────────────────────────────────────────────────────────

        void Snapshot() => _snapshot = DeepCopy(_machine.MachineState);

        void Populate()
        {
            _suppress = true;
            RefreshTrackCombo();
            _trackCombo.SetSelectedIndex(0);

            // Machines sorted A-Z (change 5)
            _machineCombo.ClearItems(); _machineCombo.AddItem("(none)");
            var buzz = _machine.BuzzHost;
            if (buzz?.Song?.Machines != null)
            {
                var self = _machine.Host?.Machine;
                foreach (var m in buzz.Song.Machines
                             .Where(m => m != self)
                             .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                    _machineCombo.AddItem(m.Name);
            }

            _midiCtrlCombo.ClearItems(); _midiCtrlCombo.AddItem("Off");
            for (int i=0;i<=127;i++) _midiCtrlCombo.AddItem(i.ToString());
            _midiChanCombo.ClearItems(); _midiChanCombo.AddItem("Off");
            for (int i=1;i<=16;i++) _midiChanCombo.AddItem(i.ToString());
            _midiChanCombo.AddItem("All");

            _midiIncDecAmtBox.Text=_machine.MidiIncDecAmount.ToString();
            _sendFreqBox.Text=_machine.SendFreq.ToString();
            _stopOnMuteCheck.IsChecked=_machine.StopOnMute==1;
            _suppress=false;
            RefreshList();
        }

        /// <summary>
        /// Rebuild the track dropdown, colouring tracks that have assignments
        /// bright green so free slots are immediately visible (change 3).
        /// </summary>
        void RefreshTrackCombo()
        {
            int prevSel = _trackCombo.SelectedIndex;
            _trackCombo.ClearItems();
            int n = Math.Max(1, _machine.ActiveTrackCount);
            for (int t = 0; t < n; t++)
            {
                bool hasAssignments = _machine.GetTrack(t).Assignments.Count > 0;
                _trackCombo.AddItem($"Track {t+1}", highlighted: hasAssignments);
            }
            _trackCombo.SetSelectedIndex(prevSel >= 0 && prevSel < n ? prevSel : 0);
        }

        void RefreshList()
        {
            _suppress=true;
            _assignmentList.Items.Clear();
            var ts=_machine.GetTrack(_selTrack);
            foreach(var a in ts.Assignments) _assignmentList.Items.Add(a.Label);
            if (_selAssignment>=ts.Assignments.Count) _selAssignment=ts.Assignments.Count-1;
            if (_selAssignment>=0) _assignmentList.SelectedIndex=_selAssignment;
            _suppress=false;
            LoadEditor(Current());
        }

        TrackAssignment Current()
        {
            if (_selAssignment<0) return null;
            var ts=_machine.GetTrack(_selTrack);
            return _selAssignment<ts.Assignments.Count ? ts.Assignments[_selAssignment] : null;
        }

        void LoadEditor(TrackAssignment a)
        {
            _suppress=true;
            if (a==null)
            {
                _machineCombo.SetSelectedIndex(0); _paramCombo.ClearItems(); _targetTrackCombo.ClearItems();
                _midiCtrlCombo.SetSelectedIndex(0); _midiChanCombo.SetSelectedIndex(0);
                _midiFeedbackCheck.IsChecked=false; _midiIncDecCheck.IsChecked=false; _midiWrapCheck.IsChecked=false;
                _curveEditor.SetEnv(null); _paramInfoText.Text="(no assignment selected)";
                _suppress=false; return;
            }
            int mIdx=_machineCombo.IndexOf(a.MachineName); _machineCombo.SetSelectedIndex(mIdx>0?mIdx:0);
            RefreshParamCombos(a);
            _midiCtrlCombo.SetSelectedIndex(a.MidiController<0?0:a.MidiController+1);
            _midiChanCombo.SetSelectedIndex(a.MidiChannel==0?0:a.MidiChannel==17?_midiChanCombo.Count-1:a.MidiChannel);
            _midiFeedbackCheck.IsChecked=a.MidiFeedback; _midiIncDecCheck.IsChecked=a.MidiIncDec; _midiWrapCheck.IsChecked=a.MidiIncDecWrap;
            _curveEditor.SetEnv(a.Mapping); UpdateParamInfo(a);
            _suppress=false;
        }

        void RefreshParamCombos(TrackAssignment a)
        {
            _paramCombo.ClearItems(); _targetTrackCombo.ClearItems();
            _paramCombo.AddItem("(none)"); _targetTrackCombo.AddItem("All tracks");
            IMachine mac=null;
            if (a!=null&&!string.IsNullOrEmpty(a.MachineName))
                mac=_machine.BuzzHost?.Song?.Machines?.FirstOrDefault(m=>m.Name==a.MachineName);
            if (mac!=null)
            {
                var allp=PeerCtrlMachine.GetAllParams(mac);
                foreach(var p in allp) _paramCombo.AddItem(p.Name);
                _paramCombo.SetSelectedIndex((a.ParamIndex>=0&&a.ParamIndex<allp.Count)?a.ParamIndex+1:0);
                for(int t=0;t<mac.TrackCount;t++) _targetTrackCombo.AddItem($"Track {t+1}");
                _targetTrackCombo.SetSelectedIndex(a.TargetTrack>=0?a.TargetTrack+1:0);
            }
            else { _paramCombo.SetSelectedIndex(0); _targetTrackCombo.SetSelectedIndex(0); }
        }

        void UpdateParamInfo(TrackAssignment a)
        {
            if (a?.ResolvedParam==null){_paramInfoText.Text="(parameter not resolved)";return;}
            var p=a.ResolvedParam;
            _paramInfoText.Text=$"Name:  {p.Name}\nRange: {p.MinValue} – {p.MaxValue}\nType:  {p.Type}\nGroup: {p.Group?.Type}";
        }

        void MachineChanged()
        {
            if (_suppress) return;
            var a=Current(); if(a==null)return;
            a.MachineName=_machineCombo.SelectedIndex<=0?"":_machineCombo.SelectedItem??"";
            _machine.ResolveAssignment(a); RefreshParamCombos(a); UpdateParamInfo(a); RefreshList();
        }

        void ParamChanged()
        {
            if (_suppress) return;
            var a=Current(); if(a==null)return;
            a.ParamIndex=_paramCombo.SelectedIndex-1;
            _machine.ResolveAssignment(a); UpdateParamInfo(a); RefreshList();
        }

        // ── MIDI Learn ─────────────────────────────────────────────────────────

        void StartLearn()
        {
            if (Current() == null) return;   // need an assignment selected
            _learning = true;
            _learnBtn.Content    = "Cancel";
            _learnBtn.Background = Br("#AA3333");
        }

        void StopLearn()
        {
            _learning = false;
            if (_learnBtn != null)
            {
                _learnBtn.Content    = "Learn";
                _learnBtn.Background = Br("#3A3A3A");
            }
        }

        // Called from machine's audio thread – marshal to UI thread
        void OnMidiLearn(int ctrl, int channel, int value)
        {
            if (!_learning) return;
            // BeginInvoke: never block the MIDI thread on the dialog thread.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_learning) return;
                var a = Current();
                if (a == null) { StopLearn(); return; }

                // Inc/Dec encoders send CC 96 (inc) / 97 (dec) with their own
                // controller number as the value. Otherwise 96/97 are learned
                // as ordinary absolute CCs, like any other controller.
                int learned = (a.MidiIncDec && (ctrl == 96 || ctrl == 97)) ? value : ctrl;

                _suppress = true;
                a.MidiController = learned;
                a.MidiChannel    = channel + 1;   // store as 1-based
                _midiCtrlCombo.SetSelectedIndex(learned + 1);
                _midiChanCombo.SetSelectedIndex(channel + 1);
                _suppress = false;

                StopLearn();
            }));
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        static DarkCombo DC() => new DarkCombo();
        static SolidColorBrush Br(string hex) { hex=hex.TrimStart('#'); if(hex.Length==3)hex=$"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}"; var b=new SolidColorBrush(Color.FromRgb(Convert.ToByte(hex.Substring(0,2),16),Convert.ToByte(hex.Substring(2,2),16),Convert.ToByte(hex.Substring(4,2),16))); b.Freeze(); return b; }
        static TextBlock Lbl(string t)=>new TextBlock{Text=t,Foreground=Br("#DDD"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,4,0)};
        static Button Btn(string t,RoutedEventHandler h){var b=new Button{Content=t,Background=Br("#3A3A3A"),Foreground=Br("#DDD"),BorderBrush=Br("#555"),Padding=new Thickness(8,3,8,3),MinWidth=64,Margin=new Thickness(0,0,4,0)};b.Click+=h;return b;}
        static CheckBox Chk(string t,RoutedEventHandler h){var c=new CheckBox{Content=t,Foreground=Br("#DDD"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)};c.Checked+=h;c.Unchecked+=h;return c;}
        static GroupBox GBox(string hdr,UIElement content)=>new GroupBox{Header=hdr,Foreground=Br("#AAA"),BorderBrush=Br("#555"),Content=content,Margin=new Thickness(0,0,0,6),Padding=new Thickness(4)};
        static Grid LblGrid((string label,UIElement ctrl)[] rows){var g=new Grid{Margin=new Thickness(4)};g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(85)});g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});foreach(var _ in rows)g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});for(int i=0;i<rows.Length;i++){var l=Lbl(rows[i].label);Grid.SetRow(l,i);Grid.SetColumn(l,0);g.Children.Add(l);Grid.SetRow(rows[i].ctrl,i);Grid.SetColumn(rows[i].ctrl,1);g.Children.Add(rows[i].ctrl);}return g;}
        static void SetRow(Grid g,int r,UIElement e){Grid.SetRow(e,r);g.Children.Add(e);}
        static void SetCol(Grid g,int c,UIElement e){Grid.SetColumn(e,c);g.Children.Add(e);}
        static PeerCtrlState DeepCopy(PeerCtrlState src){var xs=new XmlSerializer(typeof(PeerCtrlState));using var ms=new MemoryStream();xs.Serialize(ms,src);ms.Position=0;return(PeerCtrlState)xs.Deserialize(ms);}
    }

    // =========================================================================
    // CurveCanvas
    // =========================================================================
    public class CurveCanvas : FrameworkElement
    {
        EnvData _env;
        const double DOT_R=5,HIT_R=8;
        int _dragIdx=-1,_hoverIdx=-1; bool _dragging;
        static readonly Pen PC=MkP(0x44,0xBB,0xFF,1.5),PG=MkP(0x33,0x33,0x33,0.5),PA=MkP(0x55,0x55,0x55,1.0);
        static readonly Brush BD=MkB(0x44,0xBB,0xFF),BH=MkB(0xFF,0xCC,0x44);
        static Pen MkP(byte r,byte g,byte b,double t){var p=new Pen(new SolidColorBrush(Color.FromRgb(r,g,b)),t);p.Freeze();return p;}
        static Brush MkB(byte r,byte g,byte b){var br=new SolidColorBrush(Color.FromRgb(r,g,b));br.Freeze();return br;}
        public CurveCanvas(){ClipToBounds=true;}
        public void SetEnv(EnvData env){_env=env;InvalidateVisual();}
        Point ToC(EnvPoint p)=>new Point(p.X/65535.0*ActualWidth,(1-p.Y/65535.0)*ActualHeight);
        EnvPoint FromC(Point pt)=>new EnvPoint{X=(int)Math.Round(pt.X/ActualWidth*65535),Y=(int)Math.Round((1-pt.Y/ActualHeight)*65535)};
        // Ensure Points is initialised before any mutation (handles null default)
        List<EnvPoint> Pts() {
            if (_env.Points == null) _env.Points = EnvData.DefaultPoints();
            return _env.Points;
        }

        int Hit(Point pt){if(_env==null)return -1;var pts=_env.Points??EnvData.DefaultPoints();for(int i=0;i<pts.Count;i++){var c=ToC(pts[i]);double dx=pt.X-c.X,dy=pt.Y-c.Y;if(dx*dx+dy*dy<=HIT_R*HIT_R)return i;}return -1;}
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e){if(_env==null)return;var pos=e.GetPosition(this);int h=Hit(pos);if(h>=0){_dragIdx=h;_dragging=true;}else{var pts=Pts();var np=FromC(pos);np.X=Math.Max(1,Math.Min(65534,np.X));np.Y=Math.Max(0,Math.Min(65535,np.Y));pts.Add(np);pts.Sort((a,b)=>a.X.CompareTo(b.X));_dragIdx=pts.FindIndex(p=>p.X==np.X&&p.Y==np.Y);_dragging=true;InvalidateVisual();}CaptureMouse();}
        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            if (_env == null) return;
            var pos = e.GetPosition(this);
            int hit = Hit(pos);

            // Remove a point if right-clicking directly on one (interior points only)
            if (hit > 0 && hit < Pts().Count - 1)
            {
                Pts().RemoveAt(hit);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Otherwise show context menu with curve operations
            var menu = new ContextMenu();
            menu.Background  = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
            menu.Foreground  = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            menu.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

            var miMirror = new MenuItem { Header = "Mirror (flip horizontal)" };
            miMirror.Background = menu.Background; miMirror.Foreground = menu.Foreground;
            miMirror.Click += (s, _) => {
                foreach (var p in Pts()) p.X = 65535 - p.X;
                Pts().Sort((a, b) => a.X.CompareTo(b.X));
                InvalidateVisual();
            };

            var miInvert = new MenuItem { Header = "Invert (flip vertical)" };
            miInvert.Background = menu.Background; miInvert.Foreground = menu.Foreground;
            miInvert.Click += (s, _) => {
                foreach (var p in Pts()) p.Y = 65535 - p.Y;
                InvalidateVisual();
            };

            var miReset = new MenuItem { Header = "Reset to linear" };
            miReset.Background = menu.Background; miReset.Foreground = menu.Foreground;
            miReset.Click += (s, _) => { _env.Reset(); InvalidateVisual(); };

            menu.Items.Add(miMirror);
            menu.Items.Add(miInvert);
            menu.Items.Add(new Separator());
            menu.Items.Add(miReset);
            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }
        protected override void OnMouseMove(MouseEventArgs e){var pos=e.GetPosition(this);_hoverIdx=Hit(pos);if(_dragging&&_dragIdx>=0&&_env!=null){var pts=Pts();var np=FromC(pos);np.Y=Math.Max(0,Math.Min(65535,np.Y));if(_dragIdx==0)np.X=0;else if(_dragIdx==pts.Count-1)np.X=65535;else{int xMin=pts[_dragIdx-1].X+1,xMax=pts[_dragIdx+1].X-1;np.X=Math.Max(xMin,Math.Min(xMax,np.X));}pts[_dragIdx]=np;}InvalidateVisual();}
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e){_dragging=false;ReleaseMouseCapture();}
        protected override void OnRender(DrawingContext dc){double w=ActualWidth,h=ActualHeight;if(w<=0||h<=0)return;for(int i=1;i<4;i++){dc.DrawLine(PG,new Point(w*i/4,0),new Point(w*i/4,h));dc.DrawLine(PG,new Point(0,h*i/4),new Point(w,h*i/4));}dc.DrawLine(PA,new Point(w/2,0),new Point(w/2,h));dc.DrawLine(PA,new Point(0,h/2),new Point(w,h/2));if(_env==null)return;var pts2=_env.Points??EnvData.DefaultPoints();if(pts2.Count<2)return;var geo=new StreamGeometry();using(var ctx=geo.Open()){ctx.BeginFigure(ToC(pts2[0]),false,false);for(int i=1;i<pts2.Count;i++)ctx.LineTo(ToC(pts2[i]),true,false);}geo.Freeze();dc.DrawGeometry(null,PC,geo);for(int i=0;i<pts2.Count;i++)dc.DrawEllipse(i==_hoverIdx?BH:BD,null,ToC(pts2[i]),DOT_R,DOT_R);}
        protected override Size MeasureOverride(Size a)=>new Size(Math.Min(a.Width,9999),Math.Min(a.Height,9999));
    }
}
