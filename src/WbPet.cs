// WorkBuddy usage pet panel - WPF, per-pixel transparent window.
// Build: build-pet.cmd  (csc /target:winexe, no console window)
//
// Behaviour:
//   - loops the APNG frames of the active skin (assets/skins/<id>/)
//   - dark speech bubble with usage tiles; shown on hover and for 6s after a turn
//   - emotion layer: reads pet-events.txt (written by the Stop hook), renders a
//     persona-styled line + accent colour + motion, with throttle and dedupe
//   - ambient rules on its own clock: late night / long session / nap / sit too long
//   - right-click: skins, persona, verbosity, mute 1h, hide 10min, exit
//   - remembers position / persona / verbosity / event offset in pet-state.txt
//
// NOTE: this file must stay UTF-8 **with BOM**. csc reads BOM-less files as ANSI,
// which corrupts the Chinese literals and emoji below.

// System.Windows.Shapes.Path and System.IO.Path share a simple name, so
// importing both namespaces makes every "Path" reference ambiguous (CS0104).
// The shape type is therefore imported under an alias (alias directives must
// precede all plain using directives).
using ShapePath = System.Windows.Shapes.Path;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WbPet
{
    public class PetWindow : Window
    {
        private static readonly Brush BgBrush = MakeBrush(0x1B, 0x1E, 0x26);
        private static readonly Brush BorderBrushDark = MakeBrush(0x33, 0x39, 0x49);
        private static readonly Brush ValueBrush = MakeBrush(0xED, 0xEF, 0xF5);
        private static readonly Brush CaptionBrush = MakeBrush(0x6B, 0x73, 0x85);
        private static readonly Brush TitleBrush = MakeBrush(0x8A, 0x93, 0xA8);
        private static readonly Brush TimeBrush = MakeBrush(0x5C, 0x64, 0x78);
        private static readonly Brush EmotionBrush = MakeBrush(0xE8, 0xEA, 0xF0);

        private sealed class PersonaDef
        {
            public string Id = "";
            public string Name = "";
            public string Accent = "#F9A8D4";
        }

        private sealed class PetEvent
        {
            public int Seq;
            public long Ts;
            public string TurnId = "";
            public string Emotion = "calm";
            public int Pri = 2;
            public string Motion = "none";
            public string Accent = "#333949";
            public string Title = "";
            public string Text = "";
            public int DurSec = 6;
        }

        private readonly string _baseDir;
        private readonly string _dataFile;
        private readonly string _assetsDir;
        private readonly string _skinsDir;
        private readonly string _catalogFile;
        private readonly string _currentFile;
        private readonly string _statusFile;
        private readonly string _switchScript;
        private readonly string _eventsFile;
        private readonly string _stateFile;
        private readonly string _ledgerFile;
        private readonly string _personasFile;

        private readonly List<BitmapImage> _frames = new List<BitmapImage>();
        private readonly List<int> _delays = new List<int>();
        private readonly Dictionary<string, MenuItem> _skinItems = new Dictionary<string, MenuItem>();
        private readonly Dictionary<string, PersonaDef> _personaMap = new Dictionary<string, PersonaDef>();
        private readonly Dictionary<string, List<string[]>> _lineMap = new Dictionary<string, List<string[]>>();
        private readonly Dictionary<string, DateTime> _emoCool = new Dictionary<string, DateTime>();
        private readonly List<DateTime> _recentBubbles = new List<DateTime>();

        private string _loadedSkin = string.Empty;
        private string _persona = "cute";
        private string _verbosity = "normal";
        private long _eventOffset;
        private int _lastSeq;
        private string _lastTurnId = string.Empty;
        private DateTime _quietUntil = DateTime.MinValue;
        private DateTime _lastBubbleAt = DateTime.MinValue;
        private PetEvent _pendingEvent;
        private DateTime _pendingUntil = DateTime.MinValue;
        private bool _sleepMode;
        private DateTime _napSince = DateTime.MinValue;
        private DateTime _lastNapWake = DateTime.MinValue;
        private DateTime _nightAnnounced = DateTime.MinValue;
        private DateTime _longSessionAnnounced = DateTime.MinValue;
        private DateTime _sitAnnounced = DateTime.MinValue;
        private DateTime _lastSaveAt = DateTime.MinValue;

        private int _idx = 0;
        private bool _firstLoad = true;
        private DateTime _lastWrite = DateTime.MinValue;
        private DateTime _tempUntil = DateTime.MinValue;
        private DateTime _hideUntil = DateTime.MinValue;

        private Image _mascot;
        private Grid _bubble;
        private Border _bubbleBody;
        private ShapePath _tail;
        private Border _dot;
        private TextBlock _title;
        private TextBlock _timeText;
        private TextBlock _turnIn;
        private TextBlock _turnOut;
        private TextBlock _cache;
        private TextBlock _credit;
        private TextBlock _emotionText;
        private TextBlock _todayValue;
        private TextBlock _todaySessions;
        private TextBlock _footer;
        private ScaleTransform _scale;
        private TranslateTransform _shift;

        private DispatcherTimer _frameTimer;
        private DispatcherTimer _dataTimer;
        private DispatcherTimer _hideTimer;
        private DispatcherTimer _lifeTimer;
        private DispatcherTimer _skinTimer;
        private DispatcherTimer _eventTimer;
        private DispatcherTimer _clockTimer;

        private static Brush MakeBrush(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public PetWindow(string baseDir, string dataFile)
        {
            _baseDir = baseDir;
            _dataFile = dataFile;
            _assetsDir = Path.Combine(baseDir, "assets");
            _skinsDir = Path.Combine(_assetsDir, "skins");
            _catalogFile = Path.Combine(_assetsDir, "skins.json");
            _currentFile = Path.Combine(_skinsDir, "current.txt");
            _statusFile = Path.Combine(_skinsDir, "status.txt");
            _switchScript = Path.Combine(Path.Combine(baseDir, "tools"), "switch-skin.js");
            _eventsFile = Path.Combine(baseDir, "pet-events.txt");
            _stateFile = Path.Combine(baseDir, "pet-state.txt");
            _ledgerFile = Path.Combine(baseDir, "pet-ledger.txt");
            _personasFile = Path.Combine(_assetsDir, "personas.txt");

            Width = 340;
            Height = 448;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            FontFamily = new FontFamily("Microsoft YaHei UI");

            Rect wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 16;
            Top = wa.Bottom - Height - 8;

            LoadPersonas();
            LoadState();

            _loadedSkin = ReadCurrentSkinId();

            BuildUi();
            LoadFrames(ResolveFramesDir(_loadedSkin));
            SeekEventsToEnd();
            StartTimers();
            UpdatePanel();
        }

        // ---------- UI ----------
        private void BuildUi()
        {
            Grid root = new Grid();

            StackPanel stack = new StackPanel();
            stack.VerticalAlignment = VerticalAlignment.Bottom;
            stack.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Margin = new Thickness(0, 0, 0, 6);

            _bubble = new Grid();
            _bubble.Visibility = Visibility.Collapsed;
            _bubble.Margin = new Thickness(0, 0, 0, 8);
            _bubble.HorizontalAlignment = HorizontalAlignment.Center;

            // tail behind the body so its top edge is hidden
            _tail = new ShapePath();
            _tail.Fill = BgBrush;
            _tail.Stroke = BorderBrushDark;
            _tail.StrokeThickness = 1.2;
            _tail.Data = Geometry.Parse("M 0,0 L 26,0 L 13,13 Z");
            _tail.HorizontalAlignment = HorizontalAlignment.Center;
            _tail.VerticalAlignment = VerticalAlignment.Bottom;
            _tail.Margin = new Thickness(0, 0, 0, -13);
            _bubble.Children.Add(_tail);

            _bubbleBody = new Border();
            _bubbleBody.Background = BgBrush;
            _bubbleBody.BorderBrush = BorderBrushDark;
            _bubbleBody.BorderThickness = new Thickness(0.5);
            _bubbleBody.CornerRadius = new CornerRadius(16);
            _bubbleBody.Padding = new Thickness(15, 11, 15, 12);
            _bubbleBody.Width = 288;

            DropShadowEffect shadow = new DropShadowEffect();
            shadow.BlurRadius = 14;
            shadow.ShadowDepth = 4;
            shadow.Direction = 270;
            shadow.Opacity = 0.32;
            shadow.Color = Colors.Black;
            _bubbleBody.Effect = shadow;

            StackPanel inner = new StackPanel();

            // header: emotion dot + persona name (left), time (right)
            Grid header = new Grid();

            StackPanel who = new StackPanel();
            who.Orientation = Orientation.Horizontal;
            who.HorizontalAlignment = HorizontalAlignment.Left;
            who.VerticalAlignment = VerticalAlignment.Center;

            _dot = new Border();
            _dot.Width = 5;
            _dot.Height = 5;
            _dot.CornerRadius = new CornerRadius(2.5);
            _dot.Margin = new Thickness(0, 0, 6, 0);
            _dot.VerticalAlignment = VerticalAlignment.Center;
            _dot.Background = new SolidColorBrush(Color.FromRgb(0x6B, 0x7B, 0x9E));
            who.Children.Add(_dot);

            _title = new TextBlock();
            _title.Text = "WorkBuddy";
            _title.FontSize = 10.5;
            _title.Foreground = TitleBrush;
            _title.VerticalAlignment = VerticalAlignment.Center;
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            _title.MaxWidth = 170;
            TextOptions.SetTextFormattingMode(_title, TextFormattingMode.Display);
            who.Children.Add(_title);

            header.Children.Add(who);

            _timeText = new TextBlock();
            _timeText.FontSize = 10.5;
            _timeText.Foreground = TimeBrush;
            _timeText.HorizontalAlignment = HorizontalAlignment.Right;
            TextOptions.SetTextFormattingMode(_timeText, TextFormattingMode.Display);
            header.Children.Add(_timeText);

            inner.Children.Add(header);

            // emotion line: the main visual of the bubble
            _emotionText = new TextBlock();
            _emotionText.FontSize = 15;
            _emotionText.Foreground = EmotionBrush;
            _emotionText.TextWrapping = TextWrapping.Wrap;
            _emotionText.Margin = new Thickness(0, 8, 0, 12);
            _emotionText.MaxHeight = 70;
            _emotionText.TextTrimming = TextTrimming.CharacterEllipsis;
            _emotionText.Visibility = Visibility.Collapsed;
            TextOptions.SetTextFormattingMode(_emotionText, TextFormattingMode.Display);
            inner.Children.Add(_emotionText);

            inner.Children.Add(MakeDivider());

            // today's credits, the largest number on the card
            Grid today = new Grid();
            today.Margin = new Thickness(0, 11, 0, 12);

            StackPanel todayLeft = new StackPanel();
            todayLeft.Orientation = Orientation.Horizontal;
            todayLeft.HorizontalAlignment = HorizontalAlignment.Left;
            todayLeft.VerticalAlignment = VerticalAlignment.Bottom;

            TextBlock todayCap = new TextBlock();
            todayCap.Text = "\u4ECA\u65E5";
            todayCap.FontSize = 10;
            todayCap.Foreground = CaptionBrush;
            todayCap.VerticalAlignment = VerticalAlignment.Bottom;
            todayCap.Margin = new Thickness(0, 0, 6, 4);
            TextOptions.SetTextFormattingMode(todayCap, TextFormattingMode.Display);
            todayLeft.Children.Add(todayCap);

            _todayValue = new TextBlock();
            _todayValue.Text = "-";
            _todayValue.FontSize = 23;
            _todayValue.FontWeight = FontWeights.Medium;
            _todayValue.Foreground = ValueBrush;
            _todayValue.VerticalAlignment = VerticalAlignment.Bottom;
            _todayValue.Margin = new Thickness(0, 0, 5, 0);
            TextOptions.SetTextFormattingMode(_todayValue, TextFormattingMode.Display);
            todayLeft.Children.Add(_todayValue);

            TextBlock todayUnit = new TextBlock();
            todayUnit.Text = "\u79EF\u5206";
            todayUnit.FontSize = 10;
            todayUnit.Foreground = CaptionBrush;
            todayUnit.VerticalAlignment = VerticalAlignment.Bottom;
            todayUnit.Margin = new Thickness(0, 0, 0, 4);
            TextOptions.SetTextFormattingMode(todayUnit, TextFormattingMode.Display);
            todayLeft.Children.Add(todayUnit);

            today.Children.Add(todayLeft);

            _todaySessions = new TextBlock();
            _todaySessions.FontSize = 10.5;
            _todaySessions.Foreground = TimeBrush;
            _todaySessions.HorizontalAlignment = HorizontalAlignment.Right;
            _todaySessions.VerticalAlignment = VerticalAlignment.Bottom;
            _todaySessions.Margin = new Thickness(0, 0, 0, 4);
            TextOptions.SetTextFormattingMode(_todaySessions, TextFormattingMode.Display);
            today.Children.Add(_todaySessions);

            inner.Children.Add(today);

            inner.Children.Add(MakeDivider());

            // this turn's metrics: one compact row, icon + value only
            Grid metrics = new Grid();
            metrics.Margin = new Thickness(0, 10, 0, 0);
            for (int i = 0; i < 4; i++) { metrics.ColumnDefinitions.Add(new ColumnDefinition()); }

            _turnIn = AddMetric(metrics, 0, "M8,2 L14,9 L10.5,9 L10.5,14 L5.5,14 L5.5,9 L2,9 Z", "#5EEAD4", HorizontalAlignment.Left, "\u8F93\u5165 token");
            _turnOut = AddMetric(metrics, 1, "M8,14 L2,7 L5.5,7 L5.5,2 L10.5,2 L10.5,7 L14,7 Z", "#A78BFA", HorizontalAlignment.Center, "\u8F93\u51FA token");
            _cache = AddMetric(metrics, 2, "M9.5,1 L3,9 L7,9 L6.5,15 L13,7 L9,7 Z", "#FBBF24", HorizontalAlignment.Center, "\u7F13\u5B58\u547D\u4E2D\u7387");
            _credit = AddMetric(metrics, 3, "M8,2 L13,8 L8,14 L3,8 Z", "#34D399", HorizontalAlignment.Right, "\u672C\u8F6E\u79EF\u5206");

            inner.Children.Add(metrics);

            // footer only carries transient messages such as skin switching progress
            _footer = new TextBlock();
            _footer.FontSize = 10;
            _footer.Foreground = CaptionBrush;
            _footer.HorizontalAlignment = HorizontalAlignment.Center;
            _footer.TextWrapping = TextWrapping.Wrap;
            _footer.Margin = new Thickness(0, 7, 0, 0);
            _footer.Visibility = Visibility.Collapsed;
            TextOptions.SetTextFormattingMode(_footer, TextFormattingMode.Display);
            inner.Children.Add(_footer);

            _bubbleBody.Child = inner;
            _bubble.Children.Add(_bubbleBody);
            stack.Children.Add(_bubble);

            _mascot = new Image();
            _mascot.Width = 190;
            _mascot.Height = 208;
            _mascot.Stretch = Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(_mascot, BitmapScalingMode.HighQuality);

            _scale = new ScaleTransform(1, 1);
            _shift = new TranslateTransform(0, 0);
            TransformGroup group = new TransformGroup();
            group.Children.Add(_scale);
            group.Children.Add(_shift);
            _mascot.RenderTransform = group;
            _mascot.RenderTransformOrigin = new Point(0.5, 1);

            _mascot.MouseEnter += OnMascotEnter;
            _mascot.MouseLeave += OnMascotLeave;
            _mascot.MouseLeftButtonDown += OnMascotMouseDown;
            _mascot.MouseLeftButtonUp += OnMascotMouseUp;
            _mascot.ContextMenu = BuildMenu();
            stack.Children.Add(_mascot);

            _bubble.MouseEnter += OnMascotEnter;
            _bubble.MouseLeave += OnMascotLeave;

            root.Children.Add(stack);
            Content = root;

            LocationChanged += OnWindowMoved;
        }

        private static Border MakeDivider()
        {
            Border divider = new Border();
            divider.Height = 1;
            divider.Background = MakeBrush(0x26, 0x2B, 0x36);
            return divider;
        }

        /** 本轮指标：图标 + 数值横排一枚，鼠标悬停显示含义 */
        private TextBlock AddMetric(Grid grid, int col, string iconPath, string color, HorizontalAlignment align, string tip)
        {
            StackPanel cell = new StackPanel();
            cell.Orientation = Orientation.Horizontal;
            cell.HorizontalAlignment = align;
            cell.VerticalAlignment = VerticalAlignment.Center;
            cell.ToolTip = tip;
            Grid.SetColumn(cell, col);

            ShapePath icon = new ShapePath();
            icon.Data = Geometry.Parse(iconPath);
            icon.Stretch = Stretch.Uniform;
            icon.Width = 9;
            icon.Height = 9;
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 4, 0);
            icon.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            cell.Children.Add(icon);

            TextBlock value = new TextBlock();
            value.Text = "-";
            value.FontSize = 11;
            value.Foreground = MakeBrush(0x98, 0xA0, 0xB4);
            value.VerticalAlignment = VerticalAlignment.Center;
            TextOptions.SetTextFormattingMode(value, TextFormattingMode.Display);
            cell.Children.Add(value);

            grid.Children.Add(cell);
            return value;
        }

        private ContextMenu BuildMenu()
        {
            ContextMenu menu = new ContextMenu();

            MenuItem refresh = new MenuItem();
            refresh.Header = "Refresh";
            refresh.Click += delegate { UpdatePanel(); };
            menu.Items.Add(refresh);

            // skins
            List<string[]> skins = ReadCatalog();
            if (skins.Count > 0)
            {
                MenuItem skinRoot = new MenuItem();
                skinRoot.Header = "\u6362\u5F62\u8C61";
                foreach (string[] s in skins)
                {
                    MenuItem item = new MenuItem();
                    item.Header = s[1];
                    item.Tag = s[0];
                    if (s[0] == _loadedSkin)
                    {
                        item.IsChecked = true;
                        item.FontWeight = FontWeights.Bold;
                    }
                    item.Click += OnSkinClick;
                    _skinItems[s[0]] = item;
                    skinRoot.Items.Add(item);
                }
                menu.Items.Add(skinRoot);
            }

            // persona
            if (_personaMap.Count > 0)
            {
                MenuItem personaRoot = new MenuItem();
                personaRoot.Header = "\u4EBA\u683C";
                foreach (KeyValuePair<string, PersonaDef> kv in _personaMap)
                {
                    MenuItem item = new MenuItem();
                    item.Header = kv.Value.Name;
                    item.Tag = kv.Key;
                    if (kv.Key == _persona)
                    {
                        item.IsChecked = true;
                        item.FontWeight = FontWeights.Bold;
                    }
                    item.Click += OnPersonaClick;
                    personaRoot.Items.Add(item);
                }
                menu.Items.Add(personaRoot);
            }

            // verbosity
            MenuItem verbRoot = new MenuItem();
            verbRoot.Header = "\u5520\u53E8\u7A0B\u5EA6";
            verbRoot.Items.Add(MakeVerbosityItem("minimal", "\u6781\u7B80"));
            verbRoot.Items.Add(MakeVerbosityItem("normal", "\u514B\u5236\uFF08\u9ED8\u8BA4\uFF09"));
            verbRoot.Items.Add(MakeVerbosityItem("chatty", "\u8BDD\u75E8"));
            menu.Items.Add(verbRoot);

            menu.Items.Add(new Separator());

            MenuItem mute = new MenuItem();
            mute.Header = "\u9759\u97F3 1 \u5C0F\u65F6";
            mute.Click += delegate
            {
                _quietUntil = DateTime.Now.AddHours(1);
                SaveState();
                ShowBubbleFor(3);
            };
            menu.Items.Add(mute);

            MenuItem hide = new MenuItem();
            hide.Header = "Hide for 10 min";
            hide.Click += delegate
            {
                _hideUntil = DateTime.Now.AddMinutes(10);
                Hide();
            };
            menu.Items.Add(hide);

            menu.Items.Add(new Separator());

            MenuItem exit = new MenuItem();
            exit.Header = "Exit";
            exit.Click += delegate { Close(); };
            menu.Items.Add(exit);

            return menu;
        }

        private MenuItem MakeVerbosityItem(string id, string label)
        {
            MenuItem item = new MenuItem();
            item.Header = label;
            item.Tag = id;
            if (id == _verbosity)
            {
                item.IsChecked = true;
                item.FontWeight = FontWeights.Bold;
            }
            item.Click += OnVerbosityClick;
            return item;
        }

        private void OnPersonaClick(object sender, RoutedEventArgs e)
        {
            MenuItem clicked = sender as MenuItem;
            if (clicked == null) { return; }
            MenuItem parent = clicked.Parent as MenuItem;
            if (parent != null)
            {
                foreach (object o in parent.Items)
                {
                    MenuItem mi = o as MenuItem;
                    if (mi != null) { mi.IsChecked = false; mi.FontWeight = FontWeights.Normal; }
                }
            }
            clicked.IsChecked = true;
            clicked.FontWeight = FontWeights.Bold;
            _persona = (string)clicked.Tag;
            SaveState();

            PetEvent ev = new PetEvent();
            ev.Emotion = "happy";
            ev.Pri = 2;
            ev.Motion = "bounce";
            ev.Accent = PersonaAccent();
            ev.Title = PersonaTitle();
            ev.Text = RenderText("personaSwitch", new Dictionary<string, string> { { "name", PersonaName() } });
            ev.DurSec = 5;
            PlayEvent(ev);
        }

        private void OnVerbosityClick(object sender, RoutedEventArgs e)
        {
            MenuItem clicked = sender as MenuItem;
            if (clicked == null) { return; }
            MenuItem parent = clicked.Parent as MenuItem;
            if (parent != null)
            {
                foreach (object o in parent.Items)
                {
                    MenuItem mi = o as MenuItem;
                    if (mi != null) { mi.IsChecked = false; mi.FontWeight = FontWeights.Normal; }
                }
            }
            clicked.IsChecked = true;
            clicked.FontWeight = FontWeights.Bold;
            _verbosity = (string)clicked.Tag;
            SaveState();
        }

        private void OnSkinClick(object sender, RoutedEventArgs e)
        {
            MenuItem clicked = sender as MenuItem;
            if (clicked == null) { return; }
            foreach (KeyValuePair<string, MenuItem> kv in _skinItems)
            {
                kv.Value.IsChecked = false;
                kv.Value.FontWeight = FontWeights.Normal;
            }
            clicked.IsChecked = true;
            clicked.FontWeight = FontWeights.Bold;
            SwitchSkin((string)clicked.Tag);
        }

        // ---------- personas ----------
        private void LoadPersonas()
        {
            _personaMap.Clear();
            _lineMap.Clear();
            try
            {
                if (!File.Exists(_personasFile)) { return; }
                foreach (string raw in File.ReadAllLines(_personasFile, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) { continue; }
                    string[] p = line.Split('|');
                    if (p[0] == "persona" && p.Length >= 4)
                    {
                        PersonaDef def = new PersonaDef();
                        def.Id = p[1];
                        def.Name = p[2];
                        def.Accent = p[3];
                        _personaMap[p[1]] = def;
                    }
                    else if (p[0] == "line" && p.Length >= 6)
                    {
                        string key = p[1] + "|" + p[2];
                        if (!_lineMap.ContainsKey(key)) { _lineMap[key] = new List<string[]>(); }
                        _lineMap[key].Add(new string[] { p[4], p[5] });
                    }
                }
            }
            catch (Exception) { }
        }

        private PersonaDef CurrentPersona()
        {
            PersonaDef def;
            if (_personaMap.TryGetValue(_persona, out def)) { return def; }
            if (_personaMap.TryGetValue("cute", out def)) { return def; }
            PersonaDef fallback = new PersonaDef();
            fallback.Id = "cute";
            fallback.Name = "WorkBuddy";
            fallback.Accent = "#F9A8D4";
            return fallback;
        }

        private string PersonaName()
        {
            return CurrentPersona().Name;
        }

        private string PersonaAccent()
        {
            return CurrentPersona().Accent;
        }

        private string PersonaTitle()
        {
            return CurrentPersona().Name;
        }

        private string RenderText(string key, Dictionary<string, string> vars)
        {
            string lookup = _persona + "|" + key;
            string text = "";
            List<string[]> list;
            if (_lineMap.TryGetValue(lookup, out list) && list.Count > 0)
            {
                int index = Math.Abs(_lastSeq + _idx) % list.Count;
                text = list[index][0];
            }
            else
            {
                text = key;
            }

            if (vars != null)
            {
                foreach (KeyValuePair<string, string> kv in vars)
                {
                    text = text.Replace("{" + kv.Key + "}", kv.Value);
                }
            }
            return text;
        }

        private string RenderEmoji(string key)
        {
            string lookup = _persona + "|" + key;
            List<string[]> list;
            if (_lineMap.TryGetValue(lookup, out list) && list.Count > 0) { return list[0][1]; }
            return "";
        }

        // ---------- frames / skins ----------
        private string ReadCurrentSkinId()
        {
            try
            {
                if (!File.Exists(_currentFile)) { return string.Empty; }
                string[] lines = File.ReadAllLines(_currentFile);
                return lines.Length > 0 ? lines[0].Trim() : string.Empty;
            }
            catch (Exception) { return string.Empty; }
        }

        private string ResolveFramesDir(string skinId)
        {
            if (!string.IsNullOrEmpty(skinId))
            {
                string dir = Path.Combine(_skinsDir, skinId);
                if (File.Exists(Path.Combine(dir, "manifest.json"))) { return dir; }
            }
            string legacy = Path.Combine(_assetsDir, "frames");
            if (File.Exists(Path.Combine(legacy, "manifest.json"))) { return legacy; }
            return null;
        }

        private void LoadFrames(string dir)
        {
            _frames.Clear();
            _delays.Clear();
            _idx = 0;

            if (dir == null)
            {
                _mascot.Source = null;
                return;
            }

            try
            {
                string delayFile = Path.Combine(dir, "delays.txt");
                if (File.Exists(delayFile))
                {
                    foreach (string line in File.ReadAllLines(delayFile))
                    {
                        int d;
                        if (int.TryParse(line.Trim(), out d)) { _delays.Add(d); }
                    }
                }

                string[] files = Directory.GetFiles(dir, "*.png");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    BitmapImage bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.UriSource = new Uri(f, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    _frames.Add(bmp);
                }

                if (_frames.Count > 0)
                {
                    _mascot.Source = _frames[0];
                    if (_frameTimer != null && _frames.Count > 1 && !_frameTimer.IsEnabled)
                    {
                        _frameTimer.Start();
                    }
                }
            }
            catch (Exception) { }
        }

        private List<string[]> ReadCatalog()
        {
            List<string[]> result = new List<string[]>();
            try
            {
                if (!File.Exists(_catalogFile)) { return result; }
                string raw = File.ReadAllText(_catalogFile);
                MatchCollection ms = Regex.Matches(
                    raw, "\"id\"\\s*:\\s*\"([^\"]+)\"[\\s\\S]*?\"name\"\\s*:\\s*\"([^\"]+)\"");
                foreach (Match m in ms)
                {
                    result.Add(new string[] { m.Groups[1].Value, m.Groups[2].Value });
                }
            }
            catch (Exception) { }
            return result;
        }

        private void SwitchSkin(string skinId)
        {
            try
            {
                if (!File.Exists(_switchScript)) { return; }
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "node";
                psi.Arguments = "\"" + _switchScript + "\" " + skinId;
                psi.WorkingDirectory = _baseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
                ShowBubbleFor(8);
            }
            catch (Exception) { }
        }

        // ---------- event channel ----------
        /** 面板启动时跳到文件尾：昨晚缓存的事件不再连播 */
        private void SeekEventsToEnd()
        {
            try
            {
                if (!File.Exists(_eventsFile)) { return; }
                using (FileStream fs = new FileStream(_eventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    _eventOffset = fs.Length;
                }
                _lastSeq = LastSeqInFile();
                _lastTurnId = string.Empty;
            }
            catch (Exception) { }
        }

        private int LastSeqInFile()
        {
            try
            {
                if (!File.Exists(_eventsFile)) { return 0; }
                string[] lines = File.ReadAllLines(_eventsFile, Encoding.UTF8);
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string line = lines[i];
                    if (line.Length == 0) { continue; }
                    int bar = line.IndexOf('|');
                    if (bar <= 0) { continue; }
                    int seq;
                    if (int.TryParse(line.Substring(0, bar), out seq)) { return seq; }
                }
            }
            catch (Exception) { }
            return 0;
        }

        private void PollEvents()
        {
            if (!File.Exists(_eventsFile)) { return; }
            try
            {
                byte[] buffer;
                int read;
                using (FileStream fs = new FileStream(_eventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < _eventOffset) { _eventOffset = 0; }   // 被轮转了
                    if (fs.Length <= _eventOffset) { return; }
                    long remain = fs.Length - _eventOffset;
                    if (remain > 65536) { remain = 65536; }
                    buffer = new byte[remain];
                    fs.Seek(_eventOffset, SeekOrigin.Begin);
                    read = fs.Read(buffer, 0, buffer.Length);
                }
                if (read <= 0) { return; }

                string chunk = Encoding.UTF8.GetString(buffer, 0, read);
                int lastNewline = chunk.LastIndexOf('\n');
                if (lastNewline < 0) { return; }   // 半行，下轮再读

                string complete = chunk.Substring(0, lastNewline + 1);
                _eventOffset += Encoding.UTF8.GetByteCount(complete);

                foreach (string line in complete.Split('\n'))
                {
                    if (line.Trim().Length == 0) { continue; }
                    PetEvent ev = ParseEvent(line);
                    if (ev != null) { HandleEvent(ev); }
                }
                SaveState();
            }
            catch (Exception) { }
        }

        private static PetEvent ParseEvent(string line)
        {
            string[] p = line.Split('|');
            if (p.Length < 10) { return null; }
            PetEvent ev = new PetEvent();
            if (!int.TryParse(p[0], out ev.Seq)) { return null; }
            long ts;
            if (long.TryParse(p[1], out ts)) { ev.Ts = ts; }
            ev.TurnId = p[2];
            ev.Emotion = p[3];
            int pri;
            ev.Pri = int.TryParse(p[4], out pri) ? pri : 2;
            ev.Motion = p[5];
            ev.Accent = p[6];
            ev.Title = p[7];
            ev.Text = p[8];
            int dur;
            ev.DurSec = int.TryParse(p[9], out dur) ? dur : 6;
            return ev;
        }

        private void HandleEvent(PetEvent ev)
        {
            if (ev.Seq <= _lastSeq) { return; }
            long age = UnixMs() - ev.Ts;
            if (ev.Ts > 0 && age > 120000) { return; }             // 陈旧事件
            if (ev.TurnId.Length > 0 && ev.TurnId == _lastTurnId) { return; } // 同一轮只播一条

            _lastSeq = ev.Seq;
            _lastTurnId = ev.TurnId;

            if (_sleepMode && ev.Pri >= 1)
            {
                WakeUp();
            }

            if (!AllowPlay(ev)) { return; }

            if (IsPointerOverPanel())
            {
                _pendingEvent = ev;
                _pendingUntil = DateTime.Now.AddSeconds(60);
                return;
            }

            PlayEvent(ev);
        }

        private bool AllowPlay(PetEvent ev)
        {
            DateTime now = DateTime.Now;
            if (now < _quietUntil) { return false; }

            if (ev.Pri < 3)
            {
                if (_lastBubbleAt != DateTime.MinValue && (now - _lastBubbleAt).TotalSeconds < 45) { return false; }

                DateTime lastSame;
                if (_emoCool.TryGetValue(ev.Emotion, out lastSame) && (now - lastSame).TotalMinutes < 3) { return false; }

                _recentBubbles.RemoveAll(delegate(DateTime t) { return (now - t).TotalMinutes > 10; });
                if (_recentBubbles.Count >= 3) { return false; }
            }
            return true;
        }

        private void PlayEvent(PetEvent ev)
        {
            DateTime now = DateTime.Now;
            _lastBubbleAt = now;
            _recentBubbles.Add(now);
            _emoCool[ev.Emotion] = now;

            ApplyAccent(ev.Accent);
            _title.Text = PersonaName();
            _emotionText.Text = ev.Text;
            _emotionText.Visibility = Visibility.Visible;
            _tempUntil = now.AddSeconds(Math.Max(3, ev.DurSec));

            PlayMotion(ev.Motion);
            ShowBubbleFor(ev.DurSec);
            SaveState();
        }

        /** 情绪色只落在一颗 5px 的圆点上，卡片描边保持中性 */
        private void ApplyAccent(string hex)
        {
            try
            {
                Color color = (Color)ColorConverter.ConvertFromString(hex);
                _dot.Background = new SolidColorBrush(color);
            }
            catch (Exception) { }
        }

        private void ShowBubbleFor(int seconds)
        {
            _tempUntil = DateTime.Now.AddSeconds(Math.Max(2, seconds));
            _hideTimer.Stop();
            _bubble.Visibility = Visibility.Visible;
        }

        // ---------- motions ----------
        private void ResetTransforms()
        {
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            _shift.BeginAnimation(TranslateTransform.YProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _shift.X = 0;
            _shift.Y = 0;
            _scale.ScaleX = 1;
            _scale.ScaleY = 1;
        }

        private void PlayMotion(string motion)
        {
            if (motion == null) { motion = "none"; }
            ResetTransforms();
            if (motion == "bounce") { Bounce(9, 2, 280); }
            else if (motion == "bounce3") { Bounce(13, 3, 240); }
            else if (motion == "shake") { Shake(); }
            else if (motion == "squash") { Squash(); }
            else if (motion == "breathe") { Breathe(); }
            else if (motion == "sleep") { SetSleep(true); }
            else { SetSleep(false); }
        }

        private void Bounce(double px, int times, int ms)
        {
            DoubleAnimation anim = new DoubleAnimation();
            anim.From = 0.0;
            anim.To = -px;
            anim.Duration = new Duration(TimeSpan.FromMilliseconds(ms));
            anim.AutoReverse = true;
            anim.RepeatBehavior = new RepeatBehavior(times);
            anim.EasingFunction = new QuadraticEase();
            _shift.BeginAnimation(TranslateTransform.YProperty, anim);
        }

        private void Shake()
        {
            DoubleAnimationUsingKeyFrames anim = new DoubleAnimationUsingKeyFrames();
            anim.Duration = new Duration(TimeSpan.FromMilliseconds(420));
            double[] offsets = new double[] { 0, -6, 6, -4, 4, 0 };
            for (int i = 0; i < offsets.Length; i++)
            {
                LinearDoubleKeyFrame frame = new LinearDoubleKeyFrame(offsets[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 70)));
                anim.KeyFrames.Add(frame);
            }
            _shift.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        private void Squash()
        {
            DoubleAnimation sx = new DoubleAnimation(1.0, 1.12, new Duration(TimeSpan.FromMilliseconds(160)));
            sx.AutoReverse = true;
            sx.RepeatBehavior = new RepeatBehavior(2);
            sx.EasingFunction = new QuadraticEase();
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);

            DoubleAnimation sy = new DoubleAnimation(1.0, 0.9, new Duration(TimeSpan.FromMilliseconds(160)));
            sy.AutoReverse = true;
            sy.RepeatBehavior = new RepeatBehavior(2);
            sy.EasingFunction = new QuadraticEase();
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        }

        private void Breathe()
        {
            DoubleAnimation sy = new DoubleAnimation(1.0, 1.045, new Duration(TimeSpan.FromMilliseconds(900)));
            sy.AutoReverse = true;
            sy.RepeatBehavior = new RepeatBehavior(2);
            sy.EasingFunction = new SineEase();
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        }

        private void SetSleep(bool sleeping)
        {
            _sleepMode = sleeping;
            DoubleAnimation fade = new DoubleAnimation(sleeping ? 0.72 : 1.0, new Duration(TimeSpan.FromMilliseconds(600)));
            _mascot.BeginAnimation(OpacityProperty, fade);
            if (!sleeping) { _napSince = DateTime.MinValue; }
        }

        private void WakeUp()
        {
            _sleepMode = false;
            SetSleep(false);
            DateTime now = DateTime.Now;
            if ((now - _lastNapWake).TotalMinutes < 1) { return; }
            _lastNapWake = now;

            PetEvent ev = new PetEvent();
            ev.Emotion = "happy";
            ev.Pri = 1;
            ev.Motion = "bounce";
            ev.Accent = "#F9A8D4";
            ev.Title = PersonaTitle();
            ev.Text = RenderText("napWake", null);
            ev.DurSec = 4;
            PlayEvent(ev);
        }

        // ---------- ambient clock ----------
        private static long UnixMs()
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(DateTime.UtcNow - epoch).TotalMilliseconds;
        }

        private static DateTime FromUnixMs(long ms)
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddMilliseconds(ms).ToLocalTime();
        }

        private void EvaluateAmbient()
        {
            DateTime now = DateTime.Now;
            long lastActivityMs = 0;
            long sessionStartMs = 0;
            try
            {
                if (File.Exists(_ledgerFile))
                {
                    foreach (string line in File.ReadAllLines(_ledgerFile))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) { continue; }
                        string k = line.Substring(0, i).Trim();
                        string v = line.Substring(i + 1).Trim();
                        long n;
                        if (!long.TryParse(v, out n)) { continue; }
                        if (k == "lastActivityTs") { lastActivityMs = n; }
                        if (k == "sessionStartTs") { sessionStartMs = n; }
                    }
                }
            }
            catch (Exception) { }

            if (lastActivityMs <= 0) { lastActivityMs = UnixMs(); }
            if (sessionStartMs <= 0) { sessionStartMs = lastActivityMs; }

            DateTime last = FromUnixMs(lastActivityMs);
            DateTime start = FromUnixMs(sessionStartMs);

            // 14 打盹：15 分钟无活动
            if (!_sleepMode && (now - last).TotalMinutes >= 15)
            {
                SetSleep(true);
                _napSince = now;
            }

            if (_sleepMode) { return; }

            // 13 深夜：23:00-06:00，每晚一次
            bool isNight = now.Hour >= 23 || now.Hour < 6;
            if (isNight && _nightAnnounced.Date != now.Date)
            {
                _nightAnnounced = now;
                PlayAmbient("sleepy", "lateNight", 1, "breathe", 6,
                    new Dictionary<string, string> { { "h", now.Hour.ToString() } });
                return;
            }

            // 12 连续工作超过 2 小时，每 2 小时一次
            if ((now - start).TotalHours >= 2 && (now - _longSessionAnnounced).TotalHours >= 2)
            {
                _longSessionAnnounced = now;
                PlayAmbient("tender", "longSession", 1, "breathe", 6,
                    new Dictionary<string, string> { { "h", ((int)(now - start).TotalHours).ToString() } });
                return;
            }

            // 16 久坐：连续 90 分钟，每 90 分钟一次
            if ((now - start).TotalMinutes >= 90 && (now - _sitAnnounced).TotalMinutes >= 90)
            {
                _sitAnnounced = now;
                PlayAmbient("tender", "sitLong", 1, "breathe", 6, null);
            }
        }

        private void PlayAmbient(string emotion, string key, int pri, string motion, int durSec, Dictionary<string, string> vars)
        {
            PetEvent ev = new PetEvent();
            ev.Emotion = emotion;
            ev.Pri = pri;
            ev.Motion = motion;
            ev.Accent = EmotionAccent(emotion);
            string emoji = RenderEmoji(key);
            ev.Title = (emoji.Length > 0 ? emoji + " " : "") + PersonaTitle();
            ev.Text = RenderText(key, vars);
            ev.DurSec = durSec;
            if (!AllowPlay(ev)) { return; }
            PlayEvent(ev);
        }

        private static string EmotionAccent(string emotion)
        {
            switch (emotion)
            {
                case "happy": return "#F9A8D4";
                case "proud": return "#FBBF24";
                case "worried": return "#F87171";
                case "tender": return "#FDA4AF";
                case "sleepy": return "#6B7B9E";
                case "surprised": return "#A78BFA";
                default: return "#333949";
            }
        }

        // ---------- state ----------
        private void LoadState()
        {
            try
            {
                if (!File.Exists(_stateFile)) { return; }
                int x = int.MinValue;
                int y = int.MinValue;
                foreach (string line in File.ReadAllLines(_stateFile, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) { continue; }
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();
                    if (k == "persona" && v.Length > 0) { _persona = v; }
                    else if (k == "verbosity" && v.Length > 0) { _verbosity = v; }
                    else if (k == "winX") { int.TryParse(v, out x); }
                    else if (k == "winY") { int.TryParse(v, out y); }
                }

                Rect wa = SystemParameters.WorkArea;
                if (x > -10000 && y > -10000 && x < wa.Right - 80 && y < wa.Bottom - 80 && x > wa.Left - 100 && y > wa.Top - 100)
                {
                    Left = x;
                    Top = y;
                }
            }
            catch (Exception) { }
        }

        private void SaveState()
        {
            DateTime now = DateTime.Now;
            if ((now - _lastSaveAt).TotalMilliseconds < 400) { return; }
            _lastSaveAt = now;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("version=1");
                sb.AppendLine("persona=" + _persona);
                sb.AppendLine("verbosity=" + _verbosity);
                sb.AppendLine("winX=" + (int)Left);
                sb.AppendLine("winY=" + (int)Top);
                sb.AppendLine("eventOffset=" + _eventOffset);
                sb.AppendLine("lastEventSeq=" + _lastSeq);
                long quiet = 0;
                if (_quietUntil > new DateTime(2000, 1, 1))
                {
                    quiet = (long)(_quietUntil.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                }
                sb.AppendLine("quietUntil=" + quiet);
                sb.AppendLine("ambient=" + (_sleepMode ? "sleepy" : "awake"));

                string tmp = _stateFile + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(_stateFile))
                {
                    try { File.Replace(tmp, _stateFile, null); }
                    catch (Exception) { File.Copy(tmp, _stateFile, true); File.Delete(tmp); }
                }
                else
                {
                    File.Move(tmp, _stateFile);
                }
            }
            catch (Exception) { }
        }

        private void OnWindowMoved(object sender, EventArgs e)
        {
            SaveState();
        }

        // ---------- timers ----------
        private void StartTimers()
        {
            _frameTimer = new DispatcherTimer();
            _frameTimer.Interval = TimeSpan.FromMilliseconds(83);
            _frameTimer.Tick += OnFrameTick;
            if (_frames.Count > 1) { _frameTimer.Start(); }

            _dataTimer = new DispatcherTimer();
            _dataTimer.Interval = TimeSpan.FromMilliseconds(2000);
            _dataTimer.Tick += delegate { UpdatePanel(); };
            _dataTimer.Start();

            _hideTimer = new DispatcherTimer();
            _hideTimer.Interval = TimeSpan.FromMilliseconds(450);
            _hideTimer.Tick += delegate
            {
                _hideTimer.Stop();
                if (DateTime.Now < _tempUntil) { return; }
                _bubble.Visibility = Visibility.Collapsed;
            };

            _lifeTimer = new DispatcherTimer();
            _lifeTimer.Interval = TimeSpan.FromSeconds(3);
            _lifeTimer.Tick += delegate
            {
                if (DateTime.Now < _hideUntil) { return; }
                bool visible = WorkBuddyVisible();
                if (visible)
                {
                    if (!IsVisible) { Show(); }
                }
                else
                {
                    if (IsVisible) { Hide(); }
                }
            };
            _lifeTimer.Start();

            _skinTimer = new DispatcherTimer();
            _skinTimer.Interval = TimeSpan.FromMilliseconds(900);
            _skinTimer.Tick += delegate
            {
                string id = ReadCurrentSkinId();
                if (id.Length == 0 || id == _loadedSkin) { return; }
                string dir = ResolveFramesDir(id);
                if (dir == null) { return; }
                _loadedSkin = id;
                LoadFrames(dir);
                Bounce(9, 2, 280);
                ShowBubbleFor(4);
            };
            _skinTimer.Start();

            _eventTimer = new DispatcherTimer();
            _eventTimer.Interval = TimeSpan.FromMilliseconds(600);
            _eventTimer.Tick += delegate { PollEvents(); TryFlushPending(); };
            _eventTimer.Start();

            _clockTimer = new DispatcherTimer();
            _clockTimer.Interval = TimeSpan.FromSeconds(30);
            _clockTimer.Tick += delegate { EvaluateAmbient(); };
            _clockTimer.Start();
        }

        private void TryFlushPending()
        {
            if (_pendingEvent == null) { return; }
            if (IsPointerOverPanel()) { return; }
            if (DateTime.Now > _pendingUntil) { _pendingEvent = null; return; }
            PetEvent ev = _pendingEvent;
            _pendingEvent = null;
            PlayEvent(ev);
        }

        private bool IsPointerOverPanel()
        {
            Point p = Mouse.GetPosition(this);
            return p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight;
        }

        private void OnFrameTick(object sender, EventArgs e)
        {
            if (_frames.Count < 2) { _frameTimer.Stop(); return; }
            _idx = (_idx + 1) % _frames.Count;
            _mascot.Source = _frames[_idx];

            int d = 83;
            if (_idx < _delays.Count) { d = _delays[_idx]; }
            if (_sleepMode) { d = (int)(d * 2.2); }
            if (d < 40) { d = 40; }
            if (_sleepMode && d < 180) { d = 180; }
            _frameTimer.Interval = TimeSpan.FromMilliseconds(d);
        }

        // ---------- interaction ----------
        private void OnMascotEnter(object sender, MouseEventArgs e)
        {
            _hideTimer.Stop();
            _bubble.Visibility = Visibility.Visible;
        }

        private void OnMascotLeave(object sender, MouseEventArgs e)
        {
            if (DateTime.Now < _tempUntil) { return; }
            _hideTimer.Start();
        }

        private Point _dragOrigin;
        private bool _dragging;

        private void OnMascotMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) { return; }
            _dragOrigin = e.GetPosition(this);
            _dragging = true;
            try { DragMove(); }
            catch (Exception) { }
        }

        private void OnMascotMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) { return; }
            _dragging = false;
            SaveState();
        }

        // ---------- data ----------
        private void UpdatePanel()
        {
            try
            {
                if (!File.Exists(_dataFile)) { return; }

                DateTime stamp = File.GetLastWriteTimeUtc(_dataFile);
                bool changed = !_firstLoad && stamp != _lastWrite;
                _lastWrite = stamp;

                string raw = ReadAllTextShared(_dataFile);
                if (raw == null || raw.Trim().Length == 0) { return; }

                Dictionary<string, string> map = new Dictionary<string, string>();
                string[] lines = raw.Replace("\r\n", "\n").Split('\n');
                foreach (string line in lines)
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        map[line.Substring(0, eq)] = line.Substring(eq + 1).Trim();
                    }
                }
                if (map.Count == 0) { return; }

                _timeText.Text = GetValue(map, "time", "");
                _turnIn.Text = GetValue(map, "turnIn", "-");
                _turnOut.Text = GetValue(map, "turnOut", "-");
                _cache.Text = GetValue(map, "cacheRate", "-") + "%";
                _credit.Text = GetValue(map, "credit", "-");

                string todayCredit = GetValue(map, "todayCredit", "-");
                string sessions = GetValue(map, "todaySessions", "0");
                _todayValue.Text = todayCredit;
                _todaySessions.Text = sessions + " \u4F1A\u8BDD";

                // 有事件通道时不再用 mtime 弹气泡，避免和情绪事件双弹
                if (changed && !File.Exists(_eventsFile))
                {
                    _tempUntil = DateTime.Now.AddSeconds(6);
                    _hideTimer.Stop();
                    _bubble.Visibility = Visibility.Visible;
                    Bounce(9, 2, 280);
                }
                _firstLoad = false;
            }
            catch (Exception) { }

            try
            {
                // 页脚只用于临时消息（换形象进度等），平时隐藏
                if (File.Exists(_statusFile))
                {
                    string st = File.ReadAllText(_statusFile, Encoding.UTF8).Trim();
                    if (st.Length > 0)
                    {
                        _footer.Text = st;
                        _footer.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _footer.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    _footer.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception) { }
        }

        private static string GetValue(Dictionary<string, string> map, string key, string fallback)
        {
            string v;
            if (map.TryGetValue(key, out v) && v.Length > 0) { return v; }
            return fallback;
        }

        private static string ReadAllTextShared(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader sr = new StreamReader(fs))
                    {
                        return sr.ReadToEnd();
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(40);
                }
            }
            return null;
        }

        private static bool WorkBuddyVisible()
        {
            try
            {
                Process[] procs = Process.GetProcessesByName("WorkBuddy");
                if (procs.Length == 0) { return false; }
                foreach (Process p in procs)
                {
                    if (p.MainWindowHandle != IntPtr.Zero) { return true; }
                }
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            Mutex mutex = null;
            try
            {
                mutex = new Mutex(false, "Global\\WorkBuddyUsagePanel");
                if (!mutex.WaitOne(0)) { return; }
            }
            catch (AbandonedMutexException) { }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dataFile = Path.Combine(baseDir, "panel-data.txt");
            if (Environment.GetCommandLineArgs().Length > 1)
            {
                dataFile = Environment.GetCommandLineArgs()[1];
            }

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            PetWindow win = new PetWindow(baseDir, dataFile);
            app.Run(win);

            GC.KeepAlive(mutex);
        }
    }
}
