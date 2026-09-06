// Windows Process Cleaner — Docker: запуск CLI с таймаутом, поиск vhdx, сжатие диска
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
    public partial class Engine
    {
        // ================= DOCKER =================
        // stdout и stderr вычитываются параллельно: последовательный ReadToEnd вставал
        // намертво, когда docker много писал в stderr (труба заполнялась, процесс ждал нас,
        // мы — его). exit: -1 = CLI не запустился, -2 = не уложился в 2 минуты и убит.
        public string RunCapture(string exe, string args, out int exit)
        {
            exit = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return Tr.S("[ошибка] не удалось запустить ", "[error] failed to start ") + exe;
                    StringBuilder o = new StringBuilder(), e = new StringBuilder();
                    Thread drainOut = new Thread(delegate() { try { o.Append(p.StandardOutput.ReadToEnd()); } catch { } });
                    Thread drainErr = new Thread(delegate() { try { e.Append(p.StandardError.ReadToEnd()); } catch { } });
                    drainOut.IsBackground = true; drainErr.IsBackground = true;
                    drainOut.Start(); drainErr.Start();
                    bool exited = p.WaitForExit(120000);
                    if (exited) exit = p.ExitCode;
                    else { exit = RunTimeout; try { p.Kill(); } catch { } }
                    drainOut.Join(3000); drainErr.Join(3000);
                    string res = o.ToString();
                    if (e.Length > 0) res += (res.Length > 0 ? "\r\n" : "") + e.ToString();
                    if (!exited)
                        res += (res.Length > 0 ? "\r\n" : "")
                             + Tr.S("[ошибка] команда не завершилась за 2 минуты и остановлена",
                                    "[error] the command did not finish within 2 minutes and was stopped");
                    return res.Trim();
                }
            }
            catch (Exception ex)
            {
                return Tr.S("[ошибка] ", "[error] ") + ex.Message
                     + Tr.S("\r\nВозможно, CLI не установлен или отсутствует в PATH.",
                            "\r\nThe CLI may not be installed or is not in PATH.");
            }
        }

        public string Docker(string args)
        {
            int ec;
            string outp = RunCapture("docker", args, out ec);
            // docker печатает LF; TextBox требует CRLF, иначе строки слипаются
            outp = outp.Replace("\r\n", "\n").Replace("\n", "\r\n");
            return "> docker " + args + "\r\n" + outp + "\r\n";
        }

        // Находит самый большой виртуальный диск Docker (WSL2).
        public string FindDockerVhdx()
        {
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] cands = {
                Path.Combine(lad, "Docker\\wsl\\disk\\docker_data.vhdx"),
                Path.Combine(lad, "Docker\\wsl\\data\\ext4.vhdx"),
                Path.Combine(lad, "Docker\\wsl\\main\\ext4.vhdx")
            };
            string best = null; long bestSize = -1;
            foreach (string c in cands)
            {
                try { if (File.Exists(c)) { long s = new FileInfo(c).Length; if (s > bestSize) { bestSize = s; best = c; } } }
                catch { }
            }
            return best;
        }

        private static string Lf(string s) { return s == null ? "" : s.Replace("\r\n", "\n").Replace("\n", "\r\n"); }

        // Что удалить перед сжатием: 0 = ничего, 1 = безопасное (остановленные контейнеры, образы
        // без тега, кэш сборки), 2 = + все неиспользуемые образы, 3 = всё, включая неиспользуемые
        // тома. Раньше кнопка «сжать диск» всегда делала system prune -a --volumes: сжатие этого
        // не требует, а неиспользуемые тома — это базы остановленных проектов, их так не теряют.
        public static string[] DockerPruneCommands(int scope)
        {
            switch (scope)
            {
                case 1: return new string[] { "container prune -f", "image prune -f", "builder prune -a -f" };
                case 2: return new string[] { "container prune -f", "image prune -a -f", "builder prune -a -f" };
                case 3: return new string[] { "system prune -a -f --volumes", "builder prune -a -f" };
                default: return new string[0];
            }
        }

        // ОДНА КНОПКА: удалить выбранное (scope) -> остановить Docker ->
        // сжать vhdx (реально вернуть место Windows) -> перезапустить Docker.
        public string CompactDockerDisk(int scope)
        {
            StringBuilder sb = new StringBuilder();
            int ec;

            // exit -1 = docker.exe не запустился (CLI нет); любой другой ненулевой код —
            // CLI есть, но демон не отвечает: prune невозможен, а сжать диск всё равно можно.
            string ver = RunCapture("docker", "version --format {{.Server.Version}}", out ec);
            if (ec == -1)
                return Tr.S("Docker CLI не найден (docker.exe нет в PATH).", "Docker CLI not found (docker.exe is not in PATH).")
                     + "\r\n" + ver;
            if (ec == 0)
            {
                sb.AppendLine(Tr.S("=== Занято до очистки ===", "=== Usage before cleanup ==="));
                sb.AppendLine(Lf(RunCapture("docker", "system df", out ec)));
                sb.AppendLine();

                // 1) очистка перед сжатием — ровно то, что выбрал пользователь
                string[] cmds = DockerPruneCommands(scope);
                if (cmds.Length > 0)
                {
                    sb.AppendLine(Tr.S("=== Очистка перед сжатием ===", "=== Pruning before compaction ==="));
                    foreach (string cmd in cmds)
                    {
                        sb.AppendLine("> docker " + cmd);
                        sb.AppendLine(Lf(RunCapture("docker", cmd, out ec)));
                    }
                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine(Tr.S("Демон Docker не отвечает — очистка пропущена, будет только сжатие диска.",
                                   "The Docker daemon is not responding — pruning skipped, only the disk will be compacted."));
                sb.AppendLine(Lf(ver));
                sb.AppendLine();
            }

            // 2) сжатие виртуального диска
            string vhdx = FindDockerVhdx();
            long before = 0, after = 0;
            if (vhdx == null)
            {
                sb.AppendLine(Tr.S("Виртуальный диск Docker не найден — сжатие пропущено.",
                                   "Docker virtual disk not found — compaction skipped."));
            }
            else
            {
                try { before = new FileInfo(vhdx).Length; } catch { }
                sb.AppendLine(Tr.S("=== Сжатие диска ===", "=== Compacting disk ==="));
                sb.AppendLine(Tr.S("Диск: ", "Disk: ") + vhdx);
                sb.AppendLine(Tr.S("Размер до сжатия: ", "Size before compaction: ") + FormatBytes(before));
                // остановить процессы Docker Desktop, чтобы освободить файл vhdx
                sb.AppendLine(Tr.S("Остановка Docker Desktop…", "Stopping Docker Desktop…"));
                RunCapture("taskkill", "/F /IM \"Docker Desktop.exe\"", out ec);
                RunCapture("taskkill", "/F /IM com.docker.backend.exe", out ec);
                RunCapture("taskkill", "/F /IM com.docker.build.exe", out ec);
                RunCapture("taskkill", "/F /IM com.docker.dev-envs.exe", out ec);
                sb.AppendLine("> wsl --shutdown");
                RunCapture("wsl", "--shutdown", out ec);
                System.Threading.Thread.Sleep(5000);

                string script = "select vdisk file=\"" + vhdx + "\"\r\n" +
                                "attach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\nexit\r\n";
                string scriptPath = Path.Combine(Path.GetTempPath(), "wpc_compact.txt");
                try { File.WriteAllText(scriptPath, script); } catch { }
                sb.AppendLine("> diskpart compact vdisk …");
                RunCapture("diskpart", "/s \"" + scriptPath + "\"", out ec);
                try { File.Delete(scriptPath); } catch { }

                after = before;
                try { after = new FileInfo(vhdx).Length; } catch { }
                sb.AppendLine(Tr.S("Размер после сжатия: ", "Size after compaction: ") + FormatBytes(after));
                long freed = before - after;
                sb.AppendLine(Tr.S("✓ Освобождено на диске Windows: ", "✓ Reclaimed on Windows disk: ") +
                              FormatBytes(freed > 0 ? freed : 0));
                if (freed <= 0)
                    sb.AppendLine(Tr.S("(если 0 — полностью закройте Docker Desktop и повторите: файл был занят)",
                                       "(if 0 — fully quit Docker Desktop and retry: the file was locked)"));
            }

            // 3) перезапуск Docker Desktop
            sb.AppendLine();
            bool started = StartDockerDesktop();
            sb.AppendLine(started
                ? Tr.S("Docker Desktop запускается…", "Docker Desktop is starting…")
                : Tr.S("Не удалось найти Docker Desktop.exe — запустите Docker вручную.",
                       "Docker Desktop.exe not found — start Docker manually."));
            return sb.ToString();
        }

        private bool StartDockerDesktop()
        {
            string[] cands = {
                Path.Combine(_programFiles ?? "", "Docker\\Docker\\Docker Desktop.exe"),
                Path.Combine(_programFilesX86 ?? "", "Docker\\Docker\\Docker Desktop.exe")
            };
            foreach (string c in cands)
            {
                try
                {
                    if (File.Exists(c))
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(c);
                        psi.UseShellExecute = true;
                        using (Process.Start(psi)) { }
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }
    }
}
