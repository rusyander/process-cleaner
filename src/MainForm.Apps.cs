// Windows Process Cleaner — вкладка «Программы»: список и деинсталляция
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
    public partial class MainForm
    {
        // ---------- Вкладка: Программы (деинсталляция) ----------

        private Control BuildAppsTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel top = MkToolbar();

            Button btnRefresh = MkFlowButton(Tr.S("Обновить список", "Refresh list"), 170, true);
            btnRefresh.Click += delegate { RefreshApps(true); };
            Button btnUninstall = MkFlowButton(Tr.S("Удалить выбранное", "Uninstall selected"), 200, true);
            btnUninstall.Click += delegate { DoUninstall(); };

            Label warn = MkNote(Tr.S("Запускается штатный деинсталлятор программы (может открыть своё окно/запросить подтверждение).",
                                     "Launches the program's own uninstaller (may open its own window / ask for confirmation)."), true);
            _lblAppsInfo = MkNote(Tr.S("Нажмите «Обновить список»", "Click “Refresh list”"), false);

            top.Controls.Add(btnRefresh);
            top.Controls.Add(btnUninstall);

            _lvApps = new FastListView();
            _lvApps.Dock = DockStyle.Fill;
            _lvApps.View = View.Details;
            _lvApps.CheckBoxes = true;
            _lvApps.FullRowSelect = true;
            _lvApps.Columns.Add(Tr.S("Программа", "Program"), 340);
            _lvApps.Columns.Add(Tr.S("Версия", "Version"), 130);
            _lvApps.Columns.Add(Tr.S("Издатель", "Publisher"), 260);
            _lvApps.Columns.Add(Tr.S("Размер", "Size"), 100);
            SetupOwnerDraw(_lvApps);

            tab.Controls.Add(_lvApps);
            tab.Controls.Add(_lblAppsInfo);
            tab.Controls.Add(warn);
            tab.Controls.Add(top);
            return tab;
        }

        // Список установленных программ читается из реестра и для каждой записи ищет exe
        // на диске. В UI-потоке это давало многосекундное замирание при каждом
        // переключении на вкладку. Теперь: фон + кэш, чтобы повторный вход был мгновенным.
        private int _appsBusy;
        private int _uninstBusy;      // идёт очередь деинсталляций (фоновый поток)
        private string _appsNote;     // одноразовая приписка к строке «Установленных программ: N»

        private void RefreshApps() { RefreshApps(false); }

        private void RefreshApps(bool force)
        {
            if (!force && _apps != null && _apps.Count > 0) { PopulateApps(_apps); return; }
            if (Interlocked.CompareExchange(ref _appsBusy, 1, 0) != 0) return;
            _lblAppsInfo.Text = Tr.S("Чтение списка программ…", "Reading program list…");
            Thread t = new Thread(delegate()
            {
                List<InstalledApp> found = null;
                try { found = _engine.GetInstalledApps(); }
                catch { found = new List<InstalledApp>(); }
                UiPost(delegate { _apps = found; PopulateApps(found); });
                Interlocked.Exchange(ref _appsBusy, 0);
            });
            t.IsBackground = true;
            t.Start();
        }

        private void PopulateApps(List<InstalledApp> apps)
        {
            _lvApps.BeginUpdate();
            try
            {
                _lvApps.Items.Clear();
                List<ListViewItem> rows = new List<ListViewItem>();
                foreach (InstalledApp a in apps)
                {
                    ListViewItem it = new ListViewItem(a.Name);
                    it.SubItems.Add(a.Version ?? "");
                    it.SubItems.Add(a.Publisher ?? "");
                    it.SubItems.Add(a.EstimatedSizeBytes > 0 ? Engine.FormatBytes(a.EstimatedSizeBytes) : "");
                    it.ToolTipText = a.Name + (string.IsNullOrEmpty(a.ExePath) ? "" : "\r\n" + a.ExePath);
                    it.Tag = a;
                    it.ForeColor = _theme.Text;
                    it.BackColor = _theme.Surface;
                    rows.Add(it);
                }
                _lvApps.Items.AddRange(rows.ToArray());
            }
            finally { _lvApps.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvApps);
            string note = _appsNote ?? Tr.S("   ·   отметьте и нажмите «Удалить выбранное»", "   ·   check and click “Uninstall selected”");
            _appsNote = null;
            _lblAppsInfo.Text = Tr.S("Установленных программ: ", "Installed programs: ") + apps.Count + note;
        }

        private void DoUninstall()
        {
            if (_uninstBusy != 0)
            {
                _lblAppsInfo.Text = Tr.S("Дождитесь окончания текущей деинсталляции.", "Wait for the current uninstall to finish.");
                return;
            }
            List<InstalledApp> sel = new List<InstalledApp>();
            foreach (ListViewItem it in _lvApps.Items)
                if (it.Checked && it.Tag is InstalledApp) sel.Add((InstalledApp)it.Tag);
            if (sel.Count == 0) { MsgInfo(Tr.S("Не выбрано ни одной программы.", "No programs selected."), Tr.S("Программы", "Programs")); return; }

            List<InstalledApp> queue = new List<InstalledApp>();
            foreach (InstalledApp a in sel)
            {
                DialogResult dr = MessageBox.Show(this, Tr.S("Удалить «", "Uninstall “") + a.Name + Tr.S("»?", "”?"),
                    Tr.S("Деинсталляция", "Uninstall"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr == DialogResult.Yes) queue.Add(a);
            }
            if (queue.Count == 0) return;
            if (Interlocked.CompareExchange(ref _uninstBusy, 1, 0) != 0) return;

            // Деинсталляторы — по очереди, с ожиданием: два MSI разом = «уже идёт другая установка»,
            // а запущенные скопом окна перекрывают друг друга. Ждём только сам запущенный процесс:
            // msiexec и setup.exe, передавшие работу дочернему, нам не видны. Отказ запуска
            // (нет команды, деинсталлятор удалён вместе с папкой игры) раньше глотался молча.
            Thread t = new Thread(delegate()
            {
                int launched = 0;
                List<string> failed = new List<string>();
                for (int i = 0; i < queue.Count; i++)
                {
                    InstalledApp a = queue[i];
                    string progress = Tr.S("Идёт деинсталляция: ", "Uninstalling: ") + a.Name + " (" + (i + 1) + "/" + queue.Count + ")…";
                    UiPost(delegate { _lblAppsInfo.Text = progress; });
                    Process p = null; string err;
                    try { err = _engine.RunUninstall(a, out p); }
                    catch (Exception ex) { err = ex.Message; }
                    if (err != null) { failed.Add(a.Name + " — " + err); continue; }
                    launched++;
                    if (p != null)
                    {
                        try { p.WaitForExit(); } catch { }
                        p.Dispose();
                    }
                }
                Interlocked.Exchange(ref _uninstBusy, 0);
                int launchedCopy = launched;
                UiPost(delegate
                {
                    if (failed.Count > 0)
                        MsgError(Tr.S("Не удалось запустить деинсталлятор:\r\n", "Failed to launch the uninstaller:\r\n") + string.Join("\r\n", failed.ToArray()));
                    _appsNote = Tr.S("   ·   деинсталляция завершена: запущено ", "   ·   uninstall finished: launched ") + launchedCopy +
                        (failed.Count > 0 ? Tr.S(", не удалось ", ", failed ") + failed.Count : "");
                    RefreshApps(true);
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
