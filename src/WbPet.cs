// WorkBuddy usage pet panel - WPF, per-pixel transparent window.
// Build: build-pet.cmd  (csc /target:winexe, no console window)
//
// Behaviour:
//   - loops the APNG frames extracted from sprite_dragon_shift28.png
//   - dark icon-card speech bubble above the head: shown on hover, and for 6s
//     after each completed turn (panel-data.txt rewritten by the Stop hook)
//   - bounces once per new turn
//   - hides itself when the WorkBuddy main window is gone
//
// Written for C# 5 (csc 4.0.30319): no interpolation, no expression bodies.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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

        private readonly string _dataFile;
        private readonly string _framesDir;
        private readonly List<BitmapImage> _frames = new List<BitmapImage>();
        private readonly List<int> _delays = new List<int>();

        private int _idx = 0;
        private bool _firstLoad = true;
        private DateTime _lastWrite = DateTime.MinValue;
        private DateTime _tempUntil = DateTime.MinValue;
        private DateTime _hideUntil = DateTime.MinValue;

        private Image _mascot;
        private Grid _bubble;
        private TextBlock _timeText;
        private TextBlock _turnIn;
        private TextBlock _turnOut;
        private TextBlock _cache;
        private TextBlock _credit;
        private TextBlock _footer;
        private TranslateTransform _shift;

        private DispatcherTimer _frameTimer;
        private DispatcherTimer _dataTimer;
        private DispatcherTimer _hideTimer;
        private DispatcherTimer _lifeTimer;

        private static Brush MakeBrush(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public PetWindow(string baseDir, string dataFile)
        {
            _dataFile = dataFile;
            _framesDir = Path.Combine(Path.Combine(baseDir, "assets"), "frames");

            Width = 340;
            Height = 400;
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

            BuildUi();
            LoadFrames();
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
            Path tail = new Path();
            tail.Fill = BgBrush;
            tail.Stroke = BorderBrushDark;
            tail.StrokeThickness = 1.2;
            tail.Data = Geometry.Parse("M 0,0 L 26,0 L 13,13 Z");
            tail.HorizontalAlignment = HorizontalAlignment.Center;
            tail.VerticalAlignment = VerticalAlignment.Bottom;
            tail.Margin = new Thickness(0, 0, 0, -13);
            _bubble.Children.Add(tail);

            Border body = new Border();
            body.Background = BgBrush;
            body.BorderBrush = BorderBrushDark;
            body.BorderThickness = new Thickness(1.2);
            body.CornerRadius = new CornerRadius(16);
            body.Padding = new Thickness(16, 12, 16, 11);
            body.Width = 272;

            DropShadowEffect shadow = new DropShadowEffect();
            shadow.BlurRadius = 18;
            shadow.ShadowDepth = 4;
            shadow.Direction = 270;
            shadow.Opacity = 0.45;
            shadow.Color = Colors.Black;
            body.Effect = shadow;

            StackPanel inner = new StackPanel();

            // header: title left, time right
            Grid header = new Grid();
            header.Margin = new Thickness(0, 0, 0, 10);

            TextBlock title = new TextBlock();
            title.Text = "WorkBuddy";
            title.FontSize = 11;
            title.Foreground = TitleBrush;
            title.HorizontalAlignment = HorizontalAlignment.Left;
            TextOptions.SetTextFormattingMode(title, TextFormattingMode.Display);
            header.Children.Add(title);

            _timeText = new TextBlock();
            _timeText.FontSize = 11;
            _timeText.Foreground = TimeBrush;
            _timeText.HorizontalAlignment = HorizontalAlignment.Right;
            TextOptions.SetTextFormattingMode(_timeText, TextFormattingMode.Display);
            header.Children.Add(_timeText);

            inner.Children.Add(header);

            // 2x2 tiles
            Grid tiles = new Grid();
            tiles.ColumnDefinitions.Add(new ColumnDefinition());
            tiles.ColumnDefinitions.Add(new ColumnDefinition());
            tiles.RowDefinitions.Add(new RowDefinition());
            tiles.RowDefinitions.Add(new RowDefinition());

            _turnIn = AddTile(tiles, 0, 0, "M8,2 L14,9 L10.5,9 L10.5,14 L5.5,14 L5.5,9 L2,9 Z", "#5EEAD4", "输入", true);
            _turnOut = AddTile(tiles, 0, 1, "M8,14 L2,7 L5.5,7 L5.5,2 L10.5,2 L10.5,7 L14,7 Z", "#A78BFA", "输出", true);
            _cache = AddTile(tiles, 1, 0, "M9.5,1 L3,9 L7,9 L6.5,15 L13,7 L9,7 Z", "#FBBF24", "缓存命中", false);
            _credit = AddTile(tiles, 1, 1, "M8,2 L13,8 L8,14 L3,8 Z", "#34D399", "积分", false);

            inner.Children.Add(tiles);

            Border divider = new Border();
            divider.Height = 1;
            divider.Background = MakeBrush(0x26, 0x2B, 0x36);
            divider.Margin = new Thickness(0, 10, 0, 7);
            inner.Children.Add(divider);

            _footer = new TextBlock();
            _footer.FontSize = 10.5;
            _footer.Foreground = CaptionBrush;
            _footer.HorizontalAlignment = HorizontalAlignment.Center;
            TextOptions.SetTextFormattingMode(_footer, TextFormattingMode.Display);
            inner.Children.Add(_footer);

            body.Child = inner;
            _bubble.Children.Add(body);
            stack.Children.Add(_bubble);

            _mascot = new Image();
            _mascot.Width = 190;
            _mascot.Height = 208;
            _mascot.Stretch = Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(_mascot, BitmapScalingMode.HighQuality);
            _shift = new TranslateTransform(0, 0);
            _mascot.RenderTransform = _shift;
            _mascot.MouseEnter += OnMascotEnter;
            _mascot.MouseLeave += OnMascotLeave;
            _mascot.MouseLeftButtonDown += OnMascotMouseDown;
            _mascot.ContextMenu = BuildMenu();
            stack.Children.Add(_mascot);

            _bubble.MouseEnter += OnMascotEnter;
            _bubble.MouseLeave += OnMascotLeave;

            root.Children.Add(stack);
            Content = root;
        }

        private TextBlock AddTile(Grid grid, int row, int col, string iconPath, string color, string caption, bool withBottomMargin)
        {
            StackPanel tile = new StackPanel();
            tile.Orientation = Orientation.Horizontal;
            if (withBottomMargin) { tile.Margin = new Thickness(0, 0, 0, 9); }
            Grid.SetRow(tile, row);
            Grid.SetColumn(tile, col);

            Path icon = new Path();
            icon.Data = Geometry.Parse(iconPath);
            icon.Stretch = Stretch.Uniform;
            icon.Width = 12;
            icon.Height = 12;
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 7, 0);
            icon.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            tile.Children.Add(icon);

            StackPanel texts = new StackPanel();

            TextBlock value = new TextBlock();
            value.Text = "-";
            value.FontSize = 14;
            value.FontWeight = FontWeights.SemiBold;
            value.Foreground = ValueBrush;
            TextOptions.SetTextFormattingMode(value, TextFormattingMode.Display);
            texts.Children.Add(value);

            TextBlock cap = new TextBlock();
            cap.Text = caption;
            cap.FontSize = 9.5;
            cap.Foreground = CaptionBrush;
            cap.Margin = new Thickness(0, 1, 0, 0);
            TextOptions.SetTextFormattingMode(cap, TextFormattingMode.Display);
            texts.Children.Add(cap);

            tile.Children.Add(texts);
            grid.Children.Add(tile);
            return value;
        }

        private ContextMenu BuildMenu()
        {
            ContextMenu menu = new ContextMenu();

            MenuItem refresh = new MenuItem();
            refresh.Header = "Refresh";
            refresh.Click += delegate { UpdatePanel(); };
            menu.Items.Add(refresh);

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

        // ---------- frames ----------
        private void LoadFrames()
        {
            try
            {
                if (!Directory.Exists(_framesDir)) { return; }
                string[] files = Directory.GetFiles(_framesDir, "*.png");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                string delayFile = Path.Combine(_framesDir, "delays.txt");
                if (File.Exists(delayFile))
                {
                    string[] lines = File.ReadAllLines(delayFile);
                    foreach (string line in lines)
                    {
                        int d;
                        if (int.TryParse(line.Trim(), out d)) { _delays.Add(d); }
                    }
                }

                foreach (string f in files)
                {
                    BitmapImage bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(f, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    _frames.Add(bmp);
                }

                if (_frames.Count > 0) { _mascot.Source = _frames[0]; }
            }
            catch (Exception) { }
        }

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
        }

        private void OnFrameTick(object sender, EventArgs e)
        {
            if (_frames.Count < 2) { _frameTimer.Stop(); return; }
            _idx = (_idx + 1) % _frames.Count;
            _mascot.Source = _frames[_idx];

            int d = 83;
            if (_idx < _delays.Count) { d = _delays[_idx]; }
            if (d < 30) { d = 30; }
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

        private void OnMascotMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch (Exception) { }
            }
        }

        private void Bounce()
        {
            DoubleAnimation anim = new DoubleAnimation();
            anim.From = 0.0;
            anim.To = -9.0;
            anim.Duration = new Duration(TimeSpan.FromMilliseconds(280));
            anim.AutoReverse = true;
            anim.RepeatBehavior = new RepeatBehavior(2);
            anim.EasingFunction = new QuadraticEase();
            _shift.BeginAnimation(TranslateTransform.YProperty, anim);
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
                string rate = GetValue(map, "cacheRate", "-");
                _cache.Text = rate + "%";
                _credit.Text = GetValue(map, "credit", "-");

                string todayCredit = GetValue(map, "todayCredit", "-");
                string sessions = GetValue(map, "todaySessions", "0");
                _footer.Text = "\u4ECA\u65E5 " + todayCredit + " \u79EF\u5206 \u00B7 " + sessions + " \u4F1A\u8BDD";

                if (changed)
                {
                    _tempUntil = DateTime.Now.AddSeconds(6);
                    _hideTimer.Stop();
                    _bubble.Visibility = Visibility.Visible;
                    Bounce();
                }
                _firstLoad = false;
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
