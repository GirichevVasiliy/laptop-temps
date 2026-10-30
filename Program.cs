using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Management.Infrastructure;

namespace LaptopTemps
{
    internal static class Program
    {
        private const string MutexName = "Local\\LaptopTemps.Tray";

        [STAThread]
        private static void Main(string[] args)
        {
            bool dump = Has(args, "--dump");
            bool noAdmin = Has(args, "--no-admin") || dump;
            bool elevated = Has(args, "--elevated");

            if (!noAdmin && !elevated && !IsAdmin())
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Application.ExecutablePath,
                        Arguments = "--elevated",
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                    return;
                }
                catch (Win32Exception)
                {
                    // Пользователь закрыл запрос UAC — работаем с тем, что доступно без прав.
                }
            }

            if (dump)
            {
                var snap = Sampler.Read();
                var text = new StringBuilder();
                foreach (var line in snap.Lines())
                    text.AppendLine(line);
                if (!string.IsNullOrEmpty(snap.Detail))
                    text.AppendLine(snap.Detail);
                string report = text.ToString();
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dump.txt"), report, Encoding.UTF8);
                try
                {
                    Native.AllocConsole();
                    Console.OutputEncoding = Encoding.UTF8;
                    Console.Write(report);
                }
                catch
                {
                    // Отчёт уже записан в dump.txt.
                }
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                    return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }

        private static bool Has(string[] args, string flag)
        {
            foreach (var arg in args)
            {
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        internal static bool IsAdmin()
        {
            var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal sealed class Sensor
    {
        public string Name;
        public double Celsius;
        public bool HasLimit;
        public double Limit;
        public string Extra;
    }

    internal sealed class Fan
    {
        public string Name;
        public int Rpm;
    }

    internal sealed class Snapshot
    {
        public DateTime At;
        public List<Sensor> Sensors = new List<Sensor>();
        public List<Fan> Fans = new List<Fan>();
        public bool BoostKnown;
        public bool BoostOn;
        public string Error;
        public string Detail;

        public IEnumerable<string> Lines()
        {
            yield return At.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + (Program.IsAdmin() ? "  admin" : "  user");
            foreach (var s in Sensors)
            {
                string limit = s.HasLimit ? "  предел " + s.Limit.ToString("0", CultureInfo.InvariantCulture) : "";
                string extra = string.IsNullOrEmpty(s.Extra) ? "" : "  " + s.Extra;
                yield return s.Name + "  " + s.Celsius.ToString("0.0", CultureInfo.InvariantCulture) + " °C" + limit + extra;
            }
            foreach (var f in Fans)
                yield return f.Name + "  " + f.Rpm.ToString(CultureInfo.InvariantCulture) + " об/мин";
            if (BoostKnown)
                yield return "Cooler Boost  " + (BoostOn ? "включен" : "выключен");
            if (!string.IsNullOrEmpty(Error))
                yield return Error;
        }

        public Sensor HottestApproach(double margin)
        {
            Sensor best = null;
            double bestGap = double.MaxValue;
            foreach (var s in Sensors)
            {
                if (!s.HasLimit)
                    continue;
                double gap = s.Limit - s.Celsius;
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = s;
                }
            }
            if (best != null && best.Limit - best.Celsius <= margin)
                return best;
            return null;
        }
    }

    internal sealed class AppConfig
    {
        public int MarginC = 8;
        public int CpuLimitC = 100;
        public int ZoneLimitC = 90;
        public bool StartWithWindows;
        public bool AutoBoost;

        private static string PathFile
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaptopTemps");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "config.json");
            }
        }

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(PathFile))
                    return new AppConfig();
                var ser = new JavaScriptSerializer();
                var cfg = ser.Deserialize<AppConfig>(File.ReadAllText(PathFile));
                if (cfg == null)
                    return new AppConfig();
                if (cfg.MarginC < 3) cfg.MarginC = 3;
                if (cfg.MarginC > 25) cfg.MarginC = 25;
                if (cfg.CpuLimitC < 70) cfg.CpuLimitC = 70;
                if (cfg.CpuLimitC > 115) cfg.CpuLimitC = 115;
                return cfg;
            }
            catch
            {
                return new AppConfig();
            }
        }

        public void Save()
        {
            var ser = new JavaScriptSerializer();
            File.WriteAllText(PathFile, ser.Serialize(this));
        }
    }

    internal static class Sampler
    {
        public static Snapshot Read()
        {
            var snap = new Snapshot { At = DateTime.Now };
            var notes = new StringBuilder();

            try { AcpiReader.Read(snap); }
            catch (Exception ex) { notes.AppendLine("Термозона: " + ex.Message); }

            try { NvidiaReader.Read(snap); }
            catch (Exception ex) { notes.AppendLine("GPU: " + ex.Message); }

            try { NvmeReader.Read(snap); }
            catch (Exception ex) { notes.AppendLine("SSD: " + ex.Message); }

            try { MsiLink.Read(snap, notes); }
            catch (Exception ex) { notes.AppendLine("MSI: " + ex.Message); }

            var cfg = AppConfig.Load();
            foreach (var s in snap.Sensors)
            {
                if (s.Name.StartsWith("CPU", StringComparison.Ordinal) && !s.HasLimit)
                {
                    s.HasLimit = true;
                    s.Limit = cfg.CpuLimitC;
                }
                if (s.Name.StartsWith("Термозона", StringComparison.Ordinal) && !s.HasLimit)
                {
                    s.HasLimit = true;
                    s.Limit = cfg.ZoneLimitC;
                }
            }

            snap.Error = notes.ToString().Trim();
            return snap;
        }
    }

    internal static class AcpiReader
    {
        public static void Read(Snapshot snap)
        {
            var category = new PerformanceCounterCategory("Thermal Zone Information");
            foreach (string instance in category.GetInstanceNames())
            {
                float raw;
                try
                {
                    using (var counter = new PerformanceCounter("Thermal Zone Information", "High Precision Temperature", instance, true))
                        raw = counter.NextValue();
                }
                catch
                {
                    using (var counter = new PerformanceCounter("Thermal Zone Information", "Temperature", instance, true))
                        raw = counter.NextValue();
                }

                double celsius;
                if (raw > 1500)
                    celsius = raw / 10.0 - 273.15;
                else if (raw > 150)
                    celsius = raw - 273.15;
                else
                    celsius = raw;

                if (celsius < -20 || celsius > 125)
                    continue;

                snap.Sensors.Add(new Sensor
                {
                    Name = "Термозона " + instance.Trim('\\').Replace("_TZ.", ""),
                    Celsius = celsius
                });
            }
        }
    }

    internal static class NvidiaReader
    {
        public static void Read(Snapshot snap)
        {
            string exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (!File.Exists(exe))
                return;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--query-gpu=name,temperature.gpu,temperature.gpu.tlimit --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    return;
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(8000))
                {
                    try { process.Kill(); } catch { }
                    return;
                }

                foreach (string rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = rawLine.Split(',');
                    if (parts.Length < 2)
                        continue;
                    string name = parts[0].Trim();
                    double temp;
                    if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out temp))
                        continue;
                    if (temp < 0 || temp > 120)
                        continue;

                    var sensor = new Sensor
                    {
                        Name = "GPU " + ShortGpu(name),
                        Celsius = temp
                    };
                    double tlimit;
                    if (parts.Length >= 3 &&
                        double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out tlimit) &&
                        tlimit >= 0 && tlimit < 80)
                    {
                        sensor.HasLimit = true;
                        sensor.Limit = temp + tlimit;
                        sensor.Extra = "до предела " + tlimit.ToString("0", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        sensor.HasLimit = true;
                        sensor.Limit = 87;
                    }
                    snap.Sensors.Add(sensor);
                }
            }
        }

        private static string ShortGpu(string name)
        {
            const string marker = "GeForce ";
            int i = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
                name = name.Substring(i + marker.Length);
            name = name.Replace(" Laptop GPU", "");
            return name.Trim();
        }
    }

    internal static class NvmeReader
    {
        public static void Read(Snapshot snap)
        {
            using (var session = CimSession.Create(null))
            {
                var disks = session.QueryInstances(@"root\Microsoft\Windows\Storage", "WQL", "SELECT FriendlyName, BusType FROM MSFT_PhysicalDisk");
                foreach (CimInstance disk in disks)
                {
                    string name = Convert.ToString(disk.CimInstanceProperties["FriendlyName"].Value) ?? "Диск";
                    int bus = Convert.ToInt32(disk.CimInstanceProperties["BusType"].Value ?? 0);
                    var counters = session.EnumerateAssociatedInstances(
                        @"root\Microsoft\Windows\Storage",
                        disk,
                        null,
                        "MSFT_StorageReliabilityCounter",
                        null,
                        null);
                    foreach (CimInstance counter in counters)
                    {
                        object tempValue = counter.CimInstanceProperties["Temperature"].Value;
                        if (tempValue == null)
                            continue;
                        int temp = Convert.ToInt32(tempValue);
                        if (temp <= 0 || temp > 120)
                            continue;
                        var sensor = new Sensor
                        {
                            Name = "SSD " + Clean(name),
                            Celsius = temp
                        };
                        if (bus == 17)
                        {
                            sensor.HasLimit = true;
                            sensor.Limit = 85;
                            object maxValue = counter.CimInstanceProperties["TemperatureMax"].Value;
                            if (maxValue != null)
                            {
                                int max = Convert.ToInt32(maxValue);
                                if (max >= 50 && max <= 120)
                                    sensor.Limit = max;
                            }
                        }
                        snap.Sensors.Add(sensor);
                    }
                }
            }
        }

        private static string Clean(string name)
        {
            name = name.Replace("NVMe ", "").Trim();
            if (name.Length > 32)
                name = name.Substring(0, 32).Trim();
            return name;
        }
    }

    internal static class Native
    {
        [DllImport("kernel32.dll")]
        public static extern bool AllocConsole();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr handle);
    }

    /// <summary>
    /// Читает датчики MSI и переключает только бит Cooler Boost.
    /// Кривые вентиляторов и сценарий MSI Center не записываются.
    /// </summary>
    internal static class MsiLink
    {
        private const string DllPath = @"C:\Program Files (x86)\MSI\MSI Center\MSIWMIACPI2.dll";
        private const int GetThermal = 12;
        private const int GetFan = 14;
        private const int SetData = 25;
        private const byte BoostAddress = 0x98;
        private const byte BoostBit = 0x80;

        private const int GetTemperature = 10;
        private static readonly object Gate = new object();
        private static object _lib;
        private static MethodInfo _invoke;
        private static Type _methods;
        private static Type _resultType;
        private static string _openError;

        public static void Read(Snapshot snap, StringBuilder notes)
        {
            if (!Ensure(notes))
                return;

            try
            {
                byte[] thermal = Call(GetThermal, 3, 7, notes, "Cooler Boost");
                if (thermal != null && thermal.Length > 0)
                {
                    snap.BoostKnown = true;
                    snap.BoostOn = (thermal[0] & BoostBit) != 0;
                }
            }
            catch (Exception ex)
            {
                notes.AppendLine("Cooler Boost: " + ex.Message);
            }

            try
            {
                byte[] temps = Call(GetTemperature, 0, 32, notes, "температура");
                if (temps != null && temps.Length > 0 && temps[0] > 0 && temps[0] < 125)
                    snap.Sensors.Add(new Sensor { Name = "CPU", Celsius = temps[0] });
                if (temps != null && temps.Length > 1 && temps[1] > 0 && temps[1] < 125 && !HasSimilar(snap, "GPU", temps[1]))
                    snap.Sensors.Add(new Sensor { Name = "GPU контроллера", Celsius = temps[1] });
            }
            catch (Exception ex)
            {
                notes.AppendLine("Температура: " + ex.Message);
            }

            try
            {
                byte[] fan = Call(GetFan, 0, 8, notes, "вентиляторы");
                if (fan != null)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        int offset = i * 2;
                        if (offset + 1 >= fan.Length)
                            break;
                        int? rpm = FanRpm(fan[offset], fan[offset + 1]);
                        if (rpm == null)
                            continue;
                        snap.Fans.Add(new Fan
                        {
                            Name = "Вентилятор " + (i + 1).ToString(CultureInfo.InvariantCulture),
                            Rpm = rpm.Value
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                notes.AppendLine("Вентиляторы: " + ex.Message);
            }
        }

        private static bool HasSimilar(Snapshot snap, string prefix, int celsius)
        {
            foreach (var sensor in snap.Sensors)
            {
                if (sensor.Name.StartsWith(prefix, StringComparison.Ordinal) && Math.Abs(sensor.Celsius - celsius) <= 3)
                    return true;
            }
            return false;
        }

        public static bool SetBoost(bool on, out string error)
        {
            error = null;
            var notes = new StringBuilder();
            lock (Gate)
            {
                if (!Ensure(notes))
                {
                    error = notes.ToString().Trim();
                    return false;
                }

                byte[] thermal = Call(GetThermal, 3, 7, notes, "чтение");
                if (thermal == null || thermal.Length == 0)
                {
                    error = "Не удалось прочитать состояние охлаждения. Запись не выполнялась.";
                    return false;
                }

                byte current = thermal[0];
                byte next = on ? (byte)(current | BoostBit) : (byte)(current & ~BoostBit);
                if (next == current)
                    return true;

                byte[] written = Call(SetData, BoostAddress, new[] { next }, notes, "запись");
                if (written == null)
                {
                    error = string.IsNullOrWhiteSpace(notes.ToString())
                        ? "Контроллер не принял переключение Cooler Boost."
                        : notes.ToString().Trim();
                    return false;
                }

                byte[] check = Call(GetThermal, 3, 7, notes, "проверка");
                if (check == null || ((check[0] & BoostBit) != 0) != on)
                {
                    error = "Состояние Cooler Boost не подтвердилось.";
                    return false;
                }
                return true;
            }
        }

        private static bool Ensure(StringBuilder notes)
        {
            if (_lib != null)
                return true;
            if (_openError != null)
            {
                notes.AppendLine(_openError);
                return false;
            }
            if (!File.Exists(DllPath))
            {
                _openError = "Не найден MSI Center, вентиляторы и Cooler Boost недоступны.";
                notes.AppendLine(_openError);
                return false;
            }

            try
            {
                var asm = Assembly.LoadFrom(DllPath);
                var type = asm.GetType("MSIWMIACPI2.WmiAcpiLib2");
                _methods = type.GetNestedType("Methods", BindingFlags.Public | BindingFlags.NonPublic);
                _invoke = null;
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (method.Name != "InvokeWmiMethod")
                        continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 3 && parameters[0].ParameterType == _methods)
                    {
                        _invoke = method;
                        break;
                    }
                }
                if (_invoke == null)
                    throw new InvalidOperationException("В библиотеке MSI нет вызова WMI.");

                // false: не ставить и не переустанавливать службу MSI.
                _lib = Activator.CreateInstance(type, new object[] { false });
                return true;
            }
            catch (Exception ex)
            {
                _openError = "MSI WMI: " + Unwrap(ex);
                notes.AppendLine(_openError);
                _lib = null;
                return false;
            }
        }

        private static byte[] Call(int method, byte argument, int length, StringBuilder notes, string what)
        {
            return Call(method, argument, new byte[length], notes, what);
        }

        private static byte[] Call(int method, byte argument, byte[] data, StringBuilder notes, string what)
        {
            object enumValue = Enum.ToObject(_methods, method);
            object result;
            try
            {
                result = _invoke.Invoke(_lib, new object[] { enumValue, argument, data });
            }
            catch (TargetInvocationException ex)
            {
                notes.AppendLine(what + ": " + Unwrap(ex));
                return null;
            }

            if (result == null)
            {
                notes.AppendLine(what + ": пустой ответ.");
                return null;
            }

            _resultType = result.GetType();
            int flag = Convert.ToInt32(Field(result, "Flag") ?? 0);
            var bytes = Field(result, "Data") as byte[];
            // У этой библиотеки 0 — ошибка, ненулевой флаг — данные получены.
            if (flag == 0)
            {
                string message = Field(result, "Message") as string;
                notes.AppendLine(what + ": код " + flag + (string.IsNullOrEmpty(message) ? "" : " " + message));
                return null;
            }
            return bytes;
        }

        private static string Describe(object result)
        {
            var text = new StringBuilder();
            foreach (var field in result.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object value = field.GetValue(result);
                text.Append(field.Name).Append('=');
                var bytes = value as byte[];
                if (bytes != null)
                    text.Append(Hex(bytes, 16));
                else
                    text.Append(value);
                text.Append("; ");
            }
            return text.ToString();
        }

        private static object Field(object instance, string name)
        {
            var field = _resultType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(instance);
        }

        private static void ReadClass(Snapshot snap, StringBuilder notes, string className, string property, string label)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT " + property + " FROM " + className))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        if (item[property] == null)
                            continue;
                        int value = Convert.ToInt32(item[property]);
                        if (value <= 0 || value >= 125)
                            continue;
                        bool exists = false;
                        foreach (var sensor in snap.Sensors)
                        {
                            if (sensor.Name == label)
                            {
                                exists = true;
                                break;
                            }
                        }
                        if (exists)
                            continue;
                        snap.Sensors.Add(new Sensor { Name = label, Celsius = value });
                    }
                }
            }
            catch (Exception ex)
            {
                notes.AppendLine(label + ": " + Unwrap(ex));
            }
        }

        private static void Remember(Snapshot snap, string name, byte[] data)
        {
            snap.Detail = (snap.Detail ?? "") + name + " " + Hex(data, data == null ? 0 : Math.Min(data.Length, 48)) + "\r\n";
        }

        private static bool AllZero(byte[] data)
        {
            if (data == null || data.Length == 0)
                return true;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != 0)
                    return false;
            }
            return true;
        }

        private static int? FanRpm(byte hi, byte lo)
        {
            int raw = (hi << 8) | lo;
            if (raw == 0)
                return null;
            int rpm = (int)(60000000.0 / (raw * 2.0 * 62.5));
            if (rpm < 200 || rpm > 12000)
                return null;
            return rpm;
        }

        private static string Hex(byte[] data, int max)
        {
            if (data == null || max <= 0)
                return "-";
            int n = Math.Min(data.Length, max);
            var text = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (i > 0) text.Append(' ');
                text.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        private static string Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null)
                return ex.InnerException.Message;
            return ex.Message;
        }
    }

    internal sealed class WorkingPoint
    {
        public double Celsius;
        public bool Frozen;
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly MainForm _form;
        private readonly object _gate = new object();
        private Snapshot _last = new Snapshot();
        private bool _hot;
        private bool _busy;
        private bool _weEnabledBoost;
        private bool _autoEnabled;
        private bool _autoHolding;
        private readonly Dictionary<string, WorkingPoint> _working = new Dictionary<string, WorkingPoint>();

        public TrayApp()
        {
            _form = new MainForm(ToggleBoost, SaveSettings, SetAuto);
            _autoEnabled = AppConfig.Load().AutoBoost;
            _form.SetAutoState(_autoEnabled, _autoEnabled
                ? "Автотурбо следит за датчиками."
                : "Автотурбо выключено.");
            _icon = new NotifyIcon
            {
                Icon = DrawIcon(Color.FromArgb(70, 130, 180), null),
                Visible = true,
                Text = "Температуры"
            };
            _icon.DoubleClick += delegate { ShowForm(); };
            _icon.ContextMenuStrip = BuildMenu();

            _timer = new System.Windows.Forms.Timer { Interval = 2000 };
            _timer.Tick += delegate { Poll(); };
            _timer.Start();
            Poll();
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Открыть");
            open.Click += delegate { ShowForm(); };
            var exit = new ToolStripMenuItem("Выход");
            exit.Click += delegate { ExitThread(); };
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);
            menu.Opening += delegate { RefreshMenu(menu); };
            return menu;
        }

        private void RefreshMenu(ContextMenuStrip menu)
        {
            while (menu.Items.Count > 3)
                menu.Items.RemoveAt(1);

            Snapshot snap;
            lock (_gate) snap = _last;

            var boost = new ToolStripMenuItem("Максимальное охлаждение")
            {
                CheckOnClick = false,
                Checked = snap.BoostOn,
                Enabled = snap.BoostKnown
            };
            boost.Click += delegate { ToggleBoost(!snap.BoostOn); };
            var auto = new ToolStripMenuItem("Автотурбо по уставкам")
            {
                Checked = _autoEnabled
            };
            auto.Click += delegate { SetAuto(!_autoEnabled); };
            var startup = new ToolStripMenuItem("Запускать вместе с Windows")
            {
                Checked = Startup.IsInstalled()
            };
            startup.Click += delegate
            {
                try
                {
                    if (startup.Checked)
                        Startup.Remove();
                    else
                        Startup.Install();
                    var cfg = AppConfig.Load();
                    cfg.StartWithWindows = Startup.IsInstalled();
                    cfg.Save();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(_form, ex.Message, "Автозапуск", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            menu.Items.Insert(1, startup);
            menu.Items.Insert(1, auto);
            menu.Items.Insert(1, boost);
        }

        private void Poll()
        {
            if (_busy)
                return;
            _busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Snapshot snap;
                try { snap = Sampler.Read(); }
                catch (Exception ex)
                {
                    snap = new Snapshot { At = DateTime.Now, Error = ex.Message };
                }
                try
                {
                    _form.BeginInvoke((Action)delegate { Apply(snap); });
                }
                catch
                {
                    _busy = false;
                }
            });
        }

        private void Apply(Snapshot snap)
        {
            _busy = false;
            lock (_gate) _last = snap;
            var cfg = AppConfig.Load();
            DriveAuto(snap, cfg.MarginC);
            _form.SetAutoState(_autoEnabled, _autoNote);
            _form.ShowSnapshot(snap);

            var hot = snap.HottestApproach(cfg.MarginC);
            if (hot != null && !_hot)
            {
                _hot = true;
                ShowForm();
            }
            else if (hot == null && _hot && CooledDown(snap, cfg.MarginC + 4))
            {
                _hot = false;
            }

            Color color = hot != null ? Color.FromArgb(200, 60, 40) : Color.FromArgb(40, 120, 70);
            string tip = Tip(snap, hot);
            Icon next = DrawIcon(color, Hottest(snap));
            Icon previous = _icon.Icon;
            _icon.Icon = next;
            if (previous != null)
                previous.Dispose();
            _icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        private static bool CooledDown(Snapshot snap, double margin)
        {
            foreach (var sensor in snap.Sensors)
            {
                if (sensor.HasLimit && sensor.Limit - sensor.Celsius <= margin)
                    return false;
            }
            return true;
        }

        private static Sensor Hottest(Snapshot snap)
        {
            Sensor best = null;
            foreach (var sensor in snap.Sensors)
            {
                if (best == null || sensor.Celsius > best.Celsius)
                    best = sensor;
            }
            return best;
        }

        private static string Tip(Snapshot snap, Sensor hot)
        {
            var text = new StringBuilder();
            foreach (var sensor in snap.Sensors)
            {
                if (text.Length > 0) text.Append("  ");
                text.Append(sensor.Name);
                text.Append(' ');
                text.Append(sensor.Celsius.ToString("0", CultureInfo.InvariantCulture));
                text.Append('°');
                if (text.Length > 50)
                    break;
            }
            if (hot != null)
                text.Append("  нагрев");
            if (text.Length == 0)
                text.Append("Температуры");
            return text.ToString();
        }

        private void ShowForm()
        {
            Snapshot snap;
            lock (_gate) snap = _last;
            _form.ShowSnapshot(snap);
            _form.Show();
            _form.WindowState = FormWindowState.Normal;
            _form.Activate();
        }

        private void ToggleBoost(bool on)
        {
            string error;
            bool ok = MsiLink.SetBoost(on, out error);
            if (!ok)
            {
                MessageBox.Show(_form, error ?? "Не удалось переключить охлаждение.", "Cooler Boost", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (on)
                _weEnabledBoost = true;
            if (!on)
            {
                _weEnabledBoost = false;
                _autoHolding = false;
            }
            Poll();
        }

        private string _autoNote = "";

        private void SetAuto(bool on)
        {
            _autoEnabled = on;
            var cfg = AppConfig.Load();
            cfg.AutoBoost = on;
            cfg.Save();
            if (!on)
            {
                _working.Clear();
                if (_autoHolding)
                {
                    _autoHolding = false;
                    string error;
                    if (MsiLink.SetBoost(false, out error))
                        _weEnabledBoost = false;
                    else
                        _autoNote = error;
                }
            }
            if (string.IsNullOrEmpty(_autoNote) || on)
                _autoNote = on ? "Автотурбо следит за датчиками." : "Автотурбо выключено.";
            _form.SetAutoState(on, _autoNote);
            Poll();
        }

        private void DriveAuto(Snapshot snap, int margin)
        {
            if (!_autoEnabled)
            {
                _autoNote = "Автотурбо выключено.";
                return;
            }
            if (!snap.BoostKnown)
            {
                _autoNote = "Автотурбо ждёт данные контроллера.";
                return;
            }

            bool anyHot = false;
            bool allBack = true;
            bool anyLimited = false;
            foreach (var sensor in snap.Sensors)
            {
                if (!sensor.HasLimit)
                    continue;
                anyLimited = true;
                double warning = sensor.Limit - margin;
                if (warning < 1)
                    warning = 1;
                WorkingPoint point;
                if (!_working.TryGetValue(sensor.Name, out point))
                {
                    point = new WorkingPoint { Celsius = sensor.Celsius };
                    _working[sensor.Name] = point;
                }
                else if (!_autoHolding)
                {
                    if (sensor.Celsius <= point.Celsius + 0.5)
                    {
                        point.Celsius = sensor.Celsius;
                        point.Frozen = false;
                    }
                    else if (!point.Frozen && sensor.Celsius >= point.Celsius + 4)
                    {
                        point.Frozen = true;
                    }
                }

                if (sensor.Celsius >= warning)
                    anyHot = true;
                if (sensor.Celsius > point.Celsius + 1.0)
                    allBack = false;
                sensor.Extra = "уст. " + warning.ToString("0", CultureInfo.InvariantCulture)
                    + "  раб. " + point.Celsius.ToString("0", CultureInfo.InvariantCulture);
            }

            if (!anyLimited)
            {
                _autoNote = "Нет датчиков с пределом.";
                return;
            }

            if (anyHot && !_autoHolding)
            {
                if (!snap.BoostOn)
                {
                    string error;
                    if (!MsiLink.SetBoost(true, out error))
                    {
                        _autoNote = error;
                        return;
                    }
                    snap.BoostOn = true;
                    _weEnabledBoost = true;
                }
                _autoHolding = true;
                foreach (var point in _working.Values)
                    point.Frozen = true;
                _autoNote = "Турбо включено: датчик дошёл до своей уставки.";
            }
            else if (_autoHolding && allBack)
            {
                if (snap.BoostOn)
                {
                    string error;
                    if (!MsiLink.SetBoost(false, out error))
                    {
                        _autoNote = error;
                        return;
                    }
                    snap.BoostOn = false;
                    _weEnabledBoost = false;
                }
                _autoHolding = false;
                foreach (var point in _working.Values)
                    point.Frozen = false;
                _autoNote = "Турбо выключено: температуры вернулись к рабочим.";
            }
            else if (_autoHolding)
            {
                _autoNote = "Турбо включено, ждём возврата к рабочим температурам.";
            }
            else
            {
                _autoNote = "Автотурбо следит. Включение на уставке, выключение на рабочей температуре до роста.";
            }
        }

        private void SaveSettings()
        {
            Poll();
        }

        private static Icon DrawIcon(Color color, Sensor hottest)
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(color))
                    g.FillEllipse(brush, 2, 2, 28, 28);
                if (hottest != null)
                {
                    string text = hottest.Celsius.ToString("0", CultureInfo.InvariantCulture);
                    using (var font = new Font("Segoe UI", text.Length > 2 ? 8f : 10f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(Color.White))
                    {
                        var size = g.MeasureString(text, font);
                        g.DrawString(text, font, brush, (32 - size.Width) / 2f, (32 - size.Height) / 2f);
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle))
                        return (Icon)temp.Clone();
                }
                finally
                {
                    Native.DestroyIcon(handle);
                }
            }
        }

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            if (_weEnabledBoost)
            {
                string error;
                MsiLink.SetBoost(false, out error);
            }
            _icon.Visible = false;
            _icon.Dispose();
            _form.RealClose();
            base.ExitThreadCore();
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly ListView _list;
        private readonly Button _boost;
        private readonly Button _auto;
        private readonly Label _hint;
        private readonly Label _status;
        private readonly NumericUpDown _margin;
        private readonly NumericUpDown _cpuLimit;
        private readonly Action<bool> _toggle;
        private readonly Action<bool> _setAuto;
        private readonly Action _saved;
        private bool _boostOn;
        private bool _boostKnown;
        private bool _autoOn;
        private string _autoNote = "";
        private bool _loading = true;

        public MainForm(Action<bool> toggle, Action saved, Action<bool> setAuto)
        {
            _toggle = toggle;
            _saved = saved;
            _setAuto = setAuto;
            Text = "Температуры ноутбука";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 660);
            Font = new Font("Segoe UI", 10f);
            ShowInTaskbar = true;

            var title = new Label
            {
                Text = "Датчики",
                Dock = DockStyle.Top,
                Height = 32,
                Padding = new Padding(12, 8, 0, 0),
                Font = new Font("Segoe UI", 12f, FontStyle.Bold)
            };

            _list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill
            };
            _list.Columns.Add("Датчик", 190);
            _list.Columns.Add("Значение", 340);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 292, Padding = new Padding(12) };

            _boost = new Button
            {
                Text = "Включить максимальное охлаждение",
                Dock = DockStyle.Top,
                Height = 36
            };
            _boost.Click += delegate
            {
                if (_boostKnown)
                    _toggle(!_boostOn);
            };

            _auto = new Button
            {
                Text = "Включить автотурбо по уставкам",
                Dock = DockStyle.Top,
                Height = 36
            };
            _auto.Click += delegate { _setAuto(!_autoOn); };

            var autoHint = new Label
            {
                Text = "Для каждого датчика уставка — это его предел минус запас. Турбо включается на уставке и выключается, когда все датчики вернутся к температуре до роста.",
                Dock = DockStyle.Top,
                Height = 52,
                Padding = new Padding(0, 4, 0, 0)
            };

            _hint = new Label
            {
                Text = "Выключение возвращает управление штатному ПО MSI Center. Кривые вентиляторов эта программа не меняет.",
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(0, 6, 0, 0)
            };

            var marginRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            marginRow.Controls.Add(new Label { Text = "Запас до предела", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            _margin = new NumericUpDown { Minimum = 3, Maximum = 20, Width = 50 };
            marginRow.Controls.Add(_margin);
            marginRow.Controls.Add(new Label { Text = "°C", AutoSize = true, Padding = new Padding(4, 6, 8, 0) });
            marginRow.Controls.Add(new Label { Text = "Предел CPU", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            _cpuLimit = new NumericUpDown { Minimum = 70, Maximum = 115, Width = 60 };
            marginRow.Controls.Add(_cpuLimit);

            _status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                ForeColor = Color.DimGray
            };

            bottom.Controls.Add(_boost);
            bottom.Controls.Add(_hint);
            bottom.Controls.Add(_auto);
            bottom.Controls.Add(autoHint);
            bottom.Controls.Add(_status);
            bottom.Controls.Add(marginRow);

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(title);

            var cfg = AppConfig.Load();
            _margin.Value = cfg.MarginC;
            _cpuLimit.Value = cfg.CpuLimitC;
            _margin.ValueChanged += delegate { Persist(); };
            _cpuLimit.ValueChanged += delegate { Persist(); };
            _loading = false;

            FormClosing += OnClosing;
        }

        public void RealClose()
        {
            FormClosing -= OnClosing;
            Close();
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        }

        private void Persist()
        {
            if (_loading)
                return;
            var cfg = AppConfig.Load();
            cfg.MarginC = (int)_margin.Value;
            cfg.CpuLimitC = (int)_cpuLimit.Value;
            cfg.Save();
            if (_saved != null)
                _saved();
        }

        public void ShowSnapshot(Snapshot snap)
        {
            _boostKnown = snap.BoostKnown;
            _boostOn = snap.BoostOn;
            _boost.Enabled = snap.BoostKnown;
            _boost.Text = !snap.BoostKnown
                ? "Cooler Boost недоступен"
                : snap.BoostOn
                    ? "Выключить максимальное охлаждение"
                    : "Включить максимальное охлаждение";

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var sensor in snap.Sensors)
            {
                string value = sensor.Celsius.ToString("0.0", CultureInfo.InvariantCulture) + " °C";
                if (sensor.HasLimit)
                    value += "   / " + sensor.Limit.ToString("0", CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(sensor.Extra))
                    value += "   " + sensor.Extra;
                var item = new ListViewItem(sensor.Name);
                item.SubItems.Add(value);
                if (sensor.HasLimit && sensor.Limit - sensor.Celsius <= (double)_margin.Value)
                    item.ForeColor = Color.Firebrick;
                _list.Items.Add(item);
            }
            foreach (var fan in snap.Fans)
            {
                var item = new ListViewItem(fan.Name);
                item.SubItems.Add(fan.Rpm.ToString("N0", CultureInfo.CurrentCulture) + " об/мин");
                _list.Items.Add(item);
            }
            if (snap.Sensors.Count == 0 && snap.Fans.Count == 0)
            {
                var item = new ListViewItem("Нет данных");
                item.SubItems.Add("");
                _list.Items.Add(item);
            }
            _list.EndUpdate();

            string state = !snap.BoostKnown
                ? "Состояние Cooler Boost неизвестно."
                : snap.BoostOn
                    ? "Максимальное охлаждение включено этой программой или MSI Center."
                    : "Cooler Boost выключен. Вентиляторами управляет MSI Center.";
            if (!string.IsNullOrEmpty(_autoNote))
                state += "  " + _autoNote;
            if (!string.IsNullOrEmpty(snap.Error))
                state += "  " + snap.Error.Replace("\r", " ").Replace("\n", " ");
            _status.Text = state;
        }

        public void SetAutoState(bool enabled, string note)
        {
            _autoOn = enabled;
            _autoNote = note ?? "";
            if (_auto != null)
                _auto.Text = enabled ? "Выключить автотурбо" : "Включить автотурбо по уставкам";
        }
    }

    internal static class Startup
    {
        private const string TaskName = "LaptopTemps";

        public static bool IsInstalled()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Query /TN " + TaskName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(psi))
            {
                if (process == null)
                    return false;
                process.WaitForExit(5000);
                return process.ExitCode == 0;
            }
        }

        public static void Install()
        {
            if (!Program.IsAdmin())
                throw new InvalidOperationException("Автозапуск без запроса UAC ставится только из уже запущенной от администратора программы.");

            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string exe = Application.ExecutablePath;
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><Description>Температуры ноутбука</Description></RegistrationInfo>" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + Xml(user) + "</UserId></LogonTrigger></Triggers>" +
                "<Principals><Principal><UserId>" + Xml(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>" +
                "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled>" +
                "<Hidden>false</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + Xml(exe) + "</Command><Arguments>--elevated</Arguments></Exec></Actions>" +
                "</Task>";

            string file = Path.Combine(Path.GetTempPath(), "LaptopTemps-task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Create /F /TN " + TaskName + " /XML \"" + file + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using (var process = Process.Start(psi))
            {
                string err = process.StandardError.ReadToEnd();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(8000);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException((err + " " + output).Trim());
            }
        }

        public static void Remove()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Delete /F /TN " + TaskName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(psi))
            {
                string err = process.StandardError.ReadToEnd();
                process.WaitForExit(8000);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(err.Trim());
            }
        }

        private static string Xml(string text)
        {
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
