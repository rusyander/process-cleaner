// Windows Process Cleaner
// Единый файл. Компилируется встроенным в Windows csc.exe (.NET Framework 4.x).
// Никакой сторонней установки не требуется. См. build.bat / run.bat.
//
// Возможности:
//  - Поиск забытых процессов разработки (node/python/java/vite/webpack/...).
//  - Критерии "заброшенности": мёртвый родитель, простой CPU, нет окон, нет
//    слушающих TCP-портов, нет дочерних процессов, белый список, мин. время жизни.
//  - Корректное завершение (WM_CLOSE) -> ожидание 3с -> принудительно (Kill).
//  - Очистка Standby Memory через ntdll!NtSetSystemInformation (нужны права админа).
//  - Dev Cleanup: массовое завершение по группам + занятые dev-порты.
//  - Таймер автоочистки: каждые N часов (1..24), сохраняется в конфиге.
//  - Системный трей с индикацией активности и меню.
//  - Автозапуск вместе с Windows (HKCU\...\Run).
//  - История очисток и настройки в JSON (%APPDATA%\WindowsProcessCleaner).
//  - Single-instance через локальный TCP-порт 49876 (обычно свободен).

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
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WindowsProcessCleaner
{
    // ------------------------------------------------------------------ //
    //  Локализация: Tr.S("русский", "english") возвращает строку по языку.
    // ------------------------------------------------------------------ //
    internal static class Tr
    {
        public static bool En;
        public static string S(string ru, string en) { return En ? en : ru; }
    }

    // ------------------------------------------------------------------ //
    //  Конфигурация и история (сериализуются в JSON)
    // ------------------------------------------------------------------ //
    [DataContract]
    public class AppConfig
    {
        [DataMember] public double CpuThresholdPercent;   // порог CPU %
        [DataMember] public int IdleMinutes;              // время простоя, мин
        [DataMember] public int MinLifetimeMinutes;       // мин. время жизни процесса, мин
        [DataMember] public int AutoIntervalHours;        // период автоочистки, 1..24
        [DataMember] public bool AutoEnabled;             // включена ли автоочистка
        [DataMember] public bool Autostart;               // автозапуск с Windows
        [DataMember] public bool StartMinimized;          // стартовать свёрнутым в трей
        [DataMember] public string Theme;                 // "system" | "light" | "dark"
        [DataMember] public bool GlobalScan;              // сканировать ВСЕ процессы, не только dev
        [DataMember] public int GlobalIdleMinutes;        // мин. простой для глобального режима (безопасность)
        [DataMember] public bool GlobalExcludeInstalled;  // не трогать установленный софт (Program Files)
        [DataMember] public string Language;              // "ru" | "en"
        [DataMember] public List<string> Watchlist;       // отслеживаемые процессы
        [DataMember] public List<string> Whitelist;       // белый список (не трогать)
        [DataMember] public List<int> DevPorts;           // популярные dev-порты
        [DataMember] public int MonitorIntervalSeconds;   // период тика мониторинга, 5..300
        [DataMember] public bool MonitorEnabled;          // фоновый мониторинг CPU вообще нужен
        [DataMember] public bool EmptyWorkingSets;        // сбрасывать рабочие наборы всех процессов
        [DataMember] public int CleanSkipRecentMinutes;   // не удалять файлы, изменённые за последние N мин
        [DataMember] public bool CleanLogEnabled;         // писать лог очистки
        [DataMember] public List<string> CleanExclude;    // пути, которые никогда не чистить
        [DataMember] public List<string> UpdateExclude;   // Id пакетов, которые не предлагать к обновлению
        [DataMember] public bool UpdateIncludeUnknown;    // показывать пакеты с неопределённой текущей версией
        [DataMember] public bool UpdateUseChoco;          // опрашивать Chocolatey, если он установлен
        [DataMember] public int UpdateBatchSize;           // сколько пакетов отдавать менеджеру одной командой
        // Версия схемы: отличает "поле отсутствует в старом config.json" (bool => false)
        // от "пользователь выключил". Без неё апгрейд молча гасит новые флаги.
        [DataMember] public int ConfigVersion;

        public static AppConfig Default()
        {
            AppConfig c = new AppConfig();
            c.CpuThresholdPercent = 0.1;
            c.IdleMinutes = 5;
            c.MinLifetimeMinutes = 5;
            c.AutoIntervalHours = 4;
            c.AutoEnabled = false;
            c.Autostart = false;
            c.StartMinimized = false;
            c.Theme = "system";
            c.GlobalScan = false;
            c.GlobalIdleMinutes = 30;
            c.GlobalExcludeInstalled = true;
            c.Language = "ru";
            c.Watchlist = new List<string>(new string[] {
                "node.exe","npm.exe","pnpm.exe","yarn.exe","bun.exe",
                "python.exe","pythonw.exe","java.exe","gradle.exe",
                "vite.exe","webpack.exe","next.exe","cargo.exe",
                "go.exe","deno.exe","ruby.exe","php.exe"
            });
            c.Whitelist = new List<string>(new string[] {
                "explorer.exe","wininit.exe","svchost.exe","dwm.exe","System","Registry",
                "docker.exe","com.docker.backend.exe","vmmem.exe","wsl.exe",
                "postgres.exe","mysqld.exe","redis-server.exe",
                "steam.exe","discord.exe","chrome.exe","firefox.exe","msedge.exe"
            });
            c.DevPorts = new List<int>(new int[] {
                3000,3001,3002,4173,5173,5174,8080,8000,8888,4200,4300,5000,5555,9000,9090,1337,19006
            });
            c.MonitorIntervalSeconds = 15;
            c.MonitorEnabled = true;
            // Сброс рабочих наборов выключен по умолчанию: он выдавливает страницы
            // ВСЕХ процессов, после чего система заметно тормозит, пока они грузятся обратно.
            c.EmptyWorkingSets = false;
            c.CleanSkipRecentMinutes = 10;
            c.CleanLogEnabled = true;
            c.CleanExclude = new List<string>();
            c.UpdateExclude = new List<string>();
            c.UpdateIncludeUnknown = true;
            c.UpdateUseChoco = true;
            c.UpdateBatchSize = 5;
            c.ConfigVersion = CurrentVersion;
            return c;
        }

        public const int CurrentVersion = 3;

        public void Normalize()
        {
            if (Watchlist == null) Watchlist = Default().Watchlist;
            if (Whitelist == null) Whitelist = Default().Whitelist;
            if (DevPorts == null) DevPorts = Default().DevPorts;
            if (CleanExclude == null) CleanExclude = new List<string>();
            if (UpdateExclude == null) UpdateExclude = new List<string>();
            // Миграции строго по одной ступени и БЕЗ присваивания CurrentVersion внутри:
            // иначе конфиг версии 0 перескочит на текущую, пропустив дефолты следующих ступеней.
            if (ConfigVersion < 1)
            {
                // конфиг от старой сборки: включаем новые возможности по умолчанию
                MonitorEnabled = true;
                CleanLogEnabled = true;
                CleanSkipRecentMinutes = 10;
            }
            if (ConfigVersion < 2)
            {
                UpdateIncludeUnknown = true;
                UpdateUseChoco = true;
            }
            if (ConfigVersion < 3)
            {
                UpdateBatchSize = 5;
            }
            ConfigVersion = CurrentVersion;
            // 1 = по одному (точный статус из кода возврата); больше 20 в одной команде
            // не даёт выигрыша и растягивает срок, за который непонятно, что происходит.
            if (UpdateBatchSize < 1) UpdateBatchSize = 1;
            if (UpdateBatchSize > 20) UpdateBatchSize = 20;
            if (MonitorIntervalSeconds < 5) MonitorIntervalSeconds = 15;
            if (MonitorIntervalSeconds > 300) MonitorIntervalSeconds = 300;
            if (CleanSkipRecentMinutes < 0) CleanSkipRecentMinutes = 0;
            if (string.IsNullOrEmpty(Theme)) Theme = "system";
            if (string.IsNullOrEmpty(Language)) Language = "ru";
            if (GlobalIdleMinutes < 1) GlobalIdleMinutes = 30;
            if (AutoIntervalHours < 1) AutoIntervalHours = 1;
            if (AutoIntervalHours > 24) AutoIntervalHours = 24;
            if (IdleMinutes < 0) IdleMinutes = 0;
            if (MinLifetimeMinutes < 0) MinLifetimeMinutes = 0;
            if (CpuThresholdPercent < 0) CpuThresholdPercent = 0;
        }
    }

    [DataContract]
    public class HistoryEntry
    {
        [DataMember] public string DateTime;
        [DataMember] public int TerminatedCount;
        [DataMember] public long FreedBytes;
        [DataMember] public List<string> Processes;
    }

    [DataContract]
    public class HistoryFile
    {
        [DataMember] public List<HistoryEntry> Entries;
    }

    // ------------------------------------------------------------------ //
    //  Модель найденного процесса
    // ------------------------------------------------------------------ //
    public class ProcInfo
    {
        public int Pid;
        public int ParentPid;
        public string Name;        // node.exe
        public string Category;    // Node.js
        public string Path;
        public TimeSpan Uptime;
        public double CpuPercent;
        public long RamBytes;
        public bool HasWindow;
        public bool ListensTcp;
        public bool HasChildren;
        public bool ParentAlive;
        public TimeSpan IdleFor;
        public bool Whitelisted;
        public bool UserOwned;     // принадлежит текущему пользователю
        public bool IsSystemPath;  // лежит в системной папке Windows
        public bool IsCandidate;   // кандидат на завершение
        public string Reason;      // почему кандидат / почему нет
    }

    // ------------------------------------------------------------------ //
    //  WinAPI
    // ------------------------------------------------------------------ //
    internal static class Native
    {
        // --- Toolhelp снимок процессов ---
        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        public const uint TH32CS_SNAPPROCESS = 0x00000002;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        // --- Окна ---
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        // --- TCP таблица (владелец PID) ---
        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedTcpTable(IntPtr pTable, ref int size,
            bool order, int af, int tableClass, int reserved);

        public const int AF_INET = 2;
        public const int TCP_TABLE_OWNER_PID_ALL = 5;
        public const int MIB_TCP_STATE_LISTEN = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;   // сетевой порядок байт, значимы младшие 2 байта
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        // --- Завершение по WM_CLOSE ---
        public const uint WM_CLOSE = 0x0010;
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        // --- Standby Memory (ntdll) ---
        [DllImport("ntdll.dll")]
        public static extern int NtSetSystemInformation(int infoClass, IntPtr info, int length);
        public const int SystemMemoryListInformation = 0x50;
        public const int MemoryPurgeStandbyList = 4;
        public const int MemoryEmptyWorkingSets = 2;

        // --- Привилегии ---
        [StructLayout(LayoutKind.Sequential)]
        public struct LUID { public uint LowPart; public int HighPart; }
        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }

        public const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        public const uint TOKEN_QUERY = 0x0008;

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool LookupPrivilegeValue(string host, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
            ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        // --- Владелец процесса (SID) ---
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        public struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_USER { public SID_AND_ATTRIBUTES User; }

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr token, int infoClass,
            IntPtr buf, int len, out int retLen);
        [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr str);
        public const int TokenUser = 1;

        public static string GetProcessUserSid(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try { return GetProcessUserSid(h); }
            finally { CloseHandle(h); }
        }

        // Тот же SID, но по уже открытому хэндлу — чтобы не открывать процесс повторно.
        public static string GetProcessUserSid(IntPtr h)
        {
            if (h == IntPtr.Zero) return null;
            {
                IntPtr token;
                if (!OpenProcessToken(h, TOKEN_QUERY, out token)) return null;
                try
                {
                    int len = 0;
                    GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out len);
                    if (len <= 0) return null;
                    IntPtr buf = Marshal.AllocHGlobal(len);
                    try
                    {
                        if (!GetTokenInformation(token, TokenUser, buf, len, out len)) return null;
                        TOKEN_USER tu = (TOKEN_USER)Marshal.PtrToStructure(buf, typeof(TOKEN_USER));
                        IntPtr sidStr;
                        if (!ConvertSidToStringSid(tu.User.Sid, out sidStr)) return null;
                        try { return Marshal.PtrToStringAuto(sidStr); }
                        finally { LocalFree(sidStr); }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { CloseHandle(token); }
            }
        }

        // --- Быстрый опрос процесса ---
        // Process.GetProcessById() в .NET Framework на КАЖДЫЙ вызов снимает полный
        // список всех процессов системы (NtQuerySystemInformation с большим буфером).
        // Опрос N процессов через него стоит O(N^2); при 300 процессах тик мониторинга
        // занимает секунды и вешает UI. Ниже — прямые вызовы: один OpenProcess на процесс.
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_TERMINATE = 0x0001;
        public const uint SYNCHRONIZE = 0x00100000;
        public const uint WAIT_OBJECT_0 = 0;
        public const uint WAIT_TIMEOUT = 0x102;
        public const uint STILL_ACTIVE = 259;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit,
            out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public IntPtr PeakWorkingSetSize;
            public IntPtr WorkingSetSize;
            public IntPtr QuotaPeakPagedPoolUsage;
            public IntPtr QuotaPagedPoolUsage;
            public IntPtr QuotaPeakNonPagedPoolUsage;
            public IntPtr QuotaNonPagedPoolUsage;
            public IntPtr PagefileUsage;
            public IntPtr PeakPagefileUsage;
        }
        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool GetProcessMemoryInfo(IntPtr h, out PROCESS_MEMORY_COUNTERS c, uint size);

        // Путь к exe без Process.MainModule: MainModule перечисляет ВСЕ модули процесса
        // и бросает исключение при несовпадении битности — на цикле это очень дорого.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr h, uint flags,
            StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr h, uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr h, out uint code);

        public static string QueryImagePath(IntPtr h)
        {
            if (h == IntPtr.Zero) return null;
            StringBuilder sb = new StringBuilder(1024);
            int len = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref len)) return sb.ToString(0, len);
            return null;
        }

        public static bool QueryTimes(IntPtr h, out TimeSpan cpu, out DateTime startLocal)
        {
            cpu = TimeSpan.Zero; startLocal = DateTime.MinValue;
            long creation, exit, kernel, user;
            if (!GetProcessTimes(h, out creation, out exit, out kernel, out user)) return false;
            cpu = TimeSpan.FromTicks(kernel + user);
            try { startLocal = DateTime.FromFileTime(creation); } catch { startLocal = DateTime.MinValue; }
            return true;
        }

        public static long QueryWorkingSet(IntPtr h)
        {
            PROCESS_MEMORY_COUNTERS c;
            if (GetProcessMemoryInfo(h, out c, (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS))))
                return c.WorkingSetSize.ToInt64();
            return 0;
        }

        // --- Память системы ---
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buf);

        // --- Тёмный заголовок окна (DWM) ---
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        // --- Корзина ---
        [StructLayout(LayoutKind.Sequential, Pack = 0)]
        public struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int SHQueryRecycleBin(string rootPath, ref SHQUERYRBINFO info);
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string rootPath, uint flags);
        public const uint SHERB_NOCONFIRMATION = 0x1;
        public const uint SHERB_NOPROGRESSUI = 0x2;
        public const uint SHERB_NOSOUND = 0x4;

        public static bool EnablePrivilege(string name)
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                return false;
            try
            {
                LUID luid;
                if (!LookupPrivilegeValue(null, name, out luid)) return false;
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.Count = 1;
                tp.Luid = luid;
                tp.Attributes = SE_PRIVILEGE_ENABLED;
                return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(token); }
        }
    }

    // ------------------------------------------------------------------ //
    //  Строка занятого порта (для Dev Cleanup)
    // ------------------------------------------------------------------ //
    public class PortRow
    {
        public int Port;
        public int Pid;
        public string ProcName;
    }

    // Категория очистки диска (набор известных мусорных путей).
    public class CleanTarget
    {
        public string Path;
        public bool ContentsOnly;        // удалять содержимое, саму папку оставить
        public string Mask;              // маска файлов, null = все (для winapp2-правил и thumbcache_*.db)
        public bool Recurse = true;      // спускаться в подпапки
        public int MinAgeMinutes;        // не трогать файлы, изменённые за последние N минут (0 = без фильтра)
    }
    public class CleanCategory
    {
        public string Id;
        public string Title;
        public string Desc;
        public List<CleanTarget> Targets = new List<CleanTarget>();
        public bool RecycleBin;
        public bool Recommended;
        public long Size;
        public int FileCount;
        public bool Analyzed;
        public string Note;              // почему пусто / что не удалось посчитать
    }
    public class CleanResult
    {
        public long Freed;
        public int Errors;
        public int FilesDeleted;
        public List<string> Log = new List<string>();
    }

    // Доступное обновление программы (winget / Chocolatey).
    public class UpdateItem
    {
        public string Name;         // отображаемое имя, как его знает менеджер пакетов
        public string Id;           // идентификатор пакета — им и обновляем
        public string Current;      // установленная версия ("Unknown" / "< 1.2" бывают)
        public string Available;    // версия, доступная в источнике
        public string Manager;      // "winget" | "choco"
        public string Source;       // источник внутри менеджера (winget/msstore/...)
        public bool Duplicate;      // тот же софт уже виден через другой менеджер
        public string Status;        // результат последней попытки обновления
        public bool LastOk;         // удалась ли последняя попытка (не выводить из текста Status)

        // Насколько велик скачок версии: 3 крупное, 2 среднее, 1 мелкое, 0 неизвестно.
        // Это масштаб по номеру версии, а НЕ оценка безопасности: ни winget, ни
        // Chocolatey не отдают CVE/severity, вывести настоящую критичность из их
        // данных нельзя, и притворяться, что можно, было бы обманом.
        public int SeverityLevel;
        public string SeverityText;  // подпись для колонки «Важность»
    }

    // Установленная программа (для деинсталляции / автозапуска).
    public class InstalledApp
    {
        public string Name;
        public string Version;
        public string Publisher;
        public string UninstallCmd;
        public string QuietCmd;
        public string ExePath;   // главный exe (из DisplayIcon), если удалось определить
        public long EstimatedSizeBytes;
        public bool InAutostart; // вычисляется во вкладке автозапуска
    }

    // Запись автозапуска (реестр Run или папка «Автозагрузка»).
    public class AutostartEntry
    {
        public string Name;
        public string Command;
        public string ExePath;
        public string SourceLabel;
        public int Kind;        // 0 HKCU Run, 1 HKLM Run, 2 HKLM WOW Run, 3 Startup(user), 4 Startup(common)
        public string RegName;  // имя значения в реестре
        public string LnkPath;  // путь к ярлыку в папке автозагрузки
    }

    // ------------------------------------------------------------------ //
    //  Тема оформления (светлая / тёмная / по системе)
    // ------------------------------------------------------------------ //
    public class Theme
    {
        public bool Dark;
        public Color Bg;          // фон окна
        public Color Surface;     // фон полей/списков
        public Color Text;        // основной текст
        public Color Subtle;      // приглушённый текст
        public Color Accent;      // акцент (кнопки, выделение)
        public Color AccentText;  // текст на акценте
        public Color Border;      // границы
        public Color CandidateBg; // строка-кандидат
        public Color WhiteBg;     // строка из белого списка
        public Color Header;      // фон заголовков колонок

        public static Theme Light()
        {
            Theme t = new Theme();
            t.Dark = false;
            t.Bg = Color.FromArgb(243, 244, 246);
            t.Surface = Color.FromArgb(255, 255, 255);
            t.Text = Color.FromArgb(28, 30, 34);
            t.Subtle = Color.FromArgb(110, 116, 124);
            t.Accent = Color.FromArgb(37, 99, 235);
            t.AccentText = Color.White;
            t.Border = Color.FromArgb(214, 218, 224);
            t.CandidateBg = Color.FromArgb(255, 243, 214);
            t.WhiteBg = Color.FromArgb(226, 240, 228);
            t.Header = Color.FromArgb(233, 236, 240);
            return t;
        }

        public static Theme DarkTheme()
        {
            Theme t = new Theme();
            t.Dark = true;
            t.Bg = Color.FromArgb(24, 25, 28);
            t.Surface = Color.FromArgb(37, 39, 44);
            t.Text = Color.FromArgb(228, 230, 234);
            t.Subtle = Color.FromArgb(150, 156, 164);
            t.Accent = Color.FromArgb(59, 130, 246);
            t.AccentText = Color.White;
            t.Border = Color.FromArgb(58, 61, 68);
            t.CandidateBg = Color.FromArgb(74, 60, 30);
            t.WhiteBg = Color.FromArgb(38, 54, 40);
            t.Header = Color.FromArgb(45, 47, 53);
            return t;
        }

        // Разрешить "system" через реестр Windows.
        public static Theme Resolve(string mode)
        {
            if (mode == "light") return Light();
            if (mode == "dark") return DarkTheme();
            // system
            return SystemIsLight() ? Light() : DarkTheme();
        }

        public static bool SystemIsLight()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int) return ((int)v) != 0;
                    }
                }
            }
            catch { }
            return true; // по умолчанию светлая
        }
    }

    // ------------------------------------------------------------------ //
    //  Ядро: сканер и операции над процессами
    // ------------------------------------------------------------------ //
    public class Engine
    {
        public AppConfig Config;
        private readonly string _dir;
        private readonly string _configPath;
        private readonly string _historyPath;

        // Мониторинг CPU/простоя между тиками
        private class CpuSample { public TimeSpan Cpu; public DateTime At; }
        private readonly Dictionary<int, CpuSample> _lastCpu = new Dictionary<int, CpuSample>();
        private readonly Dictionary<int, double> _cpuPercent = new Dictionary<int, double>();
        private readonly Dictionary<int, DateTime> _idleSince = new Dictionary<int, DateTime>();

        // MonitorTick и Scan работают в фоновых потоках и оба трогают словари ниже —
        // весь доступ только под _sync. Взаимная сериализация здесь и есть желаемое
        // поведение: два тяжёлых обхода процессов одновременно всё равно не нужны.
        private readonly object _sync = new object();

        // Путь к exe и SID владельца не меняются за жизнь процесса — кэшируем по PID,
        // сверяя время старта (PID переиспользуются). Оба запроса дорогие.
        private readonly Dictionary<int, string> _pathCache = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _sidCache = new Dictionary<int, string>();
        private readonly Dictionary<int, DateTime> _startCache = new Dictionary<int, DateTime>();
        // Процессы, которые не дают PROCESS_VM_READ: не тратим на них OpenProcess дважды за тик.
        private readonly HashSet<int> _noVmRead = new HashSet<int>();

        private struct ProcStat
        {
            public bool Ok;
            public TimeSpan Cpu;
            public DateTime Start;
            public long WorkingSet;
            public string Path;
            public string Sid;
        }

        // Один OpenProcess — все нужные данные. Вызывать только под _sync.
        private ProcStat QueryStat(int pid, bool wantPath, bool wantSid)
        {
            ProcStat st = new ProcStat();
            uint baseAccess = Native.PROCESS_QUERY_LIMITED_INFORMATION;
            bool tryVm = !_noVmRead.Contains(pid);
            IntPtr h = Native.OpenProcess(tryVm ? baseAccess | Native.PROCESS_VM_READ : baseAccess, false, pid);
            if (h == IntPtr.Zero && tryVm)
            {
                _noVmRead.Add(pid);
                h = Native.OpenProcess(baseAccess, false, pid);
            }
            if (h == IntPtr.Zero) return st;
            try
            {
                TimeSpan cpu; DateTime start;
                if (Native.QueryTimes(h, out cpu, out start))
                {
                    st.Cpu = cpu; st.Start = start; st.Ok = true;

                    // PID переиспользован — старые путь/SID относятся к другому процессу
                    DateTime knownStart;
                    if (_startCache.TryGetValue(pid, out knownStart) && knownStart != start)
                    {
                        _pathCache.Remove(pid); _sidCache.Remove(pid); _noVmRead.Remove(pid);
                    }
                    _startCache[pid] = start;
                }
                st.WorkingSet = Native.QueryWorkingSet(h);

                if (wantPath)
                {
                    string cached;
                    if (_pathCache.TryGetValue(pid, out cached)) st.Path = cached;
                    else { st.Path = Native.QueryImagePath(h) ?? ""; _pathCache[pid] = st.Path; }
                }
                if (wantSid)
                {
                    string cached;
                    if (_sidCache.TryGetValue(pid, out cached)) st.Sid = cached;
                    else { st.Sid = Native.GetProcessUserSid(h); _sidCache[pid] = st.Sid; }
                }
            }
            finally { Native.CloseHandle(h); }
            return st;
        }

        // Вызывать только под _sync.
        private void ForgetDead(HashSet<int> alive)
        {
            List<int> dead = new List<int>();
            foreach (int k in _lastCpu.Keys) if (!alive.Contains(k)) dead.Add(k);
            foreach (int k in dead) { _lastCpu.Remove(k); _cpuPercent.Remove(k); _idleSince.Remove(k); }
            dead.Clear();
            foreach (int k in _startCache.Keys) if (!alive.Contains(k)) dead.Add(k);
            foreach (int k in dead)
            {
                _startCache.Remove(k); _pathCache.Remove(k); _sidCache.Remove(k); _noVmRead.Remove(k);
            }
        }

        private string _currentUserSid;
        private string _winDir;
        private string _programFiles;
        private string _programFilesX86;
        private int _selfPid;

        // Никогда не завершать в глобальном режиме (сверх белого списка):
        // критичные системные/оболочечные процессы + типовые фоновые утилиты,
        // которые обычно должны работать постоянно (облака, драйверы, мессенджеры).
        private static readonly HashSet<string> _critical = new HashSet<string>(
            new string[] {
                // --- ядро Windows и оболочка ---
                "system","registry","idle","smss.exe","csrss.exe","wininit.exe","winlogon.exe",
                "services.exe","lsass.exe","lsaiso.exe","fontdrvhost.exe","dwm.exe","explorer.exe",
                "taskhostw.exe","taskhost.exe","sihost.exe","ctfmon.exe","runtimebroker.exe","conhost.exe",
                "dllhost.exe","searchhost.exe","searchapp.exe","startmenuexperiencehost.exe","shellexperiencehost.exe",
                "textinputhost.exe","applicationframehost.exe","searchindexer.exe","lockapp.exe",
                "wudfhost.exe","spoolsv.exe","audiodg.exe","memcompression","sechealthsystray.exe",
                "securityhealthservice.exe","msmpeng.exe","nissrv.exe","widgets.exe","widgetservice.exe",
                "windowsprocesscleaner.exe","dax3api.exe","phoneexperiencehost.exe",
                // --- облака / синхронизация ---
                "onedrive.exe","dropbox.exe","dropboxupdate.exe","googledrivefs.exe","googledrivesync.exe",
                "yandexdisk.exe","yandexdisk2.exe","megasync.exe","nextcloud.exe",
                // --- мессенджеры / медиа (обычно свёрнуты в трей без окна) ---
                "telegram.exe","discord.exe","slack.exe","teams.exe","ms-teams.exe","whatsapp.exe",
                "spotify.exe","zoom.exe","viber.exe","skype.exe",
                // --- драйверы / вендорские службы ---
                "nvcontainer.exe","nvsphelper64.exe","nvidia web helper.exe","nvdisplay.container.exe",
                "rtkauduservice64.exe","ravbg64.exe","igfxem.exe","igfxext.exe","igfxtray.exe",
                "lghub.exe","lghub_agent.exe","logioptionsplus_agent.exe","logi_lamparray_service.exe",
                "razer synapse service.exe","razercentralservice.exe","steelseriesgg.exe",
                "armourycrate.service.exe","asus","icue.exe","corsair.service.exe",
                // --- прочее ПО, которому нужен фон ---
                "steam.exe","steamwebhelper.exe","epicgameslauncher.exe","msedgewebview2.exe",
                "adobeupdateservice.exe","creative cloud.exe","ccxprocess.exe","acrotray.exe",
                "1password.exe","bitwarden.exe","keepass.exe","keepassxc.exe"
            }, StringComparer.OrdinalIgnoreCase);

        public Engine()
        {
            _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                 "WindowsProcessCleaner");
            Directory.CreateDirectory(_dir);
            _configPath = Path.Combine(_dir, "config.json");
            _historyPath = Path.Combine(_dir, "history.json");
            LoadConfig();

            try { _currentUserSid = WindowsIdentity.GetCurrent().User.Value; } catch { _currentUserSid = null; }
            try { _winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows); } catch { _winDir = @"C:\Windows"; }
            try { _programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); } catch { _programFiles = @"C:\Program Files"; }
            try { _programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86); } catch { _programFilesX86 = @"C:\Program Files (x86)"; }
            try { _selfPid = Process.GetCurrentProcess().Id; } catch { _selfPid = 0; }
        }

        public string DataDir { get { return _dir; } }

        // ---------- Конфиг ----------
        public void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    using (FileStream fs = File.OpenRead(_configPath))
                    {
                        DataContractJsonSerializer s = new DataContractJsonSerializer(typeof(AppConfig));
                        Config = (AppConfig)s.ReadObject(fs);
                    }
                }
            }
            catch { Config = null; }
            if (Config == null) Config = AppConfig.Default();
            Config.Normalize();
        }

        public void SaveConfig()
        {
            Config.Normalize();
            using (FileStream fs = File.Create(_configPath))
            {
                DataContractJsonSerializer s = new DataContractJsonSerializer(typeof(AppConfig));
                s.WriteObject(fs, Config);
            }
        }

        // ---------- История ----------
        public HistoryFile LoadHistory()
        {
            try
            {
                if (File.Exists(_historyPath))
                {
                    using (FileStream fs = File.OpenRead(_historyPath))
                    {
                        DataContractJsonSerializer s = new DataContractJsonSerializer(typeof(HistoryFile));
                        HistoryFile h = (HistoryFile)s.ReadObject(fs);
                        if (h.Entries == null) h.Entries = new List<HistoryEntry>();
                        return h;
                    }
                }
            }
            catch { }
            HistoryFile empty = new HistoryFile();
            empty.Entries = new List<HistoryEntry>();
            return empty;
        }

        public void AppendHistory(HistoryEntry e)
        {
            HistoryFile h = LoadHistory();
            h.Entries.Insert(0, e);
            if (h.Entries.Count > 500) h.Entries = h.Entries.Take(500).ToList();
            using (FileStream fs = File.Create(_historyPath))
            {
                DataContractJsonSerializer s = new DataContractJsonSerializer(typeof(HistoryFile));
                s.WriteObject(fs, h);
            }
        }

        // ---------- Категории для отображения ----------
        public static string Categorize(string exe)
        {
            string n = exe.ToLowerInvariant();
            if (n == "node.exe" || n == "next.exe") return "Node.js";
            if (n == "npm.exe") return "npm";
            if (n == "pnpm.exe") return "pnpm";
            if (n == "yarn.exe") return "yarn";
            if (n == "bun.exe") return "Bun";
            if (n == "python.exe" || n == "pythonw.exe") return "Python";
            if (n == "java.exe" || n == "gradle.exe") return "Java";
            if (n == "vite.exe") return "Vite";
            if (n == "webpack.exe") return "Webpack";
            if (n == "cargo.exe") return "Cargo";
            if (n == "go.exe") return "Go";
            if (n == "deno.exe") return "Deno";
            if (n == "ruby.exe") return "Ruby";
            if (n == "php.exe") return "PHP";
            return exe;
        }

        // ---------- Снимок процессов ----------
        private class RawProc { public int Pid; public int Ppid; public string Name; }

        private List<RawProc> Snapshot()
        {
            List<RawProc> list = new List<RawProc>();
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
            try
            {
                Native.PROCESSENTRY32 e = new Native.PROCESSENTRY32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(Native.PROCESSENTRY32));
                if (Native.Process32First(snap, ref e))
                {
                    do
                    {
                        RawProc r = new RawProc();
                        r.Pid = (int)e.th32ProcessID;
                        r.Ppid = (int)e.th32ParentProcessID;
                        r.Name = e.szExeFile;
                        list.Add(r);
                    } while (Native.Process32Next(snap, ref e));
                }
            }
            finally { Native.CloseHandle(snap); }
            return list;
        }

        // visible  — процессы с видимым озаглавленным окном (для dev-режима);
        // anyTop   — процессы с любым верхнеуровневым окном, в т.ч. скрытым
        //            (для глобального режима: защищает свёрнутые в трей приложения).
        private void WindowPids(out HashSet<int> visible, out HashSet<int> anyTop)
        {
            HashSet<int> v = new HashSet<int>();
            HashSet<int> a = new HashSet<int>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                a.Add((int)pid);
                if (Native.IsWindowVisible(h) && Native.GetWindowTextLength(h) > 0)
                    v.Add((int)pid);
                return true;
            }, IntPtr.Zero);
            visible = v;
            anyTop = a;
        }

        private bool IsUnderSystem(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.ToLowerInvariant();
            if (!string.IsNullOrEmpty(_winDir) && p.StartsWith(_winDir.ToLowerInvariant())) return true;
            if (p.Contains("\\windowsapps\\") || p.Contains("\\systemapps\\")) return true;
            return false;
        }

        private bool IsInstalledLocation(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.ToLowerInvariant();
            if (!string.IsNullOrEmpty(_programFiles) && p.StartsWith(_programFiles.ToLowerInvariant())) return true;
            if (!string.IsNullOrEmpty(_programFilesX86) && p.StartsWith(_programFilesX86.ToLowerInvariant())) return true;
            return false;
        }

        // Возвращает пары (pid, port) всех строк TCP; listeners — множество PID со LISTEN.
        public List<PortRow> TcpRows(out HashSet<int> listeners)
        {
            listeners = new HashSet<int>();
            List<PortRow> rows = new List<PortRow>();
            int size = 0;
            Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, Native.AF_INET,
                Native.TCP_TABLE_OWNER_PID_ALL, 0);
            if (size <= 0) return rows;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                uint ret = Native.GetExtendedTcpTable(buf, ref size, false, Native.AF_INET,
                    Native.TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != 0) return rows;
                int count = Marshal.ReadInt32(buf);
                IntPtr rowPtr = new IntPtr(buf.ToInt64() + 4);
                int rowSize = Marshal.SizeOf(typeof(Native.MIB_TCPROW_OWNER_PID));
                for (int i = 0; i < count; i++)
                {
                    Native.MIB_TCPROW_OWNER_PID row = (Native.MIB_TCPROW_OWNER_PID)
                        Marshal.PtrToStructure(new IntPtr(rowPtr.ToInt64() + i * rowSize),
                                               typeof(Native.MIB_TCPROW_OWNER_PID));
                    int port = ((int)(row.localPort & 0xFF) << 8) | (int)((row.localPort >> 8) & 0xFF);
                    if (row.state == Native.MIB_TCP_STATE_LISTEN)
                    {
                        listeners.Add((int)row.owningPid);
                        PortRow pr = new PortRow();
                        pr.Port = port;
                        pr.Pid = (int)row.owningPid;
                        rows.Add(pr);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return rows;
        }

        // ---------- Тик мониторинга: обновляет CPU% и время простоя ----------
        public void MonitorTick()
        {
            List<RawProc> snap = Snapshot();
            DateTime now = DateTime.Now;
            int cores = Environment.ProcessorCount;
            if (cores < 1) cores = 1;

            lock (_sync)
            {
                HashSet<int> alive = new HashSet<int>();
                double threshold = Config.CpuThresholdPercent;

                foreach (RawProc r in snap)
                {
                    alive.Add(r.Pid);
                    if (r.Pid <= 4) continue; // System Idle / System

                    ProcStat st = QueryStat(r.Pid, false, false);
                    if (!st.Ok) continue;

                    CpuSample prev;
                    if (_lastCpu.TryGetValue(r.Pid, out prev))
                    {
                        double wall = (now - prev.At).TotalMilliseconds;
                        if (wall > 0)
                        {
                            double pct = (st.Cpu - prev.Cpu).TotalMilliseconds / (wall * cores) * 100.0;
                            if (pct < 0) pct = 0;
                            _cpuPercent[r.Pid] = pct;
                            if (pct < threshold)
                            {
                                if (!_idleSince.ContainsKey(r.Pid)) _idleSince[r.Pid] = now;
                            }
                            else { _idleSince.Remove(r.Pid); }
                        }
                        prev.Cpu = st.Cpu; prev.At = now;
                    }
                    else
                    {
                        CpuSample cur = new CpuSample();
                        cur.Cpu = st.Cpu; cur.At = now;
                        _lastCpu[r.Pid] = cur;
                    }
                }

                ForgetDead(alive);
            }
        }

        private HashSet<string> WatchSet()
        {
            HashSet<string> s = new HashSet<string>();
            foreach (string w in Config.Watchlist) s.Add(w.Trim().ToLowerInvariant());
            return s;
        }

        private bool IsWhitelisted(string exe)
        {
            string n = exe.ToLowerInvariant();
            string noext = n.EndsWith(".exe") ? n.Substring(0, n.Length - 4) : n;
            foreach (string w in Config.Whitelist)
            {
                string ww = w.Trim().ToLowerInvariant();
                if (ww.Length == 0) continue;
                string wwNoext = ww.EndsWith(".exe") ? ww.Substring(0, ww.Length - 4) : ww;
                if (n == ww || noext == wwNoext) return true;
            }
            return false;
        }

        // ---------- Полное сканирование ----------
        // global=false — только процессы из watchlist (dev-режим).
        // global=true  — ВСЕ процессы; кандидаты отбираются с усиленными
        //                предохранителями (только свои процессы, не системные пути).
        public List<ProcInfo> Scan(bool global)
        {
            List<RawProc> snap = Snapshot();
            HashSet<string> watch = WatchSet();
            HashSet<int> visible, anyTop;
            WindowPids(out visible, out anyTop);
            HashSet<int> listeners;
            TcpRows(out listeners);

            HashSet<int> alive = new HashSet<int>(snap.Select(p => p.Pid));
            HashSet<int> parents = new HashSet<int>(snap.Select(p => p.Ppid));

            DateTime now = DateTime.Now;
            List<ProcInfo> result = new List<ProcInfo>();

            lock (_sync)
            foreach (RawProc r in snap)
            {
                bool inWatch = watch.Contains(r.Name.ToLowerInvariant());
                if (!global && !inWatch) continue;
                if (r.Pid <= 4 || r.Pid == _selfPid) continue;

                ProcInfo info = new ProcInfo();
                info.Pid = r.Pid;
                info.ParentPid = r.Ppid;
                info.Name = r.Name;
                info.Category = Categorize(r.Name);
                info.HasWindow = global ? anyTop.Contains(r.Pid) : visible.Contains(r.Pid);
                info.ListensTcp = listeners.Contains(r.Pid);
                info.HasChildren = parents.Contains(r.Pid);
                info.ParentAlive = r.Ppid != 0 && alive.Contains(r.Ppid);
                info.Whitelisted = IsWhitelisted(r.Name);
                info.UserOwned = true;

                double pct;
                info.CpuPercent = _cpuPercent.TryGetValue(r.Pid, out pct) ? pct : 0;
                DateTime since;
                info.IdleFor = _idleSince.TryGetValue(r.Pid, out since) ? (now - since) : TimeSpan.Zero;

                // Один OpenProcess даёт RAM, время старта, путь и (для глобального режима) SID.
                ProcStat st = QueryStat(r.Pid, true, global);
                info.RamBytes = st.WorkingSet;
                info.Path = st.Path ?? "";
                info.Uptime = st.Start > DateTime.MinValue ? now - st.Start : TimeSpan.Zero;
                if (info.Uptime < TimeSpan.Zero) info.Uptime = TimeSpan.Zero;

                if (global)
                {
                    info.IsSystemPath = IsUnderSystem(info.Path);
                    info.UserOwned = _currentUserSid != null && st.Sid != null && st.Sid == _currentUserSid;
                }

                EvaluateCandidate(info, global);
                result.Add(info);
            }
            return result;
        }

        private void EvaluateCandidate(ProcInfo p, bool global)
        {
            List<string> reasons = new List<string>();
            if (p.Whitelisted) { p.IsCandidate = false; p.Reason = Tr.S("в белом списке", "whitelisted"); return; }

            if (global)
            {
                if (_critical.Contains(p.Name)) { p.IsCandidate = false; p.Reason = Tr.S("защищённый процесс", "protected process"); return; }
                if (!p.UserOwned) { p.IsCandidate = false; p.Reason = Tr.S("не ваш процесс", "not your process"); return; }
                if (p.IsSystemPath) { p.IsCandidate = false; p.Reason = Tr.S("системный компонент", "system component"); return; }
                if (string.IsNullOrEmpty(p.Path)) { p.IsCandidate = false; p.Reason = Tr.S("нет доступа к пути", "no path access"); return; }
                if (Config.GlobalExcludeInstalled && IsInstalledLocation(p.Path))
                { p.IsCandidate = false; p.Reason = Tr.S("установленное приложение", "installed application"); return; }
            }

            if (p.Uptime.TotalMinutes < Config.MinLifetimeMinutes)
            { p.IsCandidate = false; p.Reason = Tr.S("молодой процесс", "too young"); return; }

            int idleReq = global ? Math.Max(Config.IdleMinutes, Config.GlobalIdleMinutes) : Config.IdleMinutes;
            bool parentDead = !p.ParentAlive;
            bool idleEnough = p.CpuPercent < Config.CpuThresholdPercent
                              && p.IdleFor.TotalMinutes >= idleReq;
            bool noWindow = !p.HasWindow;
            bool noTcp = !p.ListensTcp;
            bool noChildren = !p.HasChildren;

            if (parentDead && idleEnough && noWindow && noTcp && noChildren)
            {
                p.IsCandidate = true;
                p.Reason = Tr.S("родитель мёртв, простой, без окон/портов/детей",
                                "orphaned, idle, no windows/ports/children");
            }
            else
            {
                p.IsCandidate = false;
                if (p.ParentAlive) reasons.Add(Tr.S("жив родитель", "parent alive"));
                if (!idleEnough) reasons.Add(Tr.S("активен/мало простоя", "active/low idle"));
                if (p.HasWindow) reasons.Add(Tr.S("есть окно", "has window"));
                if (p.ListensTcp) reasons.Add(Tr.S("слушает порт", "listens on port"));
                if (p.HasChildren) reasons.Add(Tr.S("есть дочерние", "has children"));
                p.Reason = string.Join(", ", reasons.ToArray());
            }
        }

        // ---------- Завершение ----------
        // Возвращает true, если процесс завершён. freed — освобождённая RAM (WorkingSet до убийства).
        // Карта pid -> его верхнеуровневые окна. Строится ОДНИМ обходом:
        // отдельный EnumWindows на каждый убиваемый процесс — лишний обход всего рабочего стола.
        public Dictionary<int, List<IntPtr>> WindowsByPid()
        {
            Dictionary<int, List<IntPtr>> map = new Dictionary<int, List<IntPtr>>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint wp;
                Native.GetWindowThreadProcessId(h, out wp);
                int pid = (int)wp;
                List<IntPtr> lst;
                if (!map.TryGetValue(pid, out lst)) { lst = new List<IntPtr>(); map[pid] = lst; }
                lst.Add(h);
                return true;
            }, IntPtr.Zero);
            return map;
        }

        // true — такого PID в системе больше нет (в отличие от "нет прав открыть").
        private static bool PidGone(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero) { Native.CloseHandle(h); return false; }
            return Marshal.GetLastWin32Error() == 87; // ERROR_INVALID_PARAMETER
        }

        private const uint KillAccess = Native.PROCESS_QUERY_LIMITED_INFORMATION
                                      | Native.PROCESS_TERMINATE | Native.SYNCHRONIZE;

        public bool TerminateProcess(int pid, out long freed)
        {
            List<int> one = new List<int>(); one.Add(pid);
            return TerminateMany(one, out freed) > 0;
        }

        // Пакетное завершение. Ключевое отличие от «по одному»: WM_CLOSE рассылается
        // всем сразу и ожидание общее, поэтому 20 процессов стоят ~6 с, а не 20*6 с.
        public int TerminateMany(List<int> pids, out long freed)
        {
            freed = 0;
            int killed = 0;
            if (pids == null || pids.Count == 0) return 0;

            List<int> unique = new List<int>(new HashSet<int>(pids));
            Dictionary<int, IntPtr> handles = new Dictionary<int, IntPtr>();
            Dictionary<int, long> ws = new Dictionary<int, long>();
            Dictionary<int, List<IntPtr>> winMap = WindowsByPid();

            try
            {
                foreach (int pid in unique)
                {
                    if (pid <= 4 || pid == _selfPid) continue;
                    IntPtr h = Native.OpenProcess(KillAccess | Native.PROCESS_VM_READ, false, pid);
                    if (h == IntPtr.Zero) h = Native.OpenProcess(KillAccess, false, pid);
                    if (h == IntPtr.Zero)
                    {
                        // уже умер — считаем задачу выполненной; иначе просто нет прав
                        if (PidGone(pid)) killed++;
                        continue;
                    }
                    handles[pid] = h;
                    ws[pid] = Native.QueryWorkingSet(h);
                }

                // 1) мягко: WM_CLOSE всем окнам всех процессов сразу
                bool anyWindow = false;
                foreach (KeyValuePair<int, IntPtr> kv in handles)
                {
                    List<IntPtr> wins;
                    if (!winMap.TryGetValue(kv.Key, out wins)) continue;
                    foreach (IntPtr w in wins)
                    {
                        IntPtr res;
                        Native.SendMessageTimeout(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero, 0, 200, out res);
                        anyWindow = true;
                    }
                }

                // 2) одно общее ожидание на всех
                if (anyWindow) Thread.Sleep(2500);

                // 3) кто не ушёл — принудительно
                List<int> forced = new List<int>();
                foreach (KeyValuePair<int, IntPtr> kv in handles)
                {
                    if (Native.WaitForSingleObject(kv.Value, 0) == Native.WAIT_OBJECT_0)
                    {
                        killed++; freed += ws[kv.Key];
                        continue;
                    }
                    Native.TerminateProcess(kv.Value, 1);
                    forced.Add(kv.Key);
                }
                if (forced.Count > 0)
                {
                    Thread.Sleep(500);
                    foreach (int pid in forced)
                    {
                        IntPtr h = handles[pid];
                        uint code;
                        bool gone = Native.WaitForSingleObject(h, 1500) == Native.WAIT_OBJECT_0
                                 || (Native.GetExitCodeProcess(h, out code) && code != Native.STILL_ACTIVE);
                        if (gone) { killed++; freed += ws[pid]; }
                    }
                }
            }
            finally
            {
                foreach (IntPtr h in handles.Values) Native.CloseHandle(h);
            }
            return killed;
        }

        // ---------- Очистка Standby Memory ----------
        public class MemResult { public bool Ok; public long FreedBytes; public string Message; }

        public MemResult PurgeStandby()
        {
            MemResult mr = new MemResult();
            Native.MEMORYSTATUSEX before = new Native.MEMORYSTATUSEX();
            before.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            Native.GlobalMemoryStatusEx(ref before);

            Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
            Native.EnablePrivilege("SeIncreaseQuotaPrivilege");

            // MemoryEmptyWorkingSets выдавливает страницы ВСЕХ процессов системы:
            // сразу после вызова всё, включая нас, тормозит, пока не загрузится обратно.
            // По умолчанию выключено — чистим только standby-список.
            if (Config.EmptyWorkingSets) SetMemoryList(Native.MemoryEmptyWorkingSets);
            int rc2 = SetMemoryList(Native.MemoryPurgeStandbyList);

            Native.MEMORYSTATUSEX after = new Native.MEMORYSTATUSEX();
            after.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            Native.GlobalMemoryStatusEx(ref after);

            long freed = (long)after.ullAvailPhys - (long)before.ullAvailPhys;
            mr.FreedBytes = freed > 0 ? freed : 0;

            if (rc2 == 0)
            {
                mr.Ok = true;
                mr.Message = Tr.S("Standby Memory очищена", "Standby Memory purged");
            }
            else if ((uint)rc2 == 0xC0000061)
            {
                mr.Ok = false;
                mr.Message = Tr.S("Нужны права администратора (перезапустите от админа)",
                                   "Administrator rights required (restart as admin)");
            }
            else
            {
                mr.Ok = false;
                mr.Message = "NtSetSystemInformation вернул 0x" + ((uint)rc2).ToString("X8");
            }
            return mr;
        }

        private int SetMemoryList(int command)
        {
            IntPtr p = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(p, command);
                return Native.NtSetSystemInformation(Native.SystemMemoryListInformation, p, sizeof(int));
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ---------- Массовое завершение по группе (Dev Cleanup) ----------
        public int TerminateByNames(string[] names, out long freed)
        {
            HashSet<string> want = new HashSet<string>(names.Select(n => n.ToLowerInvariant()));
            List<int> pids = new List<int>();
            foreach (RawProc r in Snapshot())
                if (want.Contains(r.Name.ToLowerInvariant()) && !IsWhitelisted(r.Name))
                    pids.Add(r.Pid);
            return TerminateMany(pids, out freed);
        }

        // ---------- Занятые dev-порты ----------
        public List<PortRow> DevPortRows()
        {
            HashSet<int> listeners;
            List<PortRow> rows = TcpRows(out listeners);
            HashSet<int> devPorts = new HashSet<int>(Config.DevPorts);
            Dictionary<int, string> names = new Dictionary<int, string>();
            foreach (RawProc r in Snapshot()) names[r.Pid] = r.Name;

            List<PortRow> outRows = new List<PortRow>();
            HashSet<string> seen = new HashSet<string>();
            foreach (PortRow pr in rows)
            {
                if (!devPorts.Contains(pr.Port)) continue;
                string key = pr.Port + ":" + pr.Pid;
                if (seen.Contains(key)) continue;
                seen.Add(key);
                string nm;
                pr.ProcName = names.TryGetValue(pr.Pid, out nm) ? nm : "(pid " + pr.Pid + ")";
                outRows.Add(pr);
            }
            outRows.Sort(delegate(PortRow a, PortRow b) { return a.Port.CompareTo(b.Port); });
            return outRows;
        }

        // ================= ОЧИСТКА ДИСКА =================
        // Только известные мусорные пути. Никакого поиска дубликатов по диску.

        private void AddDir(CleanCategory c, string path, bool contentsOnly)
        {
            AddDir(c, path, contentsOnly, null, 0);
        }

        private void AddDir(CleanCategory c, string path, bool contentsOnly, string mask, int minAgeMinutes)
        {
            // с маской по умолчанию НЕ рекурсивно: иначе "*.dmp в %WinDir%" обойдёт
            // весь C:\Windows целиком, а это минуты
            AddDir(c, path, contentsOnly, mask, minAgeMinutes, string.IsNullOrEmpty(mask));
        }

        // mask — только файлы по маске (папка остаётся); minAge — не трогать свежие файлы.
        private void AddDir(CleanCategory c, string path, bool contentsOnly, string mask,
                            int minAgeMinutes, bool recurse)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!Directory.Exists(path)) return;
                string full = Path.GetFullPath(path).TrimEnd('\\');
                // одна и та же папка часто приходит двумя путями (%TEMP% и %LOCALAPPDATA%\Temp):
                // без дедупликации она обходится дважды
                foreach (CleanTarget ex in c.Targets)
                    if (string.Equals(ex.Path, full, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ex.Mask ?? "", mask ?? "", StringComparison.OrdinalIgnoreCase)) return;
                c.Targets.Add(new CleanTarget
                {
                    Path = full,
                    ContentsOnly = contentsOnly,
                    Mask = mask,
                    MinAgeMinutes = minAgeMinutes,
                    Recurse = recurse
                });
            }
            catch { }
        }

        // Каждая подпапка *userData*\<profile> получает один и тот же набор кэшей —
        // выносим список, чтобы он не расползался по коду.
        private static readonly string[] _chromiumProfileCaches = new string[] {
            "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
            "GrShaderCache", "ShaderCache", "Media Cache", "Application Cache",
            "Service Worker\\CacheStorage", "Service Worker\\ScriptCache",
            "Storage\\ext", "optimization_guide_prediction_model_downloads",
            "component_crx_cache", "extensions_crx_cache",
        };

        private void AddChromium(CleanCategory c, string userData)
        {
            if (!Directory.Exists(userData)) return;
            // кэши уровня установки, вне профилей
            AddDir(c, Path.Combine(userData, "ShaderCache"), true);
            AddDir(c, Path.Combine(userData, "GrShaderCache"), true);
            AddDir(c, Path.Combine(userData, "GraphiteDawnCache"), true);
            AddDir(c, Path.Combine(userData, "component_crx_cache"), true);

            string[] profiles = null;
            try { profiles = Directory.GetDirectories(userData); } catch { }
            if (profiles == null) return;
            foreach (string p in profiles)
            {
                string name = Path.GetFileName(p);
                // профили — это Default, Profile 1..N, Guest Profile; служебные папки пропускаем
                bool isProfile = string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)
                              || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(name, "Guest Profile", StringComparison.OrdinalIgnoreCase)
                              || Directory.Exists(Path.Combine(p, "Cache"));
                if (!isProfile) continue;
                foreach (string sub in _chromiumProfileCaches) AddDir(c, Path.Combine(p, sub), true);
            }
        }

        private void AddFirefox(CleanCategory c, string profilesDir)
        {
            if (!Directory.Exists(profilesDir)) return;
            string[] ps = null;
            try { ps = Directory.GetDirectories(profilesDir); } catch { }
            if (ps == null) return;
            foreach (string p in ps)
            {
                AddDir(c, Path.Combine(p, "cache2"), true);
                AddDir(c, Path.Combine(p, "startupCache"), true);
                AddDir(c, Path.Combine(p, "shader-cache"), true);
                AddDir(c, Path.Combine(p, "thumbnails"), true);
                AddDir(c, Path.Combine(p, "safebrowsing"), true);
                AddDir(c, Path.Combine(p, "minidumps"), true);
            }
        }

        // Кэш Electron-приложения (Discord/Slack/Teams/VS Code и т.п.)
        private void AddElectronCache(CleanCategory c, string dir)
        {
            if (!Directory.Exists(dir)) return;
            AddDir(c, Path.Combine(dir, "Cache"), true);
            AddDir(c, Path.Combine(dir, "Code Cache"), true);
            AddDir(c, Path.Combine(dir, "GPUCache"), true);
            AddDir(c, Path.Combine(dir, "DawnCache"), true);
            AddDir(c, Path.Combine(dir, "DawnGraphiteCache"), true);
            AddDir(c, Path.Combine(dir, "DawnWebGPUCache"), true);
            AddDir(c, Path.Combine(dir, "GrShaderCache"), true);
            AddDir(c, Path.Combine(dir, "ShaderCache"), true);
            AddDir(c, Path.Combine(dir, "Service Worker\\CacheStorage"), true);
            AddDir(c, Path.Combine(dir, "Service Worker\\ScriptCache"), true);
            AddDir(c, Path.Combine(dir, "Crashpad\\reports"), true);
            AddDir(c, Path.Combine(dir, "logs"), true);
        }

        public List<CleanCategory> BuildCleanCategories()
        {
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string ad = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string temp = Path.GetTempPath();
            string sysDrive = Path.GetPathRoot(_winDir);
            List<CleanCategory> list = new List<CleanCategory>();

            // Файлы, изменённые только что, могут принадлежать идущей установке или
            // активной сессии — для temp-папок держим окно неприкосновенности.
            int fresh = Config.CleanSkipRecentMinutes;

            // Dev-кэши
            CleanCategory dev = new CleanCategory();
            dev.Id = "dev"; dev.Title = Tr.S("Dev-кэши", "Dev caches"); dev.Recommended = true;
            dev.Desc = Tr.S("npm / pnpm / yarn / bun / pip / gradle / cargo / go / NuGet / Composer (пересоздаются)",
                            "npm / pnpm / yarn / bun / pip / gradle / cargo / go / NuGet / Composer (regenerated)");
            AddDir(dev, Path.Combine(lad, "npm-cache"), true);
            AddDir(dev, Path.Combine(ad, "npm-cache"), true);
            AddDir(dev, Path.Combine(up, ".npm\\_cacache"), true);
            AddDir(dev, Path.Combine(lad, "Yarn\\Cache"), true);
            AddDir(dev, Path.Combine(lad, "Yarn\\berry\\cache"), true);
            AddDir(dev, Path.Combine(up, ".yarn\\cache"), true);
            AddDir(dev, Path.Combine(up, ".yarn\\berry\\cache"), true);
            AddDir(dev, Path.Combine(lad, "pnpm\\store"), true);
            AddDir(dev, Path.Combine(lad, "pnpm-store"), true);
            AddDir(dev, Path.Combine(up, ".pnpm-store"), true);
            AddDir(dev, Path.Combine(lad, "bun\\install\\cache"), true);
            AddDir(dev, Path.Combine(up, ".bun\\install\\cache"), true);
            AddDir(dev, Path.Combine(lad, "deno"), true);
            AddDir(dev, Path.Combine(lad, "pip\\Cache"), true);
            AddDir(dev, Path.Combine(lad, "pip\\cache"), true);
            AddDir(dev, Path.Combine(up, ".cache\\pip"), true);
            AddDir(dev, Path.Combine(up, ".gradle\\caches"), true);
            AddDir(dev, Path.Combine(up, ".cargo\\registry\\cache"), true);
            AddDir(dev, Path.Combine(up, ".cargo\\registry\\src"), true);
            AddDir(dev, Path.Combine(up, "go\\pkg\\mod\\cache\\download"), true);
            AddDir(dev, Path.Combine(up, ".nuget\\packages"), true);
            AddDir(dev, Path.Combine(lad, "NuGet\\Cache"), true);
            AddDir(dev, Path.Combine(lad, "NuGet\\v3-cache"), true);
            AddDir(dev, Path.Combine(lad, "NuGet\\plugins-cache"), true);
            AddDir(dev, Path.Combine(lad, "Composer"), true);
            AddDir(dev, Path.Combine(ad, "Composer\\cache"), true);
            AddDir(dev, Path.Combine(lad, "Temp\\gradle"), true);
            if (dev.Targets.Count > 0) list.Add(dev);

            // Тяжёлые dev-загрузки: восстановимы, но качаются заново долго — не рекомендуем по умолчанию
            CleanCategory devBig = new CleanCategory();
            devBig.Id = "devbig"; devBig.Title = Tr.S("Dev: скачанные тулчейны", "Dev: downloaded toolchains");
            devBig.Desc = Tr.S("браузеры Playwright/Puppeteer/Cypress, кэш electron-builder — скачаются заново",
                               "Playwright/Puppeteer/Cypress browsers, electron-builder cache — will re-download");
            AddDir(devBig, Path.Combine(lad, "ms-playwright"), true);
            AddDir(devBig, Path.Combine(lad, "puppeteer"), true);
            AddDir(devBig, Path.Combine(up, ".cache\\puppeteer"), true);
            AddDir(devBig, Path.Combine(lad, "Cypress\\Cache"), true);
            AddDir(devBig, Path.Combine(lad, "electron"), true);
            AddDir(devBig, Path.Combine(lad, "electron-builder\\Cache"), true);
            AddDir(devBig, Path.Combine(up, ".gradle\\wrapper\\dists"), true);
            if (devBig.Targets.Count > 0) list.Add(devBig);

            // Системный мусор
            CleanCategory sys = new CleanCategory();
            sys.Id = "sys"; sys.Title = Tr.S("Системный мусор", "System junk"); sys.Recommended = true; sys.RecycleBin = true;
            sys.Desc = Tr.S("temp, Корзина, кэш Windows Update, дампы падений, отчёты об ошибках, Delivery Optimization",
                            "temp, Recycle Bin, Windows Update cache, crash dumps, error reports, Delivery Optimization");
            AddDir(sys, temp, true, null, fresh);
            AddDir(sys, Path.Combine(lad, "Temp"), true, null, fresh);
            AddDir(sys, Path.Combine(_winDir, "Temp"), true, null, fresh);
            AddDir(sys, Path.Combine(_winDir, "SoftwareDistribution\\Download"), true);
            AddDir(sys, Path.Combine(_winDir, "ServiceProfiles\\NetworkService\\AppData\\Local\\Microsoft\\Windows\\DeliveryOptimization\\Cache"), true);
            AddDir(sys, Path.Combine(lad, "CrashDumps"), true);
            AddDir(sys, Path.Combine(lad, "Microsoft\\Windows\\WER"), true);
            AddDir(sys, Path.Combine(pd, "Microsoft\\Windows\\WER"), true);
            AddDir(sys, Path.Combine(_winDir, "LiveKernelReports"), true);
            AddDir(sys, Path.Combine(_winDir, "Minidump"), true);
            AddDir(sys, Path.Combine(_winDir, "Prefetch"), true, null, 0);
            AddDir(sys, Path.Combine(_winDir, "Panther"), true);
            AddDir(sys, Path.Combine(_winDir, "Installer\\$PatchCache$"), true);
            AddDir(sys, _winDir, true, "*.dmp", 0);
            AddDir(sys, _winDir, true, "MEMORY.DMP", 0);
            list.Add(sys);

            // Кэши отрисовки/эскизов Windows — восстанавливаются автоматически
            CleanCategory shell = new CleanCategory();
            shell.Id = "shell"; shell.Title = Tr.S("Кэши Windows (эскизы, иконки, шейдеры)", "Windows caches (thumbnails, icons, shaders)");
            shell.Recommended = true;
            shell.Desc = Tr.S("thumbcache/iconcache, DirectX- и GPU-кэши, кэш шрифтов — Windows пересоберёт сама",
                              "thumbcache/iconcache, DirectX and GPU caches, font cache — Windows rebuilds them");
            string explorerDir = Path.Combine(lad, "Microsoft\\Windows\\Explorer");
            AddDir(shell, explorerDir, true, "thumbcache_*.db", 0);
            AddDir(shell, explorerDir, true, "iconcache_*.db", 0);
            AddDir(shell, Path.Combine(lad, "Microsoft\\Windows\\INetCache"), true);
            AddDir(shell, Path.Combine(lad, "D3DSCache"), true);
            AddDir(shell, Path.Combine(lad, "NVIDIA\\DXCache"), true);
            AddDir(shell, Path.Combine(lad, "NVIDIA\\GLCache"), true);
            AddDir(shell, Path.Combine(lad, "NVIDIA\\ComputeCache"), true);
            AddDir(shell, Path.Combine(lad, "AMD\\DxCache"), true);
            AddDir(shell, Path.Combine(lad, "AMD\\DxcCache"), true);
            AddDir(shell, Path.Combine(lad, "Intel\\ShaderCache"), true);
            AddDir(shell, Path.Combine(_winDir, "ServiceProfiles\\LocalService\\AppData\\Local\\FontCache"), true);
            if (shell.Targets.Count > 0) list.Add(shell);

            // Кэши браузеров
            CleanCategory br = new CleanCategory();
            br.Id = "browser"; br.Title = Tr.S("Кэши браузеров", "Browser caches");
            br.Desc = Tr.S("Chrome / Edge / Brave / Yandex / Opera / Vivaldi / Firefox — только кэш (пароли, куки и история не трогаются)",
                           "Chrome / Edge / Brave / Yandex / Opera / Vivaldi / Firefox — cache only (passwords, cookies, history untouched)");
            AddChromium(br, Path.Combine(lad, "Google\\Chrome\\User Data"));
            AddChromium(br, Path.Combine(lad, "Google\\Chrome Beta\\User Data"));
            AddChromium(br, Path.Combine(lad, "Google\\Chrome SxS\\User Data"));
            AddChromium(br, Path.Combine(lad, "Microsoft\\Edge\\User Data"));
            AddChromium(br, Path.Combine(lad, "Microsoft\\Edge Dev\\User Data"));
            AddChromium(br, Path.Combine(lad, "BraveSoftware\\Brave-Browser\\User Data"));
            AddChromium(br, Path.Combine(lad, "Yandex\\YandexBrowser\\User Data"));
            AddChromium(br, Path.Combine(lad, "Vivaldi\\User Data"));
            AddChromium(br, Path.Combine(ad, "Opera Software\\Opera Stable"));
            AddChromium(br, Path.Combine(ad, "Opera Software\\Opera GX Stable"));
            AddFirefox(br, Path.Combine(lad, "Mozilla\\Firefox\\Profiles"));
            AddFirefox(br, Path.Combine(ad, "Mozilla\\Firefox\\Profiles"));
            if (br.Targets.Count > 0) list.Add(br);

            // Кэши приложений (Electron/медиа/IDE)
            CleanCategory apps = new CleanCategory();
            apps.Id = "appcache"; apps.Title = Tr.S("Кэши приложений", "App caches");
            apps.Recommended = true;
            apps.Desc = Tr.S("Discord / Slack / Teams / Spotify / VS Code / JetBrains / Steam / Telegram — только кэш",
                             "Discord / Slack / Teams / Spotify / VS Code / JetBrains / Steam / Telegram — cache only");
            AddElectronCache(apps, Path.Combine(ad, "discord"));
            AddElectronCache(apps, Path.Combine(ad, "discordptb"));
            AddElectronCache(apps, Path.Combine(ad, "discordcanary"));
            AddElectronCache(apps, Path.Combine(ad, "Slack"));
            AddElectronCache(apps, Path.Combine(ad, "Microsoft\\Teams"));
            AddElectronCache(apps, Path.Combine(lad, "Microsoft\\Teams"));
            AddElectronCache(apps, Path.Combine(ad, "Code"));
            AddElectronCache(apps, Path.Combine(ad, "Cursor"));
            AddElectronCache(apps, Path.Combine(ad, "Postman"));
            AddElectronCache(apps, Path.Combine(ad, "Figma"));
            AddElectronCache(apps, Path.Combine(ad, "Notion"));
            AddElectronCache(apps, Path.Combine(ad, "obsidian"));
            AddDir(apps, Path.Combine(ad, "Code\\CachedData"), true);
            AddDir(apps, Path.Combine(ad, "Code\\CachedExtensionVSIXs"), true);
            AddDir(apps, Path.Combine(ad, "Code\\logs"), true);
            AddDir(apps, Path.Combine(ad, "Cursor\\CachedData"), true);
            AddDir(apps, Path.Combine(ad, "Cursor\\logs"), true);
            AddDir(apps, Path.Combine(lad, "Spotify\\Storage"), true);
            AddDir(apps, Path.Combine(lad, "Spotify\\Data"), true);
            AddDir(apps, Path.Combine(lad, "Spotify\\Browser"), true);
            AddDir(apps, Path.Combine(lad, "Steam\\htmlcache"), true);
            AddDir(apps, Path.Combine(ad, "Telegram Desktop\\tdata\\user_data\\cache"), true);
            AddDir(apps, Path.Combine(ad, "Telegram Desktop\\tdata\\emoji"), true);
            AddDir(apps, Path.Combine(lad, "Adobe\\Common\\Media Cache Files"), true);
            AddDir(apps, Path.Combine(lad, "Unity\\cache"), true);
            AddJetBrains(apps, Path.Combine(lad, "JetBrains"));
            AddSteamShaderCache(apps, sysDrive);
            if (apps.Targets.Count > 0) list.Add(apps);

            // Старые логи
            CleanCategory logs = new CleanCategory();
            logs.Id = "logs"; logs.Title = Tr.S("Старые логи", "Old logs");
            logs.Desc = Tr.S("логи CBS/DISM/установки Windows, npm/yarn, Docker Desktop",
                             "CBS/DISM/Windows setup logs, npm/yarn, Docker Desktop");
            AddDir(logs, Path.Combine(_winDir, "Logs\\CBS"), true);
            AddDir(logs, Path.Combine(_winDir, "Logs\\DISM"), true);
            AddDir(logs, Path.Combine(_winDir, "Logs\\MoSetup"), true);
            AddDir(logs, Path.Combine(_winDir, "Logs\\WindowsUpdate"), true);
            AddDir(logs, Path.Combine(_winDir, "Logs\\SIH"), true);
            AddDir(logs, Path.Combine(lad, "npm-cache\\_logs"), true);
            AddDir(logs, Path.Combine(up, ".npm\\_logs"), true);
            AddDir(logs, Path.Combine(lad, "Yarn\\logs"), true);
            AddDir(logs, Path.Combine(ad, "Docker Desktop\\log"), true);
            AddDir(logs, Path.Combine(lad, "Docker\\log"), true);
            if (logs.Targets.Count > 0) list.Add(logs);

            // Следы недавних файлов (приватность) — то, что FluentCleaner называет "recently opened"
            CleanCategory recent = new CleanCategory();
            recent.Id = "recent"; recent.Title = Tr.S("Списки недавних файлов", "Recent file lists");
            recent.Desc = Tr.S("«Недавние документы», списки переходов проводника и Office (сами файлы не трогаются)",
                               "Recent documents, Explorer/Office jump lists (the files themselves are untouched)");
            AddDir(recent, Path.Combine(ad, "Microsoft\\Windows\\Recent"), true, "*.lnk", 0);
            AddDir(recent, Path.Combine(ad, "Microsoft\\Windows\\Recent\\AutomaticDestinations"), true);
            AddDir(recent, Path.Combine(ad, "Microsoft\\Windows\\Recent\\CustomDestinations"), true);
            AddDir(recent, Path.Combine(ad, "Microsoft\\Office\\Recent"), true);
            if (recent.Targets.Count > 0) list.Add(recent);

            // Старые драйверы + Windows.old
            CleanCategory drv = new CleanCategory();
            drv.Id = "drivers"; drv.Title = Tr.S("Старые драйверы + Windows.old", "Old drivers + Windows.old");
            drv.Desc = Tr.S("installer-мусор NVIDIA/AMD/Intel, папка старой Windows (DriverStore не трогается)",
                            "NVIDIA/AMD/Intel installer leftovers, old Windows folder (DriverStore untouched)");
            AddDir(drv, Path.Combine(sysDrive, "NVIDIA"), false);
            AddDir(drv, Path.Combine(pd, "NVIDIA Corporation\\Downloader"), true);
            AddDir(drv, Path.Combine(lad, "NVIDIA Corporation\\NV_Cache"), true);
            AddDir(drv, Path.Combine(sysDrive, "AMD"), false);
            AddDir(drv, Path.Combine(sysDrive, "Intel"), false);
            AddDir(drv, Path.Combine(sysDrive, "Windows.old"), false);
            AddDir(drv, Path.Combine(sysDrive, "$Windows.~BT"), false);
            AddDir(drv, Path.Combine(sysDrive, "$Windows.~WS"), false);
            if (drv.Targets.Count > 0) list.Add(drv);

            // Правила из winapp2.ini, если база положена рядом (формат FluentCleaner/BleachBit)
            try { list.AddRange(LoadWinapp2Categories()); } catch { }

            return list;
        }

        // Кэши JetBrains-IDE: подпапки вида ...\JetBrains\IntelliJIdea2024.1\{caches,log,tmp}
        private void AddJetBrains(CleanCategory c, string jbRoot)
        {
            if (!Directory.Exists(jbRoot)) return;
            string[] ides = null;
            try { ides = Directory.GetDirectories(jbRoot); } catch { return; }
            if (ides == null) return;
            foreach (string ide in ides)
            {
                AddDir(c, Path.Combine(ide, "caches"), true);
                AddDir(c, Path.Combine(ide, "log"), true);
                AddDir(c, Path.Combine(ide, "tmp"), true);
            }
        }

        // ================= БАЗА ПРАВИЛ winapp2.ini =================
        // Формат, который используют FluentCleaner / BleachBit / CCleaner: тысячи
        // готовых правил "где у какого приложения лежит кэш". Подключается, только если
        // файл реально положен рядом с exe или в каталог данных — своей базы мы не везём.
        //
        // Сознательные ограничения (те же, что у FluentCleaner):
        //  - RegKey* игнорируются: чистка реестра не делается вообще;
        //  - секции с ExcludeKey* и Warning= пропускаются целиком — правило само
        //    сообщает, что там есть чего не трогать, и угадывать мы не будем.
        public string Winapp2Path
        {
            get
            {
                string local = Path.Combine(_dir, "winapp2.ini");
                if (File.Exists(local)) return local;
                try
                {
                    string beside = Path.Combine(
                        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "winapp2.ini");
                    if (File.Exists(beside)) return beside;
                }
                catch { }
                return null;
            }
        }

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
                File.WriteAllBytes(Winapp2TargetPath, data);
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

        // Steam ставится куда угодно — берём путь из реестра, а не угадываем диск.
        private void AddSteamShaderCache(CleanCategory c, string sysDrive)
        {
            string steam = null;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    if (k != null) steam = k.GetValue("SteamPath") as string;
            }
            catch { }
            if (string.IsNullOrEmpty(steam)) return;
            steam = steam.Replace('/', '\\');
            AddDir(c, Path.Combine(steam, "steamapps\\shadercache"), true);
            AddDir(c, Path.Combine(steam, "appcache\\httpcache"), true);
            AddDir(c, Path.Combine(steam, "logs"), true);
        }

        // Отмена длинного анализа/удаления: пользователь ушёл с вкладки или закрыл окно.
        private volatile bool _cancelDisk;
        public void CancelDiskWork() { _cancelDisk = true; }
        public void ResetDiskCancel() { _cancelDisk = false; }
        public bool DiskCancelled { get { return _cancelDisk; } }

        // Обход каталога БЕЗ сбора всех путей в память и БЕЗ рекурсии.
        // Старая версия складывала в List<string> путь каждого файла (для %TEMP% или
        // .nuget\packages это сотни тысяч строк и сотни МБ), а потом делала на каждый
        // ещё один new FileInfo(f).Length — второй поход к ФС за уже полученными данными.
        // EnumerateFileSystemInfos отдаёт размер сразу, стек вместо рекурсии не боится
        // глубоких node_modules.
        private delegate void FileVisitor(FileInfo fi);

        // Страховка от циклов, которые не помечены точкой повторного разбора.
        // Реальных деревьев такой глубины не бывает; путь всё равно упёрся бы в MAX_PATH.
        private const int MaxWalkDepth = 96;

        private static bool IsDotName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return name.Trim(' ', '.').Length == 0;
        }

        private void Walk(CleanTarget t, FileVisitor onFile, List<string> dirsOut, ref int errors)
        {
            DirectoryInfo root;
            try { root = new DirectoryInfo(t.Path); if (!root.Exists) return; }
            catch { return; }

            string mask = string.IsNullOrEmpty(t.Mask) ? "*" : t.Mask;
            DateTime cutoff = t.MinAgeMinutes > 0
                ? DateTime.Now.AddMinutes(-t.MinAgeMinutes)
                : DateTime.MaxValue;

            Stack<DirectoryInfo> stack = new Stack<DirectoryInfo>();
            Stack<int> depths = new Stack<int>();
            stack.Push(root); depths.Push(0);
            while (stack.Count > 0)
            {
                if (_cancelDisk) return;
                DirectoryInfo dir = stack.Pop();
                int depth = depths.Pop();

                // junction/symlink: за ним может лежать что угодно, включая корень диска
                try { if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue; }
                catch { continue; }

                IEnumerable<FileSystemInfo> children;
                try { children = dir.EnumerateFileSystemInfos(); }
                catch { errors++; continue; }

                IEnumerator<FileSystemInfo> it;
                try { it = children.GetEnumerator(); }
                catch { errors++; continue; }
                using (it)
                {
                    while (true)
                    {
                        FileSystemInfo fsi;
                        // MoveNext сам может бросить на недоступном элементе — не роняем весь обход
                        try { if (!it.MoveNext()) break; fsi = it.Current; }
                        catch { errors++; break; }
                        if (_cancelDisk) return;

                        FileInfo fi = fsi as FileInfo;
                        if (fi != null)
                        {
                            if (t.MinAgeMinutes > 0)
                            {
                                try { if (fi.LastWriteTime > cutoff) continue; } catch { }
                            }
                            if (mask != "*" && !MaskMatch(fi.Name, mask)) continue;
                            onFile(fi);
                        }
                        else if (t.Recurse && fsi is DirectoryInfo)
                        {
                            DirectoryInfo sub = (DirectoryInfo)fsi;
                            // Имя вида ".", "..", ".. " (с хвостовым пробелом или точкой)
                            // Win32 нормализует, отбрасывая хвост, — путь "X\.. " превращается
                            // в родителя X, и обход зацикливается навсегда. Такие каталоги
                            // создаются только через сырые NT-пути и легальными не бывают.
                            if (IsDotName(sub.Name)) continue;
                            if (depth >= MaxWalkDepth) continue;
                            if (dirsOut != null) dirsOut.Add(sub.FullName);
                            stack.Push(sub); depths.Push(depth + 1);
                        }
                    }
                }
            }
        }

        // Маска в стиле winapp2: "*.log", "thumbcache_*.db", "*".
        private static bool MaskMatch(string name, string mask)
        {
            if (mask == "*" || mask == "*.*") return true;
            int star = mask.IndexOf('*');
            if (star < 0) return string.Equals(name, mask, StringComparison.OrdinalIgnoreCase);
            string head = mask.Substring(0, star);
            string tail = mask.Substring(star + 1);
            if (tail.IndexOf('*') >= 0)
            {
                // несколько звёздочек — сводим к «содержит все куски по порядку»
                string[] parts = mask.Split('*');
                int pos = 0;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Length == 0) continue;
                    int at = name.IndexOf(parts[i], pos, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) return false;
                    if (i == 0 && at != 0) return false;
                    pos = at + parts[i].Length;
                }
                string last = parts[parts.Length - 1];
                if (last.Length > 0 && !name.EndsWith(last, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            if (name.Length < head.Length + tail.Length) return false;
            return name.StartsWith(head, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
        }

        public void AnalyzeCategory(CleanCategory c)
        {
            long total = 0; int cnt = 0; int errors = 0;
            foreach (CleanTarget t in c.Targets)
            {
                if (_cancelDisk) break;
                if (!IsAllowedTarget(t.Path)) continue;
                Walk(t, delegate(FileInfo fi)
                {
                    try { total += fi.Length; cnt++; } catch { }
                }, null, ref errors);
            }
            if (c.RecycleBin)
            {
                Native.SHQUERYRBINFO info = new Native.SHQUERYRBINFO();
                info.cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO));
                try { if (Native.SHQueryRecycleBin(null, ref info) == 0) { total += info.i64Size; cnt += (int)info.i64NumItems; } }
                catch { }
            }
            c.Size = total; c.FileCount = cnt; c.Analyzed = !_cancelDisk;
            c.Note = errors > 0
                ? Tr.S("часть папок недоступна (" + errors + ")", errors + " folder(s) not accessible")
                : null;
        }

        // Анализ категорий параллельно: узкое место — задержки ФС, а не CPU,
        // поэтому несколько потоков дают кратный выигрыш на холодном кэше.
        public void AnalyzeCategories(List<CleanCategory> cats, Action<CleanCategory> onDone)
        {
            if (cats == null || cats.Count == 0) return;
            int next = -1;
            int workers = Math.Min(cats.Count, Math.Max(2, Environment.ProcessorCount));
            if (workers > 8) workers = 8;
            Thread[] pool = new Thread[workers];
            object gate = new object();

            for (int i = 0; i < workers; i++)
            {
                pool[i] = new Thread(delegate()
                {
                    while (true)
                    {
                        int idx = Interlocked.Increment(ref next);
                        if (idx >= cats.Count || _cancelDisk) return;
                        CleanCategory c = cats[idx];
                        try { AnalyzeCategory(c); } catch { }
                        if (onDone != null) { lock (gate) { try { onDone(c); } catch { } } }
                    }
                });
                pool[i].IsBackground = true;
                pool[i].Start();
            }
            foreach (Thread t in pool) t.Join();
        }

        // Предохранитель: не удаляем корни дисков, ключевые системные папки целиком,
        // папки с данными пользователя и всё, что он сам внёс в исключения.
        private static readonly string[] _neverTouch = new string[] {
            "\\windows\\system32", "\\windows\\syswow64", "\\windows\\winsxs",
            "\\windows\\system32\\drivers", "\\windows\\fonts",
            "\\system volume information", "\\$recycle.bin",
            "\\windows\\system32\\config", "\\windows\\assembly",
            "\\windows\\servicing", "\\windows\\boot", "\\windows\\inf",
        };

        private bool IsAllowedTarget(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p;
            try { p = Path.GetFullPath(path).TrimEnd('\\'); } catch { return false; }
            if (p.Length < 4) return false;

            string root = (Path.GetPathRoot(p) ?? "").TrimEnd('\\');
            if (string.Equals(p, root, StringComparison.OrdinalIgnoreCase)) return false;

            string pl = p.ToLowerInvariant();
            if (pl == _winDir.ToLowerInvariant()) return false;
            if (!string.IsNullOrEmpty(_programFiles) && pl == _programFiles.ToLowerInvariant()) return false;
            if (!string.IsNullOrEmpty(_programFilesX86) && pl == _programFilesX86.ToLowerInvariant()) return false;

            foreach (string bad in _neverTouch)
                if (pl == root.ToLowerInvariant() + bad || pl.EndsWith(bad)) return false;

            // папки с данными, которые чистилка не должна затрагивать даже по ошибке в правиле
            if (IsUserDataRoot(pl)) return false;

            // никогда не чистим собственный каталог данных (там конфиг, история, логи)
            if (pl.StartsWith(_dir.ToLowerInvariant())) return false;

            foreach (string ex in Config.CleanExclude)
            {
                if (string.IsNullOrEmpty(ex)) continue;
                string exl;
                try { exl = Path.GetFullPath(ex.Trim()).TrimEnd('\\').ToLowerInvariant(); } catch { continue; }
                if (exl.Length == 0) continue;
                if (pl == exl || pl.StartsWith(exl + "\\")) return false;
            }
            return true;
        }

        private bool IsUserDataRoot(string pathLower)
        {
            Environment.SpecialFolder[] guarded = new Environment.SpecialFolder[] {
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.System,
            };
            foreach (Environment.SpecialFolder sf in guarded)
            {
                string f;
                try { f = Environment.GetFolderPath(sf); } catch { continue; }
                if (string.IsNullOrEmpty(f)) continue;
                if (pathLower == f.TrimEnd('\\').ToLowerInvariant()) return true;
            }
            return false;
        }

        // Удаление одной цели потоково: файл удаляется сразу, как только найден.
        private long DeleteTarget(CleanTarget t, CleanResult res)
        {
            long freed = 0;
            if (!Directory.Exists(t.Path)) return 0;

            List<string> dirs = new List<string>();
            int errors = 0;
            int deleted = 0;
            Walk(t, delegate(FileInfo fi)
            {
                try
                {
                    long l = fi.Length;
                    if ((fi.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        try { fi.Attributes = FileAttributes.Normal; } catch { }
                    }
                    fi.Delete();
                    freed += l;
                    deleted++;
                }
                catch { errors++; }   // занят другим процессом или нет прав — это норма
            }, dirs, ref errors);

            // подпапки — от самых глубоких к верхним; только пустые уйдут
            if (string.IsNullOrEmpty(t.Mask))
            {
                dirs.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });
                foreach (string d in dirs) { try { Directory.Delete(d, false); } catch { } }
                if (!t.ContentsOnly) { try { Directory.Delete(t.Path, false); } catch { } }
            }

            res.Errors += errors;
            res.FilesDeleted += deleted;
            return freed;
        }

        public CleanResult CleanCategories(List<CleanCategory> cats)
        {
            CleanResult res = new CleanResult();
            if (cats == null) return res;

            foreach (CleanCategory c in cats)
            {
                if (_cancelDisk) break;
                long catFreed = 0;
                foreach (CleanTarget t in c.Targets)
                {
                    if (_cancelDisk) break;
                    if (!IsAllowedTarget(t.Path))
                    {
                        res.Log.Add("SKIP (guard) " + t.Path);
                        continue;
                    }
                    long f = DeleteTarget(t, res);
                    catFreed += f;
                    res.Log.Add(FormatBytes(f).PadLeft(10) + "  " + t.Path
                                + (string.IsNullOrEmpty(t.Mask) ? "" : "  [" + t.Mask + "]"));
                }
                if (c.RecycleBin && !_cancelDisk)
                {
                    long binSize = 0;
                    Native.SHQUERYRBINFO info = new Native.SHQUERYRBINFO();
                    info.cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO));
                    try { if (Native.SHQueryRecycleBin(null, ref info) == 0) binSize = info.i64Size; }
                    catch { }
                    try
                    {
                        Native.SHEmptyRecycleBin(IntPtr.Zero, null,
                            Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
                        catFreed += binSize;
                        res.Log.Add(FormatBytes(binSize).PadLeft(10) + "  " + Tr.S("Корзина", "Recycle Bin"));
                    }
                    catch { }
                }
                res.Freed += catFreed;
                res.Log.Add("--- " + c.Title + ": " + FormatBytes(catFreed));
            }

            if (Config.CleanLogEnabled) WriteCleanLog(res);
            return res;
        }

        // Лог очистки — как в FluentCleaner: видно, что именно и сколько было удалено.
        private void WriteCleanLog(CleanResult res)
        {
            try
            {
                string path = Path.Combine(_dir, "clean-" + DateTime.Now.ToString("yyyy-MM") + ".log");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                foreach (string line in res.Log) sb.AppendLine(line);
                sb.AppendLine("TOTAL " + FormatBytes(res.Freed) + "  files=" + res.FilesDeleted
                              + "  skipped=" + res.Errors);
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public string CleanLogPath
        {
            get { return Path.Combine(_dir, "clean-" + DateTime.Now.ToString("yyyy-MM") + ".log"); }
        }

        // ================= ДЕИНСТАЛЛЯЦИЯ ПРОГРАММ =================
        private readonly Dictionary<string, string[]> _installExes =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        // ---------- Обновления программ (winget / Chocolatey) ----------
        //
        // Почему именно так:
        //  * winget — это официальная база Microsoft (winget-pkgs, десятки тысяч манифестов).
        //    Он сам сопоставляет установленную программу из "Установка и удаление" с пакетом,
        //    поэтому сравнение версий делает он, а не мы. Своя база версий была бы заведомо
        //    менее точной и быстро устаревала.
        //  * Chocolatey добавляется как второй источник, если он установлен: там есть пакеты,
        //    которых нет в winget.
        //  * Реестр не правим, ничего не скачиваем сами — обновление выполняет менеджер пакетов.
        //
        // Машинно-читаемого вывода у `winget upgrade` нет (проверено на 1.29: только таблица),
        // поэтому таблица разбирается по НАЧАЛАМ КОЛОНОК из строки заголовка. Разбиение по
        // пробелам здесь неприменимо: в реальном выводе колонки регулярно разделены одним
        // пробелом (длинное имя, версия вида "26183.1903.4892.4448"), а сами версии бывают
        // с пробелом внутри ("7.0.6 (43848)", "< 17.14.35"). Колонки берутся ПО ПОРЯДКУ,
        // а не по именам заголовков, — иначе локализованный winget не распознаётся.
        private volatile bool _cancelUpdates;
        public void CancelUpdateWork() { _cancelUpdates = true; }
        public void ResetUpdateCancel() { _cancelUpdates = false; }
        public bool UpdatesCancelled { get { return _cancelUpdates; } }

        public string UpdateLogPath
        {
            get { return Path.Combine(_dir, "updates-" + DateTime.Now.ToString("yyyy-MM") + ".log"); }
        }

        private static bool ToolAvailable(string exe, string args)
        {
            string so; int code;
            return RunCapture(exe, args, 20000, out so, out code) && code == 0;
        }

        private bool? _hasWinget, _hasChoco;
        public bool HasWinget
        {
            get
            {
                if (_hasWinget == null) _hasWinget = ToolAvailable("winget.exe", "--version");
                return _hasWinget.Value;
            }
        }
        public bool HasChoco
        {
            get
            {
                if (_hasChoco == null) _hasChoco = ToolAvailable("choco.exe", "--version");
                return _hasChoco.Value;
            }
        }

        // Запуск консольной утилиты с чтением stdout. stderr читается отдельным потоком:
        // если его не вычитывать, буфер трубы заполняется и процесс встаёт навсегда.
        private static bool RunCapture(string exe, string args, int timeoutMs, out string stdout, out int exitCode)
        {
            stdout = string.Empty;
            exitCode = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                // winget иначе рисует прогресс-спиннер и ждёт нажатий
                psi.EnvironmentVariables["WINGET_DISABLE_INTERACTIVITY"] = "1";
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    StringBuilder err = new StringBuilder();
                    Thread drain = new Thread(delegate()
                    {
                        try { err.Append(p.StandardError.ReadToEnd()); } catch { }
                    });
                    drain.IsBackground = true;
                    drain.Start();

                    string outText = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        stdout = outText;
                        return false;
                    }
                    try { drain.Join(2000); } catch { }
                    exitCode = p.ExitCode;
                    stdout = outText;
                    if (stdout.Length == 0 && err.Length > 0) stdout = err.ToString();
                    return true;
                }
            }
            catch { return false; }   // утилиты нет в PATH — это нормально
        }

        // Начала колонок в строке заголовка: колонка начинается там, где после
        // разрыва в 2+ пробела снова идёт непробельный символ.
        public static List<int> ColumnStarts(string header)
        {
            List<int> starts = new List<int>();
            if (string.IsNullOrEmpty(header)) return starts;
            starts.Add(0);
            int i = 0;
            while (i < header.Length)
            {
                if (header[i] == ' ')
                {
                    int j = i;
                    while (j < header.Length && header[j] == ' ') j++;
                    if (j - i >= 2 && j < header.Length) starts.Add(j);
                    i = j;
                }
                else i++;
            }
            return starts;
        }

        private static string Slice(string line, List<int> starts, int col)
        {
            if (col >= starts.Count) return string.Empty;
            int a = starts[col];
            if (a >= line.Length) return string.Empty;
            int b = (col + 1 < starts.Count) ? starts[col + 1] : line.Length;
            if (b > line.Length) b = line.Length;
            if (b <= a) return string.Empty;
            return line.Substring(a, b - a).Trim();
        }

        public static List<UpdateItem> ParseWingetTable(string text)
        {
            List<UpdateItem> list = new List<UpdateItem>();
            if (string.IsNullOrEmpty(text)) return list;

            string[] raw = text.Replace("\r", "\n").Split('\n');
            List<int> starts = null;
            string prev = null;

            foreach (string line in raw)
            {
                if (line.Trim().Length == 0) continue;

                // строка-разделитель из дефисов: колонки берём из предыдущей строки
                string t = line.Trim();
                if (starts == null && t.Length > 10 && t.Replace("-", "").Length == 0)
                {
                    starts = ColumnStarts(prev);
                    if (starts.Count < 4) starts = null;   // не та таблица
                    continue;
                }
                prev = line;
                if (starts == null) continue;

                string id = Slice(line, starts, 1);
                // Хвост вида "35 upgrades available." и заметки про пины колонок не имеют
                if (id.Length == 0 || id.IndexOf(' ') >= 0) continue;
                string name = Slice(line, starts, 0);
                string cur = Slice(line, starts, 2);
                string avail = Slice(line, starts, 3);
                if (name.Length == 0 || avail.Length == 0) continue;

                UpdateItem u = new UpdateItem();
                u.Name = name;
                u.Id = id;
                u.Current = cur;
                u.Available = avail;
                u.Manager = "winget";
                u.Source = starts.Count > 4 ? Slice(line, starts, 4) : "winget";
                list.Add(u);
            }
            return list;
        }

        public static List<UpdateItem> ParseChocoOutdated(string text)
        {
            List<UpdateItem> list = new List<UpdateItem>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string line in text.Replace("\r", "\n").Split('\n'))
            {
                // формат -r: name|current|available|pinned
                string[] p = line.Split('|');
                if (p.Length < 4) continue;
                string name = p[0].Trim();
                if (name.Length == 0 || name.IndexOf(' ') >= 0) continue;
                bool pinned;
                if (!bool.TryParse(p[3].Trim(), out pinned)) continue;   // отсекает заголовки/мусор
                if (pinned) continue;
                if (p[1].Trim() == p[2].Trim()) continue;

                UpdateItem u = new UpdateItem();
                u.Name = name;
                u.Id = name;
                u.Current = p[1].Trim();
                u.Available = p[2].Trim();
                u.Manager = "choco";
                u.Source = "chocolatey";
                list.Add(u);
            }
            return list;
        }

        // Сопоставление choco-пакета с winget-пакетом: сравниваем нормализованное имя
        // с последним сегментом winget-Id (Graphviz.Graphviz -> graphviz). Совпало — помечаем
        // дублем, но НЕ скрываем: решение остаётся за пользователем.
        private static string NormalizeKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // Один и тот же софт в winget и choco зовётся по-разному:
        // Graphviz.Graphviz/graphviz, Python.Python.3.14/python314, python3,
        // Microsoft.VCRedist.2015+.x64/vcredist140. Точного маппинга между
        // менеджерами не существует (его нет и в UniGetUI), поэтому сравниваем
        // нормализованные строки: имя choco против всего Id и против каждого
        // сегмента Id, в обе стороны. Порог 5 символов отсекает мусорные
        // совпадения вроде "x64" и "2015".
        // Ошибка здесь безопасна в обе стороны: пометка «дубль» только
        // предупреждает и исключает строку из кнопки «Все» — ничего не удаляет
        // и не блокирует ручную отметку.
        // Токены разрядности/года совпадают у чего угодно и значат не софт, а вариант
        // сборки: сами по себе они не признак одного и того же пакета.
        private static readonly string[] _archTokens = { "x64", "x86", "arm64", "arm", "win32", "win64", "amd64" };

        private static bool IsJunkToken(string k)
        {
            if (k.Length == 0) return true;
            bool allDigits = true;
            foreach (char c in k) if (!char.IsDigit(c)) { allDigits = false; break; }
            if (allDigits) return true;
            foreach (string t in _archTokens) if (k == t) return true;
            return false;
        }

        // Версии в реальном выводе бывают "7.0.6 (43848)", "< 17.14.35", "1.29.279.0",
        // "26.00", "v2.5.1", "Unknown". Берём числовые группы по порядку и игнорируем
        // всё остальное: сравнивать нужно только цифры.
        public static int[] ParseVersionParts(string v)
        {
            if (string.IsNullOrEmpty(v)) return new int[0];
            if (v.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0) return new int[0];
            List<int> parts = new List<int>();
            int i = 0;
            while (i < v.Length && parts.Count < 6)
            {
                if (!char.IsDigit(v[i])) { i++; continue; }
                int start = i;
                while (i < v.Length && char.IsDigit(v[i])) i++;
                string num = v.Substring(start, i - start);
                if (num.Length > 9) num = num.Substring(0, 9);   // защита от переполнения
                int n;
                if (int.TryParse(num, out n)) parts.Add(n);
            }
            return parts.ToArray();
        }

        private static int PartAt(int[] a, int i) { return i < a.Length ? a[i] : 0; }

        // "<" в текущей версии значит «winget не знает точную» — доверия к сравнению нет.
        public static void ClassifySeverity(UpdateItem u)
        {
            if (u == null) return;
            int[] cur = ParseVersionParts(u.Current);
            int[] av = ParseVersionParts(u.Available);
            bool fuzzy = !string.IsNullOrEmpty(u.Current) && u.Current.IndexOf('<') >= 0;

            if (cur.Length == 0 || av.Length == 0 || fuzzy)
            {
                u.SeverityLevel = 0;
                u.SeverityText = Tr.S("неизвестно", "unknown");
                return;
            }

            if (PartAt(av, 0) != PartAt(cur, 0))
            {
                u.SeverityLevel = 3;
                u.SeverityText = Tr.S("крупное", "major");
                return;
            }
            // Semver 0.x: там ломающие изменения выходят во второй позиции, а не в первой.
            if (PartAt(cur, 0) == 0 && PartAt(av, 1) != PartAt(cur, 1))
            {
                u.SeverityLevel = 3;
                u.SeverityText = Tr.S("крупное", "major");
                return;
            }
            if (PartAt(av, 1) != PartAt(cur, 1))
            {
                u.SeverityLevel = 2;
                u.SeverityText = Tr.S("среднее", "minor");
                return;
            }
            u.SeverityLevel = 1;
            u.SeverityText = Tr.S("мелкое", "patch");
        }

        public static bool LooksLikeSameSoftware(string chocoName, List<string> wingetIds)
        {
            string c = NormalizeKey(chocoName);
            if (c.Length < 3 || wingetIds == null || IsJunkToken(c)) return false;
            foreach (string id in wingetIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                string full = NormalizeKey(id);
                if (full.Length == 0) continue;
                if (full == c) return true;
                if (c.Length >= 5 && full.IndexOf(c, StringComparison.Ordinal) >= 0) return true;
                foreach (string part in id.Split('.'))
                {
                    string p = NormalizeKey(part);
                    if (IsJunkToken(p)) continue;
                    if (p == c) return true;
                    if (p.Length >= 5 && c.IndexOf(p, StringComparison.Ordinal) >= 0) return true;
                }
            }
            return false;
        }

        public List<UpdateItem> ScanUpdates(out string note)
        {
            note = null;
            List<UpdateItem> all = new List<UpdateItem>();
            List<string> notes = new List<string>();

            if (HasWinget)
            {
                string args = "upgrade --accept-source-agreements --disable-interactivity";
                if (Config.UpdateIncludeUnknown) args += " --include-unknown";
                string so; int code;
                if (RunCapture("winget.exe", args, 300000, out so, out code))
                    all.AddRange(ParseWingetTable(so));
                else
                    notes.Add(Tr.S("winget не ответил вовремя", "winget timed out"));
            }
            else notes.Add(Tr.S("winget не найден", "winget not found"));

            if (_cancelUpdates) { note = Tr.S("Отменено", "Cancelled"); return all; }

            if (Config.UpdateUseChoco && HasChoco)
            {
                string so; int code;
                // choco outdated возвращает 2, когда обновления есть — это не ошибка
                if (RunCapture("choco.exe", "outdated -r --limit-output --no-color", 300000, out so, out code))
                {
                    List<UpdateItem> ch = ParseChocoOutdated(so);
                    List<string> wingetIds = new List<string>();
                    foreach (UpdateItem w in all) wingetIds.Add(w.Id);
                    foreach (UpdateItem c in ch)
                        c.Duplicate = LooksLikeSameSoftware(c.Name, wingetIds);
                    all.AddRange(ch);
                }
                else notes.Add(Tr.S("choco не ответил вовремя", "choco timed out"));
            }

            // исключения пользователя
            if (Config.UpdateExclude != null && Config.UpdateExclude.Count > 0)
            {
                Dictionary<string, bool> skip = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (string s in Config.UpdateExclude)
                {
                    string v = (s ?? "").Trim();
                    if (v.Length > 0) skip[v] = true;
                }
                List<UpdateItem> kept = new List<UpdateItem>();
                foreach (UpdateItem u in all)
                    if (!skip.ContainsKey(u.Id) && !skip.ContainsKey(u.Name)) kept.Add(u);
                all = kept;
            }

            foreach (UpdateItem u in all) ClassifySeverity(u);

            // Крупные наверх: список длинный, и то, что меняет мажорную версию,
            // пользователь должен увидеть без прокрутки.
            all.Sort(delegate(UpdateItem a, UpdateItem b)
            {
                if (a.SeverityLevel != b.SeverityLevel) return b.SeverityLevel - a.SeverityLevel;
                int d = string.Compare(a.Manager, b.Manager, StringComparison.OrdinalIgnoreCase);
                if (d != 0) return d;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            if (notes.Count > 0) note = string.Join(" · ", notes.ToArray());
            return all;
        }

        // Обновление одного пакета силами самого менеджера. Возвращает true при успехе.
        public bool ApplyUpdate(UpdateItem u, out string message)
        {
            message = null;
            if (u == null) return false;
            string exe, args;
            if (u.Manager == "choco")
            {
                exe = "choco.exe";
                args = "upgrade " + u.Id + " -y --no-progress --limit-output";
            }
            else
            {
                exe = "winget.exe";
                args = "upgrade --id " + u.Id + " --exact --silent --disable-interactivity"
                     + " --accept-package-agreements --accept-source-agreements";
                // без этого пакеты с Current=Unknown winget обновлять отказывается
                if (string.IsNullOrEmpty(u.Current) ||
                    u.Current.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) >= 0)
                    args += " --include-unknown";
            }

            string so; int code;
            bool finished = RunCapture(exe, args, 1800000, out so, out code);   // 30 мин на установщик
            if (!finished)
            {
                message = Tr.S("превышено время ожидания", "timed out");
                u.Status = message;
                u.LastOk = false;
                AppendUpdateLog(u, false, message);
                return false;
            }
            bool ok = code == 0;
            if (!ok)
            {
                string tail = LastMeaningfulLine(so);
                message = Tr.S("код ", "exit ") + code + (tail.Length > 0 ? ": " + tail : "");
            }
            else message = Tr.S("обновлено до ", "updated to ") + u.Available;
            u.Status = message;
            u.LastOk = ok;
            AppendUpdateLog(u, ok, message);
            return ok;
        }

        // Что менеджер СЕЙЧАС считает устаревшим: ключ → доступная версия.
        // Нужно для проверки результата группового обновления.
        private Dictionary<string, string> QueryOutdatedMap(string manager)
        {
            string so; int code;
            List<UpdateItem> list;
            if (manager == "choco")
            {
                if (!RunCapture("choco.exe", "outdated -r --limit-output --no-color", 300000, out so, out code))
                    return null;
                list = ParseChocoOutdated(so);
            }
            else
            {
                string a = "upgrade --accept-source-agreements --disable-interactivity";
                if (Config.UpdateIncludeUnknown) a += " --include-unknown";
                if (!RunCapture("winget.exe", a, 300000, out so, out code)) return null;
                list = ParseWingetTable(so);
            }
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (UpdateItem u in list) map[u.Id] = u.Available;
            return map;
        }

        // Разбивка выбранного на команды. Смешивать менеджеров в одной команде нельзя,
        // поэтому группируем сначала по менеджеру, потом нарезаем по batch. Порядок
        // внутри менеджера сохраняем — он уже отсортирован по важности.
        public static List<List<UpdateItem>> BuildUpdateGroups(List<UpdateItem> sel, int batch)
        {
            List<List<UpdateItem>> groups = new List<List<UpdateItem>>();
            if (sel == null || sel.Count == 0) return groups;
            if (batch < 1) batch = 1;

            List<string> managers = new List<string>();
            foreach (UpdateItem u in sel)
            {
                string m = u.Manager ?? "";
                if (!managers.Contains(m)) managers.Add(m);
            }
            foreach (string mgr in managers)
            {
                List<UpdateItem> ofMgr = new List<UpdateItem>();
                foreach (UpdateItem u in sel) if ((u.Manager ?? "") == mgr) ofMgr.Add(u);
                for (int i = 0; i < ofMgr.Count; i += batch)
                {
                    List<UpdateItem> g = new List<UpdateItem>();
                    for (int j = i; j < ofMgr.Count && j < i + batch; j++) g.Add(ofMgr[j]);
                    groups.Add(g);
                }
            }
            return groups;
        }

        private static string QuoteArg(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return "\"" + s.Replace("\"", "") + "\"";
        }

        // Групповое обновление: менеджеру отдаётся сразу несколько пакетов одной
        // командой — оба это принимают (проверено живьём: `winget upgrade <q1> <q2>`,
        // `choco upgrade a b`).
        //
        // Настоящей ПАРАЛЛЕЛЬНОСТИ здесь нет и быть не может: Windows Installer держит
        // машинный мьютекс _MSIExecute, а Chocolatey — свой глобальный лок, поэтому два
        // установщика, запущенных одновременно, просто отвалятся с ошибкой. Внутри
        // одной команды менеджер ставит пакеты по очереди сам; выигрыш — старт процесса
        // и загрузка индекса источника один раз на группу, а не на каждый пакет.
        //
        // Результат по каждому пакету НЕ вытаскиваем из вывода: он локализован (у
        // пользователя winget отвечает по-русски) и его формат не документирован.
        // Вместо этого повторно спрашиваем менеджер, что ещё устарело, и сверяем —
        // это верно в любой локали. Возвращает число успешно обновлённых, статус
        // каждого пакета кладёт в u.Status.
        public int ApplyUpdateBatch(List<UpdateItem> group, out string groupMessage)
        {
            groupMessage = null;
            if (group == null || group.Count == 0) return 0;

            string manager = group[0].Manager;
            StringBuilder ids = new StringBuilder();
            bool anyUnknown = false;
            foreach (UpdateItem u in group)
            {
                ids.Append(' ').Append(QuoteArg(u.Id));
                if (string.IsNullOrEmpty(u.Current) ||
                    u.Current.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) >= 0) anyUnknown = true;
            }

            string exe, args;
            if (manager == "choco")
            {
                exe = "choco.exe";
                args = "upgrade" + ids + " -y --no-progress --limit-output";
            }
            else
            {
                exe = "winget.exe";
                args = "upgrade --exact --silent --disable-interactivity"
                     + " --accept-package-agreements --accept-source-agreements" + ids;
                if (anyUnknown) args += " --include-unknown";
            }

            // 30 мин на первый установщик + 15 на каждый следующий, но не больше 2 часов
            int timeout = 1800000 + 900000 * (group.Count - 1);
            if (timeout > 7200000) timeout = 7200000;

            string so; int code;
            bool finished = RunCapture(exe, args, timeout, out so, out code);
            if (!finished)
            {
                groupMessage = Tr.S("превышено время ожидания", "timed out");
                foreach (UpdateItem u in group)
                {
                    u.Status = groupMessage;
                    u.LastOk = false;
                    AppendUpdateLog(u, false, groupMessage);
                }
                return 0;
            }

            Dictionary<string, string> still = QueryOutdatedMap(manager);
            int ok = 0;

            if (still == null)
            {
                // Проверить не смогли — честно говорим это, а не выдаём код за успех.
                bool good = code == 0;
                groupMessage = Tr.S("код ", "exit ") + code;
                foreach (UpdateItem u in group)
                {
                    u.Status = good
                        ? Tr.S("вероятно обновлено (проверка недоступна)", "probably updated (verify unavailable)")
                        : Tr.S("код ", "exit ") + code + ": " + LastMeaningfulLine(so);
                    u.LastOk = good;
                    if (good) ok++;
                    AppendUpdateLog(u, good, u.Status);
                }
                return ok;
            }

            foreach (UpdateItem u in group)
            {
                string left;
                bool listed = still.TryGetValue(u.Id, out left);
                bool good;
                if (!listed) good = true;                                   // пропал из списка устаревших
                else if (!string.Equals(left, u.Available, StringComparison.OrdinalIgnoreCase))
                    good = true;                                            // подтянулся, но вышла ещё новее
                else good = false;                                          // так и остался на старой

                u.Status = good
                    ? Tr.S("обновлено до ", "updated to ") + u.Available
                    : Tr.S("не обновилось (осталось ", "not updated (still ") + u.Current + ")";
                u.LastOk = good;
                if (good) ok++;
                AppendUpdateLog(u, good, u.Status);
            }

            groupMessage = Tr.S("обновлено ", "updated ") + ok + "/" + group.Count;
            return ok;
        }

        private static string LastMeaningfulLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string[] lines = text.Replace("\r", "\n").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string l = lines[i].Trim();
                if (l.Length > 0) return l.Length > 160 ? l.Substring(0, 160) : l;
            }
            return string.Empty;
        }

        private void AppendUpdateLog(UpdateItem u, bool ok, string message)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ");
                sb.Append(ok ? "OK   " : "FAIL ");
                sb.Append(u.Manager).Append("  ").Append(u.Id).Append("  ");
                sb.Append(u.Current).Append(" -> ").Append(u.Available);
                if (!string.IsNullOrEmpty(message)) sb.Append("  ").Append(message);
                File.AppendAllText(UpdateLogPath, sb.ToString() + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

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

        public void RunUninstall(InstalledApp app)
        {
            string cmd = app.UninstallCmd;
            if (string.IsNullOrEmpty(cmd)) return;
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + cmd);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
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
                        string key = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                        foreach (string e in exes)
                        {
                            string fn = new string(Path.GetFileNameWithoutExtension(e).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                            if (key.Length > 2 && (key.Contains(fn) || fn.Contains(key))) return e;
                        }
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

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunKeyPathWow = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";

        public List<AutostartEntry> GetAutostartEntries()
        {
            List<AutostartEntry> list = new List<AutostartEntry>();
            ReadRun(Registry.CurrentUser, RunKeyPath, 0, "HKCU\\Run", list);
            ReadRun(Registry.LocalMachine, RunKeyPath, 1, "HKLM\\Run", list);
            ReadRun(Registry.LocalMachine, RunKeyPathWow, 2, "HKLM\\Run (32-bit)", list);
            ReadStartupFolder(Environment.SpecialFolder.Startup, 3, "Автозагрузка (пользователь)", list);
            ReadStartupFolder(Environment.SpecialFolder.CommonStartup, 4, "Автозагрузка (общая)", list);
            return list;
        }

        private void ReadRun(RegistryKey root, string sub, int kind, string label, List<AutostartEntry> list)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(sub))
                {
                    if (k == null) return;
                    foreach (string name in k.GetValueNames())
                    {
                        string cmd = k.GetValue(name) as string;
                        if (string.IsNullOrEmpty(cmd)) continue;
                        AutostartEntry e = new AutostartEntry();
                        e.Name = name; e.Command = cmd; e.ExePath = ParseExeFromCommand(cmd);
                        e.Kind = kind; e.SourceLabel = label; e.RegName = name;
                        list.Add(e);
                    }
                }
            }
            catch { }
        }

        private void ReadStartupFolder(Environment.SpecialFolder folder, int kind, string label, List<AutostartEntry> list)
        {
            try
            {
                string dir = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".ini") continue;
                    string target = ext == ".lnk" ? ResolveLnk(f) : f;
                    AutostartEntry e = new AutostartEntry();
                    e.Name = Path.GetFileNameWithoutExtension(f);
                    e.Command = target != null ? target : f;
                    e.ExePath = target; e.Kind = kind; e.SourceLabel = label; e.LnkPath = f;
                    list.Add(e);
                }
            }
            catch { }
        }

        private static string NormPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            try { return Path.GetFullPath(p).TrimEnd('\\').ToLowerInvariant(); }
            catch { return p.Trim().TrimEnd('\\').ToLowerInvariant(); }
        }

        public bool IsExeInAutostart(string exe, List<AutostartEntry> entries)
        {
            string np = NormPath(exe);
            if (np == null) return false;
            foreach (AutostartEntry e in entries)
            {
                string ep = NormPath(e.ExePath);
                if (ep != null && ep == np) return true;
            }
            return false;
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "App";
            string s = name.Replace('\\', ' ').Replace('/', ' ').Trim();
            if (s.Length > 60) s = s.Substring(0, 60);
            return s;
        }

        public void AddAutostart(string name, string exe)
        {
            if (string.IsNullOrEmpty(exe)) return;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k != null) k.SetValue(SanitizeName(name), "\"" + exe + "\"");
                }
            }
            catch { }
        }

        public void RemoveAutostart(string exe, string name)
        {
            RemoveFromRun(Registry.CurrentUser, RunKeyPath, exe, name);
            RemoveFromRun(Registry.LocalMachine, RunKeyPath, exe, name);
            RemoveFromRun(Registry.LocalMachine, RunKeyPathWow, exe, name);
            RemoveStartupLnk(Environment.SpecialFolder.Startup, exe);
            RemoveStartupLnk(Environment.SpecialFolder.CommonStartup, exe);
        }

        private void RemoveFromRun(RegistryKey root, string sub, string exe, string name)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(sub, true))
                {
                    if (k == null) return;
                    string np = NormPath(exe);
                    string san = SanitizeName(name);
                    List<string> toDelete = new List<string>();
                    foreach (string vn in k.GetValueNames())
                    {
                        string cmd = k.GetValue(vn) as string;
                        string ep = NormPath(ParseExeFromCommand(cmd));
                        if ((np != null && ep == np) || vn == san) toDelete.Add(vn);
                    }
                    foreach (string vn in toDelete) k.DeleteValue(vn, false);
                }
            }
            catch { }
        }

        private void RemoveStartupLnk(Environment.SpecialFolder folder, string exe)
        {
            try
            {
                string dir = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                string np = NormPath(exe);
                foreach (string f in Directory.GetFiles(dir, "*.lnk"))
                {
                    string target = NormPath(ResolveLnk(f));
                    if (target != null && target == np) { try { File.Delete(f); } catch { } }
                }
            }
            catch { }
        }

        // ================= DOCKER =================
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
                Process p = Process.Start(psi);
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit(120000);
                exit = p.HasExited ? p.ExitCode : -1;
                string res = o;
                if (!string.IsNullOrEmpty(e)) res += (res.Length > 0 ? "\r\n" : "") + e;
                return res.Trim();
            }
            catch (Exception ex)
            {
                return "[ошибка] " + ex.Message +
                    "\r\nВозможно, CLI не установлен или отсутствует в PATH.";
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

        // ОДНА КНОПКА: очистить всё неиспользуемое -> остановить Docker ->
        // сжать vhdx (реально вернуть место Windows) -> перезапустить Docker.
        public string CompactDockerDisk()
        {
            StringBuilder sb = new StringBuilder();
            int ec;

            // проверка, что docker вообще есть
            string ver = RunCapture("docker", "version --format {{.Server.Version}}", out ec);
            if (ec != 0 && ver.IndexOf("[") >= 0)
                return Tr.S("Docker CLI не найден или демон не запущен.", "Docker CLI not found or the daemon is not running.");

            sb.AppendLine(Tr.S("=== Занято до очистки ===", "=== Usage before cleanup ==="));
            sb.AppendLine(Lf(RunCapture("docker", "system df", out ec)));
            sb.AppendLine();

            // 1) очистка всего неиспользуемого (контейнеры/образы/тома/сети/кэш сборки)
            sb.AppendLine(Tr.S("=== Очистка неиспользуемого ===", "=== Pruning unused ==="));
            sb.AppendLine("> docker system prune -a -f --volumes");
            sb.AppendLine(Lf(RunCapture("docker", "system prune -a -f --volumes", out ec)));
            sb.AppendLine("> docker builder prune -a -f");
            sb.AppendLine(Lf(RunCapture("docker", "builder prune -a -f", out ec)));
            sb.AppendLine();

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
                        Process.Start(psi);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        // ---------- Автозапуск с Windows ----------
        // Приложение запускается от администратора (requireAdministrator), поэтому
        // ключ реестра Run для автозапуска не годится (Windows не запускает из него
        // приложения с повышенными правами). Используем Планировщик задач с
        // наивысшими правами — тогда при входе в систему UAC не появляется.
        private const string TaskName = "WindowsProcessCleaner";

        public void ApplyAutostart(bool enabled)
        {
            // почистить возможный устаревший Run-ключ
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k != null && k.GetValue(TaskName) != null) k.DeleteValue(TaskName, false);
                }
            }
            catch { }

            try
            {
                string args;
                if (enabled)
                {
                    string exe = Application.ExecutablePath;
                    args = "/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exe +
                           "\\\" /tray\" /SC ONLOGON /RL HIGHEST /F";
                }
                else
                {
                    args = "/Delete /TN \"" + TaskName + "\" /F";
                }
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                if (p != null) p.WaitForExit(5000);
            }
            catch { }
        }

        public static string FormatBytes(long b)
        {
            double v = b;
            string[] u = Tr.En ? new string[] { "B", "KB", "MB", "GB", "TB" }
                                : new string[] { "Б", "КБ", "МБ", "ГБ", "ТБ" };
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString("0.0", CultureInfo.InvariantCulture) + " " + u[i];
        }
    }

    // ------------------------------------------------------------------ //
    //  Главная форма
    // ------------------------------------------------------------------ //
    // ListView с двойной буферизацией. Без неё owner-draw перерисовывает каждую ячейку
    // прямо на экране: при 300 строках и 11 колонках прокрутка мерцает и «залипает».
    // Нативный LVS_EX_DOUBLEBUFFER — единственный способ, который корректно работает
    // вместе с OwnerDraw (managed DoubleBuffered на ListView не даёт эффекта).
    public class FastListView : ListView
    {
        private const int LVM_FIRST = 0x1000;
        private const int LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;
        private const int LVS_EX_DOUBLEBUFFER = 0x00010000;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public FastListView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            View = View.Details;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                SendMessage(Handle, LVM_SETEXTENDEDLISTVIEWSTYLE,
                    new IntPtr(LVS_EX_DOUBLEBUFFER), new IntPtr(LVS_EX_DOUBLEBUFFER));
            }
            catch { }
        }
    }

    public class MainForm : Form
    {
        private readonly Engine _engine;
        private NotifyIcon _tray;
        private Icon _iconIdle, _iconActive;
        private System.Threading.Timer _monitor;            // тик мониторинга CPU (фоновый поток)
        private int _monitorBusy;                           // 0/1 — тик уже идёт, следующий пропускаем
        private int _scanBusy;                              // 0/1 — идёт сканирование процессов
        private int _purgeBusy;                             // 0/1 — идёт очистка памяти
        private int _autoBusy;                              // 0/1 — идёт автоочистка
        private volatile bool _closing;
        private System.Windows.Forms.Timer _autoTimer;      // автоочистка
        private DateTime _nextAuto = DateTime.MaxValue;
        private bool _reallyExit = false;

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
            BuildIcons();
            BuildUi();
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

        private void BuildIcons()
        {
            _iconIdle = MakeIcon(Color.FromArgb(58, 166, 85));    // зелёная — чисто
            _iconActive = MakeIcon(Color.FromArgb(224, 150, 40)); // оранжевая — есть кандидаты
            _iconWindow = MakeIcon(Color.FromArgb(45, 120, 224));  // синяя — иконка окна/панели задач
        }

        // Многоразмерная иконка из файла (крипче в трее/на панели задач); фолбэк — рисованная.
        private Icon LoadAppIcon()
        {
            try
            {
                string path = Path.Combine(Application.StartupPath, "icon.ico");
                if (File.Exists(path)) return new Icon(path);
            }
            catch { }
            return _iconWindow;
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Чёткая иконка с настоящим альфа-каналом: собираем многоразмерный .ico
        // из PNG (16..64px). GetHicon НЕ используем — он теряет прозрачность и даёт
        // чёрный ореол/невидимость в трее.
        private Icon MakeIcon(Color c)
        {
            int[] sizes = { 16, 20, 24, 32, 48, 64 };
            Bitmap[] bmps = new Bitmap[sizes.Length];
            for (int i = 0; i < sizes.Length; i++) bmps[i] = DrawIconBitmap(sizes[i], c);
            Icon ico = IconFromBitmaps(bmps);
            foreach (Bitmap b in bmps) b.Dispose();
            return ico;
        }

        private Bitmap DrawIconBitmap(int S, Color c)
        {
            Bitmap bmp = new Bitmap(S, S);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                int m = Math.Max(1, (int)Math.Round(S * 0.07));
                int rad = Math.Max(2, (int)Math.Round(S * 0.24));
                Rectangle rect = new Rectangle(m, m, S - 2 * m, S - 2 * m);
                using (GraphicsPath gp = RoundedRect(rect, rad))
                using (LinearGradientBrush br = new LinearGradientBrush(
                    rect, ControlPaint.Light(c, 0.28f), ControlPaint.Dark(c, 0.10f), 90f))
                    g.FillPath(br, gp);
                using (Pen p = new Pen(Color.White, Math.Max(1.4f, S * 0.11f)))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                    g.DrawLines(p, new PointF[] {
                        new PointF(S * 0.30f, S * 0.53f),
                        new PointF(S * 0.44f, S * 0.68f),
                        new PointF(S * 0.72f, S * 0.33f) });
                }
            }
            return bmp;
        }

        // Сборка .ico из набора PNG (сохраняет альфу) и создание Icon из потока.
        private static Icon IconFromBitmaps(Bitmap[] sizes)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter bw = new BinaryWriter(ms);
                bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
                byte[][] pngs = new byte[sizes.Length][];
                for (int i = 0; i < sizes.Length; i++)
                {
                    using (MemoryStream s = new MemoryStream())
                    {
                        sizes[i].Save(s, ImageFormat.Png);
                        pngs[i] = s.ToArray();
                    }
                }
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int S = sizes[i].Width;
                    bw.Write((byte)(S >= 256 ? 0 : S));
                    bw.Write((byte)(S >= 256 ? 0 : S));
                    bw.Write((byte)0); bw.Write((byte)0);
                    bw.Write((ushort)1); bw.Write((ushort)32);
                    bw.Write((uint)pngs[i].Length);
                    bw.Write((uint)offset);
                    offset += pngs[i].Length;
                }
                for (int i = 0; i < sizes.Length; i++) bw.Write(pngs[i]);
                bw.Flush();
                ms.Position = 0;
                return new Icon(ms);
            }
        }

        // ---------- Красивая отрисовка таблиц (owner-draw под тему) ----------
        private void SetupOwnerDraw(ListView lv)
        {
            lv.OwnerDraw = true;
            lv.GridLines = false;
            lv.ShowItemToolTips = true; // полный текст обрезанных ячеек по наведению
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.DrawColumnHeader += Lv_DrawColumnHeader;
            lv.DrawItem += delegate(object s, DrawListViewItemEventArgs e) { e.DrawDefault = false; };
            lv.DrawSubItem += Lv_DrawSubItem;
            // последняя колонка занимает всю оставшуюся ширину — без белой "добивки" заголовка
            lv.Resize += delegate { AutoFillLastColumn(lv); };
        }

        private bool _inAutoFill, _autoFillPending;
        private readonly List<ListView> _autoFillQueue = new List<ListView>();

        private void AutoFillLastColumn(ListView lv) { AutoFillLastColumn(lv, false); }

        // force = «дёрнуть ширину дважды». Common controls не сбрасывают уже
        // выставленный WS_HSCROLL сами: замерено — при colSum=995 и client=997 полоса
        // продолжала висеть, и исчезала только после сообщения о смене ширины колонки.
        // В обработчике Resize это не нужно (там ширина и так меняется) и вызвало бы
        // лишнюю перерисовку на каждый пиксель растягивания окна.
        private void AutoFillLastColumn(ListView lv, bool force)
        {
            if (lv == null || lv.Columns.Count == 0) return;
            // Width = -2 сам вызывает Resize; без флага обработчик Resize уходит в
            // рекурсивный шторм пересчётов ширины при каждом растягивании окна.
            if (_inAutoFill) return;
            _inAutoFill = true;
            try
            {
                // Ширину считаем сами, а не через -2 (LVSCW_AUTOSIZE_USEHEADER): нативный
                // расчёт стабильно перебирает на 2 px, и из-за этого во ВСЕХ списках висел
                // бесполезный горизонтальный скроллбар (замерено: colSum=999 при client=997).
                // ClientSize.Width уже не включает вертикальный скроллбар, поэтому остаток
                // получается точным.
                int last = lv.Columns.Count - 1;
                int used = 0;
                for (int i = 0; i < last; i++) used += lv.Columns[i].Width;
                // 2 px запаса: при сумме РОВНО в ширину клиента control не сбрасывает
                // уже выставленный WS_HSCROLL, и полоса остаётся висеть впустую.
                int rest = lv.ClientSize.Width - used - 2;
                if (rest >= 60)
                {
                    if (force && lv.Columns[last].Width == rest) lv.Columns[last].Width = rest - 1;
                    lv.Columns[last].Width = rest;
                }
                else lv.Columns[last].Width = -2;   // не влезает — пусть решает система
            }
            catch { }
            finally { _inAutoFill = false; }
        }

        // Второй проход ПОСЛЕ того, как список разложится: на момент AddRange
        // вертикального скроллбара ещё нет, ClientSize шире на его толщину, и
        // посчитанная там последняя колонка оказывается на ~17 px слишком широкой —
        // отсюда и лишний горизонтальный скроллбар.
        // Планировать этот проход ИЗ САМОГО AutoFillLastColumn нельзя: каждый вызов
        // ставил бы следующий, и UI-поток забивался бы сообщениями насмерть
        // (проверено — приложение переставало отвечать).
        // Вызывается только из UI-потока (обработчики идут через UiPost), поэтому
        // очередь без блокировок.
        private void AutoFillLastColumnDeferred(ListView lv)
        {
            if (lv == null) return;
            AutoFillLastColumn(lv);
            if (!IsHandleCreated) return;
            if (!_autoFillQueue.Contains(lv)) _autoFillQueue.Add(lv);
            if (_autoFillPending) return;
            _autoFillPending = true;
            BeginInvoke(new MethodInvoker(delegate
            {
                _autoFillPending = false;
                ListView[] pend = _autoFillQueue.ToArray();
                _autoFillQueue.Clear();
                foreach (ListView t in pend)
                    if (t != null && !t.IsDisposed) AutoFillLastColumn(t, true);
            }));
        }

        // Кисти/перья/шрифты для owner-draw живут в полях, а не создаются на каждую
        // ячейку: при 300 строках × 11 колонок это было ~6600 GDI-объектов на одну
        // перерисовку списка, а new Font в отрисовке заголовка — самый дорогой из них.
        private SolidBrush _brHeader, _brSurface, _brSel, _brAccent;
        private Pen _penBorder, _penAccent, _penCheck, _penSurfaceEdge;
        private Font _fontHeader;
        private readonly Dictionary<int, SolidBrush> _rowBrushes = new Dictionary<int, SolidBrush>();

        private void DisposeThemeGdi()
        {
            if (_brHeader != null) { _brHeader.Dispose(); _brHeader = null; }
            if (_brSurface != null) { _brSurface.Dispose(); _brSurface = null; }
            if (_brSel != null) { _brSel.Dispose(); _brSel = null; }
            if (_brAccent != null) { _brAccent.Dispose(); _brAccent = null; }
            if (_penBorder != null) { _penBorder.Dispose(); _penBorder = null; }
            if (_penAccent != null) { _penAccent.Dispose(); _penAccent = null; }
            if (_penCheck != null) { _penCheck.Dispose(); _penCheck = null; }
            if (_penSurfaceEdge != null) { _penSurfaceEdge.Dispose(); _penSurfaceEdge = null; }
            if (_fontHeader != null) { _fontHeader.Dispose(); _fontHeader = null; }
            foreach (SolidBrush b in _rowBrushes.Values) b.Dispose();
            _rowBrushes.Clear();
        }

        private void BuildThemeGdi()
        {
            DisposeThemeGdi();
            _brHeader = new SolidBrush(_theme.Header);
            _brSurface = new SolidBrush(_theme.Surface);
            _brSel = new SolidBrush(_theme.Dark ? ControlPaint.Light(_theme.Accent, 0.15f)
                                                : ControlPaint.Light(_theme.Accent, 0.72f));
            _brAccent = new SolidBrush(_theme.Accent);
            _penBorder = new Pen(_theme.Border);
            _penAccent = new Pen(_theme.Accent, 1.6f);
            _penSurfaceEdge = new Pen(_theme.Border, 1.6f);
            _penCheck = new Pen(_theme.AccentText, 2.2f);
            _penCheck.StartCap = LineCap.Round; _penCheck.EndCap = LineCap.Round;
            _penCheck.LineJoin = LineJoin.Round;
            _fontHeader = new Font(Font, FontStyle.Bold);
        }

        private SolidBrush RowBrush(Color c)
        {
            int key = c.ToArgb();
            SolidBrush b;
            if (_rowBrushes.TryGetValue(key, out b)) return b;
            b = new SolidBrush(c);
            _rowBrushes[key] = b;
            return b;
        }

        private void Lv_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            if (_brHeader == null) BuildThemeGdi();
            e.Graphics.FillRectangle(_brHeader, e.Bounds);
            e.Graphics.DrawLine(_penBorder, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
            e.Graphics.DrawLine(_penBorder, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            Rectangle tr = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, _fontHeader, tr, _theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void Lv_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (_brSurface == null) BuildThemeGdi();
            ListView lv = (ListView)sender;

            Color bg = e.Item.BackColor;
            if (bg.IsEmpty || bg.A == 0) bg = _theme.Surface;
            e.Graphics.FillRectangle(e.Item.Selected ? _brSel : RowBrush(bg), e.Bounds);
            e.Graphics.DrawLine(_penBorder, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

            int textX = e.Bounds.Left + 8;
            if (e.ColumnIndex == 0 && lv.CheckBoxes)
            {
                int box = 17;
                int bx = e.Bounds.Left + 6;
                int by = e.Bounds.Top + (e.Bounds.Height - box) / 2;
                DrawCheck(e.Graphics, new Rectangle(bx, by, box, box), e.Item.Checked);
                textX = bx + box + 8;
            }

            Color fg = e.Item.ForeColor;
            if (fg.IsEmpty || fg.A == 0) fg = _theme.Text;
            Rectangle rt = new Rectangle(textX, e.Bounds.Top, e.Bounds.Right - textX - 6, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.SubItem != null ? e.SubItem.Text : "", lv.Font, rt, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void DrawCheck(Graphics g, Rectangle r, bool check)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath gp = RoundedRect(r, 4))
            {
                g.FillPath(check ? _brAccent : _brSurface, gp);
                g.DrawPath(check ? _penAccent : _penSurfaceEdge, gp);
            }
            if (check)
                g.DrawLines(_penCheck, new Point[] {
                    new Point(r.Left + 4, r.Top + 9),
                    new Point(r.Left + 7, r.Top + 12),
                    new Point(r.Left + 13, r.Top + 5) });
            g.SmoothingMode = SmoothingMode.Default;
        }

        private void RoundControl(Control c, int radius)
        {
            try
            {
                if (c.Width <= 2 || c.Height <= 2) return;
                using (GraphicsPath gp = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius))
                    c.Region = new Region(gp);
            }
            catch { }
        }

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
        private const int PageScan = 0, PageDev = 1, PageClean = 2, PageDocker = 3,
                          PageApps = 4, PageUpdates = 5, PageStartup = 6,
                          PageSettings = 7, PageHistory = 8;

        private ListView VisibleList(int index)
        {
            switch (index)
            {
                case PageScan: return _lvScan;
                case PageDev: return _lvPorts;
                case PageClean: return _lvClean;
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
            AutoFillLastColumnDeferred(_lvApps);
            AutoFillLastColumnDeferred(_lvUpdates);
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

        // ---------- Тема ----------
        private void ApplyThemeAll()
        {
            BuildThemeGdi();
            BackColor = _theme.Bg;
            ForeColor = _theme.Text;
            ApplyThemeTo(this);
            Control nav = null;
            foreach (Control c in Controls) if (c.Name == "nav") { nav = c; break; }
            if (nav != null) nav.BackColor = _theme.Bg;
            UpdateNav();
            ApplyTitleBar();
            Invalidate();
        }

        private void ApplyTitleBar()
        {
            if (!IsHandleCreated) return;
            try
            {
                int on = _theme.Dark ? 1 : 0;
                if (Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4) != 0)
                    Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, 4);
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBar();
        }

        private void ApplyThemeTo(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is Button)
                {
                    Button b = (Button)c;
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderSize = 1;
                    b.UseVisualStyleBackColor = false;
                    bool primary = (b.Tag as string) == "primary";
                    if (primary)
                    {
                        b.BackColor = _theme.Accent;
                        b.ForeColor = _theme.AccentText;
                        b.FlatAppearance.BorderColor = _theme.Accent;
                        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(_theme.Accent, 0.1f);
                    }
                    else
                    {
                        b.BackColor = _theme.Surface;
                        b.ForeColor = _theme.Text;
                        b.FlatAppearance.BorderColor = _theme.Border;
                        b.FlatAppearance.MouseOverBackColor = _theme.Dark
                            ? ControlPaint.Light(_theme.Surface, 0.15f)
                            : ControlPaint.Dark(_theme.Surface, 0.03f);
                    }
                    RoundControl(b, 8);
                }
                else if (c is RichTextBox)
                {
                    c.BackColor = _theme.Surface;
                    c.ForeColor = _theme.Text;
                }
                else if (c is TextBox)
                {
                    c.BackColor = _theme.Surface;
                    c.ForeColor = _theme.Text;
                    ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
                }
                else if (c is NumericUpDown)
                {
                    c.BackColor = _theme.Surface;
                    c.ForeColor = _theme.Text;
                    ((NumericUpDown)c).BorderStyle = BorderStyle.FixedSingle;
                }
                else if (c is ComboBox)
                {
                    c.BackColor = _theme.Surface;
                    c.ForeColor = _theme.Text;
                    ((ComboBox)c).FlatStyle = FlatStyle.Flat;
                }
                else if (c is ListView)
                {
                    c.BackColor = _theme.Surface;
                    c.ForeColor = _theme.Text;
                    ((ListView)c).BorderStyle = BorderStyle.FixedSingle;
                }
                else if (c is Label)
                {
                    c.BackColor = Color.Transparent;
                    if (c.Name == "section") c.ForeColor = _theme.Accent;
                    else if (c.Name == "warn" || c.Name == "muted") c.ForeColor = _theme.Subtle;
                    else c.ForeColor = _theme.Text;
                }
                else if (c is CheckBox)
                {
                    c.BackColor = Color.Transparent;
                    c.ForeColor = _theme.Text;
                }
                else if (c is TabControl)
                {
                    c.BackColor = _theme.Bg;
                    c.ForeColor = _theme.Text;
                }
                else if (c is TabPage || c is Panel || c is FlowLayoutPanel)
                {
                    c.BackColor = _theme.Bg;
                    c.ForeColor = _theme.Text;
                }
                if (c.Controls.Count > 0) ApplyThemeTo(c);
            }
        }

        // ---------- UI ----------
        private void BuildUi()
        {
            Text = "Windows Process Cleaner";
            Width = 1060;
            Height = 740;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10.5F);
            MinimumSize = new Size(940, 620);
            Icon = _iconWindow;
            ShowIcon = true;

            // Собственная навигация вместо TabControl (полностью тематизируется).
            Panel nav = new Panel();
            nav.Dock = DockStyle.Top;
            nav.Height = 48;
            nav.Name = "nav";

            _content = new Panel();
            _content.Dock = DockStyle.Fill;

            _pages = new Control[] { BuildScanTab(), BuildDevTab(), BuildCleanTab(), BuildDockerTab(), BuildAppsTab(), BuildUpdatesTab(), BuildStartupTab(), BuildSettingsTab(), BuildHistoryTab() };
            string[] titles = { Tr.S("Сканирование", "Scan"), "Dev Cleanup", Tr.S("Очистка диска", "Disk Cleanup"), "Docker", Tr.S("Программы", "Programs"), Tr.S("Обновления", "Updates"), Tr.S("Автозапуск", "Startup"), Tr.S("Настройки", "Settings"), Tr.S("История", "History") };

            // Ширины считаем по тексту: вкладок стало 9, и захардкоженные значения
            // перестали влезать в окно. Если сумма всё равно больше — режем отступ.
            Font navFont = new Font(Font, FontStyle.Bold);
            int[] widths = new int[titles.Length];
            int pad = 22, avail = ClientSize.Width - 16;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int sum = 8;
                for (int i = 0; i < titles.Length; i++)
                {
                    widths[i] = TextRenderer.MeasureText(titles[i], navFont).Width + pad;
                    sum += widths[i] + 4;
                }
                if (sum <= avail) break;
                pad = pad > 12 ? pad - 6 : pad;
            }
            _navButtons = new Button[titles.Length];
            int nx = 8;
            for (int i = 0; i < titles.Length; i++)
            {
                Button b = new Button();
                b.Text = titles[i];
                b.Left = nx; b.Top = 4; b.Width = widths[i]; b.Height = 40;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.Font = new Font(Font, FontStyle.Bold);
                int idx = i;
                b.Click += delegate { ShowPage(idx); };
                nav.Controls.Add(b);
                _navButtons[i] = b;
                nx += widths[i] + 4;
            }
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
                    _tray.ShowBalloonTip(2000, "Windows Process Cleaner",
                        Tr.S("Свёрнуто в трей. Работает в фоне.", "Minimized to tray. Running in background."), ToolTipIcon.Info);
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

        private Button MkButton(string text, int x, int y, int w, bool primary)
        {
            Button b = new Button();
            b.Text = text;
            b.Left = x; b.Top = y; b.Width = w; b.Height = 36;
            if (primary) b.Tag = "primary";
            return b;
        }

        private Control BuildScanTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 128;

            // ряд 1
            Button btnScan = MkButton(Tr.S("Сканировать", "Scan"), 0, 6, 150, true);
            btnScan.Click += delegate { DoScan(); };
            Button btnSelAll = MkButton(Tr.S("Выбрать все", "Select all"), 160, 6, 130, false);
            btnSelAll.Click += delegate { SetAllChecks(true); };
            Button btnSelNone = MkButton(Tr.S("Снять выбор", "Clear"), 298, 6, 130, false);
            btnSelNone.Click += delegate { SetAllChecks(false); };

            _chkGlobal = new CheckBox();
            _chkGlobal.Text = Tr.S("Все процессы (глобально)", "All processes (global)");
            _chkGlobal.Left = 450; _chkGlobal.Top = 6; _chkGlobal.Width = 280; _chkGlobal.Height = 22;
            _chkGlobal.CheckedChanged += delegate
            {
                _engine.Config.GlobalScan = _chkGlobal.Checked;
                _engine.SaveConfig();
            };
            Label lblWarn = new Label();
            lblWarn.Name = "warn";
            lblWarn.Text = Tr.S("⚠ завершает любые ваши простаивающие/осиротевшие процессы",
                                "⚠ terminates any of your idle/orphaned processes");
            lblWarn.Left = 450; lblWarn.Top = 30; lblWarn.Width = 460; lblWarn.Height = 18;
            lblWarn.Font = new Font(Font.FontFamily, 9.5F);
            lblWarn.AutoEllipsis = true;

            // ряд 2
            Button btnClean = MkButton(Tr.S("Очистить выбранные", "Clean selected"), 0, 52, 200, true);
            btnClean.Click += delegate { DoClean(); };
            Button btnAuto = MkButton(Tr.S("Автоочистка всех неактивных", "Auto-clean all inactive"), 210, 52, 250, true);
            btnAuto.Click += delegate { DoAutoCleanButton(); };
            Button btnPurge = MkButton(Tr.S("Очистить память", "Purge memory"), 470, 52, 170, false);
            btnPurge.Click += delegate { DoPurgeOnly(); };

            top.Controls.Add(btnScan);
            top.Controls.Add(btnSelAll);
            top.Controls.Add(btnSelNone);
            top.Controls.Add(_chkGlobal);
            top.Controls.Add(lblWarn);
            _lblSummary = new Label();
            _lblSummary.Left = 0; _lblSummary.Top = 98; _lblSummary.Width = 980; _lblSummary.Height = 26;
            _lblSummary.TextAlign = ContentAlignment.MiddleLeft;
            _lblSummary.Text = Tr.S("Нажмите «Сканировать»", "Click “Scan”");

            top.Controls.Add(btnClean);
            top.Controls.Add(btnAuto);
            top.Controls.Add(btnPurge);
            top.Controls.Add(_lblSummary);

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
            tab.Controls.Add(top);
            return tab;
        }

        private Control BuildDevTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Top;
            flow.Height = 140;
            flow.Padding = new Padding(6);

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

            Panel portsBar = new Panel();
            portsBar.Dock = DockStyle.Top;
            portsBar.Height = 40;
            Label lblP = new Label();
            lblP.Text = Tr.S("Занятые dev-порты:", "Busy dev ports:");
            lblP.Left = 8; lblP.Top = 12; lblP.Width = 160;
            Button btnRefresh = new Button();
            btnRefresh.Text = Tr.S("Обновить", "Refresh");
            btnRefresh.Left = 170; btnRefresh.Top = 8; btnRefresh.Width = 100; btnRefresh.Height = 26;
            btnRefresh.Click += delegate { RefreshPorts(); };
            Button btnKillPort = new Button();
            btnKillPort.Text = Tr.S("Завершить выбранные порты", "Kill selected ports");
            btnKillPort.Left = 278; btnKillPort.Top = 8; btnKillPort.Width = 220; btnKillPort.Height = 26;
            btnKillPort.Click += delegate { KillSelectedPorts(); };
            portsBar.Controls.Add(lblP);
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
            tab.Controls.Add(flow);
            return tab;
        }

        private void AddDevButton(FlowLayoutPanel flow, string title, string[] names)
        {
            Button b = new Button();
            b.Text = title;
            b.Width = 150; b.Height = 32;
            b.Margin = new Padding(4);
            b.Click += delegate
            {
                long freed;
                int n = _engine.TerminateByNames(names, out freed);
                string msg = Tr.S("Завершено: ", "Terminated: ") + n + Tr.S(" · освобождено ~", " · freed ~") + Engine.FormatBytes(freed);
                _tray.ShowBalloonTip(2000, title, msg, ToolTipIcon.Info);
                MessageBox.Show(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            flow.Controls.Add(b);
        }

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
            lblTheme.Text = Tr.S("Тема оформления:", "Theme:"); lblTheme.Left = lx; lblTheme.Top = y + 4; lblTheme.Width = 250;
            body.Controls.Add(lblTheme);
            _cmbTheme = new ComboBox();
            _cmbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbTheme.Left = cx; _cmbTheme.Top = y; _cmbTheme.Width = 200;
            _cmbTheme.Items.AddRange(new object[] { Tr.S("По системе", "System"), Tr.S("Светлая", "Light"), Tr.S("Тёмная", "Dark") });
            _cmbTheme.SelectedIndexChanged += delegate { PreviewTheme(); };
            body.Controls.Add(_cmbTheme);
            y += 36;

            Label lblLang = new Label();
            lblLang.Text = "Язык / Language:"; lblLang.Left = lx; lblLang.Top = y + 4; lblLang.Width = 250;
            body.Controls.Add(lblLang);
            _cmbLang = new ComboBox();
            _cmbLang.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbLang.Left = cx; _cmbLang.Top = y; _cmbLang.Width = 200;
            _cmbLang.Items.AddRange(new object[] { "Русский", "English" });
            body.Controls.Add(_cmbLang);
            y += 44;

            // ---- ПРАВАЯ КОЛОНКА ----
            int rx = 540, ry = 8, rw = 400;
            SectionHeader(body, Tr.S("Списки", "Lists"), rx, ref ry);
            AddLabel(body, Tr.S("Отслеживаемые процессы (по одному в строке):", "Watched processes (one per line):"), rx, ref ry);
            _txtWatch = MakeMultilineAt(body, rx, ref ry, rw, 150);
            AddLabel(body, Tr.S("Белый список — никогда не завершать:", "Whitelist — never terminate:"), rx, ref ry);
            _txtWhite = MakeMultilineAt(body, rx, ref ry, rw, 150);
            AddLabel(body, Tr.S("Dev-порты (через запятую):", "Dev ports (comma-separated):"), rx, ref ry);
            _txtPorts = new TextBox();
            _txtPorts.Left = rx; _txtPorts.Top = ry; _txtPorts.Width = rw;
            body.Controls.Add(_txtPorts);
            ry += 36;
            AddLabel(body, Tr.S("Не чистить эти пути (по одному в строке):", "Never clean these paths (one per line):"), rx, ref ry);
            _txtCleanExclude = MakeMultilineAt(body, rx, ref ry, rw, 90);
            AddLabel(body, Tr.S("Не предлагать обновления (Id пакета в строке):",
                                "Never offer updates for (package Id per line):"), rx, ref ry);
            _txtUpdExclude = MakeMultilineAt(body, rx, ref ry, rw, 90);

            // Распорка: AutoScroll считает границу по нижнему краю контролов.
            Panel spacer = new Panel();
            spacer.Left = lx; spacer.Top = Math.Max(y, ry); spacer.Width = 8; spacer.Height = 8;
            body.Controls.Add(spacer);

            // ---- КНОПКИ ----
            Button save = new Button();
            save.Text = Tr.S("Сохранить настройки", "Save settings");
            save.Tag = "primary";
            save.Left = lx; save.Top = 8; save.Width = 210; save.Height = 36;
            save.Click += delegate { SaveSettingsFromUi(); };
            bar.Controls.Add(save);

            Button openDir = new Button();
            openDir.Text = Tr.S("Папка данных", "Data folder");
            openDir.Left = lx + 222; openDir.Top = 8; openDir.Width = 160; openDir.Height = 36;
            openDir.Click += delegate { try { Process.Start("explorer.exe", _engine.DataDir); } catch { } };
            bar.Controls.Add(openDir);

            return tab;
        }

        private void SectionHeader(Panel tab, string text, int lx, ref int y)
        {
            Label l = new Label();
            l.Text = text; l.Left = lx; l.Top = y; l.AutoSize = true;
            l.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            l.Name = "section";
            tab.Controls.Add(l);
            y += 30;
        }

        private NumericUpDown MakeNum(Panel tab, string label, int lx, int cx, ref int y,
            decimal min, decimal max, int dec, decimal step)
        {
            Label l = new Label();
            l.Text = label; l.Left = lx; l.Top = y + 4; l.Width = cx - lx - 8;
            tab.Controls.Add(l);
            NumericUpDown n = new NumericUpDown();
            n.Left = cx; n.Top = y; n.Width = 120;
            n.Minimum = min; n.Maximum = max; n.DecimalPlaces = dec; n.Increment = step;
            tab.Controls.Add(n);
            y += 34;
            return n;
        }

        private CheckBox MakeCheck(Panel tab, string label, int lx, ref int y)
        {
            CheckBox c = new CheckBox();
            c.Text = label; c.Left = lx; c.Top = y; c.Width = 480; c.Height = 24;
            tab.Controls.Add(c);
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
            y += 24;
        }

        private TextBox MakeMultilineAt(Panel tab, int lx, ref int y, int w, int h)
        {
            TextBox t = new TextBox();
            t.Multiline = true; t.ScrollBars = ScrollBars.Vertical;
            t.Left = lx; t.Top = y; t.Width = w; t.Height = h;
            tab.Controls.Add(t);
            y += h + 12;
            return t;
        }

        private Control BuildHistoryTab()
        {
            Panel tab = new Panel();
            Panel bar = new Panel();
            bar.Dock = DockStyle.Top; bar.Height = 40;
            Button refresh = new Button();
            refresh.Text = Tr.S("Обновить", "Refresh"); refresh.Left = 8; refresh.Top = 8; refresh.Width = 100; refresh.Height = 26;
            refresh.Click += delegate { RefreshHistory(); };
            bar.Controls.Add(refresh);

            _lvHistory = new FastListView();
            _lvHistory.Dock = DockStyle.Fill;
            _lvHistory.View = View.Details;
            _lvHistory.FullRowSelect = true;
            _lvHistory.Columns.Add(Tr.S("Дата и время", "Date and time"), 175);
            _lvHistory.Columns.Add(Tr.S("Завершено", "Terminated"), 100);
            _lvHistory.Columns.Add(Tr.S("Освобождено", "Freed"), 120);
            _lvHistory.Columns.Add(Tr.S("Процессы", "Processes"), 460);
            SetupOwnerDraw(_lvHistory);

            tab.Controls.Add(_lvHistory);
            tab.Controls.Add(bar);
            return tab;
        }

        // ---------- Вкладка: Очистка диска ----------
        private Control BuildCleanTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 96;

            Button btnAnalyze = MkButton(Tr.S("Анализировать", "Analyze"), 0, 6, 150, true);
            btnAnalyze.Click += delegate { DoAnalyzeDisk(); };
            Button btnClean = MkButton(Tr.S("Удалить выбранное", "Delete selected"), 158, 6, 180, true);
            btnClean.Click += delegate { DoCleanDisk(); };
            _btnCleanCancel = MkButton(Tr.S("Стоп", "Stop"), 346, 6, 80, false);
            _btnCleanCancel.Enabled = false;
            _btnCleanCancel.Click += delegate { CancelDisk(); };
            Button btnAll = MkButton(Tr.S("Все", "All"), 434, 6, 70, false);
            btnAll.Click += delegate { SetCleanChecks(true); };
            Button btnNone = MkButton(Tr.S("Ничего", "None"), 512, 6, 90, false);
            btnNone.Click += delegate { SetCleanChecks(false); };
            Button btnRules = MkButton(Tr.S("Правила winapp2", "winapp2 rules"), 610, 6, 170, false);
            btnRules.Click += delegate { DoLoadWinapp2(); };
            Button btnLog = MkButton(Tr.S("Лог", "Log"), 788, 6, 80, false);
            btnLog.Click += delegate { OpenCleanLog(); };

            Label warn = new Label();
            warn.Name = "muted";
            warn.Text = Tr.S("⚠ Файлы удаляются безвозвратно. Код, проекты, системные папки и данные не трогаются. Закройте браузеры для полной очистки кэша.",
                             "⚠ Files are deleted permanently. Code, projects, system folders and data are never touched. Close browsers to fully clear cache.");
            warn.Left = 0; warn.Top = 48; warn.Width = 1010; warn.Height = 18;
            warn.Font = new Font(Font.FontFamily, 9.5F);
            warn.AutoEllipsis = true;

            _lblCleanTotal = new Label();
            _lblCleanTotal.Left = 0; _lblCleanTotal.Top = 70; _lblCleanTotal.Width = 980; _lblCleanTotal.Height = 24;
            _lblCleanTotal.Text = Tr.S("Нажмите «Анализировать»", "Click “Analyze”");

            top.Controls.Add(btnAnalyze);
            top.Controls.Add(btnClean);
            top.Controls.Add(_btnCleanCancel);
            top.Controls.Add(btnAll);
            top.Controls.Add(btnNone);
            top.Controls.Add(btnRules);
            top.Controls.Add(btnLog);
            top.Controls.Add(warn);
            top.Controls.Add(_lblCleanTotal);

            _lvClean = new FastListView();
            _lvClean.Dock = DockStyle.Fill;
            _lvClean.View = View.Details;
            _lvClean.CheckBoxes = true;
            _lvClean.FullRowSelect = true;
            _lvClean.Columns.Add(Tr.S("Категория", "Category"), 230);
            _lvClean.Columns.Add(Tr.S("Размер", "Size"), 110);
            _lvClean.Columns.Add(Tr.S("Файлов", "Files"), 90);
            _lvClean.Columns.Add(Tr.S("Что чистится", "What is cleaned"), 520);
            SetupOwnerDraw(_lvClean);

            tab.Controls.Add(_lvClean);
            tab.Controls.Add(top);
            return tab;
        }

        private int _diskBusy;

        // Анализ идёт в фоне и показывает категории по мере готовности, а не одним
        // куском в конце: обход .nuget\packages или Windows.old — это минуты, и раньше
        // всё это время список был пуст без признаков жизни.
        // ---------- Обновления программ ----------

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
                MessageBox.Show(Tr.S("Отметьте программы, которые больше не предлагать.",
                                     "Check the programs you no longer want offered."));
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
                MessageBox.Show(Tr.S("Лог пуст — обновления ещё не устанавливались.",
                                     "The log is empty — no updates have been installed yet."));
                return;
            }
            try { Process.Start("notepad.exe", path); } catch { }
        }

        private void DoApplyUpdates()
        {
            List<UpdateItem> sel = CheckedUpdates();
            if (sel.Count == 0)
            {
                MessageBox.Show(Tr.S("Отметьте, что обновить.", "Check what to update."));
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
            if (MessageBox.Show(ask, Tr.S("Обновление программ", "Updating programs"),
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                Interlocked.Exchange(ref _updBusy, 0);
                return;
            }

            _engine.ResetUpdateCancel();
            if (_btnUpdCancel != null) _btnUpdCancel.Enabled = true;
            _lblUpdInfo.Text = Tr.S("Обновление… 0/", "Updating… 0/") + sel.Count;

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
                Interlocked.Exchange(ref _updBusy, 0);
                UiPost(delegate
                {
                    if (_btnUpdCancel != null) _btnUpdCancel.Enabled = false;
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

        private void DoAnalyzeDisk()
        {
            if (Interlocked.CompareExchange(ref _diskBusy, 1, 0) != 0) return;
            _engine.ResetDiskCancel();
            _lblCleanTotal.Text = Tr.S("Анализ…", "Analyzing…");
            _lvClean.Items.Clear();
            _cleanCats = null;
            if (_btnCleanCancel != null) _btnCleanCancel.Enabled = true;

            Thread t = new Thread(delegate()
            {
                List<CleanCategory> cats;
                try { cats = _engine.BuildCleanCategories(); }
                catch { cats = new List<CleanCategory>(); }

                UiPost(delegate { _cleanCats = cats; PopulateClean(cats); });

                int done = 0;
                try
                {
                    _engine.AnalyzeCategories(cats, delegate(CleanCategory c)
                    {
                        int n = Interlocked.Increment(ref done);
                        UiPost(delegate { UpdateCleanRow(c, n, cats.Count); });
                    });
                }
                catch { }

                UiPost(delegate
                {
                    UpdateCleanTotal(true);
                    if (_btnCleanCancel != null) _btnCleanCancel.Enabled = false;
                });
                Interlocked.Exchange(ref _diskBusy, 0);
            });
            t.IsBackground = true;
            t.Start();
        }

        private void SetCleanChecks(bool value)
        {
            _lvClean.BeginUpdate();
            try { foreach (ListViewItem it in _lvClean.Items) it.Checked = value; }
            finally { _lvClean.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvClean);
        }

        private void CancelDisk()
        {
            _engine.CancelDiskWork();
            _lblCleanTotal.Text = Tr.S("Остановлено пользователем.", "Cancelled by user.");
        }

        private void PopulateClean(List<CleanCategory> cats)
        {
            _lvClean.BeginUpdate();
            try
            {
                _lvClean.Items.Clear();
                List<ListViewItem> rows = new List<ListViewItem>();
                foreach (CleanCategory c in cats)
                {
                    ListViewItem it = new ListViewItem(c.Title);
                    it.SubItems.Add("…");
                    it.SubItems.Add("");
                    it.SubItems.Add(c.Desc ?? "");
                    it.Tag = c;
                    it.ForeColor = _theme.Text;
                    it.BackColor = _theme.Surface;
                    rows.Add(it);
                }
                _lvClean.Items.AddRange(rows.ToArray());
            }
            finally { _lvClean.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvClean);
        }

        private void UpdateCleanRow(CleanCategory c, int done, int total)
        {
            foreach (ListViewItem it in _lvClean.Items)
            {
                if (it.Tag != c) continue;
                it.SubItems[1].Text = Engine.FormatBytes(c.Size);
                it.SubItems[2].Text = c.FileCount.ToString();
                if (!string.IsNullOrEmpty(c.Note)) it.SubItems[3].Text = c.Desc + "  ·  " + c.Note;
                it.Checked = c.Recommended && c.Size > 0;
                break;
            }
            _lblCleanTotal.Text = Tr.S("Анализ… ", "Analyzing… ") + done + "/" + total;
            if (done == total) UpdateCleanTotal(true);
        }

        private void UpdateCleanTotal(bool finished)
        {
            long total = 0;
            if (_cleanCats != null) foreach (CleanCategory c in _cleanCats) total += c.Size;
            string extra = "";
            if (_engine.Winapp2RuleCount > 0)
                extra = Tr.S("   ·   правил winapp2: ", "   ·   winapp2 rules: ") + _engine.Winapp2RuleCount;
            _lblCleanTotal.Text = (finished ? Tr.S("Всего мусора найдено: ", "Total junk found: ")
                                            : Tr.S("Найдено пока: ", "Found so far: "))
                + Engine.FormatBytes(total)
                + Tr.S("   ·   отметьте категории и нажмите «Удалить выбранное»",
                       "   ·   check categories and click “Delete selected”") + extra;
        }

        private void DoCleanDisk()
        {
            if (_cleanCats == null) { MessageBox.Show(Tr.S("Сначала нажмите «Анализировать».", "Click “Analyze” first.")); return; }
            if (_diskBusy != 0)
            {
                MessageBox.Show(Tr.S("Дождитесь окончания анализа.", "Wait for the analysis to finish."));
                return;
            }
            List<CleanCategory> sel = new List<CleanCategory>();
            long size = 0;
            foreach (ListViewItem it in _lvClean.Items)
                if (it.Checked && it.Tag is CleanCategory) { CleanCategory c = (CleanCategory)it.Tag; sel.Add(c); size += c.Size; }
            if (sel.Count == 0) { MessageBox.Show(Tr.S("Не выбрано ни одной категории.", "No categories selected.")); return; }

            DialogResult dr = MessageBox.Show(
                Tr.S("Удалить ", "Delete ") + Engine.FormatBytes(size) +
                Tr.S(" в " + sel.Count + " категориях?\r\nДействие необратимо (файлы удаляются мимо Корзины).",
                     " across " + sel.Count + " categories?\r\nThis is irreversible (files bypass the Recycle Bin)."),
                Tr.S("Очистка диска", "Disk Cleanup"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return;

            if (Interlocked.CompareExchange(ref _diskBusy, 1, 0) != 0) return;
            _engine.ResetDiskCancel();
            _lblCleanTotal.Text = Tr.S("Удаление…", "Deleting…");
            if (_btnCleanCancel != null) _btnCleanCancel.Enabled = true;

            Thread t = new Thread(delegate()
            {
                CleanResult res = null;
                try { res = _engine.CleanCategories(sel); }
                catch { }
                Interlocked.Exchange(ref _diskBusy, 0);
                UiPost(delegate
                {
                    if (_btnCleanCancel != null) _btnCleanCancel.Enabled = false;
                    if (res == null) { _lblCleanTotal.Text = Tr.S("Очистка не выполнена.", "Cleanup failed."); return; }
                    _lblCleanTotal.Text = Tr.S("✓ Освобождено: ", "✓ Freed: ") + Engine.FormatBytes(res.Freed)
                        + Tr.S("   ·   файлов: ", "   ·   files: ") + res.FilesDeleted
                        + (res.Errors > 0 ? Tr.S("   ·   пропущено (заняты/нет доступа): ",
                                                 "   ·   skipped (locked/no access): ") + res.Errors : "");
                    if (_tray != null)
                        _tray.ShowBalloonTip(3000, Tr.S("Очистка диска", "Disk Cleanup"),
                            Tr.S("Освобождено ~", "Freed ~") + Engine.FormatBytes(res.Freed), ToolTipIcon.Info);
                    DoAnalyzeDisk();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Подключение базы winapp2.ini (как в FluentCleaner) — скачивание по запросу.
        private void DoLoadWinapp2()
        {
            string have = _engine.Winapp2Path;
            string msg = have != null
                ? Tr.S("База правил уже подключена:\r\n", "Rule database already attached:\r\n") + have
                  + Tr.S("\r\n\r\nСкачать свежую версию?", "\r\n\r\nDownload a fresh copy?")
                : Tr.S("Скачать базу правил winapp2.ini (~5 МБ) из открытого репозитория Winapp2?\r\n\r\n"
                       + "Это тысячи готовых правил «где у какого приложения лежит кэш» — тот же формат, "
                       + "что использует FluentCleaner. Реестр не чистится ни при каких правилах.",
                       "Download the winapp2.ini rule database (~5 MB) from the public Winapp2 repository?\r\n\r\n"
                       + "These are thousands of ready rules describing where each application keeps its cache — "
                       + "the same format FluentCleaner uses. The registry is never cleaned, whatever a rule says.");
            if (MessageBox.Show(msg, "winapp2.ini", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _lblCleanTotal.Text = Tr.S("Загрузка базы правил…", "Downloading rule database…");
            Thread t = new Thread(delegate()
            {
                string err = null;
                try { _engine.DownloadWinapp2(); }
                catch (Exception ex) { err = ex.Message; }
                UiPost(delegate
                {
                    if (err != null)
                    {
                        _lblCleanTotal.Text = Tr.S("Не удалось скачать: ", "Download failed: ") + err;
                        return;
                    }
                    DoAnalyzeDisk();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void OpenCleanLog()
        {
            string path = _engine.CleanLogPath;
            if (!File.Exists(path))
            {
                MessageBox.Show(Tr.S("Лог пока пуст — очистка ещё не выполнялась.",
                                     "The log is empty — no cleanup has run yet."));
                return;
            }
            try { Process.Start("notepad.exe", "\"" + path + "\""); }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        // ---------- Вкладка: Программы (деинсталляция) ----------
        private Control BuildUpdatesTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 84;

            Button btnCheck = MkButton(Tr.S("Проверить обновления", "Check for updates"), 0, 6, 200, true);
            btnCheck.Click += delegate { DoScanUpdates(); };
            Button btnApply = MkButton(Tr.S("Обновить выбранное", "Update selected"), 210, 6, 190, true);
            btnApply.Click += delegate { DoApplyUpdates(); };
            _btnUpdCancel = MkButton(Tr.S("Стоп", "Stop"), 410, 6, 80, false);
            _btnUpdCancel.Enabled = false;
            _btnUpdCancel.Click += delegate { _engine.CancelUpdateWork(); };
            Button btnAll = MkButton(Tr.S("Все", "All"), 500, 6, 70, false);
            btnAll.Click += delegate { SetAllUpdateChecks(true); };
            Button btnNone = MkButton(Tr.S("Ничего", "None"), 578, 6, 90, false);
            btnNone.Click += delegate { SetAllUpdateChecks(false); };
            Button btnSkip = MkButton(Tr.S("Не предлагать", "Never offer"), 676, 6, 150, false);
            btnSkip.Click += delegate { ExcludeSelectedUpdates(); };
            Button btnLog = MkButton(Tr.S("Лог", "Log"), 834, 6, 80, false);
            btnLog.Click += delegate { OpenUpdateLog(); };

            Label warn = new Label();
            warn.Name = "muted";
            warn.Text = Tr.S("Обновляет сам менеджер пакетов (winget/Chocolatey), реестр не правится. «Важность» — масштаб скачка версии, а не оценка безопасности.",
                             "The package manager itself (winget/Chocolatey) updates, no registry edits. “Impact” is the size of the version jump, not a security rating.");
            warn.Left = 0; warn.Top = 46; warn.Width = 1010; warn.Height = 18;
            warn.Font = new Font(Font.FontFamily, 9.5F);
            warn.AutoEllipsis = true;

            _lblUpdInfo = new Label();
            _lblUpdInfo.Left = 0; _lblUpdInfo.Top = 64; _lblUpdInfo.Width = 1010; _lblUpdInfo.Height = 20;
            _lblUpdInfo.Text = Tr.S("Нажмите «Проверить обновления»", "Click “Check for updates”");

            top.Controls.Add(btnCheck);
            top.Controls.Add(btnApply);
            top.Controls.Add(_btnUpdCancel);
            top.Controls.Add(btnAll);
            top.Controls.Add(btnNone);
            top.Controls.Add(btnSkip);
            top.Controls.Add(btnLog);
            top.Controls.Add(warn);
            top.Controls.Add(_lblUpdInfo);

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
            tab.Controls.Add(top);
            return tab;
        }

        private Control BuildAppsTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 84;

            Button btnRefresh = MkButton(Tr.S("Обновить список", "Refresh list"), 0, 6, 170, true);
            btnRefresh.Click += delegate { RefreshApps(true); };
            Button btnUninstall = MkButton(Tr.S("Удалить выбранное", "Uninstall selected"), 180, 6, 200, true);
            btnUninstall.Click += delegate { DoUninstall(); };

            Label warn = new Label();
            warn.Name = "muted";
            warn.Text = Tr.S("Запускается штатный деинсталлятор программы (может открыть своё окно/запросить подтверждение).",
                             "Launches the program's own uninstaller (may open its own window / ask for confirmation).");
            warn.Left = 0; warn.Top = 46; warn.Width = 980; warn.Height = 18;
            warn.Font = new Font(Font.FontFamily, 9.5F);
            warn.AutoEllipsis = true;

            _lblAppsInfo = new Label();
            _lblAppsInfo.Left = 0; _lblAppsInfo.Top = 64; _lblAppsInfo.Width = 980; _lblAppsInfo.Height = 20;
            _lblAppsInfo.Text = Tr.S("Нажмите «Обновить список»", "Click “Refresh list”");

            top.Controls.Add(btnRefresh);
            top.Controls.Add(btnUninstall);
            top.Controls.Add(warn);
            top.Controls.Add(_lblAppsInfo);

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
            tab.Controls.Add(top);
            return tab;
        }

        // Список установленных программ читается из реестра и для каждой записи ищет exe
        // на диске. В UI-потоке это давало многосекундное замирание при каждом
        // переключении на вкладку. Теперь: фон + кэш, чтобы повторный вход был мгновенным.
        private int _appsBusy;

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
            _lblAppsInfo.Text = Tr.S("Установленных программ: ", "Installed programs: ") + apps.Count +
                Tr.S("   ·   отметьте и нажмите «Удалить выбранное»", "   ·   check and click “Uninstall selected”");
        }

        private void DoUninstall()
        {
            List<InstalledApp> sel = new List<InstalledApp>();
            foreach (ListViewItem it in _lvApps.Items)
                if (it.Checked && it.Tag is InstalledApp) sel.Add((InstalledApp)it.Tag);
            if (sel.Count == 0) { MessageBox.Show(Tr.S("Не выбрано ни одной программы.", "No programs selected.")); return; }

            foreach (InstalledApp a in sel)
            {
                DialogResult dr = MessageBox.Show(Tr.S("Удалить «", "Uninstall “") + a.Name + Tr.S("»?", "”?"),
                    Tr.S("Деинсталляция", "Uninstall"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr != DialogResult.Yes) continue;
                try { _engine.RunUninstall(a); }
                catch (Exception ex) { MessageBox.Show(Tr.S("Не удалось запустить деинсталлятор: ", "Failed to launch uninstaller: ") + ex.Message); }
            }
            _lblAppsInfo.Text = Tr.S("Деинсталляторы запущены. Обновите список после завершения.",
                                     "Uninstallers launched. Refresh the list when done.");
        }

        // ---------- Вкладка: Docker ----------
        private Control BuildDockerTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Top;
            flow.Height = 130;

            AddDockerButton(flow, Tr.S("Обзор занятого места", "Disk usage (df)"), "system df", false);
            AddDockerButton(flow, Tr.S("Подробно (df -v)", "Details (df -v)"), "system df -v", false);
            AddDockerButton(flow, Tr.S("Удалить остановленные контейнеры", "Remove stopped containers"), "container prune -f", true);
            AddDockerButton(flow, Tr.S("Удалить неиспользуемые образы", "Remove unused images"), "image prune -a -f", true);
            AddDockerButton(flow, Tr.S("Удалить неиспользуемые тома", "Remove unused volumes"), "volume prune -f", true);
            AddDockerButton(flow, Tr.S("Очистить кэш сборки", "Clear build cache"), "builder prune -a -f", true);
            AddDockerButton(flow, Tr.S("Полная очистка", "Full cleanup"), "system prune -a -f --volumes", true);

            Button bCompact = new Button();
            bCompact.Text = Tr.S("★ Очистить всё и сжать диск Docker (вернуть место Windows)",
                                 "★ Clean all & compact Docker disk (reclaim Windows space)");
            bCompact.Width = 470; bCompact.Height = 38; bCompact.Margin = new Padding(4, 6, 4, 4);
            bCompact.Tag = "primary";
            bCompact.Click += delegate { DoCompactDocker(); };
            flow.Controls.Add(bCompact);

            Label note = new Label();
            note.Name = "muted";
            note.Dock = DockStyle.Top;
            note.Height = 58;
            note.Text = Tr.S(
                "Удаляется только НЕиспользуемое (prune): остановленные контейнеры, образы без тегов/ссылок, " +
                "тома без владельцев, кэш сборки. Запущенные контейнеры и используемые образы не трогаются.\r\n" +
                "⚠ prune освобождает место ВНУТРИ виртуального диска Docker, но сам файл на Windows не уменьшается. " +
                "Чтобы реально вернуть место на диске Windows — «Сжать диск Docker» (остановит Docker и сожмёт vhdx).\r\n" +
                "Kubernetes не включён: его очистка бьёт по живому кластеру.",
                "Only UNUSED data is removed (prune): stopped containers, dangling/unreferenced images, " +
                "unused volumes, build cache. Running containers and used images are never touched.\r\n" +
                "⚠ prune frees space INSIDE Docker's virtual disk, but the file on Windows doesn't shrink. " +
                "To actually reclaim Windows disk space use “Compact Docker disk” (stops Docker and compacts the vhdx).\r\n" +
                "Kubernetes is not included: its cleanup affects a live cluster.");
            note.Font = new Font(Font.FontFamily, 9.5F);

            _txtDocker = new RichTextBox();
            _txtDocker.Dock = DockStyle.Fill;
            _txtDocker.ReadOnly = true;
            _txtDocker.WordWrap = false;
            _txtDocker.BorderStyle = BorderStyle.FixedSingle;
            _txtDocker.Font = new Font("Consolas", 10F);
            _txtDocker.Text = Tr.S(
                "Нажмите «Обзор занятого места», чтобы увидеть, сколько занимает Docker.\r\n" +
                "Требуется установленный Docker CLI (Docker Desktop) и запущенный демон.",
                "Click “Disk usage (df)” to see how much space Docker uses.\r\n" +
                "Requires an installed Docker CLI (Docker Desktop) and a running daemon.");

            tab.Controls.Add(_txtDocker);
            tab.Controls.Add(note);
            tab.Controls.Add(flow);
            return tab;
        }

        private void AddDockerButton(FlowLayoutPanel flow, string title, string args, bool destructive)
        {
            Button b = new Button();
            b.Text = title;
            b.Width = 230; b.Height = 34;
            b.Margin = new Padding(4);
            b.Click += delegate { RunDocker(title, args, destructive); };
            flow.Controls.Add(b);
        }

        private void RunDocker(string title, string args, bool destructive)
        {
            if (destructive)
            {
                DialogResult dr = MessageBox.Show(
                    Tr.S("Выполнить: docker " + args + " ?\r\nБудут удалены неиспользуемые данные Docker.",
                         "Run: docker " + args + " ?\r\nUnused Docker data will be removed."),
                    "Docker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (dr != DialogResult.Yes) return;
            }
            _txtDocker.Text = Tr.S("Выполняется: docker ", "Running: docker ") + args + " …";
            Cursor = Cursors.WaitCursor;
            Thread t = new Thread(delegate()
            {
                string res = _engine.Docker(args);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Cursor = Cursors.Default;
                        _txtDocker.Text = res;
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DoCompactDocker()
        {
            DialogResult dr = MessageBox.Show(
                Tr.S("Будет выполнено одним действием:\r\n" +
                     "  1. удалено всё неиспользуемое (образы, тома, кэш сборки, остановленные контейнеры);\r\n" +
                     "  2. остановлен Docker (все запущенные контейнеры завершатся!);\r\n" +
                     "  3. сжат виртуальный диск — реально освободится место на диске Windows;\r\n" +
                     "  4. Docker Desktop запустится снова.\r\n\r\nПродолжить?",
                     "This will do, in one action:\r\n" +
                     "  1. remove all unused data (images, volumes, build cache, stopped containers);\r\n" +
                     "  2. stop Docker (all running containers will exit!);\r\n" +
                     "  3. compact the virtual disk — actually frees Windows disk space;\r\n" +
                     "  4. start Docker Desktop again.\r\n\r\nContinue?"),
                "Docker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return;

            _txtDocker.Text = Tr.S("Очистка и сжатие диска Docker… это может занять пару минут, не закрывайте окно.",
                                   "Cleaning and compacting Docker disk… this may take a couple of minutes, don't close the window.");
            Cursor = Cursors.WaitCursor;
            Thread t = new Thread(delegate()
            {
                string res = _engine.CompactDockerDisk();
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Cursor = Cursors.Default;
                        _txtDocker.Text = res;
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------- Вкладка: Автозапуск ----------
        private Control BuildStartupTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 84;

            Button btnRefresh = MkButton(Tr.S("Обновить список", "Refresh list"), 0, 6, 170, true);
            btnRefresh.Click += delegate { RefreshStartup(true); };

            Label warn = new Label();
            warn.Name = "muted";
            warn.Text = Tr.S("Галочка = программа в автозапуске Windows. Поставьте — добавить, снимите — убрать. По умолчанию выкл.",
                             "Checkbox = program is in Windows startup. Check to add, uncheck to remove. Off by default.");
            warn.Left = 0; warn.Top = 46; warn.Width = 980; warn.Height = 18;
            warn.Font = new Font(Font.FontFamily, 9.5F);
            warn.AutoEllipsis = true;

            _lblStartupInfo = new Label();
            _lblStartupInfo.Left = 0; _lblStartupInfo.Top = 64; _lblStartupInfo.Width = 980; _lblStartupInfo.Height = 20;
            _lblStartupInfo.Text = Tr.S("Нажмите «Обновить список»", "Click “Refresh list”");

            top.Controls.Add(btnRefresh);
            top.Controls.Add(warn);
            top.Controls.Add(_lblStartupInfo);

            _lvStartup = new FastListView();
            _lvStartup.Dock = DockStyle.Fill;
            _lvStartup.View = View.Details;
            _lvStartup.CheckBoxes = true;
            _lvStartup.FullRowSelect = true;
            _lvStartup.Columns.Add(Tr.S("Программа", "Program"), 300);
            _lvStartup.Columns.Add(Tr.S("Издатель / источник", "Publisher / source"), 220);
            _lvStartup.Columns.Add(Tr.S("Файл автозапуска", "Startup target"), 460);
            SetupOwnerDraw(_lvStartup);
            _lvStartup.ItemChecked += Startup_ItemChecked;

            tab.Controls.Add(_lvStartup);
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
                    it.SubItems.Add(a.Publisher != null ? a.Publisher : "");
                    it.SubItems.Add(a.ExePath != null ? a.ExePath : Tr.S("(exe не найден)", "(exe not found)"));
                    it.ToolTipText = a.Name + (string.IsNullOrEmpty(a.ExePath) ? "" : "\r\n" + a.ExePath);
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
                    it.SubItems.Add(e.SourceLabel != null ? e.SourceLabel : "");
                    it.SubItems.Add(e.Command != null ? e.Command : "");
                    it.ToolTipText = e.Name + "\r\n" + (e.Command != null ? e.Command : "");
                    it.Tag = e;
                    it.Checked = true;
                    it.ForeColor = _theme.Text;
                    it.BackColor = _theme.CandidateBg;
                    rows.Add(it);
                    onCount++;
                }
                _lvStartup.Items.AddRange(rows.ToArray());
            }
            finally
            {
                _lvStartup.EndUpdate();
                AutoFillLastColumnDeferred(_lvStartup);
                _suppressStartup = false;
            }

            _lblStartupInfo.Text = Tr.S("Программ: ", "Programs: ") + apps.Count +
                Tr.S("   ·   в автозапуске: ", "   ·   in startup: ") + onCount +
                Tr.S("   ·   оранжевым — записи автозапуска вне списка установленных", "   ·   orange — startup entries outside the installed list");
            AutoFillLastColumnDeferred(_lvStartup);
        }

        private void Startup_ItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_suppressStartup) return;
            ListViewItem it = e.Item;
            object tag = it.Tag;
            try
            {
                if (tag is InstalledApp)
                {
                    InstalledApp app = (InstalledApp)tag;
                    if (it.Checked)
                    {
                        if (string.IsNullOrEmpty(app.ExePath))
                        {
                            MessageBox.Show(Tr.S("Не удалось определить exe этой программы — добавить в автозапуск нельзя.",
                                                 "Could not determine this program's exe — cannot add to startup."),
                                Tr.S("Автозапуск", "Startup"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            _suppressStartup = true; it.Checked = false; _suppressStartup = false;
                            return;
                        }
                        _engine.AddAutostart(app.Name, app.ExePath);
                    }
                    else
                    {
                        _engine.RemoveAutostart(app.ExePath, app.Name);
                    }
                }
                else if (tag is AutostartEntry)
                {
                    AutostartEntry ent = (AutostartEntry)tag;
                    if (it.Checked)
                    {
                        if (!string.IsNullOrEmpty(ent.ExePath)) _engine.AddAutostart(ent.Name, ent.ExePath);
                    }
                    else
                    {
                        _engine.RemoveAutostart(ent.ExePath, ent.Name);
                        if (!string.IsNullOrEmpty(ent.LnkPath)) { try { File.Delete(ent.LnkPath); } catch { } }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Tr.S("Ошибка: ", "Error: ") + ex.Message);
            }
            // Реестр только что изменился — кэш автозапуска больше не соответствует
            // действительности, следующий вход на вкладку должен перечитать его.
            _autostartCache = null;
        }

        // ---------- Трей ----------
        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = _iconIdle;
            _tray.Text = "Windows Process Cleaner";
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowWindow(); };

            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add(new MenuItem(Tr.S("Открыть", "Open"), delegate { ShowWindow(); }));
            menu.MenuItems.Add(new MenuItem(Tr.S("Сканировать сейчас", "Scan now"), delegate { ShowWindow(); DoScan(); }));
            menu.MenuItems.Add(new MenuItem(Tr.S("Очистить сейчас", "Clean now"), delegate { RunAutoClean(true); }));
            menu.MenuItems.Add(new MenuItem(Tr.S("Очистить Standby Memory", "Purge Standby Memory"), delegate { DoPurgeOnly(); }));
            _miAuto = new MenuItem(Tr.S("Автоочистка по таймеру", "Auto-clean timer"), delegate { ToggleAuto(); });
            menu.MenuItems.Add(_miAuto);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(new MenuItem(Tr.S("Перезапустить от администратора", "Restart as administrator"), delegate { RestartAsAdmin(); }));
            menu.MenuItems.Add(new MenuItem(Tr.S("Выход", "Exit"), delegate { ExitApp(); }));
            _tray.ContextMenu = menu;
        }

        public void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void ExitApp()
        {
            _reallyExit = true;
            _tray.Visible = false;
            Application.Exit();
        }

        private void ToggleAuto()
        {
            _engine.Config.AutoEnabled = !_engine.Config.AutoEnabled;
            _engine.SaveConfig();
            LoadSettingsToUi();
            RescheduleAuto();
        }

        private void RestartAsAdmin()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                ExitApp();
            }
            catch { /* пользователь отклонил UAC */ }
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
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in byCat) parts.Add(kv.Key + " " + kv.Value);
            sb.Append(string.Join("  ", parts.ToArray()));
            _lblSummary.Text = sb.ToString();
            UpdateTrayState();
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
                MessageBox.Show(Tr.S("Не выбрано ни одного процесса.", "No processes selected."),
                    Tr.S("Очистка", "Clean"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult dr = MessageBox.Show(
                Tr.S("Завершить процессов: ", "Terminate processes: ") + toKill.Count + "?",
                Tr.S("Подтверждение", "Confirm"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dr != DialogResult.Yes) return;
            ExecuteKill(toKill);
        }

        // Автоочистка по кнопке: завершить все найденные неактивные (кандидаты).
        private void DoAutoCleanButton()
        {
            if (_lastScan == null || _lastScan.Count == 0) DoScan();
            List<ProcInfo> cands = _lastScan.Where(p => p.IsCandidate).ToList();
            if (cands.Count == 0)
            {
                MessageBox.Show(Tr.S("Неактивных (заброшенных) процессов не найдено.", "No inactive (abandoned) processes found."),
                    Tr.S("Автоочистка", "Auto-clean"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StringBuilder list = new StringBuilder();
            foreach (ProcInfo p in cands.Take(20)) list.AppendLine("• " + p.Name + " (pid " + p.Pid + ")");
            if (cands.Count > 20) list.AppendLine(Tr.S("… и ещё ", "… and ") + (cands.Count - 20) + Tr.S("", " more"));
            DialogResult dr = MessageBox.Show(
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

        private void RefreshHistory()
        {
            HistoryFile h = _engine.LoadHistory();
            _lvHistory.BeginUpdate();
            try
            {
                _lvHistory.Items.Clear();
                List<ListViewItem> rows = new List<ListViewItem>();
                foreach (HistoryEntry e in h.Entries)
                {
                    ListViewItem it = new ListViewItem(e.DateTime);
                    it.SubItems.Add(e.TerminatedCount.ToString());
                    it.SubItems.Add(Engine.FormatBytes(e.FreedBytes));
                    it.SubItems.Add(e.Processes != null ? string.Join(", ", e.Processes.ToArray()) : "");
                    rows.Add(it);
                }
                _lvHistory.Items.AddRange(rows.ToArray());
            }
            finally { _lvHistory.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvHistory);
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
            if (pids.Count == 0) { MessageBox.Show(Tr.S("Не выбрано ни одного порта.", "No ports selected.")); return; }

            Thread t = new Thread(delegate()
            {
                long freed = 0;
                int killed = 0;
                try { killed = _engine.TerminateMany(pids, out freed); }
                catch { }
                int killedCopy = killed;
                UiPost(delegate
                {
                    MessageBox.Show(Tr.S("Завершено процессов: ", "Terminated: ") + killedCopy +
                        Tr.S("  ·  освобождено ~", "  ·  freed ~") + Engine.FormatBytes(freed),
                        Tr.S("Порты", "Ports"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    RefreshPorts();
                });
            });
            t.IsBackground = true;
            t.Start();
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

            bool autostartChanged = c.Autostart != _chkAutostart.Checked;
            c.Autostart = _chkAutostart.Checked;

            _engine.SaveConfig();
            if (autostartChanged || true) _engine.ApplyAutostart(c.Autostart);
            RescheduleAuto();
            if (_miAuto != null) _miAuto.Checked = c.AutoEnabled;

            MessageBox.Show(Tr.S("Настройки сохранены.", "Settings saved."),
                Tr.S("Настройки", "Settings"), MessageBoxButtons.OK, MessageBoxIcon.Information);

            if (langChanged)
                MessageBox.Show(
                    Tr.S("Язык изменится после перезапуска приложения.",
                         "The language will change after you restart the app."),
                    Tr.S("Язык", "Language"), MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        // ---------- Автоочистка по расписанию ----------
        private void RescheduleAuto()
        {
            if (_engine.Config.AutoEnabled)
                _nextAuto = DateTime.Now.AddHours(_engine.Config.AutoIntervalHours);
            else
                _nextAuto = DateTime.MaxValue;
        }

        private void CheckAutoSchedule()
        {
            if (!_engine.Config.AutoEnabled) return;
            if (DateTime.Now >= _nextAuto)
            {
                RunAutoClean(false);
                _nextAuto = DateTime.Now.AddHours(_engine.Config.AutoIntervalHours);
            }
        }

        // ---------- Трей: индикация ----------
        private void UpdateTrayState()
        {
            int candidates = 0;
            foreach (ProcInfo p in _lastScan) if (p.IsCandidate) candidates++;
            if (_tray == null) return;
            if (candidates > 0)
            {
                _tray.Icon = _iconActive;
                _tray.Text = Tr.S("Process Cleaner · кандидатов: ", "Process Cleaner · candidates: ") + candidates;
            }
            else
            {
                _tray.Icon = _iconIdle;
                _tray.Text = "Windows Process Cleaner";
            }
        }

        private static string YesNo(bool v) { return v ? Tr.S("да", "yes") : Tr.S("нет", "no"); }

        private static string FormatSpan(TimeSpan t)
        {
            string s = Tr.S("с", "s"), m = Tr.S("м", "m"), h = Tr.S("ч", "h");
            if (t.TotalSeconds < 1) return "-";
            if (t.TotalMinutes < 1) return (int)t.TotalSeconds + s;
            if (t.TotalHours < 1) return (int)t.TotalMinutes + m;
            return (int)t.TotalHours + h + " " + t.Minutes + m;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_startHidden)
            {
                Hide();
            }
            _ready = true;
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
        }

        private bool _startHidden = false;
        public void SetStartHidden(bool v) { _startHidden = v; }
        private bool _selfTest = false;
        public void SetSelfTest(bool v) { _selfTest = v; }
    }

    // ------------------------------------------------------------------ //
    //  Точка входа + single-instance через локальный порт
    // ------------------------------------------------------------------ //
    static class Program
    {
        private const int SingleInstancePort = 49876; // обычно свободный порт
        private static MainForm _form;
        private static Mutex _instanceMutex;          // держим ссылку: иначе GC соберёт и снимет владение

        [STAThread]
        static void Main(string[] args)
        {
            bool startTray = args != null && args.Contains("/tray");

            // /auto — тихая очистка диска без окна, для планировщика задач
            // (тот же сценарий, что /AUTO у FluentCleaner). Работает и когда основной
            // экземпляр уже запущен, поэтому проверяется до захвата single-instance.
            if (args != null && (args.Contains("/auto") || args.Contains("/AUTO")))
            {
                RunHeadlessClean();
                return;
            }

            // /analyze — только посчитать и напечатать, ничего не удалять.
            // Нужен, чтобы проверять правила очистки без риска что-то потерять.
            if (args != null && args.Contains("/analyze"))
            {
                RunHeadlessAnalyze();
                return;
            }

            // Признак «я единственный» — именованный мьютекс, а НЕ занятость порта.
            // Порт после аварийного завершения остаётся занятым ещё какое-то время
            // (висящие сокеты в CLOSE_WAIT/TIME_WAIT), и тогда приложение молча
            // не запускалось вообще: bind не удался, значит «уже запущено» — и выход.
            // Мьютекс освобождается ядром сразу, как процесс умер, при любом сценарии.
            bool primary;
            _instanceMutex = new Mutex(true, @"Local\WindowsProcessCleaner.singleinstance", out primary);
            if (!primary)
            {
                NotifyPrimaryShow();
                return;
            }

            // Канал активации — вспомогательный: не смог занять порт, работаем без него.
            TcpListener listener;
            TryBecomePrimary(out listener);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Engine engine = new Engine();
            Tr.En = engine.Config.Language == "en";
            _form = new MainForm(engine);

            if (startTray || engine.Config.StartMinimized)
                _form.SetStartHidden(true);
            if (args != null && args.Contains("/selftest"))
                _form.SetSelfTest(true);

            // Слушаем сигналы "покажись" от повторных запусков
            StartActivationListener(listener);

            Application.Run(_form);
        }

        // Сухой прогон: строит категории, считает размеры и пишет отчёт в файл рядом
        // с конфигом. Ничего не удаляет — это диагностика правил и скорости обхода.
        private static void RunHeadlessAnalyze()
        {
            Engine engine = new Engine();
            Tr.En = engine.Config.Language == "en";
            Stopwatch sw = Stopwatch.StartNew();
            List<CleanCategory> cats = engine.BuildCleanCategories();
            long buildMs = sw.ElapsedMilliseconds;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("build categories: " + buildMs + " ms, categories=" + cats.Count
                          + ", winapp2 rules=" + engine.Winapp2RuleCount);
            sw.Restart();
            engine.AnalyzeCategories(cats, null);
            sb.AppendLine("analyze: " + sw.ElapsedMilliseconds + " ms");

            long total = 0; int files = 0;
            foreach (CleanCategory c in cats)
            {
                total += c.Size; files += c.FileCount;
                sb.AppendLine(Engine.FormatBytes(c.Size).PadLeft(10) + "  " + c.FileCount.ToString().PadLeft(7)
                              + "  " + (c.Recommended ? "[rec] " : "      ") + c.Title
                              + " (targets=" + c.Targets.Count + ")"
                              + (string.IsNullOrEmpty(c.Note) ? "" : "  !" + c.Note));
            }
            sb.AppendLine("TOTAL " + Engine.FormatBytes(total) + "  files=" + files);
            string report = sb.ToString();
            try { File.WriteAllText(Path.Combine(engine.DataDir, "analyze-report.txt"), report, Encoding.UTF8); }
            catch { }
            Console.Write(report);
        }

        // Тихий режим: чистим только рекомендованные категории и пишем результат в лог.
        // Никакого UI — процесс завершается сам, годится для расписания.
        private static void RunHeadlessClean()
        {
            try
            {
                Engine engine = new Engine();
                Tr.En = engine.Config.Language == "en";
                List<CleanCategory> cats = engine.BuildCleanCategories();
                List<CleanCategory> pick = new List<CleanCategory>();
                foreach (CleanCategory c in cats) if (c.Recommended) pick.Add(c);
                engine.AnalyzeCategories(pick, null);
                engine.CleanCategories(pick);
            }
            catch { }
        }

        private static bool TryBecomePrimary(out TcpListener listener)
        {
            listener = null;
            try
            {
                TcpListener l = new TcpListener(IPAddress.Loopback, SingleInstancePort);
                // позволяет занять порт, даже если от прошлого запуска остались
                // недозакрытые сокеты на нём
                l.ExclusiveAddressUse = false;
                l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                l.Start();
                listener = l;
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private static void NotifyPrimaryShow()
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    // Connect без таймаута может висеть десятки секунд; нам нужен
                    // быстрый отказ — окно всё равно покажет уже запущенный экземпляр.
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, SingleInstancePort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1500)) return;
                    c.EndConnect(ar);
                    byte[] msg = Encoding.ASCII.GetBytes("SHOW");
                    c.GetStream().Write(msg, 0, msg.Length);
                }
            }
            catch { }
        }

        private static void StartActivationListener(TcpListener listener)
        {
            if (listener == null) return;
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    TcpClient client;
                    // Ошибка самого listener'а — выходим; ошибка на одном соединении
                    // не должна навсегда лишать приложение канала активации.
                    try { client = listener.AcceptTcpClient(); }
                    catch { break; }

                    // using обязателен: раньше при исключении в Read соединение
                    // оставалось незакрытым и висело в CLOSE_WAIT до конца работы.
                    using (client)
                    {
                        try
                        {
                            byte[] buf = new byte[16];
                            client.ReceiveTimeout = 1000;
                            client.GetStream().Read(buf, 0, buf.Length);
                        }
                        catch { }
                    }
                    try
                    {
                        if (_form != null && !_form.IsDisposed && _form.IsHandleCreated)
                            _form.BeginInvoke((MethodInvoker)delegate { _form.ShowWindow(); });
                    }
                    catch { }
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
