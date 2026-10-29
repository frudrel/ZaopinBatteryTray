// Zaopin wireless mouse battery level in the Windows notification area.
// The 2.4 GHz receiver (CompX chipset, USB VID 3554) answers a vendor HID
// command with the mouse's battery percentage and voltage.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: System.Reflection.AssemblyTitle("Zaopin Battery Tray")]

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--test")
        {
            // Console check: prints one reading and exits.
            var st = BatteryReader.Read();
            AttachConsole(-1);
            Console.WriteLine("\nconnected={0} percent={1} charging={2} mV={3} name={4} error={5}", st.Connected, st.Percent, st.Charging, st.Millivolts, st.Name, st.Error);
            return;
        }
        if (args.Length > 0 && args[0] == "--download")
        {
            AttachConsole(-1);
            Console.WriteLine("\nsaved {0} files to {1}", HubCache.DownloadAll(), HubCache.Root);
            return;
        }
        if (args.Length > 0 && args[0] == "--serve")
        {
            AttachConsole(-1);
            Console.WriteLine("\nserving the offline hub at {0} (Ctrl+C to stop)", HubServer.Start());
            System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
        }

        bool created;
        using (var mutex = new System.Threading.Mutex(true, "ZaopinBatteryTray_SingleInstance", out created))
        {
            if (!created) return;
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.Run(new TrayContext());
        }
    }

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
}

class BatteryStatus
{
    public bool Connected;        // receiver found and the mouse answered
    public bool ReceiverFound;
    public int Percent = -1;      // -1 = unknown
    public int Millivolts;
    public bool Charging;
    public string Name = "Zaopin mouse";
    public string Error;          // why the last read failed, for the log
}

class TrayContext : ApplicationContext
{
    const int PollMs = 60000;
    const int LowThreshold = 20;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "ZaopinBatteryTray";

    readonly NotifyIcon tray = new NotifyIcon();
    readonly Timer timer = new Timer();
    readonly ToolStripMenuItem startupItem;
    readonly ToolStripMenuItem statusItem = new ToolStripMenuItem { Enabled = false };
    readonly ToolStripMenuItem estimateItem = new ToolStripMenuItem { Enabled = false };
    readonly DrainTracker drain = new DrainTracker();
    readonly ControlHub hub = new ControlHub();
    readonly ToolStripMenuItem updateHubItem;
    readonly System.Threading.SynchronizationContext ui;
    Icon currentIcon;
    bool lowWarned;
    int lastPercent = -1;
    int missedReads;
    BatteryStatus lastGood;
    const int MissesBeforeAsleep = 3;   // polls in a row (3 minutes) before calling the mouse unresponsive

    public TrayContext()
    {
        var menu = new ContextMenuStrip();
        ui = System.Threading.SynchronizationContext.Current;
        menu.Items.Add(statusItem);
        menu.Items.Add(estimateItem);
        menu.Items.Add(new ToolStripSeparator());
        var hubItem = new ToolStripMenuItem("Open Control Hub", null, delegate { OpenHub(); });
        hubItem.Font = new Font(hubItem.Font, FontStyle.Bold);
        menu.Items.Add(hubItem);
        updateHubItem = new ToolStripMenuItem("Update offline copy", null, delegate { DownloadHub(true); });
        menu.Items.Add(updateHubItem);
        menu.Items.Add("Refresh now", null, delegate { Update(); });
        startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
        startupItem.Checked = IsStartupEnabled();
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { ExitThread(); });

        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Update(); };
        tray.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenHub(); };
        hub.Closed += delegate { ui.Post(delegate { Update(); }, null); };

        timer.Interval = PollMs;
        timer.Tick += delegate { Update(); };
        timer.Start();
        Update();
        if (!HubCache.IsDownloaded) DownloadHub(false);
    }

    // Saves the whole Control Hub for offline use, in the background.
    void DownloadHub(bool userAsked)
    {
        updateHubItem.Enabled = false;
        updateHubItem.Text = "Downloading Control Hub…";
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string error = null;
            int files = 0;
            try { files = HubCache.DownloadAll(); }
            catch (Exception ex) { error = ex.Message; }
            ui.Post(delegate
            {
                updateHubItem.Enabled = true;
                updateHubItem.Text = "Update offline copy";
                if (error != null)
                {
                    if (userAsked) tray.ShowBalloonTip(5000, "Couldn't update Control Hub", error, ToolTipIcon.Warning);
                }
                else tray.ShowBalloonTip(4000, "Control Hub saved for offline use", files + " files downloaded.", ToolTipIcon.Info);
            }, null);
        });
    }

    void OpenHub()
    {
        try { hub.Open(); }
        catch (Exception ex)
        {
            tray.ShowBalloonTip(5000, "Couldn't open Control Hub", ex.Message, ToolTipIcon.Error);
            return;
        }
        Update();
    }

    void Update()
    {
        // The hub reads the same receiver channel, so stay off it while the hub is open.
        if (hub.IsOpen)
        {
            statusItem.Text = "Control Hub is open";
            estimateItem.Text = "Battery checks paused until it closes";
            tray.Text = "Zaopin: Control Hub is open";
            return;
        }

        BatteryStatus st;
        try { st = BatteryReader.Read(); }
        catch (Exception ex) { st = new BatteryStatus { Error = ex.GetType().Name + ": " + ex.Message }; }

        if (st.Connected)
        {
            if (missedReads > 0) Log.Write("read ok again after " + missedReads + " missed: " + st.Percent + "%, " + st.Millivolts + " mV");
            missedReads = 0;
            lastGood = st;
            drain.Add(st);
        }
        else
        {
            missedReads++;
            Log.Write("read failed (" + missedReads + " in a row): " + (st.ReceiverFound ? "" : "receiver not found; ") + (st.Error ?? "unknown"));
            // The radio dozes during short pauses; keep the last reading until it has been quiet a while.
            // (The drain tracker skips misses; gaps over its limit aren't counted anyway.)
            if (st.ReceiverFound && lastGood != null && missedReads < MissesBeforeAsleep) st = lastGood;
        }

        // Keep showing the last known level (greyed) while the mouse sleeps.
        if (st.Connected && st.Percent >= 0) lastPercent = st.Percent;
        else if (lastPercent >= 0) st.Percent = lastPercent;
        double? hoursLeft = st.Connected && st.Percent >= 0 && !st.Charging ? drain.HoursLeft(st.Percent) : null;
        string left = hoursLeft.HasValue ? FormatHours(hoursLeft.Value) : null;

        string text, estimate;
        if (st.Connected && st.Charging) { text = st.Name + ": " + st.Percent + "% (charging)"; estimate = "Charging"; }
        else if (st.Connected && st.Percent >= 0)
        {
            text = st.Name + ": " + st.Percent + "%";
            estimate = left != null ? "About " + left + " left" : "Estimating time left…";
        }
        else if (st.ReceiverFound)
        {
            text = "Mouse not responding" + (st.Percent >= 0 ? " (last: " + st.Percent + "%)" : "");
            estimate = "It may be off, asleep or out of range";
        }
        else { text = "Zaopin receiver not plugged in"; estimate = "No estimate"; }

        statusItem.Text = text;
        estimateItem.Text = st.Connected && st.Millivolts > 0 ? estimate + "  ·  " + (st.Millivolts / 1000.0).ToString("0.00") + " V" : estimate;
        string tip = text + "\n" + estimate;
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

        SetIcon(IconPainter.Draw(st));

        if (st.Connected && !st.Charging && st.Percent >= 0 && st.Percent <= LowThreshold)
        {
            if (!lowWarned)
            {
                tray.ShowBalloonTip(5000, "Mouse battery low",
                    st.Name + " is at " + st.Percent + "%." + (left != null ? " About " + left + " left." : ""), ToolTipIcon.Warning);
                lowWarned = true;
            }
        }
        else if (st.Charging || st.Percent > LowThreshold + 5)
        {
            lowWarned = false;
        }
    }

    static string FormatHours(double hours)
    {
        int minutes = (int)Math.Round(hours * 60);
        if (minutes < 60) return Math.Max(1, minutes) + " min";
        if (minutes < 600) return (minutes / 60) + "h " + (minutes % 60) + "m";
        return (int)Math.Round(hours) + " hours";
    }

    void SetIcon(Icon icon)
    {
        tray.Icon = icon;
        if (currentIcon != null)
        {
            Native.DestroyIcon(currentIcon.Handle);
            currentIcon.Dispose();
        }
        currentIcon = icon;
    }

    static bool IsStartupEnabled()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key != null && key.GetValue(RunName) != null;
    }

    void ToggleStartup()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (IsStartupEnabled()) key.DeleteValue(RunName, false);
            else key.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
        }
        startupItem.Checked = IsStartupEnabled();
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}

// The Zaopin Control Hub web configurator in its own window. It talks to the
// receiver over WebHID, so it runs in Edge's app mode (no tabs or address bar)
// with a private profile: that keeps it a separate process we can watch, and
// the device permission granted on first use is remembered. The page itself
// comes from HubServer, the offline copy on this PC.
class ControlHub
{
    const int SW_RESTORE = 9;

    System.Diagnostics.Process proc;
    public event EventHandler Closed;

    public bool IsOpen { get { return proc != null && !proc.HasExited; } }

    public void Open()
    {
        if (IsOpen)
        {
            proc.Refresh();
            IntPtr hwnd = proc.MainWindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, SW_RESTORE);
                Native.SetForegroundWindow(hwnd);
                return;
            }
        }

        string edge = FindEdge();
        if (edge == null) throw new FileNotFoundException("Microsoft Edge isn't installed.");
        if (HubCache.Get("index.html") == null)
            throw new IOException("The Control Hub hasn't been downloaded yet. Connect to the internet once and try again.");
        string url = HubServer.Start();
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZaopinBatteryTray", "HubProfile");
        Directory.CreateDirectory(profile);

        var psi = new System.Diagnostics.ProcessStartInfo(edge,
            "--app=" + url + " --user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check --window-size=1280,820");
        psi.UseShellExecute = false;
        proc = System.Diagnostics.Process.Start(psi);
        proc.EnableRaisingEvents = true;
        proc.Exited += delegate { var h = Closed; if (h != null) h(this, EventArgs.Empty); };
    }

    static string FindEdge()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData })
        {
            string p = Path.Combine(Environment.GetFolderPath(root), @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}

// Small text log in %LOCALAPPDATA%\ZaopinBatteryTray\battery.log, trimmed to its
// newer half once it passes 64 KB.
static class Log
{
    const long MaxBytes = 64 * 1024;
    static readonly string Path_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZaopinBatteryTray", "battery.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path_));
            var info = new FileInfo(Path_);
            if (info.Exists && info.Length > MaxBytes)
            {
                string[] lines = File.ReadAllLines(Path_);
                File.WriteAllLines(Path_, lines.Skip(lines.Length / 2).ToArray());
            }
            File.AppendAllText(Path_, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

// Offline copy of https://controlhub.top/zaopin/ under %LOCALAPPDATA%.
// Files are served from disk; anything missing is fetched once and kept.
// Links to other sites (a CDN stylesheet) are rewritten to _ext/<host>/<path>
// so they get cached too.
static class HubCache
{
    const string BaseUrl = "https://controlhub.top/zaopin/";
    static readonly int[] ReportRates = { 125, 250, 500, 1000, 2000, 4000, 8000 };
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZaopinBatteryTray", "HubCache");

    static HubCache()
    {
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
        System.Net.ServicePointManager.DefaultConnectionLimit = 8;
    }

    public static bool IsDownloaded { get { return File.Exists(LocalPath("index.html")); } }

    // Cached bytes for a hub-relative path, downloading it if it isn't on disk yet.
    public static byte[] Get(string path)
    {
        string local = LocalPath(path);
        if (local == null) return null;
        if (File.Exists(local))
        {
            try { return File.ReadAllBytes(local); } catch (IOException) { }
        }
        return Download(path);
    }

    // Fetches a path from the live site and saves it. Null if offline or missing.
    public static byte[] Download(string path)
    {
        string local = LocalPath(path);
        if (local == null) return null;
        byte[] data = Fetch(RemoteUrl(path));
        if (data == null) return null;
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            data = System.Text.Encoding.UTF8.GetBytes(RewriteLinks(System.Text.Encoding.UTF8.GetString(data)));

        Directory.CreateDirectory(Path.GetDirectoryName(local));
        string tmp = local + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, data);
        if (File.Exists(local)) File.Delete(local);
        File.Move(tmp, local);
        return data;
    }

    // Downloads (or refreshes) everything the hub can load. Returns files saved;
    // throws if the hub itself couldn't be reached.
    public static int DownloadAll()
    {
        byte[] index = Download("index.html");
        if (index == null) throw new IOException("Couldn't reach controlhub.top.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string html = System.Text.Encoding.UTF8.GetString(index);
        foreach (System.Text.RegularExpressions.Match m in Rx(html, "(?:src|href)=\"\\./([^\"?#]+)"))
            paths.Add(m.Groups[1].Value);

        // Scripts and stylesheets name the images, fonts and data files they use.
        var code = new List<string>();
        foreach (string p in new List<string>(paths))
        {
            if (!p.EndsWith(".js") && !p.EndsWith(".css")) continue;
            byte[] b = Download(p);
            if (b == null) continue;
            string text = System.Text.Encoding.UTF8.GetString(b);
            code.Add(text);
            string dir = p.Contains("/") ? p.Substring(0, p.LastIndexOf('/') + 1) : "";
            foreach (System.Text.RegularExpressions.Match m in Rx(text, "url\\(\\s*[\"']?\\./([^\"')?#]+)"))
                paths.Add(dir + m.Groups[1].Value);
        }
        foreach (string text in code)
        {
            // Image paths are built as "img/" + a relative literal.
            foreach (System.Text.RegularExpressions.Match m in Rx(text, "`([A-Za-z0-9_./-]+\\.(?:png|jpe?g|gif|svg|webp))`"))
            {
                string p = m.Groups[1].Value;
                paths.Add(p.StartsWith("img/") ? p : "img/" + p);
            }
            // Data files at the site root are loaded as `${base}name`.
            foreach (System.Text.RegularExpressions.Match m in Rx(text, "`\\$\\{\\$?\\w+\\}([A-Za-z0-9_.-]+\\.[a-z]+)`"))
                paths.Add(m.Groups[1].Value);
        }
        foreach (int rate in ReportRates) paths.Add("img/global/" + rate + ".png");

        // Device list, language packs and per-model pictures come from the config files.
        byte[] cfgBytes = Download("cfg.json");
        if (cfgBytes != null)
        {
            string cfg = System.Text.Encoding.UTF8.GetString(cfgBytes);
            var langs = System.Text.RegularExpressions.Regex.Match(cfg, "\"language\"\\s*:\\s*\\[([^\\]]*)\\]");
            foreach (System.Text.RegularExpressions.Match m in Rx(langs.Groups[1].Value, "\"([^\"]+)\""))
                paths.Add("lang/" + m.Groups[1].Value + ".json");
            AddDeviceImages(cfg, paths);
        }

        paths.Remove("index.html");
        paths.Remove("cfg.json");
        int saved = 0;
        System.Threading.Tasks.Parallel.ForEach(paths, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 6 }, p =>
        {
            try { if (Download(p) != null) System.Threading.Interlocked.Increment(ref saved); }
            catch (IOException) { }
        });
        return saved + 2;
    }

    // Each device section ("mouse": [ { "cid": 7, "cfg": [ { "mid": [1, 2] ... ) maps to
    // img/devices/<section>/<cid><mid>.png, both as two hex digits.
    static void AddDeviceImages(string cfg, HashSet<string> paths)
    {
        var sections = Rx(cfg, "\"(\\w+)\"\\s*:\\s*\\[\\s*\\{\\s*\"cid\"");
        for (int s = 0; s < sections.Count; s++)
        {
            string type = sections[s].Groups[1].Value;
            int start = sections[s].Index;
            int end = s + 1 < sections.Count ? sections[s + 1].Index : cfg.Length;
            string body = cfg.Substring(start, end - start);
            paths.Add("img/devices/" + type + "/demo.png");
            int cid = -1;
            foreach (System.Text.RegularExpressions.Match m in Rx(body, "\"(cid|mid)\"\\s*:\\s*(\\[[^\\]]*\\]|\\d+)"))
            {
                if (m.Groups[1].Value == "cid") { cid = int.Parse(m.Groups[2].Value); continue; }
                if (cid < 0) continue;
                foreach (System.Text.RegularExpressions.Match n in Rx(m.Groups[2].Value, "\\d+"))
                    paths.Add("img/devices/" + type + "/" + cid.ToString("X2") + int.Parse(n.Value).ToString("X2") + ".png");
            }
        }
    }

    static System.Text.RegularExpressions.MatchCollection Rx(string text, string pattern)
    {
        return System.Text.RegularExpressions.Regex.Matches(text, pattern);
    }

    static string RewriteLinks(string html)
    {
        return System.Text.RegularExpressions.Regex.Replace(html, "((?:src|href)=\")https://", "$1./_ext/");
    }

    static string RemoteUrl(string path)
    {
        return path.StartsWith("_ext/") ? "https://" + path.Substring(5) : BaseUrl + path;
    }

    // Maps a URL path to a file under Root, refusing anything that would escape it.
    static string LocalPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains("..") || path.Contains(":") || path.Contains("\\")) return null;
        string full = Path.GetFullPath(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    static byte[] Fetch(string url)
    {
        try
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Timeout = 15000;
            req.ReadWriteTimeout = 30000;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ZaopinBatteryTray";
            using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
        catch (System.Net.WebException) { return null; }
        catch (IOException) { return null; }
    }
}

// Serves the offline hub at http://127.0.0.1:<port>/ (a secure context, so
// WebHID works). Only this PC can connect. The port stays fixed so the
// browser keeps the device permission granted for that address.
static class HubServer
{
    static readonly int[] Ports = { 47811, 47812, 47813 };
    static System.Net.Sockets.TcpListener listener;
    static string url;

    public static string Start()
    {
        if (url != null) return url;
        foreach (int port in Ports)
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                l.Start();
                listener = l;
                url = "http://127.0.0.1:" + port + "/";
                var t = new System.Threading.Thread(AcceptLoop) { IsBackground = true };
                t.Start();
                return url;
            }
            catch (System.Net.Sockets.SocketException) { }
        }
        throw new IOException("Ports " + Ports[0] + "–" + Ports[Ports.Length - 1] + " are all in use.");
    }

    static void AcceptLoop()
    {
        while (true)
        {
            System.Net.Sockets.TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (System.Net.Sockets.SocketException) { return; }
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { Serve(client); });
        }
    }

    static void Serve(System.Net.Sockets.TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                var stream = client.GetStream();
                string requestLine = ReadHeaders(stream);
                if (requestLine == null) return;
                string[] parts = requestLine.Split(' ');
                if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "HEAD")) { Respond(stream, 405, "text/plain", null, false); return; }

                string path = Uri.UnescapeDataString(parts[1].Split('?', '#')[0]).TrimStart('/');
                if (path == "" || path.EndsWith("/")) path += "index.html";
                byte[] body = HubCache.Get(path);
                if (body == null) Respond(stream, 404, "text/plain", System.Text.Encoding.ASCII.GetBytes("Not found"), parts[0] == "HEAD");
                else Respond(stream, 200, ContentType(path), body, parts[0] == "HEAD");
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    // Reads up to the blank line ending the request headers; returns the request line.
    static string ReadHeaders(Stream s)
    {
        var sb = new System.Text.StringBuilder();
        int b;
        while (sb.Length < 16384 && (b = s.ReadByte()) >= 0)
        {
            sb.Append((char)b);
            if (sb.Length >= 4 && sb[sb.Length - 1] == '\n' && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n")
                return sb.ToString(0, sb.ToString().IndexOf("\r\n"));
        }
        return null;
    }

    static void Respond(Stream s, int status, string type, byte[] body, bool headOnly)
    {
        string reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Method Not Allowed";
        int len = body == null ? 0 : body.Length;
        string head = "HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: " + type + "\r\nContent-Length: " + len +
            "\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
        byte[] h = System.Text.Encoding.ASCII.GetBytes(head);
        s.Write(h, 0, h.Length);
        if (body != null && !headOnly) s.Write(body, 0, body.Length);
    }

    static string ContentType(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".html": return "text/html; charset=utf-8";
            case ".js": return "text/javascript; charset=utf-8";
            case ".css": return "text/css; charset=utf-8";
            case ".json": return "application/json; charset=utf-8";
            case ".png": return "image/png";
            case ".jpg": case ".jpeg": return "image/jpeg";
            case ".gif": return "image/gif";
            case ".svg": return "image/svg+xml";
            case ".webp": return "image/webp";
            case ".ico": return "image/x-icon";
            case ".ttf": return "font/ttf";
            case ".woff": return "font/woff";
            case ".woff2": return "font/woff2";
            case ".mp4": return "video/mp4";
            default: return "application/octet-stream";
        }
    }
}

// The Zaopin logo, tinted by battery level, above a fill bar.
static class IconPainter
{
    // The logo is pixel art, drawn as a grid of bands so it stays crisp at any
    // icon size: every band gets at least one pixel. Widths and heights are in
    // the logo's own proportions.
    static readonly int[] ColWeights = { 30, 30, 200, 30 };   // left side, nub, middle, right side
    static readonly int[] RowWeights = { 12, 30, 20, 22, 18, 25, 25, 30, 45, 30 };
    static readonly string[] Cells =
    {
        ".#..",   // nub on top
        "####",   // upper block: top edge
        "#..#",   //   slot
        "####",   //   middle bar
        "#..#",   //   slot
        "####",   //   bottom edge
        "....",   // gap
        "####",   // lower hook: top bar
        "#...",   //   opening
        "####",   //   bottom bar
    };

    static readonly Color Orange = Color.FromArgb(232, 112, 58);
    static readonly Color Grey = Color.FromArgb(140, 140, 140);
    const int LowColor = 20;

    public static Icon Draw(BatteryStatus st)
    {
        using (var bmp = Render(st, SystemInformation.SmallIconSize.Width))
            return Icon.FromHandle(bmp.GetHicon());
    }

    public static Bitmap Render(BatteryStatus st, int size)
    {
        Color color = !st.Connected ? Grey
                    : st.Percent < 0 ? Orange
                    : st.Charging ? Color.FromArgb(80, 160, 255)
                    : st.Percent > 50 ? Color.FromArgb(70, 210, 90)
                    : st.Percent > LowColor ? Color.FromArgb(240, 200, 40)
                    : Color.FromArgb(240, 70, 60);

        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);

            // Fill bar along the bottom edge.
            int barH = Math.Max(2, size / 8);
            if (st.Percent >= 0)
            {
                using (var bg = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                    g.FillRectangle(bg, 0, size - barH, size, barH);
                using (var fill = new SolidBrush(color))
                    g.FillRectangle(fill, 0, size - barH, Math.Max(1, size * Math.Min(st.Percent, 100) / 100), barH);
            }

            // Logo in the space above, keeping its proportions.
            int logoH = size - barH - Math.Max(1, size / 11);
            int logoW = Math.Min(size, (int)Math.Round(logoH * Sum(ColWeights) / (double)Sum(RowWeights)));
            int[] cols = Allocate(ColWeights, logoW), rows = Allocate(RowWeights, logoH);
            using (var brush = new SolidBrush(color))
            {
                int y = 0;
                for (int r = 0; r < rows.Length; r++)
                {
                    int x = (size - logoW) / 2;
                    for (int c = 0; c < cols.Length; c++)
                    {
                        if (Cells[r][c] == '#') g.FillRectangle(brush, x, y, cols[c], rows[r]);
                        x += cols[c];
                    }
                    y += rows[r];
                }
            }
        }
        return bmp;
    }

    // Splits total pixels across bands in proportion to their weights, at least one each.
    static int[] Allocate(int[] weights, int total)
    {
        int sum = Sum(weights);
        var sizes = new int[weights.Length];
        var ideal = new double[weights.Length];
        int used = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            ideal[i] = weights[i] * (double)total / sum;
            sizes[i] = Math.Max(1, (int)ideal[i]);
            used += sizes[i];
        }
        while (used != total)
        {
            // Grow the band furthest under its share, or shrink the one furthest over.
            int pick = -1;
            for (int i = 0; i < sizes.Length; i++)
            {
                if (used > total && sizes[i] <= 1) continue;
                double gap = ideal[i] - sizes[i];
                if (pick < 0 || (used < total ? gap > ideal[pick] - sizes[pick] : gap < ideal[pick] - sizes[pick])) pick = i;
            }
            if (pick < 0) break;
            sizes[pick] += used < total ? 1 : -1;
            used += used < total ? 1 : -1;
        }
        return sizes;
    }

    static int Sum(int[] values) { int s = 0; foreach (int v in values) s += v; return s; }
}

// Learns how fast the battery drains while the mouse is awake.
// Time asleep doesn't count, and the numbers are kept in the registry
// so the estimate survives restarts. Rising levels mean a recharge.
class DrainTracker
{
    const string Key = @"Software\ZaopinBatteryTray";
    const double MaxGapSeconds = 180;      // longer gaps (asleep, PC asleep) aren't counted as drain time
    const double MinDrop = 3, MinHours = 0.5, TrustDrop = 5;

    double dropped, seconds, learnedRate;  // learnedRate in % per hour, from earlier battery cycles
    int prevPercent = -1;
    DateTime lastSample = DateTime.MinValue;

    public DrainTracker()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                if (k == null) return;
                dropped = ReadDouble(k, "Dropped");
                seconds = ReadDouble(k, "Seconds");
                learnedRate = ReadDouble(k, "LearnedRate");
                prevPercent = (int)ReadDouble(k, "Prev", -1);
            }
        }
        catch { }
    }

    public void Add(BatteryStatus st)
    {
        if (!st.Connected || st.Charging || st.Percent < 0) { lastSample = DateTime.MinValue; return; }

        var now = DateTime.UtcNow;
        if (prevPercent >= 0 && st.Percent > prevPercent + 2)
        {
            // Recharged: start a new cycle.
            dropped = 0; seconds = 0; prevPercent = st.Percent;
        }
        else if (prevPercent < 0)
        {
            prevPercent = st.Percent;
        }
        else
        {
            double gap = (now - lastSample).TotalSeconds;
            if (lastSample != DateTime.MinValue && gap > 0 && gap <= MaxGapSeconds) seconds += gap;
            if (st.Percent < prevPercent) { dropped += prevPercent - st.Percent; prevPercent = st.Percent; }
        }
        lastSample = now;

        if (dropped >= TrustDrop && seconds / 3600 >= MinHours) learnedRate = CycleRate();
        Save();
    }

    public double? HoursLeft(int percent)
    {
        double rate = 0;
        double hours = seconds / 3600;
        bool cycleUsable = dropped >= MinDrop && hours >= MinHours;
        if (cycleUsable && (dropped >= TrustDrop || learnedRate <= 0)) rate = CycleRate();
        else if (learnedRate > 0) rate = learnedRate;
        if (rate <= 0) return null;
        return percent / rate;
    }

    double CycleRate() { return dropped / (seconds / 3600); }

    void Save()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                k.SetValue("Dropped", dropped.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                k.SetValue("Seconds", seconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                k.SetValue("LearnedRate", learnedRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                k.SetValue("Prev", prevPercent.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        catch { }
    }

    static double ReadDouble(RegistryKey k, string name, double fallback = 0)
    {
        double v;
        var s = k.GetValue(name) as string;
        return s != null && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
    }
}

// CompX receiver protocol: 17-byte reports with ID 0x08 on the vendor
// collection (usage page 0xFF02). Byte 1 is the command, byte 16 a checksum
// so that all bytes sum to 0x55. The battery reply (command 0x04) carries
// the percentage in byte 6, a charging flag in byte 7 and millivolts in 8–9.
static class BatteryReader
{
    const string Vid = "vid_3554";
    const byte ReportId = 0x08, CmdBattery = 0x04;
    const int ReportLen = 17, ReplyTimeoutMs = 1000;

    const int Attempts = 3, RetryDelayMs = 400;

    public static BatteryStatus Read()
    {
        var st = new BatteryStatus();
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0) System.Threading.Thread.Sleep(RetryDelayMs);
            foreach (string path in Native.HidPaths(Vid))
            {
                if (!IsCommandChannel(path)) continue;
                st.ReceiverFound = true;
                string error;
                byte[] reply = Query(path, CmdBattery, out error);
                if (reply != null && reply[6] > 100) error = "unexpected reply " + BitConverter.ToString(reply);
                if (error != null) { st.Error = error; continue; }
                st.Connected = true;
                st.Percent = reply[6];
                st.Charging = reply[7] != 0;
                st.Millivolts = (reply[8] << 8) | reply[9];
                st.Error = null;
                return st;
            }
            if (!st.ReceiverFound) return st;
        }
        return st;
    }

    static bool IsCommandChannel(string path)
    {
        using (var h = Native.CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) return false;
            IntPtr pp;
            if (!Native.HidD_GetPreparsedData(h, out pp)) return false;
            Native.HIDP_CAPS caps;
            Native.HidP_GetCaps(pp, out caps);
            Native.HidD_FreePreparsedData(pp);
            return caps.UsagePage == 0xFF02 && caps.OutputReportByteLength == ReportLen;
        }
    }

    // Sends one command and waits for its reply. On failure returns null and says why.
    static byte[] Query(string path, byte cmd, out string error)
    {
        error = null;
        var h = Native.CreateFileW(path, 0xC0000000u, 3, IntPtr.Zero, 3, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { error = "couldn't open receiver (Windows error " + Marshal.GetLastWin32Error() + ")"; return null; }
        using (var fs = new FileStream(h, FileAccess.ReadWrite, ReportLen, true))
        {
            var req = new byte[ReportLen];
            req[0] = ReportId; req[1] = cmd;
            int sum = 0;
            for (int i = 0; i < ReportLen - 1; i++) sum += req[i];
            req[ReportLen - 1] = (byte)((0x55 - sum) & 0xFF);
            try { fs.Write(req, 0, ReportLen); }
            catch (IOException ex) { error = "send failed: " + ex.Message; return null; }

            var deadline = DateTime.UtcNow.AddMilliseconds(ReplyTimeoutMs);
            string other = null;
            while (true)
            {
                int wait = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (wait <= 0) { error = "no reply" + (other != null ? " (last report " + other + ")" : ""); return null; }
                var buf = new byte[ReportLen];
                var task = fs.ReadAsync(buf, 0, ReportLen);
                try { if (!task.Wait(wait)) { error = "no reply" + (other != null ? " (last report " + other + ")" : ""); return null; } }   // disposing the stream cancels the read
                catch (AggregateException ex) { error = "read failed: " + ex.InnerException.Message; return null; }
                if (task.Result == ReportLen && buf[0] == ReportId && buf[1] == cmd) return buf;
                other = BitConverter.ToString(buf, 0, task.Result);
            }
        }
    }
}

static class Native
{
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    public static List<string> HidPaths(string filter)
    {
        var list = new List<string>();
        Guid hid;
        HidD_GetHidGuid(out hid);
        IntPtr set = SetupDiGetClassDevsW(ref hid, IntPtr.Zero, IntPtr.Zero, 0x12);  // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
        if (set == new IntPtr(-1)) return list;
        try
        {
            var d = new SP_DEVICE_INTERFACE_DATA();
            d.cbSize = Marshal.SizeOf(d);
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref d); i++)
            {
                int required;
                SetupDiGetDeviceInterfaceDetailW(set, ref d, IntPtr.Zero, 0, out required, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref d, buf, required, out required, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(buf + 4);
                    if (path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) list.Add(path);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

    [DllImport("setupapi.dll")]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
}
