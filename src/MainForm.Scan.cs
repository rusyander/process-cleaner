// Windows Process Cleaner — вкладка «Сканирование»: мониторинг, поиск и завершение процессов, автоочистка
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
        private Control BuildScanTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel top = MkToolbar();

            // ряд 1
            Button btnScan = MkFlowButton(Tr.S("Сканировать", "Scan"), 150, true);
            btnScan.Click += delegate { DoScan(); };
            Button btnSelAll = MkFlowButton(Tr.S("Выбрать все", "Select all"), 130, false);
            btnSelAll.Click += delegate { SetAllChecks(true); };
            Button btnSelNone = MkFlowButton(Tr.S("Снять выбор", "Clear"), 130, false);
            btnSelNone.Click += delegate { SetAllChecks(false); };

            _chkGlobal = new CheckBox();
            _chkGlobal.Text = Tr.S("Все процессы (глобально)", "All processes (global)");
            _chkGlobal.AutoSize = true;
            _chkGlobal.Margin = new Padding(12, 7, 8, 0);
            _chkGlobal.CheckedChanged += delegate
            {
                _engine.Config.GlobalScan = _chkGlobal.Checked;
                _engine.SaveConfig();
            };
            Label lblWarn = MkFlowLabel(Tr.S("⚠ завершает любые ваши простаивающие/осиротевшие процессы",
                                             "⚠ terminates any of your idle/orphaned processes"), true);

            // ряд 2
            Button btnClean = MkFlowButton(Tr.S("Очистить выбранные", "Clean selected"), 200, true);
            btnClean.Click += delegate { DoClean(); };
            Button btnAuto = MkFlowButton(Tr.S("Автоочистка всех неактивных", "Auto-clean all inactive"), 250, true);
            btnAuto.Click += delegate { DoAutoCleanButton(); };
            Button btnPurge = MkFlowButton(Tr.S("Очистить память", "Purge memory"), 170, false);
            btnPurge.Click += delegate { DoPurgeOnly(); };

            top.Controls.Add(btnScan);
            top.Controls.Add(btnSelAll);
            top.Controls.Add(btnSelNone);
            top.Controls.Add(_chkGlobal);
            top.Controls.Add(lblWarn);
            top.SetFlowBreak(lblWarn, true);   // второй ряд — действия очистки
            top.Controls.Add(btnClean);
            top.Controls.Add(btnAuto);
            top.Controls.Add(btnPurge);

            _lblSummary = MkNote(Tr.S("Нажмите «Сканировать»", "Click “Scan”"), false);

            _lvScan = new FastListView();
            _lvScan.Dock = DockStyle.Fill;
            _lvScan.View = View.Details;
            _lvScan.CheckBoxes = true;
            _lvScan.FullRowSelect = true;
            _lvScan.Columns.Add(Tr.S("Категория", "Category"), 120);
            _lvScan.Columns.Add(Tr.S("Имя", "Name"), 130);
            _lvScan.Columns.Add("PID", 65);
            _lvScan.Columns.Add("PPID", 65);
            _lvScan.Columns.Add("CPU %", 65);
            _lvScan.Columns.Add("RAM", 85);
            _lvScan.Columns.Add(Tr.S("Простой", "Idle"), 80);
            _lvScan.Columns.Add(Tr.S("Окно", "Window"), 55);
            _lvScan.Columns.Add(Tr.S("Порт", "Port"), 55);
            _lvScan.Columns.Add(Tr.S("Дети", "Children"), 55);
            _lvScan.Columns.Add(Tr.S("Статус", "Status"), 330);
            SetupOwnerDraw(_lvScan);

            _lblResult = new Label();
            _lblResult.Dock = DockStyle.Bottom;
            _lblResult.Height = 32;
            _lblResult.TextAlign = ContentAlignment.MiddleLeft;
            _lblResult.Padding = new Padding(2, 0, 0, 0);
            _lblResult.Text = "";

            tab.Controls.Add(_lvScan);
            tab.Controls.Add(_lblResult);
            tab.Controls.Add(_lblSummary);
            tab.Controls.Add(top);
            return tab;
        }

        // ---------- Логика ----------
        // Тик мониторинга: целиком в фоновом потоке, в UI возвращается только
        // обновление иконки трея. Interlocked не даёт тикам наложиться, если один
        // затянулся (много процессов, холодный кэш).
        private void MonitorCallback(object state)
        {
            if (_closing) return;
            if (Interlocked.CompareExchange(ref _monitorBusy, 1, 0) != 0) return;
            try { _engine.MonitorTick(); }
            catch { }
            finally { Interlocked.Exchange(ref _monitorBusy, 0); }
            UiPost(delegate { UpdateTrayState(); });
        }

        private void RestartMonitor()
        {
            if (_monitor != null) { _monitor.Dispose(); _monitor = null; }
            if (_closing || !_engine.Config.MonitorEnabled) return;
            int period = _engine.Config.MonitorIntervalSeconds * 1000;
            _monitor = new System.Threading.Timer(MonitorCallback, null, period, period);
        }

        // Безопасная отправка работы в UI-поток из фонового.
        private void UiPost(MethodInvoker action)
        {
            if (_closing) return;
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch { }
        }

        private List<ProcInfo> _lastScan = new List<ProcInfo>();
        private bool _autoAfterScan;          // «Автоочистка» нажата до первого сканирования

        // Сканирование процессов — в фоне. Раньше Scan() вместе с чтением путей и SID
        // всех процессов шло в UI-потоке, и окно висело на всё время обхода.
        private void DoScan()
        {
            if (Interlocked.CompareExchange(ref _scanBusy, 1, 0) != 0) return;
            _lblSummary.Text = Tr.S("Сканирование…", "Scanning…");
            bool global = _engine.Config.GlobalScan;
            Thread t = new Thread(delegate()
            {
                List<ProcInfo> found = null;
                try { found = _engine.Scan(global); }
                catch { found = new List<ProcInfo>(); }
                UiPost(delegate { PopulateScan(found); });
                Interlocked.Exchange(ref _scanBusy, 0);
            });
            t.IsBackground = true;
            t.Start();
        }

        private void PopulateScan(List<ProcInfo> found)
        {
            _lastScan = found ?? new List<ProcInfo>();
            Dictionary<string, int> byCat = new Dictionary<string, int>();
            int candidates = 0;

            // BeginUpdate обязателен: без него каждый Add перерисовывает весь список,
            // а с owner-draw это 300 полных перерисовок на одно заполнение.
            _lvScan.BeginUpdate();
            try
            {
                _lvScan.Items.Clear();
                ListViewItem[] rows = new ListViewItem[_lastScan.Count];
                for (int i = 0; i < _lastScan.Count; i++)
                {
                    ProcInfo p = _lastScan[i];
                    ListViewItem it = new ListViewItem(p.Category);
                    it.SubItems.Add(p.Name);
                    it.SubItems.Add(p.Pid.ToString());
                    it.SubItems.Add(p.ParentPid.ToString());
                    it.SubItems.Add(p.CpuPercent.ToString("0.00", CultureInfo.InvariantCulture));
                    it.SubItems.Add(Engine.FormatBytes(p.RamBytes));
                    it.SubItems.Add(FormatSpan(p.IdleFor));
                    it.SubItems.Add(YesNo(p.HasWindow));
                    it.SubItems.Add(YesNo(p.ListensTcp));
                    it.SubItems.Add(YesNo(p.HasChildren));
                    it.SubItems.Add(p.Reason);
                    it.ToolTipText = p.Name + " (pid " + p.Pid + ")" +
                        (string.IsNullOrEmpty(p.Path) ? "" : "\r\n" + p.Path) + "\r\n" + p.Reason;
                    it.Tag = p;
                    it.Checked = p.IsCandidate;
                    it.ForeColor = _theme.Text;
                    if (p.IsCandidate) it.BackColor = _theme.CandidateBg;
                    else if (p.Whitelisted) it.BackColor = _theme.WhiteBg;
                    else it.BackColor = _theme.Surface;
                    rows[i] = it;

                    int c;
                    byCat[p.Category] = byCat.TryGetValue(p.Category, out c) ? c + 1 : 1;
                    if (p.IsCandidate) candidates++;
                }
                _lvScan.Items.AddRange(rows);
            }
            finally
            {
                _lvScan.EndUpdate();
                AutoFillLastColumnDeferred(_lvScan);
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(Tr.S("Найдено: ", "Found: ") + _lastScan.Count +
                      Tr.S("  ·  кандидатов на завершение: ", "  ·  termination candidates: ") + candidates + "   ");
            // Простой считается только тиками мониторинга: при выключенном мониторинге
            // IdleFor всегда 0 и кандидатов не будет никогда. Раньше это выглядело как
            // «всё активно» без объяснения — теперь причина названа прямо в сводке.
            AppConfig cfg = _engine.Config;
            int idleReq = cfg.GlobalScan ? Math.Max(cfg.IdleMinutes, cfg.GlobalIdleMinutes) : cfg.IdleMinutes;
            if (candidates == 0 && _lastScan.Count > 0 && !cfg.MonitorEnabled && idleReq > 0)
                sb.Append(Tr.S("⚠ мониторинг выключен — простой не измеряется, кандидатов не будет (см. Настройки)   ",
                               "⚠ monitoring is off — idle time is not measured, so no candidates (see Settings)   "));
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in byCat) parts.Add(kv.Key + " " + kv.Value);
            sb.Append(string.Join("  ", parts.ToArray()));
            _lblSummary.Text = sb.ToString();
            UpdateTrayState();

            if (_autoAfterScan)
            {
                _autoAfterScan = false;
                if (_lastScan.Count > 0) DoAutoCleanButton();
                else _lblResult.Text = Tr.S("Процессов по списку не найдено — завершать нечего.",
                                            "No matching processes found — nothing to terminate.");
            }
        }

        private void SetAllChecks(bool value)
        {
            _lvScan.BeginUpdate();
            try { foreach (ListViewItem it in _lvScan.Items) it.Checked = value; }
            finally { _lvScan.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvScan);
            _lvScan.Invalidate();
        }

        private void DoClean()
        {
            List<ProcInfo> toKill = new List<ProcInfo>();
            foreach (ListViewItem it in _lvScan.Items)
                if (it.Checked && it.Tag is ProcInfo) toKill.Add((ProcInfo)it.Tag);

            if (toKill.Count == 0)
            {
                MessageBox.Show(this, Tr.S("Не выбрано ни одного процесса.", "No processes selected."),
                    Tr.S("Очистка", "Clean"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult dr = MessageBox.Show(this,
                Tr.S("Завершить процессов: ", "Terminate processes: ") + toKill.Count + "?",
                Tr.S("Подтверждение", "Confirm"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dr != DialogResult.Yes) return;
            ExecuteKill(toKill);
        }

        // Автоочистка по кнопке: завершить все найденные неактивные (кандидаты).
        private void DoAutoCleanButton()
        {
            // Сканирование идёт в фоне: без готового списка запускаем его, а автоочистка
            // продолжится сама из PopulateScan — второй клик больше не нужен.
            if (_lastScan == null || _lastScan.Count == 0)
            {
                _autoAfterScan = true;
                _lblResult.Text = Tr.S("Сканирование… автоочистка продолжится, как только появится список.",
                                       "Scanning… auto-clean will continue as soon as the list appears.");
                DoScan();
                return;
            }
            List<ProcInfo> cands = _lastScan.Where(p => p.IsCandidate).ToList();
            if (cands.Count == 0)
            {
                MessageBox.Show(this, Tr.S("Неактивных (заброшенных) процессов не найдено.", "No inactive (abandoned) processes found."),
                    Tr.S("Автоочистка", "Auto-clean"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StringBuilder list = new StringBuilder();
            foreach (ProcInfo p in cands.Take(20)) list.AppendLine("• " + p.Name + " (pid " + p.Pid + ")");
            if (cands.Count > 20) list.AppendLine(Tr.S("… и ещё ", "… and ") + (cands.Count - 20) + Tr.S("", " more"));
            DialogResult dr = MessageBox.Show(this,
                Tr.S("Найдено неактивных процессов: ", "Inactive processes found: ") + cands.Count +
                Tr.S(".\r\nЗавершить все?\r\n\r\n", ".\r\nTerminate all?\r\n\r\n") + list,
                Tr.S("Автоочистка", "Auto-clean"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return;
            ExecuteKill(cands);
        }

        // Общий исполнитель: завершает список, чистит память, пишет историю, обновляет UI.
        // Завершение — в фоне. TerminateProcess ждёт закрытия до нескольких секунд;
        // раньше на 20 процессах UI стоял минуты. Внутри — пакетный TerminateMany:
        // WM_CLOSE рассылается всем сразу, ожидание общее.
        private void ExecuteKill(List<ProcInfo> list)
        {
            // Второй запуск поверх идущего дважды чистил память и писал историю параллельно.
            if (Interlocked.CompareExchange(ref _killBusy, 1, 0) != 0)
            {
                _lblResult.Text = Tr.S("Дождитесь окончания текущей операции.", "Wait for the current operation to finish.");
                return;
            }
            List<int> pids = new List<int>();
            List<string> names = new List<string>();
            foreach (ProcInfo p in list) { pids.Add(p.Pid); names.Add(p.Name + " (pid " + p.Pid + ")"); }

            _lblResult.Text = Tr.S("Завершение процессов…", "Terminating…");
            Thread t = new Thread(delegate()
            {
                long freed = 0;
                int killed = 0;
                Engine.MemResult mr = null;
                try
                {
                    killed = _engine.TerminateMany(pids, out freed);
                    mr = _engine.PurgeStandby();
                }
                catch { }
                long totalFreed = freed + (mr != null ? mr.FreedBytes : 0);
                string msg = mr != null ? mr.Message : "";
                int killedCopy = killed;
                try { SaveHistory(killedCopy, totalFreed, names); } catch { }
                Interlocked.Exchange(ref _killBusy, 0);

                UiPost(delegate
                {
                    _lblResult.Text = Tr.S("✓ Завершено процессов: ", "✓ Terminated: ") + killedCopy +
                        Tr.S("    ✓ Освобождено RAM: ", "    ✓ Freed RAM: ") + Engine.FormatBytes(totalFreed) +
                        "    ·  " + msg;
                    DoScan();
                    RefreshHistory();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DoPurgeOnly()
        {
            if (Interlocked.CompareExchange(ref _purgeBusy, 1, 0) != 0) return;
            _lblResult.Text = Tr.S("Очистка памяти…", "Purging memory…");
            Thread t = new Thread(delegate()
            {
                Engine.MemResult mr = null;
                try { mr = _engine.PurgeStandby(); }
                catch { }
                Interlocked.Exchange(ref _purgeBusy, 0);
                Engine.MemResult res = mr;
                UiPost(delegate
                {
                    if (res == null)
                    {
                        _lblResult.Text = Tr.S("Очистить память не удалось.", "Memory purge failed.");
                        return;
                    }
                    string msg = res.Message + Tr.S("  ·  освобождено ~", "  ·  freed ~") + Engine.FormatBytes(res.FreedBytes);
                    _lblResult.Text = msg;
                    if (_tray != null)
                        _tray.ShowBalloonTip(2500, "Standby Memory", msg,
                            res.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Автоочистка: сканирует и завершает только кандидатов. Вызывается и из таймера
        // расписания, поэтому вся тяжёлая часть — в фоновом потоке.
        private void RunAutoClean(bool interactive)
        {
            if (Interlocked.CompareExchange(ref _autoBusy, 1, 0) != 0) return;
            if (interactive) _lblResult.Text = Tr.S("Автоочистка…", "Auto-cleaning…");
            Thread t = new Thread(delegate()
            {
                int killed = 0; long freed = 0;
                Engine.MemResult mr = null;
                List<string> names = new List<string>();
                try
                {
                    List<ProcInfo> scan = _engine.Scan(_engine.Config.GlobalScan);
                    List<int> pids = new List<int>();
                    foreach (ProcInfo p in scan)
                        if (p.IsCandidate) { pids.Add(p.Pid); names.Add(p.Name + " (pid " + p.Pid + ")"); }
                    killed = _engine.TerminateMany(pids, out freed);
                    mr = _engine.PurgeStandby();
                }
                catch { }
                long total = freed + (mr != null ? mr.FreedBytes : 0);
                try { SaveHistory(killed, total, names); } catch { }

                int killedCopy = killed;
                Interlocked.Exchange(ref _autoBusy, 0);
                UiPost(delegate
                {
                    string msg = Tr.S("Завершено: ", "Terminated: ") + killedCopy
                               + Tr.S("  ·  освобождено ~", "  ·  freed ~") + Engine.FormatBytes(total);
                    _lblResult.Text = msg;
                    if (_tray != null)
                        _tray.ShowBalloonTip(3000, Tr.S("Автоочистка выполнена", "Auto-clean done"), msg, ToolTipIcon.Info);
                    if (interactive && Visible) { DoScan(); RefreshHistory(); }
                    UpdateTrayState();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void SaveHistory(int killed, long freed, List<string> names)
        {
            HistoryEntry e = new HistoryEntry();
            e.DateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            e.TerminatedCount = killed;
            e.FreedBytes = freed;
            e.Processes = names;
            _engine.AppendHistory(e);
        }
    }
}
