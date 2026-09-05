// Windows Process Cleaner — установленные программы и деинсталляция
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
        // ================= ДЕИНСТАЛЛЯЦИЯ ПРОГРАММ =================
        private readonly Dictionary<string, string[]> _installExes =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        public List<InstalledApp> GetInstalledApps()
        {
            _installExes.Clear();
            Dictionary<string, InstalledApp> map = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
            ReadUninstall(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", map);
            ReadUninstall(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", map);
            ReadUninstall(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", map);
            List<InstalledApp> list = new List<InstalledApp>(map.Values);
            list.Sort(delegate(InstalledApp a, InstalledApp b)
            { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        private void ReadUninstall(RegistryKey root, string sub, Dictionary<string, InstalledApp> map)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(sub))
                {
                    if (k == null) return;
                    foreach (string name in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey s = k.OpenSubKey(name))
                            {
                                if (s == null) continue;
                                object sysComp = s.GetValue("SystemComponent");
                                if (sysComp is int && (int)sysComp == 1) continue;
                                if (s.GetValue("ParentKeyName") != null) continue; // патчи/апдейты
                                if (s.GetValue("ReleaseType") as string == "Security Update") continue;
                                string disp = s.GetValue("DisplayName") as string;
                                string unins = s.GetValue("UninstallString") as string;
                                if (string.IsNullOrEmpty(disp) || string.IsNullOrEmpty(unins)) continue;
                                InstalledApp app = new InstalledApp();
                                app.Name = disp;
                                app.Version = s.GetValue("DisplayVersion") as string;
                                app.Publisher = s.GetValue("Publisher") as string;
                                app.UninstallCmd = unins;
                                app.QuietCmd = s.GetValue("QuietUninstallString") as string;
                                app.ExePath = ResolveAppExe(s.GetValue("DisplayIcon") as string,
                                                            s.GetValue("InstallLocation") as string, disp);
                                object es = s.GetValue("EstimatedSize");
                                if (es is int) app.EstimatedSizeBytes = ((long)(int)es) * 1024L;
                                map[disp] = app;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // Запуск штатного деинсталлятора. null = запущен, иначе причина (уже локализована) —
        // и тогда ничего не запускалось. Win32Exception запуска — наверх, вызывающему.
        public string RunUninstall(InstalledApp app)
        {
            Process p;
            string err = RunUninstall(app, out p);
            if (p != null) p.Dispose();   // сам объект не нужен — только закрыть его handle
            return err;
        }

        // started = запущенный процесс (null, если ShellExecute не вернул handle) — вызывающий ждёт и закрывает.
        public string RunUninstall(InstalledApp app, out Process started)
        {
            started = null;
            string exe, args;
            if (!SplitUninstallCommand(app == null ? null : app.UninstallCmd, out exe, out args))
                return Tr.S("в реестре нет команды деинсталляции", "the registry has no uninstall command");
            bool rooted = false;
            try { rooted = Path.IsPathRooted(exe); } catch { }
            if (rooted && !File.Exists(exe))
                return Tr.S("деинсталлятор не найден: ", "uninstaller not found: ") + exe;
            // Раньше строка шла через cmd.exe /c: он снимает кавычки с пути, где есть «&», не умеет
            // путь с пробелами без кавычек (Android Studio, Steam, InstallShield) и ломается на
            // четырёх кавычках («RunDll32.EXE» «NVI2.DLL»,… у NVIDIA, «Docker Desktop Installer.exe»
            // «uninstall») — такие деинсталляторы молча не запускались. ShellExecute: exe + аргументы как есть,
            // имя без пути (winget, rundll32) ищется по PATH, рабочая папка = папка деинсталлятора.
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = true;
            if (rooted) { try { psi.WorkingDirectory = Path.GetDirectoryName(exe); } catch { } }
            started = Process.Start(psi);
            return null;
        }

        // Делит UninstallString на exe и аргументы: первый токен в кавычках; иначе кратчайший префикс
        // до «.exe» на границе слова — без пробелов (MsiExec.exe, C:\x\u.exe) или полный путь с пробелами
        // (C:\Program Files\X\uninstall.exe -u); иначе первое слово (winget …, rundll32 …).
        // MsiExec /I{GUID} — это диалог восстановления, удаление = /X{GUID}; /I <файл.msi> не трогаем.
        internal static bool SplitUninstallCommand(string cmd, out string exe, out string args)
        {
            exe = null; args = "";
            if (string.IsNullOrEmpty(cmd)) return false;
            cmd = Environment.ExpandEnvironmentVariables(cmd).Trim();
            if (cmd.Length == 0) return false;
            char[] ws = new char[] { ' ', '\t' };
            if (cmd[0] == '"')
            {
                int end = cmd.IndexOf('"', 1);
                if (end < 0) exe = cmd.Substring(1);
                else { exe = cmd.Substring(1, end - 1); args = cmd.Substring(end + 1); }
            }
            else
            {
                int cut = -1;
                int at = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                while (at > 0)
                {
                    int e = at + 4;
                    if (e == cmd.Length || cmd[e] == ' ' || cmd[e] == '\t')
                    {
                        string cand = cmd.Substring(0, e);
                        bool rooted = false;
                        try { rooted = Path.IsPathRooted(cand); } catch { }
                        if (cand.IndexOfAny(ws) < 0 || rooted) { cut = e; break; }
                    }
                    at = cmd.IndexOf(".exe", e, StringComparison.OrdinalIgnoreCase);
                }
                if (cut < 0)
                {
                    int sp = cmd.IndexOfAny(ws);
                    cut = sp > 0 ? sp : cmd.Length;
                }
                exe = cmd.Substring(0, cut); args = cmd.Substring(cut);
            }
            exe = exe.Trim(); args = args.Trim();
            if (exe.Length == 0) return false;
            string fn = exe;
            try { fn = Path.GetFileName(exe); } catch { }
            if (fn.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || fn.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
                args = Regex.Replace(args, @"^[/-][iI](?=\s*\{)", "/X");
            return true;
        }

        // ================= АВТОЗАПУСК =================
        private string ResolveAppExe(string displayIcon, string installLoc, string name)
        {
            // 1) из DisplayIcon ("C:\...\app.exe,0")
            if (!string.IsNullOrEmpty(displayIcon))
            {
                string ip = displayIcon.Trim().Trim('"');
                int comma = ip.LastIndexOf(',');
                if (comma > 0)
                {
                    int idx;
                    if (int.TryParse(ip.Substring(comma + 1).Trim(), out idx)) ip = ip.Substring(0, comma);
                }
                ip = ip.Trim().Trim('"');
                try { if (ip.ToLowerInvariant().EndsWith(".exe") && File.Exists(ip)) return ip; }
                catch { }
            }
            // 2) поиск exe в InstallLocation по имени программы
            try
            {
                if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
                {
                    // Разные записи реестра часто указывают на одну папку (пакеты, языковые
                    // модули): без кэша один и тот же каталог перечисляется десятки раз.
                    string cacheKey = installLoc.TrimEnd('\\').ToLowerInvariant();
                    string[] exes;
                    if (!_installExes.TryGetValue(cacheKey, out exes))
                    {
                        exes = Directory.GetFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly);
                        _installExes[cacheKey] = exes;
                    }
                    if (exes.Length == 1) return exes[0];
                    if (exes.Length > 1 && !string.IsNullOrEmpty(name))
                    {
                        // Совпадение по «содержит» в обе стороны, но не короче 3 знаков (иначе
                        // «ui.exe» подходил к любому имени), деинсталляторы и апдейтеры мимо,
                        // из нескольких подходящих — самое длинное совпадение.
                        string key = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                        string best = null; int bestLen = 0;
                        foreach (string e in exes)
                        {
                            string fn = new string(Path.GetFileNameWithoutExtension(e).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                            if (fn.Length < 3 || key.Length < 3) continue;
                            if (fn.StartsWith("unins") || fn.StartsWith("setup") || fn.Contains("update") || fn.Contains("crash")
                                || fn.Contains("report") || fn.Contains("helper")) continue;
                            if (fn == key) return e;
                            if (key.Contains(fn) || fn.Contains(key))
                            {
                                int l = Math.Min(fn.Length, key.Length);
                                if (l > bestLen) { best = e; bestLen = l; }
                            }
                        }
                        if (best != null) return best;
                    }
                }
            }
            catch { }
            return null;
        }

        private string ParseExeFromCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            cmd = cmd.Trim();
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0) return cmd.Substring(1, end - 1);
            }
            // Путь без кавычек, но с пробелами («C:\Program Files\X\x.exe /tray»): как и
            // CreateProcess, берём первый префикс, оканчивающийся на .exe, если он существует.
            int exeAt = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeAt > 0)
            {
                string cand = cmd.Substring(0, exeAt + 4);
                if (cand.IndexOf(' ') < 0) return cand;
                try { if (File.Exists(Environment.ExpandEnvironmentVariables(cand))) return cand; } catch { }
            }
            int sp = cmd.IndexOf(' ');
            string p = sp > 0 ? cmd.Substring(0, sp) : cmd;
            return p.Trim();
        }

        // COM-объект WScript.Shell создавался заново на КАЖДЫЙ ярлык (это запуск
        // out-of-proc сервера, десятки миллисекунд); держим один на весь процесс.
        private object _wshShell;
        private Type _wshType;
        private bool _wshFailed;

        private string ResolveLnk(string lnkPath)
        {
            if (_wshFailed) return null;
            try
            {
                if (_wshShell == null)
                {
                    _wshType = Type.GetTypeFromProgID("WScript.Shell");
                    if (_wshType == null) { _wshFailed = true; return null; }
                    _wshShell = Activator.CreateInstance(_wshType);
                }
                object sc = _wshType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, _wshShell,
                    new object[] { lnkPath });
                return (string)sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty,
                    null, sc, null);
            }
            catch { return null; }
        }
    }
}
