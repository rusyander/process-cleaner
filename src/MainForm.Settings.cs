// Windows Process Cleaner — вкладка «Настройки»: контролы, загрузка и сохранение
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
        private Control BuildSettingsTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(18, 14, 18, 14);

            // Кнопки закреплены снизу, содержимое скроллится: настройки растут,
            // и без этого «Сохранить» уезжает за пределы окна.
            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 52;

            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.AutoScroll = true;

            // Порядок важен: docking идёт в обратном порядке добавления,
            // поэтому Fill добавляется первым, а Bottom — последним,
            // иначе bar накроет низ body и он не доскроллится.
            tab.Controls.Add(body);
            tab.Controls.Add(bar);

            // ---- ЛЕВАЯ КОЛОНКА ----
            int lx = 18, cx = 340, y = 8;   // cx: «Простой для глобального режима, мин:» не влезал в 280
            SectionHeader(body, Tr.S("Критерии заброшенности", "Abandonment criteria"), lx, ref y);
            _numCpu = MakeNum(body, Tr.S("Порог CPU, %:", "CPU threshold, %:"), lx, cx, ref y, 0, 100, 2, 0.1M);
            _numIdle = MakeNum(body, Tr.S("Время простоя, мин:", "Idle time, min:"), lx, cx, ref y, 0, 1440, 0, 1);
            _numMinLife = MakeNum(body, Tr.S("Мин. время жизни, мин:", "Min lifetime, min:"), lx, cx, ref y, 0, 1440, 0, 1);
            _numGlobalIdle = MakeNum(body, Tr.S("Простой для глобального режима, мин:", "Idle for global mode, min:"), lx, cx, ref y, 1, 1440, 0, 1);

            y += 12;
            SectionHeader(body, Tr.S("Автоматизация", "Automation"), lx, ref y);
            _numInterval = MakeNum(body, Tr.S("Автоочистка каждые (часов, 1..24):", "Auto-clean every (hours, 1..24):"), lx, cx, ref y, 1, 24, 0, 1);
            _chkAuto = MakeCheck(body, Tr.S("Включить автоочистку по таймеру", "Enable auto-clean timer"), lx, ref y);
            _chkExcludeInstalled = MakeCheck(body, Tr.S("Глобально: не трогать Program Files", "Global: don't touch Program Files"), lx, ref y);
            _chkAutostart = MakeCheck(body, Tr.S("Запускать вместе с Windows", "Start with Windows"), lx, ref y);
            _chkStartMin = MakeCheck(body, Tr.S("Стартовать свёрнутым в трей", "Start minimized to tray"), lx, ref y);

            y += 12;
            SectionHeader(body, Tr.S("Производительность", "Performance"), lx, ref y);
            _chkMonitor = MakeCheck(body, Tr.S("Фоновый мониторинг CPU процессов", "Background CPU monitoring"), lx, ref y);
            _numMonInterval = MakeNum(body, Tr.S("Период мониторинга, с (5..300):", "Monitor period, s (5..300):"), lx, cx, ref y, 5, 300, 0, 5);
            _chkEmptyWs = MakeCheck(body, Tr.S("Сбрасывать рабочие наборы всех процессов (замедляет систему)",
                                               "Empty working sets of all processes (slows the system down)"), lx, ref y);

            y += 12;
            SectionHeader(body, Tr.S("Очистка диска", "Disk cleanup"), lx, ref y);
            _numSkipRecent = MakeNum(body, Tr.S("Не удалять файлы свежее, мин:", "Keep files newer than, min:"), lx, cx, ref y, 0, 1440, 0, 1);
            _chkCleanLog = MakeCheck(body, Tr.S("Вести лог очистки", "Write a cleanup log"), lx, ref y);

            y += 12;
            SectionHeader(body, Tr.S("Обновления программ", "Program updates"), lx, ref y);
            _chkUpdUnknown = MakeCheck(body, Tr.S("Показывать с неизвестной текущей версией",
                                                  "Show items with unknown installed version"), lx, ref y);
            _chkUpdChoco = MakeCheck(body, Tr.S("Опрашивать Chocolatey, если установлен",
                                                "Query Chocolatey when installed"), lx, ref y);
            _numUpdBatch = MakeNum(body, Tr.S("Пакетов за одну команду (1..20):", "Packages per command (1..20):"),
                                   lx, cx, ref y, 1, 20, 0, 1);

            y += 12;
            SectionHeader(body, Tr.S("Оформление", "Appearance"), lx, ref y);
            Label lblTheme = new Label();
            lblTheme.Text = Tr.S("Тема оформления:", "Theme:"); lblTheme.Left = lx; lblTheme.Top = y + 4; lblTheme.AutoSize = true;
            body.Controls.Add(lblTheme);
            _setLabels.Add(lblTheme);
            _cmbTheme = new RoundComboBox();
            _cmbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbTheme.Left = cx; _cmbTheme.Top = y; _cmbTheme.Width = 200;
            _setFields.Add(_cmbTheme);
            _cmbTheme.Items.AddRange(new object[] { Tr.S("По системе", "System"), Tr.S("Светлая", "Light"), Tr.S("Тёмная", "Dark") });
            _cmbTheme.SelectedIndexChanged += delegate { PreviewTheme(); };
            body.Controls.Add(_cmbTheme);
            y += 36;

            Label lblLang = new Label();
            lblLang.Text = "Язык / Language:"; lblLang.Left = lx; lblLang.Top = y + 4; lblLang.AutoSize = true;
            body.Controls.Add(lblLang);
            _setLabels.Add(lblLang);
            _cmbLang = new RoundComboBox();
            _cmbLang.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbLang.Left = cx; _cmbLang.Top = y; _cmbLang.Width = 200;
            _setFields.Add(_cmbLang);
            _cmbLang.Items.AddRange(new object[] { "Русский", "English" });
            body.Controls.Add(_cmbLang);
            y += 44;

            // ---- ПРАВАЯ КОЛОНКА ----
            // rx/rw — стартовые значения; настоящие считает LayoutSettings по тексту.
            int rx = 540, ry = 8, rw = 400;
            _setRight.Add(SectionHeader(body, Tr.S("Списки", "Lists"), rx, ref ry));
            AddLabel(body, Tr.S("Отслеживаемые процессы (по одному в строке):", "Watched processes (one per line):"), rx, ref ry);
            _txtWatch = MakeMultilineAt(body, rx, ref ry, rw, 150);
            AddLabel(body, Tr.S("Белый список — никогда не завершать:", "Whitelist — never terminate:"), rx, ref ry);
            _txtWhite = MakeMultilineAt(body, rx, ref ry, rw, 150);
            AddLabel(body, Tr.S("Dev-порты (через запятую):", "Dev ports (comma-separated):"), rx, ref ry);
            _txtPorts = new TextBox();
            _txtPorts.AutoSize = false;     // иначе однострочное поле не растягивается в обёртке
            Panel portsBox = MkBox(_txtPorts, new Padding(6, 4, 6, 4));
            portsBox.Left = rx; portsBox.Top = ry; portsBox.Width = rw; portsBox.Height = 30;
            body.Controls.Add(portsBox);
            _setRight.Add(portsBox);
            ry += 38;
            AddLabel(body, Tr.S("Не чистить эти пути (по одному в строке):", "Never clean these paths (one per line):"), rx, ref ry);
            _txtCleanExclude = MakeMultilineAt(body, rx, ref ry, rw, 90);
            AddLabel(body, Tr.S("Не предлагать обновления (Id пакета в строке):",
                                "Never offer updates for (package Id per line):"), rx, ref ry);
            _txtUpdExclude = MakeMultilineAt(body, rx, ref ry, rw, 90);

            // Распорка: AutoScroll считает границу по нижнему краю контролов.
            Panel spacer = new Panel();
            spacer.Left = lx; spacer.Top = Math.Max(y, ry); spacer.Width = 8; spacer.Height = 8;
            body.Controls.Add(spacer);

            // Базовая геометрия правой колонки для растяжения списков по высоте (LayoutSettings).
            foreach (Control r in _setRight) _setTop[r] = r.Top;
            _setLeftBottom = y; _setRightBottom = ry;
            _setSpacer = spacer; _setBody = body;

            LayoutSettings(body);
            body.Resize += delegate { LayoutSettings(body); };

            // ---- КНОПКИ ----
            Button save = new RoundButton();
            save.Text = Tr.S("Сохранить настройки", "Save settings");
            save.Tag = "primary";
            save.Left = lx; save.Top = 8; save.Width = 210; save.Height = 36;
            save.Click += delegate { SaveSettingsFromUi(); };
            bar.Controls.Add(save);

            Button openDir = new RoundButton();
            openDir.Text = Tr.S("Папка данных", "Data folder");
            openDir.Left = lx + 222; openDir.Top = 8; openDir.Width = 160; openDir.Height = 36;
            openDir.Click += delegate { try { Process.Start("explorer.exe", _engine.DataDir); } catch { } };
            bar.Controls.Add(openDir);

            return tab;
        }

        private Label SectionHeader(Panel tab, string text, int lx, ref int y)
        {
            Label l = new Label();
            l.Text = text; l.Left = lx; l.Top = y; l.AutoSize = true;
            l.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            l.Name = "section";
            tab.Controls.Add(l);
            y += 30;
            return l;
        }

        private NumericUpDown MakeNum(Panel tab, string label, int lx, int cx, ref int y,
            decimal min, decimal max, int dec, decimal step)
        {
            Label l = new Label();
            l.Text = label; l.Left = lx; l.Top = y + 4; l.AutoSize = true;
            tab.Controls.Add(l);
            _setLabels.Add(l);
            NumericUpDown n = new NumericUpDown();
            n.Left = cx; n.Top = y; n.Width = 120;
            _setFields.Add(n);
            n.Minimum = min; n.Maximum = max; n.DecimalPlaces = dec; n.Increment = step;
            tab.Controls.Add(n);
            y += 34;
            return n;
        }

        private CheckBox MakeCheck(Panel tab, string label, int lx, ref int y)
        {
            CheckBox c = new CheckBox();
            c.Text = label; c.Left = lx; c.Top = y; c.AutoSize = true;
            tab.Controls.Add(c);
            _setChecks.Add(c);
            y += 30;
            return c;
        }

        private void AddLabel(Panel tab, string text, int lx, ref int y)
        {
            Label l = new Label();
            // AutoSize: фиксированные 500 px вылезали за правый край и включали
            // горизонтальный скролл на странице настроек.
            l.Text = text; l.Left = lx; l.Top = y; l.AutoSize = true;
            tab.Controls.Add(l);
            _setRight.Add(l);
            y += 24;
        }

        private TextBox MakeMultilineAt(Panel tab, int lx, ref int y, int w, int h)
        {
            TextBox t = new TextBox();
            t.Multiline = true; t.ScrollBars = ScrollBars.Vertical;
            Panel box = MkBox(t, new Padding(6, 4, 2, 4));
            box.Left = lx; box.Top = y; box.Width = w; box.Height = h;
            tab.Controls.Add(box);
            _setRight.Add(box);
            _setStretch[box] = h;
            y += h + 12;
            return t;
        }

        // ---------- Настройки <-> UI ----------
        private void LoadSettingsToUi()
        {
            AppConfig c = _engine.Config;
            _numCpu.Value = (decimal)Math.Min(100, Math.Max(0, c.CpuThresholdPercent));
            _numIdle.Value = Math.Min(1440, Math.Max(0, c.IdleMinutes));
            _numMinLife.Value = Math.Min(1440, Math.Max(0, c.MinLifetimeMinutes));
            _numInterval.Value = Math.Min(24, Math.Max(1, c.AutoIntervalHours));
            _numGlobalIdle.Value = Math.Min(1440, Math.Max(1, c.GlobalIdleMinutes));
            _chkAuto.Checked = c.AutoEnabled;
            _chkExcludeInstalled.Checked = c.GlobalExcludeInstalled;
            _chkAutostart.Checked = c.Autostart;
            _chkStartMin.Checked = c.StartMinimized;
            _txtWatch.Text = string.Join("\r\n", c.Watchlist.ToArray());
            _txtWhite.Text = string.Join("\r\n", c.Whitelist.ToArray());
            _txtPorts.Text = string.Join(", ", c.DevPorts.Select(p => p.ToString()).ToArray());
            if (_chkMonitor != null) _chkMonitor.Checked = c.MonitorEnabled;
            if (_numMonInterval != null)
                _numMonInterval.Value = Math.Min(300, Math.Max(5, c.MonitorIntervalSeconds));
            if (_chkEmptyWs != null) _chkEmptyWs.Checked = c.EmptyWorkingSets;
            if (_numSkipRecent != null)
                _numSkipRecent.Value = Math.Min(1440, Math.Max(0, c.CleanSkipRecentMinutes));
            if (_chkCleanLog != null) _chkCleanLog.Checked = c.CleanLogEnabled;
            if (_txtCleanExclude != null)
                _txtCleanExclude.Text = string.Join("\r\n", c.CleanExclude.ToArray());
            if (_chkUpdUnknown != null) _chkUpdUnknown.Checked = c.UpdateIncludeUnknown;
            if (_chkUpdChoco != null) _chkUpdChoco.Checked = c.UpdateUseChoco;
            if (_numUpdBatch != null)
                _numUpdBatch.Value = Math.Min(20, Math.Max(1, c.UpdateBatchSize));
            if (_txtUpdExclude != null)
                _txtUpdExclude.Text = string.Join("\r\n", c.UpdateExclude.ToArray());
            if (_cmbTheme != null)
            {
                if (c.Theme == "light") _cmbTheme.SelectedIndex = 1;
                else if (c.Theme == "dark") _cmbTheme.SelectedIndex = 2;
                else _cmbTheme.SelectedIndex = 0;
            }
            if (_chkGlobal != null) _chkGlobal.Checked = c.GlobalScan;
            if (_cmbLang != null) _cmbLang.SelectedIndex = (c.Language == "en") ? 1 : 0;
            if (_miAuto != null) _miAuto.Checked = c.AutoEnabled;
        }

        private string ThemeModeFromCombo()
        {
            if (_cmbTheme == null) return "system";
            if (_cmbTheme.SelectedIndex == 1) return "light";
            if (_cmbTheme.SelectedIndex == 2) return "dark";
            return "system";
        }

        private void PreviewTheme()
        {
            _theme = Theme.Resolve(ThemeModeFromCombo());
            ApplyThemeAll();
        }

        private void SaveSettingsFromUi()
        {
            AppConfig c = _engine.Config;
            c.CpuThresholdPercent = (double)_numCpu.Value;
            c.IdleMinutes = (int)_numIdle.Value;
            c.MinLifetimeMinutes = (int)_numMinLife.Value;
            c.AutoIntervalHours = (int)_numInterval.Value;
            c.GlobalIdleMinutes = (int)_numGlobalIdle.Value;
            c.AutoEnabled = _chkAuto.Checked;
            c.GlobalExcludeInstalled = _chkExcludeInstalled.Checked;
            c.StartMinimized = _chkStartMin.Checked;
            c.Watchlist = ParseLines(_txtWatch.Text);
            c.Whitelist = ParseLines(_txtWhite.Text);
            c.DevPorts = ParsePorts(_txtPorts.Text);
            c.Theme = ThemeModeFromCombo();
            c.EmptyWorkingSets = _chkEmptyWs.Checked;
            c.CleanSkipRecentMinutes = (int)_numSkipRecent.Value;
            c.CleanLogEnabled = _chkCleanLog.Checked;
            c.CleanExclude = ParseLines(_txtCleanExclude.Text);
            c.UpdateIncludeUnknown = _chkUpdUnknown.Checked;
            c.UpdateUseChoco = _chkUpdChoco.Checked;
            c.UpdateBatchSize = (int)_numUpdBatch.Value;
            c.UpdateExclude = ParseLines(_txtUpdExclude.Text);

            // период/включённость мониторинга применяем сразу, без перезапуска
            bool monWas = c.MonitorEnabled;
            int monPeriodWas = c.MonitorIntervalSeconds;
            c.MonitorEnabled = _chkMonitor.Checked;
            c.MonitorIntervalSeconds = (int)_numMonInterval.Value;
            if (monWas != c.MonitorEnabled || monPeriodWas != c.MonitorIntervalSeconds)
                RestartMonitor();

            string newLang = (_cmbLang != null && _cmbLang.SelectedIndex == 1) ? "en" : "ru";
            bool langChanged = c.Language != newLang;
            c.Language = newLang;

            c.Autostart = _chkAutostart.Checked;

            _engine.SaveConfig();
            // задача планировщика пересоздаётся всегда: путь к exe мог измениться
            _engine.ApplyAutostart(c.Autostart);
            RescheduleAuto();
            if (_miAuto != null) _miAuto.Checked = c.AutoEnabled;

            // Одно окно вместо двух подряд («сохранено», потом «язык после перезапуска»).
            string saved = Tr.S("Настройки сохранены.", "Settings saved.");
            if (langChanged)
                saved += "\r\n\r\n" + Tr.S("Язык изменится после перезапуска приложения.",
                                           "The language will change after you restart the app.");
            MessageBox.Show(this, saved, Tr.S("Настройки", "Settings"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private List<string> ParseLines(string text)
        {
            List<string> list = new List<string>();
            foreach (string line in text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        private List<int> ParsePorts(string text)
        {
            List<int> list = new List<int>();
            foreach (string part in text.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int v;
                if (int.TryParse(part.Trim(), out v) && v > 0 && v < 65536) list.Add(v);
            }
            return list;
        }
    }
}
