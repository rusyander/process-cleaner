// Windows Process Cleaner — вкладка «Dev Cleanup»: группы процессов и занятые порты
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
        private Control BuildDevTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Top;
            flow.AutoSize = true;
            flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            flow.Padding = new Padding(0, 6, 0, 0);

            AddDevButton(flow, Tr.S("Все Node", "All Node"), new string[] { "node.exe", "next.exe" });
            AddDevButton(flow, Tr.S("Все Python", "All Python"), new string[] { "python.exe", "pythonw.exe" });
            AddDevButton(flow, Tr.S("Все Java", "All Java"), new string[] { "java.exe", "gradle.exe" });
            AddDevButton(flow, Tr.S("Все Vite", "All Vite"), new string[] { "vite.exe" });
            AddDevButton(flow, Tr.S("Все Webpack", "All Webpack"), new string[] { "webpack.exe" });
            AddDevButton(flow, Tr.S("Весь npm", "All npm"), new string[] { "npm.exe" });
            AddDevButton(flow, Tr.S("Весь pnpm", "All pnpm"), new string[] { "pnpm.exe" });
            AddDevButton(flow, Tr.S("Весь yarn/bun", "All yarn/bun"), new string[] { "yarn.exe", "bun.exe" });
            AddDevButton(flow, "Docker Compose", new string[] { "docker-compose.exe", "docker.exe" });
            AddDevButton(flow, "Go / Cargo / Deno", new string[] { "go.exe", "cargo.exe", "deno.exe" });

            Label lblP = MkNote(Tr.S("Занятые dev-порты", "Busy dev ports"), false);
            lblP.Height = 30; lblP.Padding = new Padding(0, 10, 0, 0);
            Label lblPHint = MkNote(Tr.S("список портов задаётся в Настройках; отметьте строки и завершите процессы, которые их держат",
                                         "the port list is set in Settings; tick rows and terminate the processes holding them"), true);
            FlowLayoutPanel portsBar = MkToolbar();
            Button btnRefresh = MkFlowButton(Tr.S("Обновить", "Refresh"), 110, false);
            btnRefresh.Click += delegate { RefreshPorts(); };
            Button btnKillPort = MkFlowButton(Tr.S("Завершить выбранные порты", "Kill selected ports"), 240, false);
            btnKillPort.Click += delegate { KillSelectedPorts(); };
            portsBar.Controls.Add(btnRefresh);
            portsBar.Controls.Add(btnKillPort);

            _lvPorts = new FastListView();
            _lvPorts.Dock = DockStyle.Fill;
            _lvPorts.View = View.Details;
            _lvPorts.CheckBoxes = true;
            _lvPorts.FullRowSelect = true;
            _lvPorts.Columns.Add(Tr.S("Порт", "Port"), 90);
            _lvPorts.Columns.Add("PID", 90);
            _lvPorts.Columns.Add(Tr.S("Процесс", "Process"), 340);
            SetupOwnerDraw(_lvPorts);

            tab.Controls.Add(_lvPorts);
            tab.Controls.Add(portsBar);
            tab.Controls.Add(lblPHint);
            tab.Controls.Add(lblP);
            tab.Controls.Add(flow);
            return tab;
        }

        private void AddDevButton(FlowLayoutPanel flow, string title, string[] names)
        {
            Button b = new RoundButton();
            b.Text = title;
            b.Width = 150; b.Height = 36;
            b.Margin = new Padding(0, 0, 8, 8);
            b.Click += delegate
            {
                long freed;
                int n = _engine.TerminateByNames(names, out freed);
                string msg = Tr.S("Завершено: ", "Terminated: ") + n + Tr.S(" · освобождено ~", " · freed ~") + Engine.FormatBytes(freed);
                _tray.ShowBalloonTip(2000, title, msg, ToolTipIcon.Info);
                MsgInfo(msg, title);
            };
            flow.Controls.Add(b);
        }

        private void RefreshPorts()
        {
            Thread t = new Thread(delegate()
            {
                List<PortRow> rows;
                try { rows = _engine.DevPortRows(); }
                catch { return; }
                UiPost(delegate
                {
                    _lvPorts.BeginUpdate();
                    try
                    {
                        _lvPorts.Items.Clear();
                        List<ListViewItem> items = new List<ListViewItem>();
                        foreach (PortRow pr in rows)
                        {
                            ListViewItem it = new ListViewItem(pr.Port.ToString());
                            it.SubItems.Add(pr.Pid.ToString());
                            it.SubItems.Add(pr.ProcName);
                            it.Tag = pr;
                            items.Add(it);
                        }
                        _lvPorts.Items.AddRange(items.ToArray());
                    }
                    finally { _lvPorts.EndUpdate(); }
                    AutoFillLastColumnDeferred(_lvPorts);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void KillSelectedPorts()
        {
            List<int> pids = new List<int>();
            foreach (ListViewItem it in _lvPorts.Items)
                if (it.Checked && it.Tag is PortRow) pids.Add(((PortRow)it.Tag).Pid);
            if (pids.Count == 0) { MsgInfo(Tr.S("Не выбрано ни одного порта.", "No ports selected."), "Dev Cleanup"); return; }

            Thread t = new Thread(delegate()
            {
                long freed = 0;
                int killed = 0;
                try { killed = _engine.TerminateMany(pids, out freed); }
                catch { }
                int killedCopy = killed;
                UiPost(delegate
                {
                    MsgInfo(Tr.S("Завершено процессов: ", "Terminated: ") + killedCopy +
                            Tr.S("  ·  освобождено ~", "  ·  freed ~") + Engine.FormatBytes(freed),
                            Tr.S("Порты", "Ports"));
                    RefreshPorts();
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
