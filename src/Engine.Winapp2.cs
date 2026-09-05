// Windows Process Cleaner — правила winapp2.ini: загрузка, переменные, Detect, категории
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
        public string Winapp2TargetPath { get { return Path.Combine(_dir, "winapp2.ini"); } }
        public int Winapp2RuleCount { get; private set; }

        private const string Winapp2Url =
            "https://raw.githubusercontent.com/MoscaDotTo/Winapp2/master/Winapp2.ini";

        public void DownloadWinapp2()
        {
            // .NET 4.0 по умолчанию не умеет TLS 1.2, а GitHub принимает только его
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            using (WebClient wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "WindowsProcessCleaner");
                byte[] data = wc.DownloadData(Winapp2Url);
                // Это должен быть ini с правилами, а не страница ошибки или заглушка: иначе
                // битый файл заменил бы рабочий, и все правила исчезли бы до следующей загрузки.
                string head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 65536));
                bool looksIni = head.IndexOf("[", StringComparison.Ordinal) >= 0
                             && (head.IndexOf("FileKey", StringComparison.OrdinalIgnoreCase) >= 0
                                 || head.IndexOf("Detect", StringComparison.OrdinalIgnoreCase) >= 0);
                if (data.Length < 10000 || !looksIni)
                    throw new InvalidDataException(Tr.S("получен не winapp2.ini (", "the response is not a winapp2.ini (")
                                                   + data.Length + Tr.S(" байт)", " bytes)"));
                string tmp = Winapp2TargetPath + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(Winapp2TargetPath)) File.Replace(tmp, Winapp2TargetPath, null);
                else File.Move(tmp, Winapp2TargetPath);
            }
        }

        private string ExpandIniVars(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOf('%') < 0) return s;
            string[][] map = new string[][] {
                new string[]{"%AppData%",            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)},
                new string[]{"%LocalAppData%",       Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)},
                new string[]{"%LocalLowAppData%",    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData\\LocalLow")},
                new string[]{"%CommonAppData%",      Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)},
                new string[]{"%ProgramData%",        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)},
                new string[]{"%ProgramFiles%",       _programFiles},
                new string[]{"%CommonProgramFiles%", Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles)},
                new string[]{"%UserProfile%",        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)},
                new string[]{"%Documents%",          Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)},
                new string[]{"%Desktop%",            Environment.GetFolderPath(Environment.SpecialFolder.Desktop)},
                new string[]{"%Pictures%",           Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)},
                new string[]{"%Music%",              Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)},
                new string[]{"%Video%",              Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)},
                new string[]{"%WinDir%",             _winDir},
                new string[]{"%SystemDrive%",        (Path.GetPathRoot(_winDir) ?? "C:\\").TrimEnd('\\')},
                new string[]{"%HomeDrive%",          (Path.GetPathRoot(_winDir) ?? "C:\\").TrimEnd('\\')},
                new string[]{"%SystemRoot%",         _winDir},
                new string[]{"%Temp%",               Path.GetTempPath().TrimEnd('\\')},
                new string[]{"%Public%",             Path.Combine(Path.GetPathRoot(_winDir) ?? "C:\\", "Users\\Public")},
            };
            foreach (string[] kv in map)
            {
                if (string.IsNullOrEmpty(kv[1])) continue;
                s = ReplaceCI(s, kv[0], kv[1].TrimEnd('\\'));
            }
            return s;
        }

        private static string ReplaceCI(string input, string find, string repl)
        {
            int at = input.IndexOf(find, StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                input = input.Substring(0, at) + repl + input.Substring(at + find.Length);
                at = input.IndexOf(find, at + repl.Length, StringComparison.OrdinalIgnoreCase);
            }
            return input;
        }

        // Проверка Detect/DetectFile: правило применимо, если сработал ХОТЯ БЫ один детектор.
        private bool Winapp2Detects(List<string> detectFiles, List<string> detectRegs)
        {
            if (detectFiles.Count == 0 && detectRegs.Count == 0) return true;
            foreach (string f in detectFiles)
            {
                string p = ExpandIniVars(f).Trim();
                if (p.Length == 0) continue;
                try
                {
                    if (p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0)
                    {
                        string dir = Path.GetDirectoryName(p);
                        string pat = Path.GetFileName(p);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            if (Directory.GetFileSystemEntries(dir, pat).Length > 0) return true;
                        }
                        continue;
                    }
                    if (Directory.Exists(p) || File.Exists(p)) return true;
                }
                catch { }
            }
            foreach (string r in detectRegs)
            {
                if (Winapp2RegExists(r)) return true;
            }
            return false;
        }

        private bool Winapp2RegExists(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            int slash = key.IndexOf('\\');
            if (slash <= 0) return false;
            string hive = key.Substring(0, slash).ToUpperInvariant();
            string sub = key.Substring(slash + 1);
            RegistryKey root;
            switch (hive)
            {
                case "HKCU": case "HKEY_CURRENT_USER": root = Registry.CurrentUser; break;
                case "HKLM": case "HKEY_LOCAL_MACHINE": root = Registry.LocalMachine; break;
                case "HKCR": case "HKEY_CLASSES_ROOT": root = Registry.ClassesRoot; break;
                case "HKU": case "HKEY_USERS": root = Registry.Users; break;
                default: return false;
            }
            try { using (RegistryKey k = root.OpenSubKey(sub)) return k != null; }
            catch { return false; }
        }

        private List<CleanCategory> LoadWinapp2Categories()
        {
            Winapp2RuleCount = 0;
            List<CleanCategory> result = new List<CleanCategory>();
            string ini = Winapp2Path;
            if (ini == null) return result;

            // группируем правила по Section=, иначе в списке будут сотни строк
            Dictionary<string, CleanCategory> groups = new Dictionary<string, CleanCategory>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> groupApps = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            string section = null, groupName = null;
            List<string> fileKeys = new List<string>();
            List<string> detectFiles = new List<string>();
            List<string> detectRegs = new List<string>();
            bool skip = false;

            // локальная функция-заменитель: закрываем накопленную секцию
            Action flush = delegate
            {
                if (section == null || skip || fileKeys.Count == 0) return;
                if (!Winapp2Detects(detectFiles, detectRegs)) return;

                string g = string.IsNullOrEmpty(groupName) ? Tr.S("Прочее", "Other") : groupName;
                CleanCategory cat;
                if (!groups.TryGetValue(g, out cat))
                {
                    cat = new CleanCategory();
                    cat.Id = "winapp2:" + g;
                    cat.Title = "winapp2 · " + g;
                    cat.Recommended = false;
                    groups[g] = cat;
                    groupApps[g] = new List<string>();
                }

                int added = 0;
                foreach (string fk in fileKeys)
                {
                    string[] parts = fk.Split('|');
                    if (parts.Length < 1) continue;
                    string dir = ExpandIniVars(parts[0]).Trim();
                    if (dir.Length == 0 || dir.IndexOf('%') >= 0) continue;   // нерасширенная переменная
                    bool recurse = false, removeSelf = false;
                    for (int i = 2; i < parts.Length; i++)
                    {
                        string flag = parts[i].Trim().ToUpperInvariant();
                        if (flag == "RECURSE") recurse = true;
                        else if (flag == "REMOVESELF") { recurse = true; removeSelf = true; }
                    }
                    string masks = parts.Length > 1 ? parts[1].Trim() : "*.*";
                    foreach (string m in masks.Split(';'))
                    {
                        string mask = m.Trim();
                        if (mask.Length == 0) continue;
                        bool all = mask == "*.*" || mask == "*";
                        int before = cat.Targets.Count;
                        // Порог «не удалять свежее N минут» распространяется и на winapp2:
                        // пользователь задаёт его в настройках и ждёт, что он действует везде.
                        AddDir(cat, dir, !removeSelf, all ? null : mask, Config.CleanSkipRecentMinutes, recurse);
                        if (cat.Targets.Count > before) added++;
                    }
                }
                if (added > 0)
                {
                    groupApps[g].Add(section);
                    Winapp2RuleCount++;
                }
            };

            foreach (string raw in File.ReadLines(ini))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

                if (line[0] == '[')
                {
                    flush();
                    section = line.Trim('[', ']').Trim();
                    groupName = null;
                    fileKeys.Clear(); detectFiles.Clear(); detectRegs.Clear();
                    skip = false;
                    continue;
                }
                if (section == null || skip) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (val.Length == 0) continue;

                if (key.StartsWith("FileKey", StringComparison.OrdinalIgnoreCase)) fileKeys.Add(val);
                else if (key.StartsWith("DetectFile", StringComparison.OrdinalIgnoreCase)) detectFiles.Add(val);
                else if (key.StartsWith("Detect", StringComparison.OrdinalIgnoreCase)
                         && !key.StartsWith("DetectOS", StringComparison.OrdinalIgnoreCase)) detectRegs.Add(val);
                else if (key.Equals("Section", StringComparison.OrdinalIgnoreCase)) groupName = val;
                else if (key.StartsWith("ExcludeKey", StringComparison.OrdinalIgnoreCase)) skip = true;
                else if (key.Equals("Warning", StringComparison.OrdinalIgnoreCase)) skip = true;
            }
            flush();

            foreach (KeyValuePair<string, CleanCategory> kv in groups)
            {
                // Группа заводится до того, как станет известно, раскрылся ли хоть один
                // FileKey: секция может пройти детект, а все её пути — оказаться
                // нераскрытой переменной. Такая группа попадала в список пустой строкой
                // «правил: 0» — чистить в ней нечего, показывать её незачем.
                if (kv.Value.Targets.Count == 0) continue;
                List<string> apps = groupApps[kv.Key];
                apps.Sort(StringComparer.OrdinalIgnoreCase);
                string head = string.Join(", ", apps.Take(6).ToArray());
                kv.Value.Desc = Tr.S("правил: ", "rules: ") + apps.Count + " · " + head
                              + (apps.Count > 6 ? " …" : "");
                result.Add(kv.Value);
            }
            result.Sort(delegate(CleanCategory a, CleanCategory b)
            { return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase); });
            return result;
        }
    }
}
