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
using System.Media;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        private static readonly Brush BorderBrushDark = MakeBrush(0x3D, 0x46, 0x5C);
        private static readonly Brush DividerBrush = MakeBrush(0x33, 0x3B, 0x4C);
        private static readonly Brush TrackBrush = MakeBrush(0x42, 0x4A, 0x5E);
        private static readonly Brush ValueBrush = MakeBrush(0xED, 0xEF, 0xF5);
        private static readonly Brush TextBodyBrush = MakeBrush(0xC3, 0xCA, 0xD8);
        private static readonly Brush CaptionBrush = MakeBrush(0x98, 0xA0, 0xB4);
        private static readonly Brush TitleBrush = MakeBrush(0x8A, 0x93, 0xA8);
        private static readonly Brush TimeBrush = MakeBrush(0x98, 0xA0, 0xB4);
        private static readonly Brush EmotionBrush = MakeBrush(0xE8, 0xEA, 0xF0);
        private static readonly Brush AccentGlowBrush = MakeBrush(0x2E, 0xE6, 0xC0);
        private static readonly Brush MenuBgBrush = MakeBrush(0x20, 0x24, 0x2F);
        private static readonly Brush MenuHoverBrush = MakeBrush(0x2A, 0x31, 0x42);
        private static readonly Brush OkBrush = MakeBrush(0x34, 0xD3, 0x99);
        private static readonly Brush WarnBrush = MakeBrush(0xFB, 0xBF, 0x24);
        private static readonly Brush OverBrush = MakeBrush(0xF8, 0x71, 0x71);

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
        private readonly string _summaryFile;
        private readonly string _weeklyFile;
        private readonly string _budgetFile;
        private readonly string _portFile;

        private readonly List<BitmapImage> _frames = new List<BitmapImage>();
        private readonly List<int> _delays = new List<int>();
        private readonly Dictionary<string, MenuItem> _skinItems = new Dictionary<string, MenuItem>();
        // 换形象子菜单与清单签名：skins.json 可在运行期被外部改动，打开菜单时按签名对齐
        private MenuItem _skinRoot;
        private string _skinSig = "";
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

        // ---- atlas（雪碧图）皮肤：与逐帧皮肤互斥 ----
        // _atlas != null 时 Source 由 CroppedBitmap 按状态/情绪给出，_frameTimer 必须停着
        private PetAtlas _atlas;
        private int _atlasSlot = -1;                       // 当前已贴到 _mascot 上的逻辑帧序号
        private int _emoSlot = -1;                         // 情绪帧（一次性，到时回到状态帧）
        private DateTime _emoSlotUntil = DateTime.MinValue;
        // 序列动画：manifest 的 seq 给某个槽位配了多帧时，由 _atlasTimer 按帧间隔推进。
        // 老图集没有 seq，槽位只有单帧，这个定时器全程停着，行为与之前完全一致。
        private DispatcherTimer _atlasTimer;
        private int _atlasPhase;

        private bool _firstLoad = true;
        private DateTime _lastWrite = DateTime.MinValue;
        private DateTime _tempUntil = DateTime.MinValue;
        private DateTime _hideUntil = DateTime.MinValue;
        private DateTime _autoUntil = DateTime.MinValue;
        private DateTime _footerUntil = DateTime.MinValue;
        private bool _pendingShown;
        private bool _topmostOn = true;
        private string _pendingCommandFull = "";

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
        private TextBlock _stateLine;
        private Border _budgetTrack;
        private Border _budgetFill;
        private TextBlock _footer;
        private Border _pendingCard;
        private TextBlock _pendingTitle;
        private TextBlock _pendingTimer;
        private TextBlock _pendingCommand;
        private Border _pendingAction;
        private Border _accentRail;
        private Run _todaySessionsMain;
        private Run _todaySessionsPct;
        private ScaleTransform _scale;
        private TranslateTransform _shift;
        private TranslateTransform _bob;

        private DispatcherTimer _frameTimer;
        private DispatcherTimer _dataTimer;
        private DispatcherTimer _hideTimer;
        private DispatcherTimer _lifeTimer;
        private DispatcherTimer _skinTimer;
        private DispatcherTimer _eventTimer;
        private DispatcherTimer _clockTimer;

        private readonly string _soundsDir;
        private SoundPlayer _doneSound;
        private SoundPlayer _alertSound;
        private bool _soundOn = true;
        private string _lastEmotion = "calm";

        // ---- live state (fed by the hook bus, read by the UI thread) ----
        private TcpListener _bus;
        private Thread _busThread;
        private int _busPort;
        private volatile string _busState = "idle";
        private volatile string _busTool = "";
        private volatile string _busDetail = "";
        // 用 Now 而不是 MinValue：MinValue 下 (now - t) 会是个天文数字，
        // /state 里会打出一个 639 亿秒的 age，看着像 bug。
        private DateTime _stateChangedAt = DateTime.Now;
        private string _shownState = "idle";
        private double _runSpeed = 1.0;
        private DispatcherTimer _stateTimer;

        // ---- budget (0 = not configured) ----
        private double _budgetDaily;
        private double _budgetMonthly;
        private double _budgetPct;
        private double _monthCredit;
        private double _monthPct;
        private double _todayCredit;
        private string _forecast = "";
        private double _avgDaily;
        private double _ctxPct;
        private CostWindow _costWindow;

        /**
         * 定位 assets 根目录。
         *
         * 为什么只兜 assets、不兜 baseDir：安装布局里 exe 与 assets 同级，直连即可命中；
         * 但开发布局里 exe 在 src/、assets 在仓库根，两者不同级，直连会拿到不存在的
         * src/assets，皮肤解析成 null，整个窗口空白。所以允许从 baseDir 向上找最多 3 级。
         *
         * 反过来，pet-state.txt / pet-events.txt / pet-bus.port 这些运行时文件**必须**
         * 留在 exe 旁边：hook 是按 exe 目录写事件、写游标的，baseDir 一旦跟着上移，
         * 面板和 hook 就会读写两个不同的文件，事件游标直接错乱。
         */
        private static string ResolveAssetsDir(string baseDir)
        {
            string direct = Path.Combine(baseDir, "assets");
            try
            {
                if (File.Exists(Path.Combine(direct, "skins.json"))) { return direct; }

                DirectoryInfo cursor = new DirectoryInfo(baseDir);
                for (int i = 0; i < 3; i++)
                {
                    if (cursor.Parent == null) { break; }
                    cursor = cursor.Parent;
                    string probe = Path.Combine(cursor.FullName, "assets");
                    if (File.Exists(Path.Combine(probe, "skins.json"))) { return probe; }
                }
            }
            catch (Exception) { }
            return direct;   // 都不命中就保持原行为，交给上层按「没有皮肤」处理
        }

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
            _assetsDir = ResolveAssetsDir(baseDir);
            _skinsDir = Path.Combine(_assetsDir, "skins");
            _catalogFile = Path.Combine(_assetsDir, "skins.json");
            _currentFile = Path.Combine(_skinsDir, "current.txt");
            _statusFile = Path.Combine(_skinsDir, "status.txt");
            _switchScript = Path.Combine(Path.Combine(baseDir, "tools"), "switch-skin.js");
            _eventsFile = Path.Combine(baseDir, "pet-events.txt");
            _stateFile = Path.Combine(baseDir, "pet-state.txt");
            _ledgerFile = Path.Combine(baseDir, "pet-ledger.txt");
            _personasFile = Path.Combine(_assetsDir, "personas.txt");
            _soundsDir = Path.Combine(_assetsDir, "sounds");
            _summaryFile = Path.Combine(baseDir, "pet-summary.txt");
            _weeklyFile = Path.Combine(baseDir, "pet-weekly.txt");
            _budgetFile = Path.Combine(baseDir, "pet-budget.txt");
            _portFile = Path.Combine(baseDir, "pet-bus.port");

            // 496 而不是 480：4 指标改竖排后气泡会长高，480 会把顶部切掉。
            // 窗口本身是透明的，多出的空白只是空气；Top 按 Height 反算，底部不动。
            Width = 340;
            Height = 496;
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
            Topmost = _topmostOn;
            LoadSounds();

            _loadedSkin = ReadCurrentSkinId();

            BuildUi();
            LoadFrames(ResolveFramesDir(_loadedSkin));
            SeekEventsToEnd();
            LoadBudget();
            StartTimers();
            UpdatePanel();
            StartBus();
            Closed += OnClosed;
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
            _tail.StrokeThickness = 1;
            _tail.Data = Geometry.Parse("M 0,0 L 26,0 L 13,13 Z");
            _tail.HorizontalAlignment = HorizontalAlignment.Center;
            _tail.VerticalAlignment = VerticalAlignment.Bottom;
            _tail.Margin = new Thickness(0, 0, 0, -13);
            _bubble.Children.Add(_tail);

            _bubbleBody = new Border();
            _bubbleBody.Background = BgBrush;
            _bubbleBody.BorderBrush = BorderBrushDark;
            _bubbleBody.BorderThickness = new Thickness(1);
            _bubbleBody.CornerRadius = new CornerRadius(16);
            _bubbleBody.Padding = new Thickness(15, 10, 15, 10);
            _bubbleBody.Width = 290;

            // 一次投影覆盖 body + tail，避免尾巴尖出现双影/接缝
            DropShadowEffect shadow = new DropShadowEffect();
            shadow.BlurRadius = 14;
            shadow.ShadowDepth = 4;
            shadow.Direction = 270;
            shadow.Opacity = 0.32;
            shadow.Color = Colors.Black;
            _bubble.Effect = shadow;

            StackPanel inner = new StackPanel();

            // header: emotion dot + persona name (left), time (right)
            Grid header = new Grid();

            StackPanel who = new StackPanel();
            who.Orientation = Orientation.Horizontal;
            who.HorizontalAlignment = HorizontalAlignment.Left;
            who.VerticalAlignment = VerticalAlignment.Center;

            // 2x14 rail + 6px 圆点共同承载情绪；卡片整圈描边保持中性，避免整卡闪烁
            _accentRail = new Border();
            _accentRail.Width = 2;
            _accentRail.Height = 14;
            _accentRail.CornerRadius = new CornerRadius(1);
            _accentRail.Margin = new Thickness(0, 0, 6, 0);
            _accentRail.VerticalAlignment = VerticalAlignment.Center;
            _accentRail.Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x88, 0x9E));
            who.Children.Add(_accentRail);

            _dot = new Border();
            _dot.Width = 6;
            _dot.Height = 6;
            _dot.CornerRadius = new CornerRadius(3);
            _dot.Margin = new Thickness(0, 0, 6, 0);
            _dot.VerticalAlignment = VerticalAlignment.Center;
            _dot.Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x88, 0x9E));
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

            // 实时状态行：只在有活动时出现，平时不占高度
            _stateLine = new TextBlock();
            _stateLine.FontSize = 12;
            _stateLine.Foreground = TitleBrush;
            _stateLine.Margin = new Thickness(0, 3, 0, 0);
            _stateLine.Visibility = Visibility.Collapsed;
            TextOptions.SetTextFormattingMode(_stateLine, TextFormattingMode.Display);
            inner.Children.Add(_stateLine);

            // emotion line: the main visual of the bubble
            _emotionText = new TextBlock();
            _emotionText.FontSize = 16;
            _emotionText.FontWeight = FontWeights.SemiBold;
            _emotionText.Foreground = EmotionBrush;
            _emotionText.TextWrapping = TextWrapping.Wrap;
            _emotionText.Margin = new Thickness(0, 6, 0, 10);
            _emotionText.MaxHeight = 70;
            _emotionText.TextTrimming = TextTrimming.CharacterEllipsis;
            _emotionText.Visibility = Visibility.Collapsed;
            TextOptions.SetTextFormattingMode(_emotionText, TextFormattingMode.Display);
            inner.Children.Add(_emotionText);

            // 授权卡与情绪文案共用同一布局槽位：同一时刻只显示其中一个。
            // 独立控件后靠 _pendingShown 判过期，不再依赖文案前缀。
            _pendingCard = new Border();
            _pendingCard.Visibility = Visibility.Collapsed;
            StackPanel pendingBody = new StackPanel();

            Grid pendingHead = new Grid();
            _pendingTitle = new TextBlock();
            _pendingTitle.FontSize = 13;
            _pendingTitle.FontWeight = FontWeights.Medium;
            _pendingTitle.Foreground = OverBrush;
            pendingHead.Children.Add(_pendingTitle);
            _pendingTimer = new TextBlock();
            _pendingTimer.FontSize = 10.5;
            _pendingTimer.Foreground = CaptionBrush;
            _pendingTimer.MinWidth = 32;
            _pendingTimer.TextAlignment = TextAlignment.Right;
            _pendingTimer.HorizontalAlignment = HorizontalAlignment.Right;
            pendingHead.Children.Add(_pendingTimer);
            pendingBody.Children.Add(pendingHead);

            _pendingCommand = new TextBlock();
            _pendingCommand.FontSize = 11;
            _pendingCommand.Foreground = TextBodyBrush;
            _pendingCommand.TextTrimming = TextTrimming.CharacterEllipsis;
            _pendingCommand.TextWrapping = TextWrapping.NoWrap;
            _pendingCommand.Margin = new Thickness(0, 4, 0, 0);
            _pendingCommand.Visibility = Visibility.Collapsed;
            pendingBody.Children.Add(_pendingCommand);

            _pendingAction = new Border();
            _pendingAction.Background = MenuBgBrush;
            _pendingAction.CornerRadius = new CornerRadius(4);
            _pendingAction.Padding = new Thickness(6, 3, 6, 3);
            _pendingAction.Margin = new Thickness(0, 4, 0, 10);
            _pendingAction.HorizontalAlignment = HorizontalAlignment.Left;
            _pendingAction.Cursor = Cursors.Hand;
            TextBlock actionText = new TextBlock();
            actionText.Text = "\u2192 \u70B9\u6211\u5207\u56DE WorkBuddy";
            actionText.FontSize = 11;
            actionText.FontWeight = FontWeights.Medium;
            actionText.Foreground = AccentGlowBrush;
            _pendingAction.Child = actionText;
            _pendingAction.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                FocusFromPendingCard();
            };
            _pendingAction.MouseEnter += delegate { _pendingAction.Background = MenuHoverBrush; };
            _pendingAction.MouseLeave += delegate { _pendingAction.Background = MenuBgBrush; };
            pendingBody.Children.Add(_pendingAction);

            _pendingCard.Child = pendingBody;
            inner.Children.Add(_pendingCard);

            inner.Children.Add(MakeDivider());

            // today's credits, the largest number on the card
            Grid today = new Grid();
            today.Margin = new Thickness(0, 10, 0, 10);

            StackPanel todayLeft = new StackPanel();
            todayLeft.Orientation = Orientation.Horizontal;
            todayLeft.HorizontalAlignment = HorizontalAlignment.Left;
            todayLeft.VerticalAlignment = VerticalAlignment.Bottom;

            TextBlock todayCap = new TextBlock();
            todayCap.Text = "\u4ECA\u65E5";
            todayCap.FontSize = 10.5;
            todayCap.Foreground = CaptionBrush;
            todayCap.VerticalAlignment = VerticalAlignment.Bottom;
            todayCap.Margin = new Thickness(0, 0, 6, 4);
            TextOptions.SetTextFormattingMode(todayCap, TextFormattingMode.Display);
            todayLeft.Children.Add(todayCap);

            _todayValue = new TextBlock();
            _todayValue.Text = "-";
            _todayValue.FontSize = 24;
            _todayValue.FontWeight = FontWeights.Medium;
            _todayValue.Foreground = ValueBrush;
            _todayValue.VerticalAlignment = VerticalAlignment.Bottom;
            _todayValue.Margin = new Thickness(0, 0, 5, 0);
            TextOptions.SetTextFormattingMode(_todayValue, TextFormattingMode.Display);
            todayLeft.Children.Add(_todayValue);

            TextBlock todayUnit = new TextBlock();
            todayUnit.Text = "\u79EF\u5206";
            todayUnit.FontSize = 10.5;
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

            // 「今日」和「本轮指标」之间那条线，同时充当预算进度条。
            // 只把分隔线从 1px 加粗到 3px，不再额外占版面——气泡高度是紧的，
            // 多一块 30px 的区块在长文案轮次里会把气泡顶出窗口。
            Grid budgetBar = new Grid();

            _budgetTrack = new Border();
            _budgetTrack.Height = 3;
            _budgetTrack.CornerRadius = new CornerRadius(1.5);
            _budgetTrack.Background = TrackBrush;
            budgetBar.Children.Add(_budgetTrack);

            _budgetFill = new Border();
            _budgetFill.Height = 3;
            _budgetFill.CornerRadius = new CornerRadius(1.5);
            _budgetFill.HorizontalAlignment = HorizontalAlignment.Left;
            _budgetFill.Background = OkBrush;
            _budgetFill.Width = 0;
            budgetBar.Children.Add(_budgetFill);

            inner.Children.Add(budgetBar);

            // 本轮指标：4 列竖排「标签 + 数值」，核心数据常驻，不依赖 ToolTip
            Grid metrics = new Grid();
            metrics.Margin = new Thickness(0, 6, 0, 0);
            for (int i = 0; i < 4; i++) { metrics.ColumnDefinitions.Add(new ColumnDefinition()); }

            _turnIn = AddMetric(metrics, 0, "\u8F93\u5165", "\u8F93\u5165 token\uFF08\u542B\u5DE5\u5177\u8C03\u7528\u91CD\u53D1\uFF09");
            _turnOut = AddMetric(metrics, 1, "\u8F93\u51FA", "\u8F93\u51FA token");
            _cache = AddMetric(metrics, 2, "\u7F13\u5B58", "\u6309 token \u52A0\u6743\u7684\u7F13\u5B58\u547D\u4E2D\u7387");
            _credit = AddMetric(metrics, 3, "\u79EF\u5206", "\u672C\u8F6E\u79EF\u5206");

            inner.Children.Add(metrics);

            // footer only carries transient messages such as skin switching progress
            _footer = new TextBlock();
            _footer.FontSize = 10.5;
            _footer.Foreground = CaptionBrush;
            _footer.HorizontalAlignment = HorizontalAlignment.Center;
            _footer.TextWrapping = TextWrapping.Wrap;
            _footer.Margin = new Thickness(0, 5, 0, 0);
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
            _bob = new TranslateTransform(0, 0);
            TransformGroup group = new TransformGroup();
            group.Children.Add(_scale);
            group.Children.Add(_bob);
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
            divider.Background = DividerBrush;
            return divider;
        }

        /** 本轮指标：标签在上、数值在下，竖排避免横排超出 64px 列宽 */
        private TextBlock AddMetric(Grid grid, int col, string label, string tip)
        {
            StackPanel cell = new StackPanel();
            cell.Margin = new Thickness(4, 0, 4, 0);
            cell.ToolTip = MakeDarkToolTip(cell, tip);
            Grid.SetColumn(cell, col);

            TextBlock cap = new TextBlock();
            cap.Text = label;
            cap.FontSize = 10.5;
            cap.Foreground = CaptionBrush;
            cap.TextWrapping = TextWrapping.NoWrap;
            cap.Margin = new Thickness(0, 0, 0, 2);
            TextOptions.SetTextFormattingMode(cap, TextFormattingMode.Display);
            cell.Children.Add(cap);

            TextBlock value = new TextBlock();
            value.Text = "-";
            value.FontSize = 12;
            value.FontWeight = FontWeights.Medium;
            value.Foreground = ValueBrush;
            value.TextWrapping = TextWrapping.NoWrap;
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            TextOptions.SetTextFormattingMode(value, TextFormattingMode.Display);
            cell.Children.Add(value);

            grid.Children.Add(cell);
            return value;
        }

        /** 暗色 ToolTip：属性级设置，避免深夜暗卡上弹系统白底造成眩光 */
        private static ToolTip MakeDarkToolTip(FrameworkElement owner, string text)
        {
            ToolTip tip = new ToolTip();
            tip.Content = text;
            tip.Background = MenuBgBrush;
            tip.Foreground = TextBodyBrush;
            tip.BorderBrush = BorderBrushDark;
            tip.BorderThickness = new Thickness(1);
            tip.FontSize = 12;
            tip.Padding = new Thickness(8, 6, 8, 6);
            tip.HasDropShadow = false;
            ToolTipService.SetInitialShowDelay(owner, 300);
            ToolTipService.SetShowDuration(owner, 8000);
            ToolTipService.SetHasDropShadow(owner, false);
            return tip;
        }

        private ContextMenu BuildMenu()
        {
            ContextMenu menu = new ContextMenu();

            MenuItem refresh = new MenuItem();
            refresh.Header = "\u5237\u65B0\u6570\u636E";
            refresh.Click += delegate { UpdatePanel(); };
            menu.Items.Add(refresh);

            // 今日小结：直接进剪贴板，方便粘进日报/周报
            MenuItem summary = new MenuItem();
            summary.Header = "\u590D\u5236\u4ECA\u65E5\u5C0F\u7ED3";
            summary.Click += delegate { CopySummary(); };
            menu.Items.Add(summary);

            MenuItem weekly = new MenuItem();
            weekly.Header = "\u590D\u5236\u672C\u5468\u5C0F\u7ED3";
            weekly.Click += delegate { CopyWeekly(); };
            menu.Items.Add(weekly);

            // 项目成本：本月各项目占比，独立窗口
            MenuItem cost = new MenuItem();
            cost.Header = "\u9879\u76EE\u6210\u672C\u2026";
            cost.Click += delegate { OpenCostWindow(); };
            menu.Items.Add(cost);

            menu.Items.Add(new Separator());

            // 日预算：直接写 pet-budget.txt，turn-usage.js 每轮读它判阈值
            MenuItem budgetRoot = new MenuItem();
            budgetRoot.Header = "\u65E5\u9884\u7B97";
            budgetRoot.Items.Add(MakeBudgetItem(budgetRoot, 0, "\u4E0D\u542F\u7528"));
            budgetRoot.Items.Add(MakeBudgetItem(budgetRoot, 50, "50 \u79EF\u5206"));
            budgetRoot.Items.Add(MakeBudgetItem(budgetRoot, 100, "100 \u79EF\u5206"));
            budgetRoot.Items.Add(MakeBudgetItem(budgetRoot, 200, "200 \u79EF\u5206"));
            budgetRoot.Items.Add(MakeBudgetItem(budgetRoot, 500, "500 \u79EF\u5206"));
            menu.Items.Add(budgetRoot);

            // 月预算：积分通常是按月给的额度，日预算只是「今天别失控」的粗线
            MenuItem monthRoot = new MenuItem();
            monthRoot.Header = "\u6708\u9884\u7B97";
            monthRoot.Items.Add(MakeMonthItem(monthRoot, 0, "\u4E0D\u542F\u7528"));
            monthRoot.Items.Add(MakeMonthItem(monthRoot, 500, "500 \u79EF\u5206"));
            monthRoot.Items.Add(MakeMonthItem(monthRoot, 1000, "1000 \u79EF\u5206"));
            monthRoot.Items.Add(MakeMonthItem(monthRoot, 2000, "2000 \u79EF\u5206"));
            monthRoot.Items.Add(MakeMonthItem(monthRoot, 5000, "5000 \u79EF\u5206"));
            menu.Items.Add(monthRoot);

            // skins：先按当前清单建一次，之后每次打开菜单再对齐（见 SyncSkinMenu）
            List<string[]> skins = ReadCatalog();
            if (skins.Count > 0)
            {
                _skinRoot = new MenuItem();
                _skinRoot.Header = "\u6362\u5F62\u8C61";
                FillSkinMenu(_skinRoot, skins);
                menu.Items.Add(_skinRoot);
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
            verbRoot.Items.Add(MakeVerbosityItem("normal", "\u514B\u5236\uFF08\u9ED8\u8BA4\uFF09"));
            verbRoot.Items.Add(MakeVerbosityItem("chatty", "\u8BDD\u75E8"));
            verbRoot.Items.Add(MakeVerbosityItem("minimal", "\u6781\u7B80"));
            menu.Items.Add(verbRoot);

            // sound
            MenuItem soundRoot = new MenuItem();
            soundRoot.Header = "\u63D0\u793A\u97F3";

            MenuItem soundOn = new MenuItem();
            soundOn.Header = "\u5F00";
            soundOn.IsChecked = _soundOn;
            soundOn.Click += delegate { SetSound(true, soundRoot); };
            soundRoot.Items.Add(soundOn);

            MenuItem soundOff = new MenuItem();
            soundOff.Header = "\u5173";
            soundOff.IsChecked = !_soundOn;
            soundOff.Click += delegate { SetSound(false, soundRoot); };
            soundRoot.Items.Add(soundOff);

            soundRoot.Items.Add(new Separator());

            MenuItem soundTest = new MenuItem();
            soundTest.Header = "\u8BD5\u542C";
            soundTest.Click += delegate
            {
                try { if (_doneSound != null) { _doneSound.Play(); } }
                catch (Exception) { }
            };
            soundRoot.Items.Add(soundTest);

            menu.Items.Add(soundRoot);

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
            hide.Header = "\u9690\u85CF 10 \u5206\u949F";
            hide.Click += delegate
            {
                _hideUntil = DateTime.Now.AddMinutes(10);
                Hide();
            };
            menu.Items.Add(hide);

            MenuItem topmost = new MenuItem();
            topmost.Header = "\u7F6E\u9876\u663E\u793A";
            topmost.IsCheckable = true;
            topmost.IsChecked = _topmostOn;
            topmost.Click += delegate
            {
                _topmostOn = !_topmostOn;
                Topmost = _topmostOn;
                topmost.IsChecked = _topmostOn;
                _lastSaveAt = DateTime.MinValue;
                SaveState();
            };
            menu.Items.Add(topmost);

            menu.Items.Add(new Separator());

            MenuItem exit = new MenuItem();
            exit.Header = "\u9000\u51FA";
            exit.Click += delegate { Close(); };
            menu.Items.Add(exit);

            // 系统 Aero2 模板硬编码悬停色/勾选 glyph/分隔线，属性层拿不到，
            // 因此采用属性级暗色化 + 文本前缀承担选中态，接受系统悬停色。
            menu.Opened += delegate
            {
                SyncSkinMenu();
                RefreshMenuChecks(menu);
            };
            StyleMenu(menu);
            return menu;
        }

        private static void StyleMenu(ContextMenu menu)
        {
            menu.Background = MenuBgBrush;
            menu.Foreground = TextBodyBrush;
            menu.BorderBrush = BorderBrushDark;
            menu.BorderThickness = new Thickness(1);
            menu.FontSize = 12;
            StyleMenuItems(menu.Items);
        }

        private static void StyleMenuItems(ItemCollection items)
        {
            foreach (object o in items)
            {
                MenuItem mi = o as MenuItem;
                if (mi == null) { continue; }
                mi.Background = Brushes.Transparent;
                mi.Foreground = TextBodyBrush;
                mi.FontSize = 12;
                mi.Height = 28;
                StyleMenuItems(mi.Items);
            }
        }

        /** 选中态用「● 」文本前缀：跨主题可见、读屏可处理 */
        private static void RefreshMenuChecks(ContextMenu menu)
        {
            RefreshChecks(menu.Items);
        }

        private static void RefreshChecks(ItemCollection items)
        {
            foreach (object o in items)
            {
                MenuItem mi = o as MenuItem;
                if (mi == null) { continue; }
                string h = mi.Header as string;
                if (h != null)
                {
                    string bare = h.StartsWith("\u25CF ") ? h.Substring(2) : h;
                    mi.Header = (mi.IsChecked ? "\u25CF " : "") + bare;
                }
                RefreshChecks(mi.Items);
            }
        }

        // ---------- budget / summary ----------
        private MenuItem MakeBudgetItem(MenuItem root, double value, string label)
        {
            return MakePresetItem(root, value, label, false);
        }

        private MenuItem MakeMonthItem(MenuItem root, double value, string label)
        {
            return MakePresetItem(root, value, label, true);
        }

        private MenuItem MakePresetItem(MenuItem root, double value, string label, bool monthly)
        {
            MenuItem item = new MenuItem();
            item.Header = label;
            item.Tag = value;
            item.IsCheckable = true;
            item.IsChecked = Math.Abs((monthly ? _budgetMonthly : _budgetDaily) - value) < 0.01;
            item.Click += delegate
            {
                foreach (object o in root.Items)
                {
                    MenuItem mi = o as MenuItem;
                    if (mi != null) { mi.IsChecked = false; }
                }
                item.IsChecked = true;
                if (monthly) { SetBudget(value, _budgetDaily); }
                else { SetBudget(_budgetMonthly, value); }
            };
            return item;
        }

        /** 读 pet-budget.txt 里的 daily / monthly；缺失或为 0 就是不启用 */
        private void LoadBudget()
        {
            _budgetDaily = 0;
            _budgetMonthly = 0;
            try
            {
                if (!File.Exists(_budgetFile)) { return; }
                foreach (string line in File.ReadAllLines(_budgetFile))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) { continue; }
                    string k = line.Substring(0, i).Trim();
                    double v;
                    if (!double.TryParse(line.Substring(i + 1).Trim(), out v) || v <= 0) { continue; }
                    if (k == "daily") { _budgetDaily = v; }
                    if (k == "monthly") { _budgetMonthly = v; }
                }
            }
            catch (Exception) { }
        }

        /** 菜单选中后立刻落盘，下一轮 turn-usage.js 就会读到新阈值 */
        private void SetBudget(double monthly, double daily)
        {
            _budgetDaily = daily;
            _budgetMonthly = monthly;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("daily=").Append(daily.ToString("0", CultureInfo.InvariantCulture)).Append("\n");
                sb.Append("monthly=").Append(monthly.ToString("0", CultureInfo.InvariantCulture)).Append("\n");
                File.WriteAllText(_budgetFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
            // 立刻按新额度重算百分比，别等下一次 2 秒轮询才变
            _budgetPct = _budgetDaily > 0 ? (_todayCredit / _budgetDaily) * 100 : 0;
            _monthPct = _budgetMonthly > 0 ? (_monthCredit / _budgetMonthly) * 100 : 0;
            UpdateBudgetUi();
            // 设置变更必须可见：用户才有机会发现月预算被误改
            _footer.Text = "\u9884\u7B97\u5DF2\u66F4\u65B0 \u00B7 \u65E5 " + _budgetDaily.ToString("0")
                + " / \u6708 " + _budgetMonthly.ToString("0") + " \u79EF\u5206";
            _footer.Visibility = Visibility.Visible;
            _footerUntil = DateTime.Now.AddSeconds(4);
            ShowBubbleFor(3);
        }

        /** 把 pet-summary.txt / pet-weekly.txt 整段复制走 */
        private void CopySummary() { CopyFileToClipboard(_summaryFile, "\u4ECA\u65E5\u5C0F\u7ED3", "\u8FD8\u6CA1\u6709\u5C0F\u7ED3\uFF0C\u5148\u8DD1\u4E00\u8F6E\u5BF9\u8BDD"); }

        private void CopyWeekly() { CopyFileToClipboard(_weeklyFile, "\u672C\u5468\u5C0F\u7ED3", "\u8FD8\u6CA1\u6709\u5468\u62A5\uFF0C\u5148\u8DD1\u4E00\u8F6E\u5BF9\u8BDD"); }

        private void CopyFileToClipboard(string path, string label, string emptyHint)
        {
            string text = "";
            try
            {
                if (File.Exists(path)) { text = File.ReadAllText(path, Encoding.UTF8).Trim(); }
            }
            catch (Exception) { }

            if (text.Length == 0)
            {
                _footer.Text = emptyHint;
                _footer.Visibility = Visibility.Visible;
                ShowBubbleFor(4);
                return;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    _footer.Text = label + "\u5DF2\u590D\u5236";
                    _footer.Visibility = Visibility.Visible;
                    ShowBubbleFor(4);
                    Bounce(7, 1, 220);
                    return;
                }
                catch (Exception) { Thread.Sleep(60); }
            }
            _footer.Text = "\u526A\u8D34\u677F\u88AB\u5360\u7528\uFF0C\u7A0D\u540E\u518D\u8BD5";
            _footer.Visibility = Visibility.Visible;
        }

        /** 项目成本看板：单例，再点一次就刷到最新并置前 */
        private void OpenCostWindow()
        {
            try
            {
                if (_costWindow != null)
                {
                    _costWindow.Refresh();
                    _costWindow.Activate();
                    return;
                }
                CostWindow win = new CostWindow(_baseDir, _dataFile);
                win.Closed += delegate { _costWindow = null; };
                // 先粗摆一个位置，CostWindow 在 Loaded 里按真实高度再钳一次
                win.Left = Left - 400;
                // 窗口加高 16px 后 Top 上移，粗摆值同步补偿，保持成本卡屏幕位置不变
                win.Top = Top + 56;
                _costWindow = win;
                win.Show();
            }
            catch (Exception ex) { LogError("OpenCostWindow", ex); }
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

        private void SetSound(bool on, MenuItem root)
        {
            _soundOn = on;
            // 立刻落盘，跳过 SaveState 的防抖
            _lastSaveAt = DateTime.MinValue;
            SaveState();
            if (root == null) { return; }
            for (int i = 0; i < root.Items.Count && i < 2; i++)
            {
                MenuItem mi = root.Items[i] as MenuItem;
                if (mi != null) { mi.IsChecked = (i == 0) == on; }
            }
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

        // ---------- sound ----------
        /** 预加载两个提示音：完成（清脆）与告警（低沉），缺失时静默降级 */
        private void LoadSounds()
        {
            try
            {
                string done = Path.Combine(_soundsDir, "turn-done.wav");
                string alert = Path.Combine(_soundsDir, "turn-alert.wav");
                if (File.Exists(done))
                {
                    _doneSound = new SoundPlayer(done);
                    _doneSound.Load();
                }
                if (File.Exists(alert))
                {
                    _alertSound = new SoundPlayer(alert);
                    _alertSound.Load();
                }
            }
            catch (Exception) { }
        }

        /** 每轮结束响一声；静音期与关闭音效时不响 */
        private void PlayDoneSound()
        {
            if (!_soundOn) { return; }
            if (DateTime.Now < _quietUntil) { return; }
            try
            {
                SoundPlayer player = _lastEmotion == "worried" && _alertSound != null ? _alertSound : _doneSound;
                if (player != null) { player.Play(); }
            }
            catch (Exception) { }
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

        /**
         * 皮肤加载的唯一入口，两种形态互斥：
         *   manifest 里 kind=atlas  → 单张雪碧图 + CroppedBitmap 按状态取帧（团团 / 可可用这条）
         *   manifest 里没有 kind    → 目录内逐帧 PNG + delays.txt 轮播（官方网络形象等旧契约，保持不变）
         * 切肤时必须先把两种状态都清干净：残留的 _frameTimer 会把旧帧盖到新 Source 上，
         * 残留的 _emoSlot 会让新角色一上来就摆着上一只的表情。
         */
        private void LoadFrames(string dir)
        {
            _atlas = null;
            _atlasSlot = -1;
            _emoSlot = -1;
            _emoSlotUntil = DateTime.MinValue;
            _atlasPhase = 0;
            if (_atlasTimer != null) { _atlasTimer.Stop(); }
            _frames.Clear();
            _delays.Clear();
            _idx = 0;

            if (_frameTimer != null && _frameTimer.IsEnabled) { _frameTimer.Stop(); }

            if (dir == null)
            {
                _mascot.Source = null;
                return;
            }

            try
            {
                string manifestFile = Path.Combine(dir, "manifest.json");
                string manifestRaw = File.Exists(manifestFile) ? File.ReadAllText(manifestFile) : "";
                if (JsonString(manifestRaw, "kind") == "atlas")
                {
                    LoadAtlasSkin(dir, manifestRaw);
                    return;
                }

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

                _mascot.Width = 190;    // 从 atlas 切回逐帧时复位
                _mascot.Height = 208;

                if (_frames.Count > 0)
                {
                    _mascot.Source = _frames[0];
                    if (_frameTimer != null && _frames.Count > 1 && !_frameTimer.IsEnabled)
                    {
                        _frameTimer.Start();
                    }
                }
                else
                {
                    _mascot.Source = null;
                }
            }
            catch (Exception) { }
        }

        /**
         * atlas 皮肤加载：image 按 manifest 所在目录解析（相对路径），
         * 两只角色共用同一张图，所以仓库和发布包里都只存一份 1.65MB 的 PNG。
         * 出错只影响形象，不影响面板：置空 Source 并落盘日志。
         */
        private void LoadAtlasSkin(string dir, string raw)
        {
            try
            {
                string imageRel = JsonString(raw, "image");
                if (imageRel.Length == 0) { throw new ArgumentException("manifest 缺少 image 字段"); }

                string imagePath = Path.GetFullPath(Path.Combine(dir, imageRel));
                int cell = JsonInt(raw, "cell", 256);
                int columns = JsonInt(raw, "columns", 8);
                int rows = JsonInt(raw, "rows", 4);
                int baseIndex = JsonInt(raw, "base", 0);
                double width = JsonInt(raw, "w", 200);
                double height = JsonInt(raw, "h", 200);

                // 可选 seq：给槽位配「帧序号列表@帧间隔」。没有这个字段的老图集
                // 依旧是一槽一帧，下面的动画定时器就不会启动。
                Dictionary<int, int[]> seq;
                Dictionary<int, int> seqMs;
                ParseSeq(raw, out seq, out seqMs);

                _atlas = PetAtlas.Load(imagePath, cell, columns, rows, baseIndex, seq, seqMs);

                _mascot.Width = width;
                _mascot.Height = height;

                int slot = AtlasSlotForState(_shownState);
                _atlasSlot = slot;
                ApplyAtlasSource(slot, true);

                // 刚启动时 state 与 _shownState 都是 idle，RenderRunState 的「状态变了」分支
                // 不会执行，SetBob 也就不会被调用——atlas 形象会是一张完全静止的贴图。
                // 这里补一次呼吸动画的建立（切肤路径同理，见 _skinTimer.Tick）。
                SetBob(_shownState);
            }
            catch (Exception ex)
            {
                _atlas = null;
                _atlasSlot = -1;
                _mascot.Source = null;
                LogError("LoadAtlasSkin", ex);
            }
        }

        /** 项目一贯不引 JSON 库，manifest 里只取几个标量字段，正则足够 */
        private static string JsonString(string raw, string key)        {
            if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(key)) { return ""; }
            Match m = Regex.Match(raw, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        private static int JsonInt(string raw, string key, int fallback)
        {
            if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(key)) { return fallback; }
            Match m = Regex.Match(raw, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9]+)");
            int value;
            if (m.Success && int.TryParse(m.Groups[1].Value, out value)) { return value; }
            return fallback;
        }

        /**
         * manifest 的可选 seq 段：槽位 → "帧序号,帧序号@帧间隔"。
         * 例如 "1": "21,22,23@260" 表示槽位 1 依次播 21/22/23 三帧，每帧 260ms。
         * 用这种扁平格式而不是嵌套对象，是为了继续用正则解析、不引 JSON 库。
         * 没有 seq 的图集（团团 / 可可）解析结果为空字典，动画层自动不启用。
         */
        private static void ParseSeq(string raw, out Dictionary<int, int[]> seq, out Dictionary<int, int> seqMs)
        {
            seq = new Dictionary<int, int[]>();
            seqMs = new Dictionary<int, int>();
            if (string.IsNullOrEmpty(raw)) { return; }

            MatchCollection ms = Regex.Matches(raw, "\"(\\d{1,2})\"\\s*:\\s*\"([0-9, @]{1,240})\"");
            foreach (Match m in ms)
            {
                int slot;
                if (!int.TryParse(m.Groups[1].Value, out slot) || slot < 0 || slot >= 16) { continue; }

                string spec = m.Groups[2].Value;
                int interval = 260;
                int at = spec.IndexOf('@');
                if (at >= 0)
                {
                    int v;
                    if (int.TryParse(spec.Substring(at + 1).Trim(), out v) && v > 0) { interval = v; }
                    spec = spec.Substring(0, at);
                }

                List<int> list = new List<int>();
                foreach (string part in spec.Split(','))
                {
                    int idx;
                    if (int.TryParse(part.Trim(), out idx) && idx >= 0) { list.Add(idx); }
                }
                if (list.Count == 0) { continue; }

                seq[slot] = list.ToArray();
                seqMs[slot] = interval;
            }
        }

        /** 状态 → 逻辑帧序号（0–15），与素材对应说明.md 的表格一一对应 */
        private static int AtlasSlotForState(string state)
        {
            switch (state)
            {
                case "thinking": return 0;
                case "working": return 1;
                case "juggling": return 2;
                case "compacting": return 3;
                case "error": return 4;
                case "done": return 5;
                case "blocked": return 6;
                case "sleeping": return 7;
                case "attention": return 8;
                default: return 15;   // idle / 未知 → calm
            }
        }

        /**
         * 情绪 → 逻辑帧序号（9–15），未知返回 -1 表示「这次不给表情帧」。
         * calm 故意不单独占帧：它就是 idle 的默认帧（15），
         * 真给了反而会在时限内把 thinking / working 的帧盖成「平静」。
         */
        private static int AtlasSlotForEmotion(string emotion)
        {
            switch (emotion)
            {
                case "happy": return 9;
                case "proud": return 10;
                case "surprised": return 11;
                case "worried": return 12;
                case "tender": return 13;
                case "sleepy": return 14;
                default: return -1;
            }
        }

        /** 打断型状态优先级最高，必须压过一次性情绪帧（见素材说明的分层规则） */
        private static bool IsInterruptState(string state)
        {
            return state == "error" || state == "blocked" || state == "attention";
        }

        /**
         * 状态帧 / 情绪帧的仲裁：
         *   1. 打断型状态最优先，情绪帧不许盖它；
         *   2. 情绪帧在 _emoSlotUntil 之前有效，到点自动回到状态帧；
         *   3. 休眠且 idle 时显示 sleeping 帧。
         * 每 500ms 被 RenderRunState 调一次，所以靠 slot 比较去抖——重复赋值会白触发重绘。
         */
        private void ApplyPetFrame()
        {
            if (_atlas == null) { return; }   // 逐帧皮肤走 _frameTimer，这里什么都不做

            DateTime now = DateTime.Now;
            int slot;
            if (!IsInterruptState(_shownState) && _emoSlot >= 0 && now < _emoSlotUntil)
            {
                slot = _emoSlot;
            }
            else if (_sleepMode && _shownState == "idle")
            {
                slot = 7;
            }
            else
            {
                slot = AtlasSlotForState(_shownState);
            }

            if (slot == _atlasSlot && _mascot.Source != null) { return; }

            _atlasSlot = slot;
            ApplyAtlasSource(slot, true);
        }

        /**
         * 把某个逻辑槽位贴到 _mascot 上。
         * 槽位只有单帧时就是一张静态图（老图集行为）；有多帧时从第 0 帧起播，
         * 并启动 _atlasTimer 按 manifest 给的间隔往后走。
         * restart=false 只在定时器推进内部使用，保留当前 phase。
         */
        private void ApplyAtlasSource(int slot, bool restart)
        {
            if (_atlas == null) { return; }
            if (restart) { _atlasPhase = 0; }

            int count = _atlas.SeqCount(slot);
            if (count <= 1)
            {
                _atlasPhase = 0;
                _mascot.Source = _atlas.GetFrame(slot, 0);
                if (_atlasTimer != null) { _atlasTimer.Stop(); }
                return;
            }

            _mascot.Source = _atlas.GetFrame(slot, _atlasPhase);
            StartAtlasTimer(_atlas.SeqMs(slot));
        }

        /**
         * 帧间隔复用 _runSpeed：执行工具时吃得快、思考时张望得慢，
         * 和时间轴上的运动语言保持一致；休眠时整体放慢，别在半夜抽动。
         */
        private void StartAtlasTimer(int baseMs)
        {
            if (_atlasTimer == null) { return; }
            double ms = baseMs * (_runSpeed > 0 ? _runSpeed : 1.0);
            if (_sleepMode) { ms *= 2.2; }
            if (ms < 45) { ms = 45; }
            _atlasTimer.Interval = TimeSpan.FromMilliseconds(ms);
            if (!_atlasTimer.IsEnabled) { _atlasTimer.Start(); }
        }

        private void OnAtlasTick()
        {
            if (_atlas == null || _atlasTimer == null) { if (_atlasTimer != null) { _atlasTimer.Stop(); } return; }

            int count = _atlas.SeqCount(_atlasSlot);
            if (count <= 1) { _atlasTimer.Stop(); return; }

            _atlasPhase = (_atlasPhase + 1) % count;
            _mascot.Source = _atlas.GetFrame(_atlasSlot, _atlasPhase);
            StartAtlasTimer(_atlas.SeqMs(_atlasSlot));
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

        /** 换形象子菜单的唯一生产者：启动时初次填充、清单变化时重建，都走这里 */
        private void FillSkinMenu(MenuItem root, List<string[]> skins)
        {
            root.Items.Clear();
            _skinItems.Clear();
            StringBuilder sig = new StringBuilder();
            foreach (string[] s in skins)
            {
                MenuItem item = new MenuItem();
                item.Header = s[1];
                item.Tag = s[0];
                // StyleMenu 只覆盖建菜单那一刻已存在的项，后加的要自己上色
                item.Background = Brushes.Transparent;
                item.Foreground = TextBodyBrush;
                item.FontSize = 12;
                item.Height = 28;
                if (s[0] == _loadedSkin)
                {
                    item.IsChecked = true;
                    item.FontWeight = FontWeights.Bold;
                }
                item.Click += OnSkinClick;
                _skinItems[s[0]] = item;
                root.Items.Add(item);
                sig.Append(s[0]).Append(',');
            }
            _skinSig = sig.ToString();
        }

        /**
         * 打开菜单时对齐清单。skins.json 在运行期是会被改的（换形象脚本、手工加形象），
         * 只在构造时读一次的话，新形象要重启面板才出现在菜单里。
         * 清单变了就重建子菜单，没变只同步勾选态——顺带修掉「用命令行切换后菜单还勾着旧形象」。
         */
        private void SyncSkinMenu()
        {
            if (_skinRoot == null) { return; }
            List<string[]> skins = ReadCatalog();
            if (skins.Count == 0) { return; }

            StringBuilder sig = new StringBuilder();
            foreach (string[] s in skins) { sig.Append(s[0]).Append(','); }
            if (sig.ToString() != _skinSig)
            {
                FillSkinMenu(_skinRoot, skins);
                return;
            }

            foreach (KeyValuePair<string, MenuItem> kv in _skinItems)
            {
                bool on = kv.Key == _loadedSkin;
                kv.Value.IsChecked = on;
                kv.Value.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
            }
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

            // 预算超支 / 数据降级 / 等待授权不被悬停挂起，必须立刻可见
            bool urgent = ev.Pri >= 3 || _busState == "blocked";
            if (IsPointerOverPanel() && !urgent)
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
                if (_verbosity != "chatty" && _lastBubbleAt != DateTime.MinValue && (now - _lastBubbleAt).TotalSeconds < 45) { return false; }

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
            _lastEmotion = ev.Emotion;

            ApplyAccent(ev.Accent);
            _title.Text = PersonaName();
            _pendingCard.Visibility = Visibility.Collapsed;
            _pendingShown = false;
            _emotionText.Text = ev.Text;
            _emotionText.Visibility = Visibility.Visible;
            _tempUntil = now.AddSeconds(Math.Max(3, ev.DurSec));

            PlayMotion(ev.Motion);

            // 情绪帧：atlas 皮肤额外贴一帧表情，逐帧皮肤没有这一层（ApplyPetFrame 直接返回）。
            // 不用显式「回退」——ApplyPetFrame 里 _emoSlotUntil 到期后自然回到状态帧。
            int emoSlot = AtlasSlotForEmotion(ev.Emotion);
            if (emoSlot >= 0)
            {
                _emoSlot = emoSlot;
                _emoSlotUntil = now.AddSeconds(Math.Max(1.2, ev.DurSec) + 0.2);
                ApplyPetFrame();
            }

            ShowBubbleFor(ev.DurSec);
            SaveState();
        }

        /** 情绪色落在 6px 圆点 / 2px rail / 尾巴描边；卡片整圈描边保持中性避免闪烁 */
        private void ApplyAccent(string hex)
        {
            try
            {
                Color color = (Color)ColorConverter.ConvertFromString(hex);
                // 三处各自新建画刷，绝不复用 static Frozen 的 BorderBrushDark
                _dot.Background = new SolidColorBrush(color);
                _accentRail.Background = new SolidColorBrush(color);
                Color tailColor = color;
                tailColor.A = 191;
                _tail.Stroke = new SolidColorBrush(tailColor);
            }
            catch (Exception) { }
        }

        /** 事件结束 / 回平静 / 授权卡过期时复位，避免尾巴残留上一次情绪色 */
        private void ResetAccent()
        {
            try
            {
                _dot.Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x88, 0x9E));
                _accentRail.Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x88, 0x9E));
                _tail.Stroke = BorderBrushDark;
            }
            catch (Exception) { }
        }

        private void ShowBubbleFor(int seconds)
        {
            DateTime now = DateTime.Now;
            int secs = Math.Max(2, seconds);
            _tempUntil = now.AddSeconds(secs);   // 保护期：期间移开鼠标不收起
            _autoUntil = now.AddSeconds(secs);   // 到期：主动收起（修复气泡永久挂屏）
            _hideTimer.Stop();
            _bubble.Visibility = Visibility.Visible;
        }

        // ---------- hook bus (live state) ----------
        /**
         * 面板自己起一个极简 HTTP 服务，hook 用 curl 把事件 POST 过来。
         *
         * 为什么是 TcpListener 而不是 HttpListener：后者走 HTTP.sys，非管理员
         * 账号必须先 netsh 注册 urlacl 才能 Start()，否则直接 AccessDenied。
         * 绑 127.0.0.1 的裸 socket 不需要任何权限，也不会弹防火墙。
         *
         * 端口写在 pet-bus.port，pet-hook.cmd 读它；端口占用时依次后移。
         */
        private void StartBus()
        {
            for (int port = 17871; port <= 17890; port++)
            {
                try
                {
                    TcpListener listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    _bus = listener;
                    _busPort = port;
                    break;
                }
                catch (Exception) { }
            }
            if (_bus == null) { return; }

            try
            {
                File.WriteAllText(_portFile,
                    _busPort.ToString(CultureInfo.InvariantCulture) + "\n", new UTF8Encoding(false));
            }
            catch (Exception) { }

            _busThread = new Thread(BusLoop);
            _busThread.IsBackground = true;
            _busThread.Start();
        }

        private void BusLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = _bus.AcceptTcpClient(); }
                catch (Exception) { return; }   // listener 已停止
                try
                {
                    ThreadPool.QueueUserWorkItem(delegate(object o) { HandleBusClient((TcpClient)o); }, client);
                }
                catch (Exception) { try { client.Close(); } catch (Exception) { } }
            }
        }

        private void HandleBusClient(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 3000;
                    NetworkStream ns = client.GetStream();
                    ns.ReadTimeout = 3000;
                    ns.WriteTimeout = 3000;

                    byte[] buf = new byte[8192];
                    int len = 0;
                    int headerEnd = -1;
                    while (len < buf.Length)
                    {
                        int n = ns.Read(buf, len, buf.Length - len);
                        if (n <= 0) { break; }
                        len += n;
                        headerEnd = IndexOfHeaderEnd(buf, len);
                        if (headerEnd >= 0) { break; }
                    }
                    if (headerEnd < 0) { return; }

                    string head = Encoding.ASCII.GetString(buf, 0, headerEnd);
                    int contentLength = HeaderValue(head, "content-length");
                    int bodyStart = headerEnd + 4;

                    // 事件体只用于取 tool_name，截断到 4KB 就够；大 payload（写文件）不整包读
                    StringBuilder body = new StringBuilder();
                    int already = len - bodyStart;
                    if (already > 0)
                    {
                        int take = already > 4096 ? 4096 : already;
                        body.Append(Encoding.UTF8.GetString(buf, bodyStart, take));
                    }
                    int remain = contentLength - already;
                    if (remain > 0)
                    {
                        int cap = remain > 4096 ? 4096 : remain;
                        byte[] rest = new byte[cap];
                        int got = 0;
                        while (got < cap)
                        {
                            int n = ns.Read(rest, got, cap - got);
                            if (n <= 0) { break; }
                            got += n;
                        }
                        body.Append(Encoding.UTF8.GetString(rest, 0, got));
                    }

                    string path = "";
                    Match line = Regex.Match(head, "^[A-Z]+\\s+(\\S+)");
                    if (line.Success) { path = line.Groups[1].Value; }

                    // GET /state：自检用，能直接看到面板现在认的是什么状态
                    if (path.StartsWith("/state"))
                    {
                        long age = (long)(DateTime.Now - _stateChangedAt).TotalSeconds;
                        string probe = "state=" + _shownState + "\ntool=" + _busTool
                            + "\ndetail=" + _busDetail + "\nage=" + age + "\nport=" + _busPort + "\n";
                        byte[] pbody = Encoding.UTF8.GetBytes(probe);
                        string phead = "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: "
                            + pbody.Length + "\r\nConnection: close\r\n\r\n";
                        byte[] pheadBytes = Encoding.ASCII.GetBytes(phead);
                        ns.Write(pheadBytes, 0, pheadBytes.Length);
                        ns.Write(pbody, 0, pbody.Length);
                        return;
                    }

                    byte[] resp = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 204 No Content\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                    ns.Write(resp, 0, resp.Length);

                    string bodyText = body.ToString();
                    string eventName = QueryValue(path, "n");
                    if (eventName.Length == 0) { eventName = MatchJson(bodyText, "hook_event_name"); }
                    string tool = MatchJson(bodyText, "tool_name");
                    string detail = MatchCommand(bodyText);

                    ApplyHookEvent(eventName, tool, detail);
                }
            }
            catch (Exception) { }
        }

        /** hook 事件名 → 面板状态；未知事件直接忽略 */
        private static string StateForEvent(string name)
        {
            switch (name)
            {
                case "UserPromptSubmit": return "thinking";
                case "PreToolUse": return "working";
                case "PostToolUse": return "working";
                case "PostToolUseFailure": return "error";
                case "StopFailure": return "error";
                case "SubagentStart": return "juggling";
                case "SubagentStop": return "working";
                case "PreCompact": return "compacting";
                case "PostCompact": return "thinking";
                case "Notification": return "attention";
                case "PermissionRequest": return "blocked";
                case "Elicitation": return "blocked";
                case "Stop": return "done";
                case "SessionStart": return "idle";
                case "SessionEnd": return "sleeping";
                default: return "";
            }
        }

        private void ApplyHookEvent(string eventName, string tool, string detail)
        {
            string state = StateForEvent(eventName);
            if (state.Length == 0) { return; }
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _busState = state;
                    _busTool = tool;
                    _busDetail = detail;
                    _stateChangedAt = DateTime.Now;
                }));
            }
            catch (Exception) { }
        }

        private static int IndexOfHeaderEnd(byte[] buf, int len)
        {
            for (int i = 3; i < len; i++)
            {
                if (buf[i - 3] == 13 && buf[i - 2] == 10 && buf[i - 1] == 13 && buf[i] == 10) { return i - 3; }
            }
            return -1;
        }

        private static int HeaderValue(string head, string name)
        {
            Match m = Regex.Match(head, "(?im)^" + name + ":\\s*(\\d+)");
            int v;
            return m.Success && int.TryParse(m.Groups[1].Value, out v) ? v : 0;
        }

        private static string QueryValue(string path, string key)
        {
            Match m = Regex.Match(path, "[?&]" + key + "=([^&\\s]+)");
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : "";
        }

        /** 只取两个字段，不做完整 JSON 解析：C# 侧没有 JSON 库，也不值得为此引一个 */
        private static string MatchJson(string text, string key)
        {
            if (text.Length == 0) { return ""; }
            Match m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\"([^\"]{0,60})\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        /**
         * 从 tool_input 里捞一条命令行预览，用于「等待你确认」时告诉你在确认什么。
         * 只认 `"command":"..."`，够覆盖 Bash / Shell；其他工具没有这个字段就返回空。
         */
        private static string MatchCommand(string text)
        {
            if (text.Length == 0) { return ""; }
            Match m = Regex.Match(text, "\"command\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.){0,300})\"");
            if (!m.Success) { return ""; }
            string s = m.Groups[1].Value;
            s = s.Replace("\\n", " ").Replace("\\r", " ").Replace("\\t", " ");
            s = s.Replace("\\\"", "\"").Replace("\\\\", "\\").Trim();
            return s;
        }

        /**
         * 状态 → 运动语言。没有为每个状态准备美术资源，所以状态只能靠
         * 节奏、位移和一行文案表达：
         *   思考=慢、执行=快而微颠、并行=持续起伏、整理=最慢、出错=抖、
         *   完成=跳一下、等待确认=挤压、会话结束=变暗。
         */
        private void RenderRunState()
        {
            DateTime now = DateTime.Now;
            string state = _busState;

            // 兜底：WorkBuddy 崩了或 hook 断了，别让它永远停在「执行中」
            double age = (now - _stateChangedAt).TotalSeconds;
            if ((state == "working" || state == "thinking" || state == "juggling") && age > 150) { state = "idle"; }
            if (state == "compacting" && age > 240) { state = "idle"; }
            if (state == "done" && age > 20) { state = "idle"; }
            if (state == "error" && age > 6) { state = "idle"; }
            if (state == "attention" && age > 30) { state = "idle"; }
            if (state == "blocked" && age > 180) { state = "idle"; }
            if (state == "sleeping" && age > 300) { state = "idle"; }

            bool changed = state != _shownState;
            string prev = _shownState;
            _shownState = state;

            switch (state)
            {
                case "working": _runSpeed = 0.62; break;
                case "juggling": _runSpeed = 0.62; break;
                case "thinking": _runSpeed = 1.55; break;
                case "compacting": _runSpeed = 1.95; break;
                case "blocked": _runSpeed = 1.20; break;
                default: _runSpeed = 1.0; break;
            }

            if (changed)
            {
                // 授权卡过期要主动收掉，否则已经点了「允许」气泡还挂在那里骗人
                if (prev == "blocked" && state != "blocked" && _pendingShown)
                {
                    DismissPendingCard();
                }

                SetBob(state);
                if (state == "error") { Shake(); }
                else if (state == "done") { Bounce(9, 2, 280); }
                else if (state == "blocked") { ShowPendingCard(); }
            }

            UpdateStateLine(state);

            // 状态帧跟随：atlas 皮肤靠这里每 500ms 对齐一次，逐帧皮肤是空操作
            try { ApplyPetFrame(); }
            catch (Exception ex) { LogError("ApplyPetFrame", ex); }
        }

        /**
         * 「轻提醒」：不接管审批，只负责让你知道有人在等你。
         * 不用悬停就能看到气泡、配一声提示音、点一下把 WorkBuddy 切到前台。
         */
        /** 授权卡收起：单一状态位 + accent 复位，两个收起路径共用 */
        private void DismissPendingCard()
        {
            _pendingShown = false;
            _pendingCard.Visibility = Visibility.Collapsed;
            _emotionText.Visibility = Visibility.Collapsed;
            _tempUntil = DateTime.MinValue;
            _autoUntil = DateTime.MinValue;
            ResetAccent();
            if (!IsPointerOverPanel()) { _bubble.Visibility = Visibility.Collapsed; }
        }

        /** 操作行与角色点击共用同一路由和 2s 防抖，避免双触发切前台 */
        private void FocusFromPendingCard()
        {
            if (_shownState != "blocked") { return; }
            if ((DateTime.Now - _lastFocusAt).TotalSeconds < 2) { return; }
            _lastFocusAt = DateTime.Now;
            FocusWorkBuddy();
        }

        private void ShowPendingCard()
        {
            string tool = _busTool.Length > 0 ? _busTool : "\u5DE5\u5177";
            _title.Text = PersonaName();
            _pendingTitle.Text = "\u7B49\u5F85\u4F60\u786E\u8BA4\uFF1A" + tool;

            long secs = (long)(DateTime.Now - _stateChangedAt).TotalSeconds;
            if (secs < 0) { secs = 0; }
            _pendingTimer.Text = secs + "s";

            // 命令预览压到 1 行 24 字，完整原文留给 ToolTip
            _pendingCommandFull = _busDetail;
            string preview = _busDetail;
            if (preview.Length > 24) { preview = preview.Substring(0, 23) + "\u2026"; }
            _pendingCommand.Text = preview;
            _pendingCommand.ToolTip = preview.Length > 0
                ? MakeDarkToolTip(_pendingCommand, _pendingCommandFull) : null;
            _pendingCommand.Visibility = preview.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            _emotionText.Visibility = Visibility.Collapsed;
            _pendingCard.Visibility = Visibility.Visible;
            _pendingShown = true;
            ApplyAccent("#F87171");   // 功能态 accent，独立于情绪 3 组
            ShowBubbleFor(25);
            Bounce(9, 2, 260);

            // 并行工具可能连着弹好几次授权，声音限流，气泡照常更新
            if ((DateTime.Now - _lastPendingAt).TotalSeconds < 8) { return; }
            _lastPendingAt = DateTime.Now;

            if (_soundOn && DateTime.Now >= _quietUntil)
            {
                try { if (_alertSound != null) { _alertSound.Play(); } }
                catch (Exception) { }
            }
        }

        /** 连续起伏用独立的 TranslateTransform，避免和一次性动作抢同一属性 */
        private void SetBob(string state)
        {
            double amp = 0;
            int ms = 1000;
            if (state == "thinking") { amp = 3.0; ms = 1500; }
            else if (state == "working") { amp = 2.0; ms = 520; }
            else if (state == "juggling") { amp = 8.0; ms = 360; }
            else if (state == "blocked") { amp = 4.0; ms = 1100; }
            else if (state == "compacting") { amp = 2.5; ms = 1700; }
            // atlas 形象是静态帧，帧间没有任何变化，靠极轻的呼吸位移补一点「活着感」；
            // 逐帧皮肤本身就在动，保持原来的完全静止（落到下面的 amp<=0 复位分支）
            else if (_atlas != null) { amp = 1.5; ms = 3600; }

            if (amp <= 0)
            {
                _bob.BeginAnimation(TranslateTransform.YProperty, null);
                _bob.Y = 0;
                return;
            }

            DoubleAnimation anim = new DoubleAnimation();
            anim.From = 0.0;
            anim.To = -amp;
            anim.Duration = new Duration(TimeSpan.FromMilliseconds(ms));
            anim.AutoReverse = true;
            anim.RepeatBehavior = RepeatBehavior.Forever;
            anim.EasingFunction = new SineEase();
            _bob.BeginAnimation(TranslateTransform.YProperty, anim);
        }

        private void UpdateStateLine(string state)
        {
            if (_stateLine == null) { return; }
            if (state == "idle")
            {
                // 闲着的时候这一行让给上下文占用——比「我在发呆」有用得多。
                // 25% 就开始显示：这个数字的价值在于「看着它涨」，不在于越线才报
                if (_ctxPct >= 25)
                {
                    _stateLine.Text = "\u25CF \u4E0A\u4E0B\u6587 " + _ctxPct.ToString("0") + "%"
                        + (_ctxPct >= 85 ? " \u00B7 \u5EFA\u8BAE\u65B0\u5F00\u4F1A\u8BDD" : "");
                    _stateLine.Foreground = _ctxPct >= 85 ? OverBrush : TitleBrush;
                    _stateLine.Visibility = Visibility.Visible;
                }
                else
                {
                    _stateLine.Visibility = Visibility.Collapsed;
                }
                return;
            }

            string label;
            switch (state)
            {
                case "thinking": label = "\u601D\u8003\u4E2D"; break;
                case "working": label = _busTool.Length > 0 ? "\u6267\u884C " + _busTool : "\u6267\u884C\u4E2D"; break;
                case "juggling": label = "\u5E76\u884C\u5B50\u4EFB\u52A1"; break;
                case "compacting": label = "\u6574\u7406\u4E0A\u4E0B\u6587"; break;
                case "error": label = "\u6267\u884C\u51FA\u9519"; break;
                case "done": label = "\u672C\u8F6E\u5B8C\u6210"; break;
                case "blocked": label = "\u7B49\u5F85\u4F60\u786E\u8BA4"; break;
                case "sleeping": label = "\u4F1A\u8BDD\u7ED3\u675F"; break;
                case "attention": label = "\u9700\u8981\u4F60\u770B\u4E00\u773C"; break;
                default: label = ""; break;
            }
            if (label.Length == 0) { _stateLine.Visibility = Visibility.Collapsed; return; }

            long secs = (long)(DateTime.Now - _stateChangedAt).TotalSeconds;
            if (secs < 0) { secs = 0; }
            string text = "\u25CF " + label;
            if (secs >= 3) { text += " \u00B7 " + secs + "s"; }

            _stateLine.Text = text;
            _stateLine.Foreground = (state == "error" || state == "blocked") ? OverBrush
                : (state == "done" ? OkBrush : TitleBrush);
            _stateLine.Visibility = Visibility.Visible;
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
                case "proud": return "#F9A8D4";
                case "surprised": return "#F9A8D4";
                case "worried": return "#FDBA9C";
                case "tender": return "#FDBA9C";
                case "sleepy": return "#8C9AB8";
                default: return "#7C889E";
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
                    else if (k == "sound") { _soundOn = v != "off"; }
                    else if (k == "topmost") { _topmostOn = v != "off"; }
                    else if (k == "winX") { int.TryParse(v, out x); }
                    else if (k == "winY") { int.TryParse(v, out y); }
                }

                Rect wa = SystemParameters.WorkArea;
                if (x > -10000 && y > -10000 && x < wa.Right - 80 && y < wa.Bottom - 80 && x > wa.Left - 100 && y > wa.Top - 100)
                {
                    Left = x;
                    Top = y;
                }

                // 窗口高度从 448 涨到 480 后，老位置会把角色顶出屏幕下沿。
                // 角色是底对齐的，所以只要保证整窗落在工作区内即可。
                if (Top + Height > wa.Bottom) { Top = wa.Bottom - Height; }
                if (Top < wa.Top) { Top = wa.Top; }
                if (Left + Width > wa.Right) { Left = wa.Right - Width; }
                if (Left < wa.Left) { Left = wa.Left; }
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
                sb.AppendLine("sound=" + (_soundOn ? "on" : "off"));
                sb.AppendLine("topmost=" + (_topmostOn ? "on" : "off"));
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

            // 图集序列动画：只有 manifest 里配了多帧 seq 才真正跑起来，
            // 单帧图集全程停在 Stop，不会额外占 UI 线程。
            _atlasTimer = new DispatcherTimer();
            _atlasTimer.Interval = TimeSpan.FromMilliseconds(260);
            _atlasTimer.Tick += delegate { OnAtlasTick(); };

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
                // 换肤时 state 没变，RenderRunState 里那条 SetBob 不会触发：
                // 不补这一行的话 逐帧皮肤→atlas 会停在 _bob=0（静态贴图），
                // atlas→逐帧皮肤 又会把 1.5px 呼吸动画残留到逐帧皮肤上。
                SetBob(_shownState);
                // 换肤后复位情绪色：否则卡片圆点 / rail / 尾巴描边会残留上一次情绪
                ResetAccent();
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

            // 状态刷新：500ms 足够让秒表和超时兜底跟上，又不至于空转
            _stateTimer = new DispatcherTimer();
            _stateTimer.Interval = TimeSpan.FromMilliseconds(500);
            _stateTimer.Tick += delegate
            {
                try { RenderRunState(); }
                catch (Exception ex) { LogError("RenderRunState", ex); }
                try { ComputeVisibility(); }
                catch (Exception ex) { LogError("ComputeVisibility", ex); }
            };
            _stateTimer.Start();
        }

        /** 单一仲裁：自动气泡到期主动收起；_tempUntil 保护期与 _autoUntil 到期分离 */
        private void ComputeVisibility()
        {
            DateTime now = DateTime.Now;
            if (_pendingShown)
            {
                long secs = (long)(now - _stateChangedAt).TotalSeconds;
                if (secs < 0) { secs = 0; }
                _pendingTimer.Text = secs + "s";
            }
            if (_bubble == null || _bubble.Visibility != Visibility.Visible) { return; }
            if (now < _tempUntil || now < _autoUntil) { return; }
            if (IsPointerOverPanel()) { return; }
            if (_pendingShown) { DismissPendingCard(); return; }
            _bubble.Visibility = Visibility.Collapsed;
            _emotionText.Visibility = Visibility.Collapsed;
            ResetAccent();
        }

        private void TryFlushPending()
        {
            if (_pendingEvent == null) { return; }
            if (IsPointerOverPanel()) { return; }
            if (DateTime.Now > _pendingUntil) { _pendingEvent = null; return; }
            PetEvent ev = _pendingEvent;
            _pendingEvent = null;
            if (!AllowPlay(ev)) { return; }   // 补播前重跑节流
            PlayEvent(ev);
        }

        /** 命中区收敛到「角色 ∪ 气泡」，透明空白不再误判为悬停 */
        private bool IsPointerOverPanel()
        {
            Point p = Mouse.GetPosition(this);
            if (Hit(_mascot, p, 4)) { return true; }
            if (_bubble != null && _bubble.Visibility == Visibility.Visible && Hit(_bubble, p, 4)) { return true; }
            return false;
        }

        private bool Hit(FrameworkElement el, Point p, double pad)
        {
            if (el == null) { return false; }
            try
            {
                Point origin = el.TransformToVisual(this).Transform(new Point(0, 0));
                return p.X >= origin.X - pad && p.Y >= origin.Y - pad
                    && p.X <= origin.X + el.ActualWidth + pad
                    && p.Y <= origin.Y + el.ActualHeight + pad;
            }
            catch (Exception) { return false; }
        }

        private void OnFrameTick(object sender, EventArgs e)
        {
            // 切到 atlas 皮肤后这个定时器本该已停，留一道保险：
            // 万一还有残留 Tick 把 _frames 的旧帧盖上去，角色会闪回上一个形象
            if (_atlas != null) { _frameTimer.Stop(); return; }
            if (_frames.Count < 2) { _frameTimer.Stop(); return; }
            _idx = (_idx + 1) % _frames.Count;
            _mascot.Source = _frames[_idx];

            int d = 83;
            if (_idx < _delays.Count) { d = _delays[_idx]; }
            if (_runSpeed > 0 && Math.Abs(_runSpeed - 1.0) > 0.001) { d = (int)(d * _runSpeed); }
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
        private bool _movedOnDrag;
        private DateTime _lastFocusAt = DateTime.MinValue;
        private DateTime _lastPendingAt = DateTime.MinValue;

        private void OnMascotMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) { return; }
            _dragOrigin = e.GetPosition(this);
            double startLeft = Left;
            double startTop = Top;
            _dragging = true;
            _movedOnDrag = false;
            try { DragMove(); }
            catch (Exception) { }
            // DragMove 是模态的，返回时窗口已经落位：位移超过 3px 算拖动，否则算点击
            _movedOnDrag = Math.Abs(Left - startLeft) > 3 || Math.Abs(Top - startTop) > 3;
        }

        private void OnMascotMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) { return; }
            _dragging = false;
            SaveState();

            if (_movedOnDrag) { return; }
            if (_shownState != "blocked") { return; }
            if ((DateTime.Now - _lastFocusAt).TotalSeconds < 2) { return; }
            _lastFocusAt = DateTime.Now;
            FocusWorkBuddy();
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        /**
         * 把 WorkBuddy 主窗口拉到前面。
         * Windows 有前台锁定，SetForegroundWindow 可能被拒——那就退一步先
         * ShowWindow(SW_RESTORE) 激活它，用户在任务栏能直接点。全部失败也不报错。
         */
        private void FocusWorkBuddy()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("WorkBuddy"))
                {
                    IntPtr h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) { continue; }
                    if (IsIconic(h)) { ShowWindow(h, 9); }
                    else { ShowWindow(h, 5); }
                    SetForegroundWindow(h);
                    return;
                }
            }
            catch (Exception) { }
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

                // 预算百分比在这里本地算，不信文件里的百分比：
                // 用户刚在菜单里改过预算时，panel-data.txt 还是上一轮的旧值。
                double bd;
                if (double.TryParse(GetValue(map, "budget", "0"), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out bd) && bd > 0)
                {
                    _budgetDaily = bd;
                }
                double bm;
                if (double.TryParse(GetValue(map, "monthBudget", "0"), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out bm) && bm > 0)
                {
                    _budgetMonthly = bm;
                }
                double tc;
                if (double.TryParse(todayCredit, NumberStyles.Float, CultureInfo.InvariantCulture, out tc))
                {
                    _todayCredit = tc;
                }
                double mc;
                if (double.TryParse(GetValue(map, "monthCredit", "0"), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out mc))
                {
                    _monthCredit = mc;
                }
                double av;
                if (double.TryParse(GetValue(map, "avgDaily", "0"), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out av))
                {
                    _avgDaily = av;
                }
                _forecast = GetValue(map, "forecast", "");
                double cx;
                if (double.TryParse(GetValue(map, "ctxPct", "0"), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out cx))
                {
                    _ctxPct = cx;
                }

                _budgetPct = _budgetDaily > 0 ? (_todayCredit / _budgetDaily) * 100 : 0;
                _monthPct = _budgetMonthly > 0 ? (_monthCredit / _budgetMonthly) * 100 : 0;

                double shownPct = _budgetMonthly > 0 ? _monthPct : _budgetPct;
                bool hasBudget = _budgetMonthly > 0 || _budgetDaily > 0;
                SetTodaySessions(sessions, shownPct, _budgetMonthly > 0, hasBudget);
                _todayValue.Foreground = (hasBudget && shownPct >= 90) ? OverBrush : ValueBrush;
                UpdateBudgetUi();

                if (changed)
                {
                    // 每轮回答结束响一声；静音期或关闭音效时不响
                    PlayDoneSound();

                    // 有事件通道时不再用 mtime 弹气泡，避免和情绪事件双弹
                    if (!File.Exists(_eventsFile))
                    {
                        _tempUntil = DateTime.Now.AddSeconds(6);
                        _hideTimer.Stop();
                        _bubble.Visibility = Visibility.Visible;
                        Bounce(9, 2, 280);
                    }
                }
                _firstLoad = false;
            }
            catch (Exception ex) { LogError("UpdatePanel", ex); }

            try
            {
                // 页脚只用于临时消息（换形象进度等），平时隐藏
                if (DateTime.Now < _footerUntil) { return; }
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

        /** 百分比拆第二个 Run：颜色 + 字重构成非颜色编码，不新增任何行 */
        private void SetTodaySessions(string sessions, double pct, bool isMonth, bool hasBudget)
        {
            if (_todaySessionsMain == null)
            {
                _todaySessionsMain = new Run();
                _todaySessionsPct = new Run();
                _todaySessions.Inlines.Add(_todaySessionsMain);
                _todaySessions.Inlines.Add(_todaySessionsPct);
            }
            _todaySessionsMain.Text = sessions + " \u4F1A\u8BDD";
            _todaySessionsMain.Foreground = CaptionBrush;
            if (!hasBudget)
            {
                _todaySessionsPct.Text = "";
                return;
            }
            _todaySessionsPct.Text = " \u00B7 " + (isMonth ? "\u6708" : "") + pct.ToString("0") + "%";
            _todaySessionsPct.Foreground = pct >= 90 ? OverBrush : (pct >= 70 ? WarnBrush : CaptionBrush);
            _todaySessionsPct.FontWeight = pct >= 90 ? FontWeights.Medium : FontWeights.Normal;
        }

        /**
         * 预算条：3px 细线按百分比填充，配色分三档。
         * 卡片内容宽 290 - 2x1 描边 - 左右内边距 30 = 258，填充宽度按这个算。
         * 配了月预算就以月为准——日预算管的是「今天别失控」，月预算才是额度本身。
         */
        private void UpdateBudgetUi()
        {
            if (_budgetFill == null || _budgetTrack == null) { return; }

            bool useMonth = _budgetMonthly > 0;
            bool any = useMonth || _budgetDaily > 0;
            if (!any)
            {
                _budgetFill.Width = 0;
                _budgetTrack.ToolTip = null;
                return;
            }

            double pct = useMonth ? _monthPct : _budgetPct;
            double clamped = pct < 0 ? 0 : (pct > 100 ? 100 : pct);
            _budgetFill.Width = 258.0 * clamped / 100.0;
            _budgetFill.Background = pct >= 90 ? OverBrush : (pct >= 70 ? WarnBrush : OkBrush);

            StringBuilder tip = new StringBuilder();
            if (_budgetDaily > 0)
            {
                tip.Append("\u4ECA\u65E5 ").Append(_todayCredit.ToString("0.#")).Append(" / ")
                   .Append(_budgetDaily.ToString("0")).Append("\uFF08").Append(_budgetPct.ToString("0")).Append("%\uFF09");
            }
            if (_budgetMonthly > 0)
            {
                if (tip.Length > 0) { tip.Append("\n"); }
                tip.Append("\u672C\u6708 ").Append(_monthCredit.ToString("0.#")).Append(" / ")
                   .Append(_budgetMonthly.ToString("0")).Append("\uFF08").Append(_monthPct.ToString("0")).Append("%\uFF09");
                if (_forecast.Length > 0)
                {
                    tip.Append("\n\u65E5\u5747 ").Append(_avgDaily.ToString("0.#")).Append(" \u00B7 ").Append(_forecast);
                }
            }
            _budgetTrack.ToolTip = MakeDarkToolTip(_budgetTrack, tip.ToString());
        }

        /** 面板侧的异常落盘。以前所有 catch 都是空的，出问题只能靠猜 */
        private void LogError(string where, Exception ex)
        {
            try
            {
                string path = Path.Combine(_baseDir, "panel-err.log");
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 65536) { File.Delete(path); }
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + where + "] " +
                    ex.GetType().Name + ": " + ex.Message + "\r\n", Encoding.UTF8);
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

        /** 退出时关掉监听并删掉端口文件：hook 看到文件没了就静默丢弃事件 */
        private void OnClosed(object sender, EventArgs e)
        {
            try
            {
                if (_bus != null) { _bus.Stop(); }
            }
            catch (Exception) { }
            try
            {
                if (File.Exists(_portFile)) { File.Delete(_portFile); }
            }
            catch (Exception) { }
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

    /**
     * 项目成本看板：本月各项目的积分占比。
     *
     * 数据来自 pet-projects.txt —— turn-usage.js 每轮把「本轮增量」累加到
     * 当前项目上，所以打开是瞬时的，也不会因为月份变长而变慢。
     * 代价是首月从安装那天开始记，不回填历史。
     */
    public class CostWindow : Window
    {
        private static readonly Brush CardBg = Frozen(0x1B, 0x1E, 0x26);
        private static readonly Brush CardBorder = Frozen(0x3D, 0x46, 0x5C);
        private static readonly Brush ValueFg = Frozen(0xED, 0xEF, 0xF5);
        private static readonly Brush MutedFg = Frozen(0x98, 0xA0, 0xB4);
        private static readonly Brush CaptionFg = Frozen(0x98, 0xA0, 0xB4);
        private static readonly Brush TitleFg = Frozen(0x8A, 0x93, 0xA8);
        private static readonly Brush TrackBg = Frozen(0x42, 0x4A, 0x5E);
        private static readonly Brush DividerFg = Frozen(0x33, 0x3B, 0x4C);
        private static readonly Brush BarFg = Frozen(0x5E, 0xEA, 0xD4);
        private static readonly Brush AccentFg = Frozen(0x2E, 0xE6, 0xC0);
        private static readonly Brush MetaFg = Frozen(0x98, 0xA0, 0xB4);
        private static readonly Brush MutedBarFg = Frozen(0x8C, 0x9A, 0xB8);
        private static readonly Brush OverFg = Frozen(0xF8, 0x71, 0x71);
        private static readonly Brush TrendOverFg = Frozen(0xFB, 0x92, 0x3C);
        private static readonly Brush MutedLineFg = Frozen(0x3D, 0x46, 0x5C);

        // 卡片不贴窗口边缘：投影要往外画 18px，贴边会被窗口边界裁掉，
        // 看起来就像「底边被切了一刀」。
        private const double ShadowPad = 12;
        private const double CardWidth = 364;
        private const double InnerWidth = 330;

        private readonly string _baseDir;
        private readonly string _projectsFile;
        private readonly string _historyFile;
        private readonly string _dataFile;
        private readonly StackPanel _rows;
        private readonly TextBlock _subtitle;
        private readonly TextBlock _monthLine;
        private readonly TextBlock _footer;
        private readonly TextBlock _trendPeak;
        private readonly Grid _trend;
        private DispatcherTimer _timer;
        private bool _placed;
        private long _lastStamp = -1;

        private static Brush Frozen(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static TextBlock MakeText(string text, double size, Brush fg)
        {
            TextBlock tb = new TextBlock();
            tb.Text = text;
            tb.FontSize = size;
            tb.Foreground = fg;
            TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);
            return tb;
        }

        public CostWindow(string baseDir, string dataFile)
        {
            _baseDir = baseDir;
            _projectsFile = Path.Combine(baseDir, "pet-projects.txt");
            _historyFile = Path.Combine(baseDir, "pet-history.txt");
            _dataFile = dataFile;

            Width = CardWidth + ShadowPad * 2;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            UseLayoutRounding = true;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            ShowActivated = true;

            Border card = new Border();
            card.Margin = new Thickness(ShadowPad);
            card.Background = CardBg;
            card.BorderBrush = CardBorder;
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(16);
            card.Padding = new Thickness(16, 14, 16, 14);
            card.MouseLeftButtonDown += delegate { try { DragMove(); } catch (Exception) { } };

            DropShadowEffect shadow = new DropShadowEffect();
            shadow.BlurRadius = 18;
            shadow.ShadowDepth = 5;
            shadow.Direction = 270;
            shadow.Opacity = 0.35;
            shadow.Color = Colors.Black;
            card.Effect = shadow;

            StackPanel body = new StackPanel();

            Grid head = new Grid();
            TextBlock title = MakeText("\u9879\u76EE\u6210\u672C \u00B7 \u672C\u6708", 13, TitleFg);
            title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(title);

            // 关闭按钮必须是一个有背景的块，不能用裸 TextBlock：
            // 一是 TextBlock 只有笔画像素能被命中，点偏一点就失效；
            // 二是它得在 MouseDown 就处理掉并把 Handled 置位，否则冒泡到卡片的
            // DragMove()——那是模态的，会吃掉后续的 MouseUp，按钮永远等不到点击。
            Border closeBtn = new Border();
            closeBtn.Width = 26;
            closeBtn.Height = 24;
            closeBtn.Background = Brushes.Transparent;
            closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
            closeBtn.VerticalAlignment = VerticalAlignment.Center;
            closeBtn.Margin = new Thickness(0, 0, -8, 0);
            closeBtn.Cursor = Cursors.Hand;
            closeBtn.ToolTip = "\u5173\u95ED\uFF08Esc\uFF09";

            TextBlock closeGlyph = MakeText("\u00D7", 15, CaptionFg);
            closeGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            closeGlyph.VerticalAlignment = VerticalAlignment.Center;
            closeBtn.Child = closeGlyph;

            closeBtn.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;      // 别让卡片把它当成拖拽起点
                Close();
            };
            closeBtn.MouseEnter += delegate { closeGlyph.Foreground = ValueFg; };
            closeBtn.MouseLeave += delegate { closeGlyph.Foreground = CaptionFg; };

            head.Children.Add(closeBtn);
            body.Children.Add(head);

            _subtitle = MakeText("", 10.5, CaptionFg);
            _subtitle.Margin = new Thickness(0, 5, 0, 3);
            body.Children.Add(_subtitle);

            // 月度额度与预测：单独一行，比「合计」更关心的是「够不够用」
            _monthLine = MakeText("", 10.5, MetaFg);
            _monthLine.TextWrapping = TextWrapping.Wrap;
            _monthLine.Margin = new Thickness(0, 0, 0, 10);
            body.Children.Add(_monthLine);

            Border divider1 = new Border();
            divider1.Height = 1;
            divider1.Background = DividerFg;
            divider1.Margin = new Thickness(0, 0, 0, 12);
            body.Children.Add(divider1);

            _rows = new StackPanel();
            body.Children.Add(_rows);

            // 近 30 天：柱高按当日积分，超日均 1.5 倍的柱子标红
            StackPanel chartBlock = new StackPanel();
            chartBlock.Margin = new Thickness(0, 0, 0, 6);

            Grid chartHead = new Grid();
            TextBlock trendCap = MakeText("\u8FD1 30 \u5929", 10.5, CaptionFg);
            chartHead.Children.Add(trendCap);
            _trendPeak = MakeText("", 10.5, CaptionFg);
            _trendPeak.HorizontalAlignment = HorizontalAlignment.Right;
            chartHead.Children.Add(_trendPeak);
            chartBlock.Children.Add(chartHead);

            TextBlock axis = MakeText("30 \u5929\u524D \u2190 \u2192 \u4ECA\u65E5", 10.5, CaptionFg);
            axis.Margin = new Thickness(0, 3, 0, 0);
            chartBlock.Children.Add(axis);

            _trend = new Grid();
            _trend.Height = 56;
            _trend.Margin = new Thickness(0, 6, 0, 0);
            for (int i = 0; i < 30; i++)
            {
                _trend.ColumnDefinitions.Add(new ColumnDefinition());
            }
            chartBlock.Children.Add(_trend);
            body.Children.Add(chartBlock);

            Border divider2 = new Border();
            divider2.Height = 1;
            divider2.Background = DividerFg;
            divider2.Margin = new Thickness(0, 0, 0, 12);
            body.Children.Add(divider2);

            _footer = MakeText("", 10.5, CaptionFg);
            _footer.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(_footer);

            card.Child = body;
            Content = card;

            Loaded += OnLoaded;
            Closed += delegate { if (_timer != null) { _timer.Stop(); } };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); } };

            Refresh();
        }

        /** 首次布局完成后才知道真实高度，这时再摆位置，靠宠物左侧 */
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_placed) { return; }
            _placed = true;
            try
            {
                Rect wa = SystemParameters.WorkArea;
                double left = Left - Width - 12;
                if (left < wa.Left + 8) { left = wa.Left + 8; }
                double top = Top;
                if (top + ActualHeight > wa.Bottom) { top = wa.Bottom - ActualHeight - 8; }
                if (top < wa.Top + 8) { top = wa.Top + 8; }
                Left = left;
                Top = top;
            }
            catch (Exception) { }

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += delegate { Refresh(); };
            _timer.Start();
        }

        private static string FmtTokens(double n)
        {
            if (n >= 1e8) { return (n / 1e8).ToString("0.00") + "\u4EBF"; }
            if (n >= 1e4) { return (n / 1e4).ToString("0.0") + "\u4E07"; }
            return Math.Round(n).ToString("0");
        }

        /**
         * 三个数据文件的修改时间之和，用来判断「要不要重建」。
         * 之前是每 2 秒无条件清空重画整棵树——开着窗口时看着在闪，
         * 而且 30 个趋势柱 + N 行控件反复分配，纯浪费。
         */
        private long SourceStamp()
        {
            return StampOf(_projectsFile) + StampOf(_historyFile) + StampOf(_dataFile);
        }

        private static long StampOf(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;
            }
            catch (Exception) { return 0L; }
        }

        public void Refresh()
        {
            try
            {
                long stamp = SourceStamp();
                if (stamp == _lastStamp) { return; }   // 数据没变，别动界面
                _lastStamp = stamp;
                string month = "";
                string since = "";
                List<string[]> rows = new List<string[]>();
                double total = 0;

                if (File.Exists(_projectsFile))
                {
                    foreach (string raw in File.ReadAllLines(_projectsFile, Encoding.UTF8))
                    {
                        if (raw.StartsWith("month=")) { month = raw.Substring(6).Trim(); continue; }
                        if (raw.StartsWith("since=")) { since = raw.Substring(6).Trim(); continue; }
                        if (!raw.StartsWith("p|")) { continue; }
                        string[] p = raw.Split('|');
                        if (p.Length < 5) { continue; }
                        rows.Add(p);
                        double c;
                        if (double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c)) { total += c; }
                    }
                }

                _rows.Children.Clear();

                if (rows.Count == 0)
                {
                    TextBlock empty = MakeText("\u672C\u6708\u8FD8\u6CA1\u6709\u8BB0\u5F55\uFF0C\u8DD1\u4E00\u8F6E\u5BF9\u8BDD\u5C31\u6709\u4E86", 12, CaptionFg);
                    empty.Margin = new Thickness(0, 4, 0, 8);
                    _rows.Children.Add(empty);
                }
                else
                {
                    for (int i = 0; i < rows.Count && i < 8; i++)
                    {
                        AddRow(rows[i], total);
                    }
                    if (rows.Count > 8)
                    {
                        TextBlock more = MakeText("\u5176\u4F59 " + (rows.Count - 8) + " \u4E2A\u9879\u76EE\u5DF2\u5408\u5E76\u8BA1\u5165\u5408\u8BA1", 10.5, MetaFg);
                        more.Margin = new Thickness(0, 0, 0, 8);
                        _rows.Children.Add(more);
                    }
                }

                // 分段记账是从「装上那天」开始的，不是月初。当它不等于 1 号时
                // 必须说明，否则「本月合计」比「今日」还小，看着像算错了。
                string sinceNote = "";
                if (since.Length == 10 && since.Substring(8, 2) != "01")
                {
                    sinceNote = " \u00B7 \u81EA " + since.Substring(5) + " \u8D77";
                }

                _subtitle.Text = rows.Count > 0
                    ? month + " \u00B7 " + rows.Count + " \u4E2A\u9879\u76EE \u00B7 \u5408\u8BA1 "
                        + total.ToString("0.#") + " \u79EF\u5206" + sinceNote
                    : "\u6682\u65E0\u6570\u636E";

                // 今日 / 本月 / 预算：复用 panel-data.txt，不重复计算
                Dictionary<string, string> pd = new Dictionary<string, string>();
                if (File.Exists(_dataFile))
                {
                    foreach (string raw in File.ReadAllLines(_dataFile, Encoding.UTF8))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq <= 0) { continue; }
                        pd[raw.Substring(0, eq).Trim()] = raw.Substring(eq + 1).Trim();
                    }
                }

                double todayCredit = ParseD(pd, "todayCredit");
                double budget = ParseD(pd, "budget");
                double monthCredit = ParseD(pd, "monthCredit");
                double monthBudget = ParseD(pd, "monthBudget");
                double avgDaily = ParseD(pd, "avgDaily");
                string forecast = "";
                if (pd.ContainsKey("forecast")) { forecast = pd["forecast"]; }

                if (monthBudget > 0)
                {
                    double mp = monthCredit / monthBudget * 100;
                    _monthLine.Text = "\u672C\u6708 " + monthCredit.ToString("0.#") + " / "
                        + monthBudget.ToString("0") + "\uFF08" + mp.ToString("0") + "%\uFF09 \u00B7 \u65E5\u5747 "
                        + avgDaily.ToString("0.#") + (forecast.Length > 0 ? " \u00B7 " + forecast : "");
                    _monthLine.Foreground = mp >= 100 ? OverFg : MetaFg;
                }
                else
                {
                    _monthLine.Text = "\u672C\u6708 " + monthCredit.ToString("0.#") + " \u79EF\u5206 \u00B7 \u672A\u8BBE\u6708\u9884\u7B97";
                    _monthLine.Foreground = MetaFg;
                }

                bool overDaily = budget > 0 && todayCredit >= budget;
                string todayLine = "\u4ECA\u65E5 " + todayCredit.ToString("0.#") + " \u79EF\u5206";
                if (budget > 0)
                {
                    todayLine += " \u00B7 \u65E5\u9884\u7B97 " + (todayCredit / budget * 100).ToString("0") + "%";
                }
                _footer.Text = todayLine;
                _footer.Foreground = overDaily ? OverFg : CaptionFg;

                BuildTrend(avgDaily);
            }
            catch (Exception ex) { Log("CostWindow.Refresh", ex); }
        }

        private void Log(string where, Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(_baseDir, "panel-err.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + where + "] "
                    + ex.GetType().Name + ": " + ex.Message + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        private static double ParseD(Dictionary<string, string> map, string key)
        {
            string v;
            double n;
            if (map.TryGetValue(key, out v)
                && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out n))
            {
                return n;
            }
            return 0;
        }

        /** 近 30 天柱状：没记录的那天留一条 1px 底线，看得出「那天没干活」而不是没数据 */
        private void BuildTrend(double avgDaily)
        {
            if (_trend == null) { return; }

            Dictionary<string, double> hist = new Dictionary<string, double>();
            try
            {
                if (File.Exists(_historyFile))
                {
                    foreach (string raw in File.ReadAllLines(_historyFile, Encoding.UTF8))
                    {
                        int eq = raw.IndexOf('=');
                        if (eq <= 0) { continue; }
                        double v;
                        if (double.TryParse(raw.Substring(eq + 1).Trim(), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out v))
                        {
                            hist[raw.Substring(0, eq).Trim()] = v;
                        }
                    }
                }
            }
            catch (Exception ex) { Log("CostWindow.trend", ex); }

            DateTime today = DateTime.Today;
            double[] vals = new double[30];
            double max = 0.01;
            for (int i = 0; i < 30; i++)
            {
                double v;
                if (hist.TryGetValue(today.AddDays(i - 29).ToString("yyyy-MM-dd"), out v))
                {
                    vals[i] = v;
                    if (v > max) { max = v; }
                }
            }

            _trend.Children.Clear();
            double threshold = avgDaily > 0 ? avgDaily * 1.5 : double.MaxValue;

            for (int i = 0; i < 30; i++)
            {
                DateTime day = today.AddDays(i - 29);

                Border bar = new Border();
                bar.Margin = new Thickness(0.5, 0, 0.5, 0);
                bar.VerticalAlignment = VerticalAlignment.Bottom;
                bar.CornerRadius = new CornerRadius(1);
                bar.Height = vals[i] > 0 ? Math.Max(2.0, vals[i] / max * 56.0) : 2.0;
                bar.Background = vals[i] <= 0 ? MutedLineFg
                    : (i == 29 ? AccentFg : (vals[i] > threshold ? TrendOverFg : BarFg));
                bar.ToolTip = day.ToString("MM-dd") + " \u00B7 "
                    + (vals[i] > 0 ? vals[i].ToString("0.#") + " \u79EF\u5206" : "\u65E0\u8BB0\u5F55");
                Grid.SetColumn(bar, i);
                _trend.Children.Add(bar);
            }

            _trendPeak.Text = max > 0.011 ? "\u5CF0\u503C " + max.ToString("0.#") : "";
        }

        /** 一行 = 项目名 + 数值 + 占比条 + 元信息；占比条宽度按积分占比 */
        private void AddRow(string[] p, double total)
        {
            string name = p[1];
            // `~` 开头是派生行（记账前未分摊），去掉标记显示，并用灰条区分
            bool derived = name.StartsWith("~");
            if (derived) { name = name.Substring(1); }
            double credit = 0;
            double prompt = 0;
            int calls = 0;
            double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out credit);
            double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out prompt);
            int.TryParse(p[4], out calls);
            double pct = total > 0 ? credit / total * 100 : 0;

            StackPanel row = new StackPanel();
            row.Margin = new Thickness(0, 0, 0, 12);

            Grid line = new Grid();
            TextBlock n = MakeText(name, 12, ValueFg);
            n.FontWeight = FontWeights.Medium;
            n.TextTrimming = TextTrimming.CharacterEllipsis;
            n.Margin = new Thickness(0, 0, 96, 0);
            line.Children.Add(n);

            TextBlock v = MakeText(credit.ToString("0.#") + " \u79EF\u5206", 12, ValueFg);
            v.HorizontalAlignment = HorizontalAlignment.Right;
            line.Children.Add(v);
            row.Children.Add(line);

            Grid bar = new Grid();
            bar.Margin = new Thickness(0, 5, 0, 0);

            Border track = new Border();
            track.Height = 3;
            track.CornerRadius = new CornerRadius(1.5);
            track.Background = TrackBg;
            bar.Children.Add(track);

            Border fill = new Border();
            fill.Height = 3;
            fill.CornerRadius = new CornerRadius(1.5);
            fill.HorizontalAlignment = HorizontalAlignment.Left;
            fill.Background = derived ? MutedBarFg : BarFg;
            fill.Width = InnerWidth * Math.Min(1.0, pct / 100.0);
            bar.Children.Add(fill);
            row.Children.Add(bar);

            TextBlock meta = MakeText(
                calls + " \u8F6E \u00B7 " + FmtTokens(prompt) + " token \u00B7 " + pct.ToString("0") + "%",
                10.5, MetaFg);
            meta.Margin = new Thickness(0, 5, 0, 0);
            row.Children.Add(meta);

            _rows.Children.Add(row);
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            // winexe 没有控制台，构造函数里抛异常就是「双击没反应」。
            // 以前这种情况什么都留不下，现在至少有一行日志。
            try
            {
                RunMain();
            }
            catch (Exception ex)
            {
                CrashLog(ex);
            }
        }

        private static void CrashLog(Exception ex)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "panel-err.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [startup] " +
                    ex.GetType().Name + ": " + ex.Message + "\r\n" + ex.StackTrace + "\r\n",
                    new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        private static void RunMain()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dataFile = Path.Combine(baseDir, "panel-data.txt");
            string[] argv = Environment.GetCommandLineArgs();

            // --cost 必须放在互斥量之前判断：宠物通常已经在跑并持有那把锁，
            // 放在后面就会「双击 --cost 没反应」——而且因为返回码是 0，还查不出原因。
            if (argv.Length > 1 && argv[1] == "--cost")
            {
                Application costApp = new Application();
                costApp.ShutdownMode = ShutdownMode.OnMainWindowClose;
                costApp.Run(new CostWindow(baseDir, dataFile));
                return;
            }

            Mutex mutex = null;
            try
            {
                mutex = new Mutex(false, "Global\\WorkBuddyUsagePanel");
                if (!mutex.WaitOne(0)) { return; }
            }
            catch (AbandonedMutexException) { }

            if (argv.Length > 1)
            {
                dataFile = argv[1];
            }

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            PetWindow win = new PetWindow(baseDir, dataFile);
            app.Run(win);

            GC.KeepAlive(mutex);
        }
    }

    /**
     * 雪碧图取帧器：一张 8×4 的 PNG 切成 32 格，每只角色占 16 格（逻辑序号 0–15）。
     * CroppedBitmap 只引用源图的一块区域、不复制像素，所以 32 帧共用一份解码结果，
     * 比「32 个独立 PNG」省内存、省文件数，也省掉定时器轮播。
     * 项目一贯零第三方依赖（manifest 用正则手解析），这里同样只吃 BCL。
     */
    internal sealed class PetAtlas
    {
        private const int SlotCount = 16;   // 每只角色 16 帧：9 状态 + 7 情绪

        private readonly BitmapSource _image;
        private readonly int _cell;
        private readonly int _columns;
        private readonly int _rows;
        private readonly int _baseIndex;
        // 缓存键换成「图集绝对序号」而不是槽位：一个槽位可能对应多帧（序列动画），
        // 老图集里槽位与序号一一对应，行为不变。
        private readonly Dictionary<int, CroppedBitmap> _cache = new Dictionary<int, CroppedBitmap>();
        private readonly object _cacheLock = new object();

        private readonly Dictionary<int, int[]> _seq;
        private readonly Dictionary<int, int> _seqMs;

        /** 已经 Freeze 的源图，可跨线程共享 */
        public BitmapSource Image { get { return _image; } }
        /** 单格边长（@2x 为 256） */
        public int Cell { get { return _cell; } }
        /** 图集列数（8） */
        public int Columns { get { return _columns; } }
        /** 该角色在整张图里的起始索引（团团 0 / 可可 16） */
        public int Base { get { return _baseIndex; } }
        /** 是否有任何槽位配了多帧序列 */
        public bool Animated { get { return _seq.Count > 0; } }

        private PetAtlas(BitmapSource image, int cell, int columns, int rows, int baseIndex,
            Dictionary<int, int[]> seq, Dictionary<int, int> seqMs)
        {
            _image = image;
            _cell = cell;
            _columns = columns;
            _rows = rows;
            _baseIndex = baseIndex;
            _seq = seq != null ? seq : new Dictionary<int, int[]>();
            _seqMs = seqMs != null ? seqMs : new Dictionary<int, int>();
        }

        /** 槽位的帧数；没有配 seq 就是 1 帧（静态贴图） */
        public int SeqCount(int slot)
        {
            int[] list;
            if (_seq.TryGetValue(slot, out list) && list.Length > 0) { return list.Length; }
            return 1;
        }

        /** 槽位的帧间隔（毫秒）；没配就给 260，和 manifest 里的习惯值一致 */
        public int SeqMs(int slot)
        {
            int ms;
            if (_seqMs.TryGetValue(slot, out ms) && ms > 0) { return ms; }
            return 260;
        }

        /**
         * 加载整张图集。用 OnLoad + Freeze：文件句柄立刻释放（换肤时不会被占用），
         * 冻结后的位图也不再参与 WPF 的依赖属性系统，省掉一次拷贝。
         * PreservePixelFormat 保证 RGBA 不被转成 BGR，否则透明底会变黑边。
         */
        public static PetAtlas Load(string imagePath, int cell, int columns, int rows, int baseIndex,
            Dictionary<int, int[]> seq, Dictionary<int, int> seqMs)
        {
            if (string.IsNullOrEmpty(imagePath)) { throw new ArgumentException("imagePath 为空"); }
            if (!File.Exists(imagePath)) { throw new FileNotFoundException("找不到雪碧图：" + imagePath, imagePath); }
            if (cell <= 0) { throw new ArgumentException("cell 必须为正数"); }
            if (columns <= 0 || rows <= 0) { throw new ArgumentException("columns / rows 必须为正数"); }

            BitmapImage image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.UriSource = new Uri(imagePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            int needWidth = columns * cell;
            int needHeight = rows * cell;
            if (image.PixelWidth < needWidth || image.PixelHeight < needHeight)
            {
                throw new ArgumentException("雪碧图尺寸不足：" + image.PixelWidth + "x" + image.PixelHeight
                    + "，按 cell=" + cell + " / " + columns + "x" + rows + " 至少需要 "
                    + needWidth + "x" + needHeight + "（" + imagePath + "）");
            }

            // base 越界会让 Get 切到图外，直接钳到能放下 16 帧的最大起点
            int safeBase = baseIndex;
            if (safeBase < 0) { safeBase = 0; }
            int maxBase = columns * rows - SlotCount;
            if (maxBase < 0) { maxBase = 0; }
            if (safeBase > maxBase) { safeBase = maxBase; }

            return new PetAtlas(image, cell, columns, rows, safeBase, seq, seqMs);
        }

        /**
         * 取某个槽位的第 i 帧：配了 seq 就按列表取模，没配就退回「基准序号 + 槽位」。
         * 取模保证任何调用方传越界也不会切到图外。
         */
        public CroppedBitmap GetFrame(int slot, int i)
        {
            if (slot < 0 || slot >= SlotCount) { slot = 0; }

            int[] list;
            if (_seq.TryGetValue(slot, out list) && list.Length > 0)
            {
                int n = list.Length;
                int phase = ((i % n) + n) % n;
                return GetIndex(list[phase]);
            }
            return GetIndex(_baseIndex + slot);
        }

        /** 取槽位的第一帧，保留老调用方式 */
        public CroppedBitmap Get(int slot)
        {
            return GetFrame(slot, 0);
        }

        /**
         * 按图集绝对序号切一格，命中缓存直接返回。
         * 双重检查加锁：绝大多数调用在第 N 帧之后全是缓存命中，
         * 不进 lock 也不新建对象；冷启动时并发也只可能多切一次，不影响正确性。
         */
        private CroppedBitmap GetIndex(int index)
        {
            int total = _columns * _rows;
            if (index < 0) { index = 0; }
            if (total > 0 && index >= total) { index = total - 1; }

            lock (_cacheLock)
            {
                CroppedBitmap cached;
                if (_cache.TryGetValue(index, out cached)) { return cached; }
            }

            int column = index % _columns;
            int row = index / _columns;
            CroppedBitmap frame = new CroppedBitmap(
                _image, new Int32Rect(column * _cell, row * _cell, _cell, _cell));
            frame.Freeze();   // 冻结后才能跨线程/复用，也能让 WPF 直接走快速路径

            lock (_cacheLock)
            {
                CroppedBitmap existing;
                if (_cache.TryGetValue(index, out existing)) { return existing; }
                _cache[index] = frame;
            }
            return frame;
        }
    }
}
