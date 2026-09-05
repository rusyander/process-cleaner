// Windows Process Cleaner — вкладка «Автозапуск»: реестр Run и папки Startup
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
        // ---------- Вкладка: Автозапуск ----------
        private Control BuildStartupTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel top = MkToolbar();

            Button btnRefresh = MkFlowButton(Tr.S("Обновить список", "Refresh list"), 170, true);
            btnRefresh.Click += delegate { RefreshStartup(true); };

            Label warn = MkNote(Tr.S("Галочка = запускается при входе. Снять — отключить, как в Диспетчере задач (запись сохраняется), поставить — включить.",
                                     "Checkbox = starts at sign-in. Uncheck to disable as Task Manager does (the entry is kept), check to enable."), true);
            _lblStartupInfo = MkNote(Tr.S("Нажмите «Обновить список»", "Click “Refresh list”"), false);

            top.Controls.Add(btnRefresh);

            _lvStartup = new FastListView();
            _lvStartup.Dock = DockStyle.Fill;
            _lvStartup.View = View.Details;
            _lvStartup.CheckBoxes = true;
            _lvStartup.FullRowSelect = true;
            _lvStartup.Columns.Add(Tr.S("Программа", "Program"), 300);
            _lvStartup.Columns.Add(Tr.S("Издатель / источник", "Publisher / source"), 220);
            _lvStartup.Columns.Add(Tr.S("Файл автозапуска", "Startup target"), 460);
            SetupOwnerDraw(_lvStartup);
            _pathColumns[_lvStartup] = 2;
            _lvStartup.ItemChecked += Startup_ItemChecked;

            tab.Controls.Add(_lvStartup);
            tab.Controls.Add(_lblStartupInfo);
            tab.Controls.Add(warn);
            tab.Controls.Add(top);
            return tab;
        }

        // Та же история, что и с вкладкой программ, только хуже: помимо реестра здесь
        // разрешаются .lnk из папок автозагрузки. В UI-потоке это подвешивало окно
        // на каждый вход на вкладку.
        private int _startupBusy;
        private List<AutostartEntry> _autostartCache;

        private void RefreshStartup() { RefreshStartup(false); }

        private void RefreshStartup(bool force)
        {
            if (!force && _apps != null && _autostartCache != null)
            {
                PopulateStartup(_apps, _autostartCache);
                return;
            }
            if (Interlocked.CompareExchange(ref _startupBusy, 1, 0) != 0) return;
            _lblStartupInfo.Text = Tr.S("Чтение автозапуска…", "Reading startup entries…");
            Thread t = new Thread(delegate()
            {
                List<InstalledApp> apps = null;
                List<AutostartEntry> entries = null;
                try
                {
                    apps = _engine.GetInstalledApps();
                    entries = _engine.GetAutostartEntries();
                }
                catch
                {
                    if (apps == null) apps = new List<InstalledApp>();
                    if (entries == null) entries = new List<AutostartEntry>();
                }
                UiPost(delegate
                {
                    _apps = apps; _autostartCache = entries;
                    PopulateStartup(apps, entries);
                });
                Interlocked.Exchange(ref _startupBusy, 0);
            });
            t.IsBackground = true;
            t.Start();
        }

        private void PopulateStartup(List<InstalledApp> apps, List<AutostartEntry> entries)
        {
            // Галочки выставляются программно, а обработчик ItemChecked пишет в реестр:
            // без этого флага одно заполнение списка перезаписало бы весь автозапуск.
            _suppressStartup = true;
            _lvStartup.BeginUpdate();
            HashSet<string> appExes = new HashSet<string>();
            int onCount = 0;
            try
            {
                _lvStartup.Items.Clear();
                List<ListViewItem> rows = new List<ListViewItem>();
                foreach (InstalledApp a in apps)
                {
                    bool on = _engine.IsExeInAutostart(a.ExePath, entries);
                    a.InAutostart = on;
                    if (!string.IsNullOrEmpty(a.ExePath)) appExes.Add(a.ExePath.ToLowerInvariant());

                    ListViewItem it = new ListViewItem(a.Name);
                    it.SubItems.Add(StartupPublisherText(a, entries));
                    it.SubItems.Add(a.ExePath != null ? a.ExePath : Tr.S("(exe не найден)", "(exe not found)"));
                    it.ToolTipText = StartupAppTip(a, entries);
                    it.Tag = a;
                    it.Checked = on;
                    it.ForeColor = _theme.Text;
                    it.BackColor = _theme.Surface;
                    rows.Add(it);
                    if (on) onCount++;
                }

                // записи автозапуска, не сопоставленные с установленными программами
                foreach (AutostartEntry e in entries)
                {
                    string ep = e.ExePath != null ? e.ExePath.ToLowerInvariant() : null;
                    if (ep != null && appExes.Contains(ep)) continue;
                    ListViewItem it = new ListViewItem(e.Name);
                    it.SubItems.Add(StartupSourceText(e));
                    it.SubItems.Add(e.Command != null ? e.Command : "");
                    it.ToolTipText = e.Name + "\r\n" + (e.Command != null ? e.Command : "");
                    it.Tag = e;
                    it.Checked = e.Enabled;
                    it.ForeColor = _theme.Text;
                    it.BackColor = _theme.CandidateBg;
                    rows.Add(it);
                    if (e.Enabled) onCount++;
                }
                _lvStartup.Items.AddRange(rows.ToArray());
            }
            finally
            {
                _lvStartup.EndUpdate();
                AutoFillLastColumnDeferred(_lvStartup);
                _suppressStartup = false;
            }

            _startupPrograms = apps.Count;
            UpdateStartupInfo(onCount);
            AutoFillLastColumnDeferred(_lvStartup);
        }

        private int _startupPrograms;

        private void UpdateStartupInfo(int onCount)
        {
            _lblStartupInfo.Text = Tr.S("Программ: ", "Programs: ") + _startupPrograms +
                Tr.S("   ·   запускаются при входе: ", "   ·   start at sign-in: ") + onCount +
                Tr.S("   ·   оранжевым — записи автозапуска вне списка установленных", "   ·   orange — startup entries outside the installed list");
        }

        private static string DisabledMark() { return Tr.S("отключено", "disabled"); }

        private static string StartupSourceText(AutostartEntry e)
        {
            string src = e.SourceLabel != null ? e.SourceLabel : "";
            return e.Enabled ? src : src + " · " + DisabledMark();
        }

        // Издатель; если записи программы есть, но все отключены в Windows — пометка
        // «отключено»: без неё снятая галочка выглядела бы как «записи нет вовсе».
        private string StartupPublisherText(InstalledApp a, List<AutostartEntry> entries)
        {
            string pub = a.Publisher != null ? a.Publisher : "";
            if (a.InAutostart || _engine.EntriesForExe(a.ExePath, entries).Count == 0) return pub;
            return pub.Length == 0 ? DisabledMark() : pub + " · " + DisabledMark();
        }

        private string StartupAppTip(InstalledApp a, List<AutostartEntry> entries)
        {
            StringBuilder sb = new StringBuilder(a.Name);
            if (!string.IsNullOrEmpty(a.ExePath)) sb.Append("\r\n").Append(a.ExePath);
            foreach (AutostartEntry e in _engine.EntriesForExe(a.ExePath, entries))
                sb.Append("\r\n").Append(e.SourceLabel).Append(e.Enabled ? "" : " · " + DisabledMark()).Append(": ").Append(e.Command);
            return sb.ToString();
        }

        private int StartupCheckedCount()
        {
            int n = 0;
            foreach (ListViewItem it in _lvStartup.Items) if (it.Checked) n++;
            return n;
        }

        private void RevertStartupCheck(ListViewItem it)
        {
            _suppressStartup = true; it.Checked = !it.Checked; _suppressStartup = false;
        }

        private void Startup_ItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_suppressStartup) return;
            ListViewItem it = e.Item;
            object tag = it.Tag;
            try
            {
                // Список записей нужен для переключения: без него программа «отключалась»
                // бы вхолостую. Кэш живёт, пока мы сами его не меняем, — «Обновить список»
                // перечитывает его целиком.
                if (_autostartCache == null) _autostartCache = _engine.GetAutostartEntries();
                if (tag is InstalledApp)
                {
                    InstalledApp app = (InstalledApp)tag;
                    if (string.IsNullOrEmpty(app.ExePath))
                    {
                        if (it.Checked)
                        {
                            MessageBox.Show(this, Tr.S("Не удалось определить exe этой программы — добавить в автозапуск нельзя.",
                                                 "Could not determine this program's exe — cannot add to startup."),
                                Tr.S("Автозапуск", "Startup"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            RevertStartupCheck(it);
                        }
                        return;
                    }
                    if (it.Checked) _engine.EnableAutostartForExe(app.Name, app.ExePath, _autostartCache);
                    else _engine.DisableAutostartForExe(app.ExePath, _autostartCache);
                    app.InAutostart = it.Checked;
                    it.SubItems[1].Text = StartupPublisherText(app, _autostartCache);
                    it.ToolTipText = StartupAppTip(app, _autostartCache);
                }
                else if (tag is AutostartEntry)
                {
                    // Только флаг StartupApproved — как Диспетчер задач. Раньше снятая галочка
                    // УДАЛЯЛА значение Run или ярлык, а возвращённая писала новую запись в
                    // HKCU\Run без параметров командной строки.
                    AutostartEntry ent = (AutostartEntry)tag;
                    _engine.SetAutostartEnabled(ent, it.Checked);
                    it.SubItems[1].Text = StartupSourceText(ent);
                }
                UpdateStartupInfo(StartupCheckedCount());
            }
            catch (Exception ex)
            {
                // В Windows ничего не изменилось — галочка возвращается, иначе список
                // показывал бы состояние, которого нет (раньше ошибка глоталась молча).
                RevertStartupCheck(it);
                MsgError(ex.Message);
            }
        }
    }
}
