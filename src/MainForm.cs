// Windows Process Cleaner — главная форма: поля, конструктор, навигация, каркас UI, диалоги
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WindowsProcessCleaner
{
    public partial class MainForm : Form
    {
        private readonly Engine _engine;
        private NotifyIcon _tray;
        private Icon _iconIdle, _iconActive;
        private System.Threading.Timer _monitor;            // тик мониторинга CPU (фоновый поток)
        private int _monitorBusy;                           // 0/1 — тик уже идёт, следующий пропускаем
        private int _scanBusy;                              // 0/1 — идёт сканирование процессов
        private int _purgeBusy;                             // 0/1 — идёт очистка памяти
        private int _autoBusy;                              // 0/1 — идёт автоочистка
        private int _killBusy;                              // 0/1 — идёт завершение выбранных процессов
        private volatile bool _closing;
        private System.Windows.Forms.Timer _autoTimer;      // автоочистка
        private DateTime _nextAuto = DateTime.MaxValue;
        private bool _reallyExit = false;
        // Необратимые фоновые операции (удаление файлов, Корзина, установка обновлений, Docker
        // prune/compact): пока список не пуст, «Выход» из трея и «Перезапустить от администратора»
        // переспрашивают — оборванное сжатие оставляет Docker остановленным, оборванная установка —
        // пакет наполовину. Крестик окна операцию не трогает: окно просто уходит в трей.
        private readonly List<string> _writeOps = new List<string>();

        private Panel _content;
        private Button[] _navButtons;
        private Control[] _pages;
        private int _currentPage;
        private ListView _lvScan;
        private Label _lblSummary, _lblResult;
        private ListView _lvPorts;
        private ListView _lvHistory;
        private ListView _lvClean;
        private Label _lblCleanTotal;
        private Button _btnCleanCancel;
        private List<CleanCategory> _cleanCats;
        private ListView _lvApps;
        private Label _lblAppsInfo;
        private List<InstalledApp> _apps;
        private ListView _lvUpdates;
        private Label _lblUpdInfo;
        private Button _btnUpdCancel;
        private List<UpdateItem> _updates;
        private int _updBusy;                               // 0/1 — идёт проверка или установка обновлений
        private bool _updApplying;                          // идёт именно установка (для подписи «Стоп»)
        private RichTextBox _txtDocker;
        private ListView _lvStartup;
        private Label _lblStartupInfo;
        private bool _suppressStartup;
        private Panel _navPanel;

        // Настройки — контролы
        private NumericUpDown _numCpu, _numIdle, _numMinLife, _numInterval, _numGlobalIdle;
        private NumericUpDown _numMonInterval, _numSkipRecent, _numUpdBatch;
        private CheckBox _chkAuto, _chkAutostart, _chkStartMin, _chkExcludeInstalled;
        private CheckBox _chkMonitor, _chkEmptyWs, _chkCleanLog;
        private CheckBox _chkUpdUnknown, _chkUpdChoco;
        private TextBox _txtWatch, _txtWhite, _txtPorts, _txtCleanExclude, _txtUpdExclude;
        private ComboBox _cmbTheme;
        private ComboBox _cmbLang;
        private CheckBox _chkGlobal;
        private MenuItem _miAuto;

        // Тема оформления
        private Theme _theme;

        public MainForm(Engine engine)
        {
            _engine = engine;
            _theme = Theme.Resolve(engine.Config.Theme);
            InitDpi();
            BuildIcons();
            SuspendLayout();
            BuildUi();
            // Масштаб под DPI включается ПОСЛЕ сборки всех контролов и до ResumeLayout:
            // форма масштабирует детей один раз при ближайшей раскладке, а всё, что
            // добавлено после этого прохода, остаётся немасштабированным (см. Layout.cs).
            ApplyDpiTo(this);
            ResumeLayout(true);
            ClampToScreen();
            BuildTray();
            ApplyThemeAll();

            // Мониторинг — в ФОНОВОМ потоке. Раньше это был WinForms-таймер, то есть
            // полный обход всех процессов выполнялся прямо в UI-потоке каждые 10 с:
            // окно замирало на несколько секунд, ровно как «вечно зависает».
            // Первый тик тоже был синхронным в конструкторе — отсюда долгий старт.
            if (_engine.Config.MonitorEnabled)
            {
                int period = _engine.Config.MonitorIntervalSeconds * 1000;
                _monitor = new System.Threading.Timer(MonitorCallback, null, 1500, period);
            }

            _autoTimer = new System.Windows.Forms.Timer();
            _autoTimer.Interval = 30000; // проверяем расписание каждые 30 c
            _autoTimer.Tick += delegate { CheckAutoSchedule(); };
            _autoTimer.Start();
            RescheduleAuto();

            LoadSettingsToUi();
        }

        // ---------- Иконки трея ----------
        private Icon _iconWindow;

        // ---------- Навигация ----------
        private void ShowPage(int index)
        {
            // Уходим со вкладки очистки — останавливаем обход диска: продолжать его
            // ради экрана, которого не видно, значит впустую грузить диск и CPU.
            if (_currentPage == PageClean && index != PageClean) _engine.CancelDiskWork();

            _currentPage = index;
            for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = (i == index);
            UpdateNav();
            // подгоняем только видимый список, а не все пять
            AutoFillLastColumnDeferred(VisibleList(index));
            RefreshCurrentPage(index);
        }

        // Порядок вкладок задан в _pages; держим индексы именами, чтобы обработчики
        // навигации не разъезжались с массивом при вставке новой вкладки.
        private const int PageScan = 0, PageDev = 1, PageClean = 2, PageDisk = 3, PageBrowsers = 4,
                          PageDocker = 5, PageApps = 6, PageUpdates = 7, PageStartup = 8,
                          PageSettings = 9, PageHistory = 10;

        private ListView VisibleList(int index)
        {
            switch (index)
            {
                case PageScan: return _lvScan;
                case PageDev: return _lvPorts;
                case PageClean: return _lvClean;
                case PageDisk: return _lvDisk;
                case PageBrowsers: return _lvBrowser;
                case PageApps: return _lvApps;
                case PageUpdates: return _lvUpdates;
                case PageStartup: return _lvStartup;
                case PageHistory: return _lvHistory;
                default: return null;
            }
        }

        // Авто-обновление списка при переходе на вкладку (лёгкие источники).
        private void RefreshCurrentPage(int index)
        {
            if (!_ready) return;
            try
            {
                switch (index)
                {
                    case PageDev: RefreshPorts(); break;
                    case PageApps: RefreshApps(); break;
                    case PageStartup: RefreshStartup(); break;
                    case PageHistory: RefreshHistory(); break;
                    case PageBrowsers: RefreshBrowsers(false); break;
                }
            }
            catch { }
        }

        private bool _ready;

        private void FillColumns()
        {
            AutoFillLastColumnDeferred(_lvScan);
            AutoFillLastColumnDeferred(_lvPorts);
            AutoFillLastColumnDeferred(_lvHistory);
            AutoFillLastColumnDeferred(_lvClean);
            AutoFillLastColumnDeferred(_lvDisk);
            AutoFillLastColumnDeferred(_lvApps);
            AutoFillLastColumnDeferred(_lvUpdates);
            AutoFillLastColumnDeferred(_lvBrowser);
        }

        private void UpdateNav()
        {
            if (_navButtons == null) return;
            for (int i = 0; i < _navButtons.Length; i++)
            {
                Button b = _navButtons[i];
                if (b == null) continue;
                b.UseVisualStyleBackColor = false;
                b.FlatAppearance.BorderSize = 0;
                b.BackColor = _theme.Bg;
                if (i == _currentPage)
                {
                    b.ForeColor = _theme.Accent;
                    b.FlatAppearance.MouseOverBackColor = _theme.Bg;
                }
                else
                {
                    b.ForeColor = _theme.Subtle;
                    b.FlatAppearance.MouseOverBackColor = _theme.Dark
                        ? ControlPaint.Light(_theme.Bg, 0.30f)
                        : ControlPaint.Dark(_theme.Bg, 0.04f);
                }
            }
            if (_navPanel != null) _navPanel.Invalidate();
        }

        // ---------- UI ----------
        private void BuildUi()
        {
            Text = "Windows Process Cleaner";
            Width = 1200;
            Height = 740;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10.5F);
            // Одиннадцать вкладок навигации даже с минимальным отступом занимают ~1090 px:
            // при прежних 1040/1060 «История» уезжала за правый край. Минимум 1130, по
            // умолчанию 1200; панели кнопок переносятся по строкам (MkToolbar) и ширину не
            // диктуют. Подгонка под рабочую область экрана — ClampToScreen() после DPI.
            MinimumSize = new Size(1130, 620);
            Icon = _iconWindow;
            ShowIcon = true;

            // Собственная навигация вместо TabControl (полностью тематизируется).
            Panel nav = new Panel();
            nav.Dock = DockStyle.Top;
            nav.Height = 48;
            nav.Name = "nav";

            _content = new Panel();
            _content.Dock = DockStyle.Fill;

            _pages = new Control[] { BuildScanTab(), BuildDevTab(), BuildCleanTab(), BuildDiskTab(), BuildBrowserTab(), BuildDockerTab(), BuildAppsTab(), BuildUpdatesTab(), BuildStartupTab(), BuildSettingsTab(), BuildHistoryTab() };
            string[] titles = { Tr.S("Сканирование", "Scan"), "Dev Cleanup", Tr.S("Очистка диска", "Disk Cleanup"), Tr.S("Диск", "Disk"), Tr.S("Браузеры", "Browsers"), "Docker", Tr.S("Программы", "Programs"), Tr.S("Обновления", "Updates"), Tr.S("Автозапуск", "Startup"), Tr.S("Настройки", "Settings"), Tr.S("История", "History") };

            _navButtons = new Button[titles.Length];
            for (int i = 0; i < titles.Length; i++)
            {
                Button b = new Button();
                b.Text = titles[i];
                b.Top = 4; b.Height = 40;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.Font = new Font(Font, FontStyle.Bold);
                int idx = i;
                b.Click += delegate { ShowPage(idx); };
                nav.Controls.Add(b);
                _navButtons[i] = b;
            }
            // Ширины считаем по тексту и пересчитываем при каждом изменении ширины окна:
            // раньше они вычислялись один раз под стартовую ширину, и на меньшей последняя
            // вкладка уходила за край. Не влезает — режем отступ (22 → 16 → 10 → 6); если и
            // так не влезает (крупный DPI, длинные заголовки), ужимаем кнопки пропорционально:
            // чуть более тесный текст лучше обрезанной вкладки.
            Font navFont = new Font(Font, FontStyle.Bold);
            EventHandler layoutNav = delegate
            {
                int avail = (nav.Parent != null ? nav.ClientSize.Width : ClientSize.Width) - 16;
                int[] widths = new int[titles.Length];
                int pad = 22;
                int sum = 0;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    sum = 8;
                    for (int i = 0; i < titles.Length; i++)
                    {
                        widths[i] = TextRenderer.MeasureText(titles[i], navFont).Width + pad;
                        sum += widths[i] + 4;
                    }
                    if (sum <= avail) break;
                    if (pad > 12) pad -= 6; else if (pad > 6) pad -= 4; else break;
                }
                if (sum > avail && avail > 0)
                {
                    int fixedPart = 8 + titles.Length * 4;
                    double k = (double)(avail - fixedPart) / (sum - fixedPart);
                    if (k > 0 && k < 1) for (int i = 0; i < titles.Length; i++) widths[i] = Math.Max(24, (int)(widths[i] * k));
                }
                int nx = 8;
                for (int i = 0; i < titles.Length; i++)
                {
                    _navButtons[i].Left = nx; _navButtons[i].Width = widths[i];
                    nx += widths[i] + 4;
                }
                nav.Invalidate();
            };
            nav.Resize += layoutNav;
            layoutNav(null, EventArgs.Empty);
            _navPanel = nav;
            nav.Paint += delegate(object s, PaintEventArgs pe)
            {
                if (_navButtons == null || _currentPage < 0 || _currentPage >= _navButtons.Length) return;
                Button b = _navButtons[_currentPage];
                using (SolidBrush br = new SolidBrush(_theme.Accent))
                    pe.Graphics.FillRectangle(br, b.Left, nav.Height - 3, b.Width, 3);
                using (Pen pen = new Pen(_theme.Border))
                    pe.Graphics.DrawLine(pen, 0, nav.Height - 1, nav.Width, nav.Height - 1);
            };

            foreach (Control page in _pages)
            {
                page.Dock = DockStyle.Fill;
                page.Visible = false;
                _content.Controls.Add(page);
            }

            Controls.Add(_content);
            Controls.Add(nav);
            ShowPage(0);

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    string ops = ActiveWriteOps();
                    _tray.ShowBalloonTip(2000, "Windows Process Cleaner",
                        Tr.S("Свёрнуто в трей. Работает в фоне.", "Minimized to tray. Running in background.")
                        + (ops != null ? Tr.S("\r\nПродолжается: ", "\r\nStill running: ") + ops : ""), ToolTipIcon.Info);
                    return;
                }
                // Реальный выход: гасим фоновую работу, иначе поток анализа продолжает
                // обходить диск, а BeginInvoke на закрытое окно бросает исключение.
                _closing = true;
                _engine.CancelDiskWork();
                if (_monitor != null) { _monitor.Dispose(); _monitor = null; }
                DisposeThemeGdi();
            };
        }

        // Все сообщения — с владельцем (центр окна, а не экрана), заголовком раздела и
        // иконкой: голый MessageBox.Show(this, text) выскакивал посреди экрана с пустым заголовком.
        private void MsgInfo(string text, string title)
        {
            MessageBox.Show(this, text, title ?? Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void MsgError(string text)
        {
            MessageBox.Show(this, text, Tr.S("Ошибка", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private bool MsgAsk(string text, string title)
        {
            return MessageBox.Show(this, text, title ?? Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        // Учёт необратимых фоновых операций (см. _writeOps). Begin — в UI-потоке до старта потока,
        // End — в самом потоке сразу после работы (не в UiPost: тот при закрытии пропускается).
        private void BeginWrite(string what) { lock (_writeOps) _writeOps.Add(what); }
        private void EndWrite(string what) { lock (_writeOps) _writeOps.Remove(what); }
        private string ActiveWriteOps()
        {
            lock (_writeOps) return _writeOps.Count == 0 ? null : string.Join(", ", _writeOps.ToArray());
        }

        // true = выходить можно (операций нет или пользователь подтвердил). Окно поднимается из
        // трея, чтобы вопрос не остался за скрытым владельцем; по умолчанию — «Нет».
        private bool ConfirmExitDuringWrite()
        {
            string ops = ActiveWriteOps();
            if (ops == null) return true;
            if (!Visible) ShowWindow();
            return MessageBox.Show(this,
                Tr.S("Сейчас выполняется: ", "In progress: ") + ops + "\r\n\r\n"
                + Tr.S("Выход прервёт операцию, и она может остаться незавершённой.\r\nВсё равно выйти?",
                       "Exiting will interrupt it, and it may be left half-done.\r\nExit anyway?"),
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private Button MkButton(string text, int x, int y, int w, bool primary)
        {
            Button b = new RoundButton();
            b.Text = text;
            b.Left = x; b.Top = y; b.Width = w; b.Height = 36;
            if (primary) b.Tag = "primary";
            return b;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_startHidden)
            {
                Hide();
            }
            _ready = true;
            if (_setBody != null) LayoutSettings(_setBody);   // растяжение списков настроек после масштаба DPI
            RefreshHistory();
            RefreshPorts();
            BeginInvoke((MethodInvoker)delegate { FillColumns(); });
            // одноразовая до-подгонка после окончательной раскладки окна
            System.Windows.Forms.Timer once = new System.Windows.Forms.Timer();
            once.Interval = 300;
            once.Tick += delegate { once.Stop(); once.Dispose(); FillColumns(); };
            once.Start();
            if (_selfTest)
                BeginInvoke((MethodInvoker)delegate { DoScan(); });
            if (_diskStartPage)
                BeginInvoke((MethodInvoker)delegate
                {
                    ShowPage(PageDisk);
                    if (!string.IsNullOrEmpty(_diskStartPath))
                    {
                        bool exists = false;
                        try { exists = Directory.Exists(_diskStartPath); } catch { }
                        if (exists) { FillDiskScopes(_diskStartPath); DoDiskScan(); }
                    }
                });
        }

        private bool _startHidden = false;
        public void SetStartHidden(bool v) { _startHidden = v; }
        private bool _selfTest = false;
        public void SetSelfTest(bool v) { _selfTest = v; }
    }
}
