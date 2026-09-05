// Windows Process Cleaner — вкладка «Обновления»: проверка, серьёзность, установка, исключения
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
        // ---------- Вкладка: Обновления программ ----------

        private void DoScanUpdates()
        {
            if (Interlocked.CompareExchange(ref _updBusy, 1, 0) != 0) return;
            _engine.ResetUpdateCancel();
            _lblUpdInfo.Text = Tr.S("Опрос менеджеров пакетов… это может занять до минуты",
                                    "Querying package managers… this can take up to a minute");
            _lvUpdates.Items.Clear();
            _updates = null;
            if (_btnUpdCancel != null) _btnUpdCancel.Enabled = true;

            Thread t = new Thread(delegate()
            {
                List<UpdateItem> found;
                string note = null;
                try { found = _engine.ScanUpdates(out note); }
                catch (Exception ex) { found = new List<UpdateItem>(); note = ex.Message; }
                string noteCopy = note;
                Interlocked.Exchange(ref _updBusy, 0);
                UiPost(delegate
                {
                    _updates = found;
                    PopulateUpdates(found, noteCopy);
                    if (_btnUpdCancel != null) _btnUpdCancel.Enabled = false;
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void PopulateUpdates(List<UpdateItem> found, string note)
        {
            found = found ?? new List<UpdateItem>();
            _lvUpdates.BeginUpdate();
            try
            {
                _lvUpdates.Items.Clear();
                ListViewItem[] rows = new ListViewItem[found.Count];
                for (int i = 0; i < found.Count; i++)
                {
                    UpdateItem u = found[i];
                    ListViewItem it = new ListViewItem(u.Name);
                    it.SubItems.Add(u.Current);
                    it.SubItems.Add(u.Available);
                    it.SubItems.Add(u.SeverityText ?? "");
                    it.SubItems.Add(u.Manager);
                    it.SubItems.Add(u.Duplicate
                        ? Tr.S("дубль — тот же софт есть в winget", "duplicate — same software via winget")
                        : "");
                    it.ToolTipText = u.Name + "\r\n" + u.Manager + ": " + u.Id
                                   + "\r\n" + u.Current + " → " + u.Available
                                   + "\r\n" + SeverityHint(u);
                    it.Tag = u;
                    // Ничего не отмечаем сами: обновление — действие пользователя.
                    it.Checked = false;
                    // Дубль приглушаем текстом, а не зелёным фоном: зелёный в этом
                    // приложении значит «в белом списке, защищено» — здесь смысл обратный.
                    it.ForeColor = u.Duplicate ? _theme.Subtle : _theme.Text;
                    it.BackColor = u.SeverityLevel == 3 ? _theme.CandidateBg : _theme.Surface;
                    rows[i] = it;
                }
                _lvUpdates.Items.AddRange(rows);
            }
            finally { _lvUpdates.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvUpdates);

            string msg;
            if (found.Count == 0)
                msg = Tr.S("Обновлений не найдено", "No updates found");
            else
                msg = Tr.S("Найдено обновлений: ", "Updates found: ") + found.Count
                    + Tr.S("  ·  отметьте нужные и нажмите «Обновить выбранное»",
                           "  ·  check the ones you want and click “Update selected”");
            if (!string.IsNullOrEmpty(note)) msg += "  ·  " + note;
            if (!_engine.HasWinget)
                msg += Tr.S("  ·  установите «Установщик приложений» из Microsoft Store, чтобы появился winget",
                            "  ·  install “App Installer” from the Microsoft Store to get winget");
            _lblUpdInfo.Text = msg;
        }

        // Подпись в колонке короткая, поэтому смысл уровня объясняем в подсказке строки.
        private static string SeverityHint(UpdateItem u)
        {
            switch (u.SeverityLevel)
            {
                case 3: return Tr.S("Важность: крупное — меняется старшая часть версии, поведение может измениться",
                                    "Impact: major — the leading version part changes, behaviour may change");
                case 2: return Tr.S("Важность: среднее — новые возможности, совместимость обычно сохраняется",
                                    "Impact: minor — new features, usually compatible");
                case 1: return Tr.S("Важность: мелкое — исправления и правки сборки",
                                    "Impact: patch — fixes and build tweaks");
                default: return Tr.S("Важность: неизвестна — менеджер не сообщает точную установленную версию",
                                     "Impact: unknown — the manager does not report the exact installed version");
            }
        }

        private void SetAllUpdateChecks(bool value)
        {
            _lvUpdates.BeginUpdate();
            try
            {
                foreach (ListViewItem it in _lvUpdates.Items)
                {
                    UpdateItem u = it.Tag as UpdateItem;
                    // «Все» не отмечает дубли: обновлять одно и то же двумя менеджерами не нужно
                    it.Checked = value && (u == null || !u.Duplicate);
                }
            }
            finally { _lvUpdates.EndUpdate(); }
        }

        private List<UpdateItem> CheckedUpdates()
        {
            List<UpdateItem> sel = new List<UpdateItem>();
            foreach (ListViewItem it in _lvUpdates.Items)
            {
                if (!it.Checked) continue;
                UpdateItem u = it.Tag as UpdateItem;
                if (u != null) sel.Add(u);
            }
            return sel;
        }

        private void ExcludeSelectedUpdates()
        {
            List<UpdateItem> sel = CheckedUpdates();
            if (sel.Count == 0)
            {
                MsgInfo(Tr.S("Отметьте программы, которые больше не предлагать.",
                             "Check the programs you no longer want offered."), Tr.S("Обновления", "Updates"));
                return;
            }
            if (_engine.Config.UpdateExclude == null)
                _engine.Config.UpdateExclude = new List<string>();
            foreach (UpdateItem u in sel)
                if (!_engine.Config.UpdateExclude.Contains(u.Id))
                    _engine.Config.UpdateExclude.Add(u.Id);
            _engine.SaveConfig();
            LoadSettingsToUi();
            for (int i = _lvUpdates.Items.Count - 1; i >= 0; i--)
                if (_lvUpdates.Items[i].Checked) _lvUpdates.Items.RemoveAt(i);
            _lblUpdInfo.Text = Tr.S("Добавлено в исключения: ", "Added to exclusions: ") + sel.Count
                             + Tr.S("  ·  список правится в Настройках", "  ·  editable in Settings");
        }

        private void OpenUpdateLog()
        {
            string path = _engine.UpdateLogPath;
            if (!File.Exists(path))
            {
                MsgInfo(Tr.S("Лог пуст — обновления ещё не устанавливались.",
                             "The log is empty — no updates have been installed yet."), Tr.S("Обновления", "Updates"));
                return;
            }
            try { Process.Start("notepad.exe", path); } catch { }
        }

        private void DoApplyUpdates()
        {
            List<UpdateItem> sel = CheckedUpdates();
            if (sel.Count == 0)
            {
                MsgInfo(Tr.S("Отметьте, что обновить.", "Check what to update."), Tr.S("Обновления", "Updates"));
                return;
            }
            if (Interlocked.CompareExchange(ref _updBusy, 1, 0) != 0) return;

            StringBuilder names = new StringBuilder();
            for (int i = 0; i < sel.Count && i < 12; i++)
                names.Append("\r\n  · ").Append(sel[i].Name).Append("  ")
                     .Append(sel[i].Current).Append(" → ").Append(sel[i].Available);
            if (sel.Count > 12) names.Append(Tr.S("\r\n  · … и ещё ", "\r\n  · … and ")).Append(sel.Count - 12);

            int batch = _engine.Config.UpdateBatchSize;
            if (batch < 1) batch = 1;
            if (batch > 20) batch = 20;

            string ask = Tr.S("Обновить программ: ", "Update programs: ") + sel.Count + names.ToString()
                       + (batch > 1
                          ? Tr.S("\r\n\r\nМенеджеру отдаём по ", "\r\n\r\nSent to the manager in groups of ") + batch
                            + Tr.S(" пакета за раз. ", " packages. ")
                          : Tr.S("\r\n\r\nПо одному пакету за раз. ", "\r\n\r\nOne package at a time. "))
                       + Tr.S("Установщики работают тихо и по очереди — одновременно их запускать нельзя, Windows Installer этого не допускает. Открытые программы могут быть перезапущены. Продолжить?",
                              "Installers run silently and sequentially — they cannot run at once, Windows Installer forbids it. Open programs may be restarted. Continue?");
            if (MessageBox.Show(this, ask, Tr.S("Обновление программ", "Updating programs"),
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                Interlocked.Exchange(ref _updBusy, 0);
                return;
            }

            _engine.ResetUpdateCancel();
            if (_btnUpdCancel != null) _btnUpdCancel.Enabled = true;
            _lblUpdInfo.Text = Tr.S("Обновление… 0/", "Updating… 0/") + sel.Count;
            string op = Tr.S("установка обновлений программ", "installing program updates");
            BeginWrite(op);
            _updApplying = true;

            List<List<UpdateItem>> groups = Engine.BuildUpdateGroups(sel, batch);

            Thread t = new Thread(delegate()
            {
                int done = 0, ok = 0, failed = 0;
                foreach (List<UpdateItem> grp in groups)
                {
                    if (_engine.UpdatesCancelled) break;
                    List<UpdateItem> g = grp;
                    UiPost(delegate
                    {
                        foreach (UpdateItem gu in g) SetUpdateRowState(gu, Tr.S("обновляется…", "updating…"));
                    });

                    int okHere;
                    if (g.Count == 1)
                    {
                        // Один пакет — берём точный код возврата, без лишнего перескана.
                        string msg;
                        bool good;
                        try { good = _engine.ApplyUpdate(g[0], out msg); }
                        catch (Exception ex) { good = false; msg = ex.Message; g[0].Status = msg; g[0].LastOk = false; }
                        okHere = good ? 1 : 0;
                    }
                    else
                    {
                        string gm;
                        try { okHere = _engine.ApplyUpdateBatch(g, out gm); }
                        catch (Exception ex)
                        {
                            okHere = 0;
                            foreach (UpdateItem gu in g) { gu.Status = ex.Message; gu.LastOk = false; }
                        }
                    }

                    done += g.Count; ok += okHere; failed += g.Count - okHere;
                    int d = done, okc = ok, badc = failed;
                    UiPost(delegate
                    {
                        foreach (UpdateItem gu in g)
                            SetUpdateRowState(gu, (gu.LastOk ? "✓ " : "✗ ") + gu.Status);
                        _lblUpdInfo.Text = Tr.S("Обновление… ", "Updating… ") + d + "/" + sel.Count
                                         + Tr.S("  ·  успешно: ", "  ·  ok: ") + okc
                                         + Tr.S("  ·  с ошибкой: ", "  ·  failed: ") + badc;
                    });
                }
                int okFinal = ok, badFinal = failed, doneFinal = done;
                bool cancelled = _engine.UpdatesCancelled;
                EndWrite(op);
                Interlocked.Exchange(ref _updBusy, 0);
                UiPost(delegate
                {
                    if (_btnUpdCancel != null) _btnUpdCancel.Enabled = false;
                    _updApplying = false;
                    _lblUpdInfo.Text = (cancelled ? Tr.S("Остановлено. ", "Stopped. ") : Tr.S("Готово. ", "Done. "))
                                     + Tr.S("Обновлено: ", "Updated: ") + okFinal
                                     + (badFinal > 0 ? Tr.S("  ·  не удалось: ", "  ·  failed: ") + badFinal : "")
                                     + Tr.S("  ·  подробности в логе", "  ·  details in the log");
                    if (_tray != null && doneFinal > 0)
                        _tray.ShowBalloonTip(3000, Tr.S("Обновление программ", "Program updates"),
                            Tr.S("Обновлено: ", "Updated: ") + okFinal
                            + (badFinal > 0 ? Tr.S(", не удалось: ", ", failed: ") + badFinal : ""),
                            badFinal > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void SetUpdateRowState(UpdateItem u, string state)
        {
            foreach (ListViewItem it in _lvUpdates.Items)
            {
                if (!ReferenceEquals(it.Tag, u)) continue;
                while (it.SubItems.Count < 6) it.SubItems.Add("");
                it.SubItems[5].Text = state;
                return;
            }
        }
        // ---------- Вкладка: Обновления программ (список) ----------

        private Control BuildUpdatesTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel top = MkToolbar();

            Button btnCheck = MkFlowButton(Tr.S("Проверить обновления", "Check for updates"), 200, true);
            btnCheck.Click += delegate { DoScanUpdates(); };
            Button btnApply = MkFlowButton(Tr.S("Обновить выбранное", "Update selected"), 190, true);
            btnApply.Click += delegate { DoApplyUpdates(); };
            _btnUpdCancel = MkFlowButton(Tr.S("Стоп", "Stop"), 80, false);
            _btnUpdCancel.Enabled = false;
            _btnUpdCancel.Click += delegate
            {
                _engine.CancelUpdateWork();
                // Установщик не убивается (полуустановленный пакет хуже лишней минуты): цикл
                // остановится после текущего пакета или группы — скажем об этом, иначе «Стоп»
                // выглядит как не сработавший.
                if (_updApplying)
                    _lblUpdInfo.Text += Tr.S("  ·  остановка после текущей установки…", "  ·  stopping after the current install…");
            };
            Button btnAll = MkFlowButton(Tr.S("Все", "All"), 70, false);
            btnAll.Click += delegate { SetAllUpdateChecks(true); };
            Button btnNone = MkFlowButton(Tr.S("Ничего", "None"), 90, false);
            btnNone.Click += delegate { SetAllUpdateChecks(false); };
            Button btnSkip = MkFlowButton(Tr.S("Не предлагать", "Never offer"), 150, false);
            btnSkip.Click += delegate { ExcludeSelectedUpdates(); };
            Button btnLog = MkFlowButton(Tr.S("Лог", "Log"), 80, false);
            btnLog.Click += delegate { OpenUpdateLog(); };

            Label warn = MkNote(Tr.S("Обновляет сам менеджер пакетов (winget/Chocolatey), реестр не правится. «Важность» — масштаб скачка версии, а не оценка безопасности.",
                                     "The package manager itself (winget/Chocolatey) updates, no registry edits. “Impact” is the size of the version jump, not a security rating."), true);
            _lblUpdInfo = MkNote(Tr.S("Нажмите «Проверить обновления»", "Click “Check for updates”"), false);

            top.Controls.Add(btnCheck);
            top.Controls.Add(btnApply);
            top.Controls.Add(_btnUpdCancel);
            top.Controls.Add(btnAll);
            top.Controls.Add(btnNone);
            top.Controls.Add(btnSkip);
            top.Controls.Add(btnLog);

            _lvUpdates = new FastListView();
            _lvUpdates.Dock = DockStyle.Fill;
            _lvUpdates.View = View.Details;
            _lvUpdates.CheckBoxes = true;
            _lvUpdates.FullRowSelect = true;
            _lvUpdates.Columns.Add(Tr.S("Программа", "Program"), 280);
            _lvUpdates.Columns.Add(Tr.S("Установлена", "Installed"), 130);
            _lvUpdates.Columns.Add(Tr.S("Доступна", "Available"), 130);
            _lvUpdates.Columns.Add(Tr.S("Важность", "Impact"), 100);
            _lvUpdates.Columns.Add(Tr.S("Источник", "Source"), 90);
            _lvUpdates.Columns.Add(Tr.S("Состояние", "State"), 240);
            SetupOwnerDraw(_lvUpdates);

            tab.Controls.Add(_lvUpdates);
            tab.Controls.Add(_lblUpdInfo);
            tab.Controls.Add(warn);
            tab.Controls.Add(top);
            return tab;
        }
    }
}
