using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Diagnostics;
using System.Windows.Forms;

class AIWorkbenchServer
{
    private const int PORT = 8787;
    private static string BASE_DIR;
    private static string OUTPUTS_DIR;
    private static HttpClient directHttpClient;
    private static HttpClient proxyHttpClient;
    private static HttpListener listener;
    private static NotifyIcon trayIcon;

    [STAThread]
    static void Main(string[] args)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)3072;
        ServicePointManager.Expect100Continue = false;
        ServicePointManager.UseNagleAlgorithm = false;
        ServicePointManager.DefaultConnectionLimit = 64;

        BASE_DIR = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        OUTPUTS_DIR = Path.Combine(BASE_DIR, "outputs");
        Directory.CreateDirectory(OUTPUTS_DIR);

        var directHandler = new HttpClientHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        directHttpClient = new HttpClient(directHandler);
        directHttpClient.Timeout = TimeSpan.FromMinutes(10);

        var proxyHandler = new HttpClientHandler
        {
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        proxyHttpClient = new HttpClient(proxyHandler);
        proxyHttpClient.Timeout = TimeSpan.FromMinutes(10);

        if (IsOurServerAlreadyRunning())
        {
            OpenBrowser();
            return;
        }

        if (IsPortBusy())
        {
            MessageBox.Show(
                "8787 端口正在被其他程序占用，请关闭冲突服务后再试。",
                "AI生图工作台",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
            return;
        }

        listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:" + PORT + "/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "本地保存服务启动失败：" + ex.Message,
                "AI生图工作台",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            return;
        }

        // 后台线程持续监听 HTTP 请求
        Thread serverThread = new Thread(ListenLoop);
        serverThread.IsBackground = true;
        serverThread.Start();

        // 是否跳过自动弹浏览器（用于自动化测试参数 --no-open）
        bool skipOpen = args.Length > 0 && args[0] == "--no-open";
        if (!skipOpen)
        {
            OpenBrowser();
        }

        // 初始化系统托盘图标，方便非技术用户随时重新打开页面或退出后台服务
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        ContextMenu menu = new ContextMenu();
        menu.MenuItems.Add("打开 AI生图工作台", (s, e) => OpenBrowser());
        menu.MenuItems.Add("打开图片保存目录 (outputs)", (s, e) => OpenOutputsFolder());
        menu.MenuItems.Add("-");
        menu.MenuItems.Add("退出本地保存服务", (s, e) => Shutdown());

        Icon appIcon = null;
        try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        if (appIcon == null) appIcon = SystemIcons.Application;

        trayIcon = new NotifyIcon();
        trayIcon.Icon = appIcon;
        trayIcon.Text = "AI生图工作台 (本地保存服务运行中)";
        trayIcon.ContextMenu = menu;
        trayIcon.DoubleClick += (s, e) => OpenBrowser();
        trayIcon.Visible = true;

        Application.Run();
    }

    static void ListenLoop()
    {
        while (listener != null && listener.IsListening)
        {
            try
            {
                HttpListenerContext ctx = listener.GetContext();
                ThreadPool.QueueUserWorkItem(o => HandleRequest(ctx));
            }
            catch
            {
                if (listener == null || !listener.IsListening) break;
                Thread.Sleep(100);
            }
        }
    }

    static void OpenOutputsFolder()
    {
        try
        {
            Directory.CreateDirectory(OUTPUTS_DIR);
            Process.Start(new ProcessStartInfo(OUTPUTS_DIR) { UseShellExecute = true });
        }
        catch { }
    }

    static void Shutdown()
    {
        try { if (trayIcon != null) trayIcon.Visible = false; } catch { }
        try { if (listener != null) listener.Stop(); } catch { }
        Application.Exit();
    }

    static bool IsOurServerAlreadyRunning()
    {
        try
        {
            using (var c = new WebClient())
            {
                c.Encoding = Encoding.UTF8;
                string res = c.DownloadString("http://127.0.0.1:" + PORT + "/api/ping");
                return res.Contains("\"status\":\"ok\"") && res.Contains(EscapeJson(OUTPUTS_DIR));
            }
        }
        catch { return false; }
    }

    static bool IsPortBusy()
    {
        try
        {
            using (var client = new System.Net.Sockets.TcpClient())
            {
                var result = client.BeginConnect("127.0.0.1", PORT, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(300));
                if (success)
                {
                    client.EndConnect(result);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    static void OpenBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo("http://127.0.0.1:" + PORT) { UseShellExecute = true });
        }
        catch { }
    }

    static void HandleRequest(HttpListenerContext ctx)
    {
        HttpListenerRequest req = ctx.Request;
        HttpListenerResponse res = ctx.Response;

        res.AddHeader("Access-Control-Allow-Origin", "*");
        res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        res.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization, X-Requested-With, X-Request-ID, X-Target-Url");
        res.AddHeader("Access-Control-Expose-Headers", "X-Workbench-Proxy-Error");

        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        string pathUrl = "/";
        try
        {
            pathUrl = Uri.UnescapeDataString(req.Url.AbsolutePath);
        }
        catch { }

        try
        {
            if (pathUrl == "/api/ping")
            {
                WriteJson(res, 200, "{\"status\":\"ok\",\"outputsDir\":\"" + EscapeJson(OUTPUTS_DIR) + "\"}");
                return;
            }

            if (pathUrl == "/api/proxy")
            {
                HandleGatewayProxy(req, res);
                return;
            }

            if (pathUrl == "/api/outputs" && req.HttpMethod == "GET")
            {
                HandleListOutputs(res);
                return;
            }

            if (pathUrl == "/api/config")
            {
                HandleLocalConfig(req, res);
                return;
            }

            if (pathUrl == "/api/check-update" && req.HttpMethod == "GET")
            {
                HandleCheckUpdate(res);
                return;
            }

            if (pathUrl == "/api/apply-update" && req.HttpMethod == "POST")
            {
                HandleApplyUpdate(res);
                return;
            }

            if (pathUrl == "/api/open-outputs" && req.HttpMethod == "POST")
            {
                HandleOpenOutputs(req, res);
                return;
            }

            if (pathUrl == "/api/save-image" && req.HttpMethod == "POST")
            {
                HandleSaveImage(req, res);
                return;
            }

            if (pathUrl == "/" || pathUrl == "/index.html")
            {
                pathUrl = File.Exists(Path.Combine(BASE_DIR, "AI生图工作台.html")) ? "/AI生图工作台.html" : "/临时生图.html";
            }

            string cleanRel = pathUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(BASE_DIR, cleanRel));

            if (!fullPath.StartsWith(BASE_DIR, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                res.StatusCode = 404;
                byte[] errBytes = Encoding.UTF8.GetBytes("页面未找到");
                res.ContentType = "text/plain; charset=utf-8";
                res.OutputStream.Write(errBytes, 0, errBytes.Length);
                res.Close();
                return;
            }

            res.StatusCode = 200;
            res.ContentType = GetMimeType(Path.GetExtension(fullPath));
            using (FileStream fs = File.OpenRead(fullPath))
            {
                res.ContentLength64 = fs.Length;
                fs.CopyTo(res.OutputStream);
            }
            res.Close();
        }
        catch (Exception ex)
        {
            try
            {
                WriteJson(res, 500, "{\"error\":\"" + EscapeJson(ex.Message) + "\"}");
            }
            catch { }
        }
    }

    static void HandleSaveImage(HttpListenerRequest req, HttpListenerResponse res)
    {
        try
        {
            string body;
            using (StreamReader reader = new StreamReader(req.InputStream, Encoding.UTF8))
            {
                body = reader.ReadToEnd();
            }

            string filename = ExtractJsonField(body, "filename");
            string imgUrl = ExtractJsonField(body, "url");
            string base64 = ExtractJsonField(body, "base64");
            if (string.IsNullOrEmpty(base64)) base64 = ExtractJsonField(body, "data");

            if (string.IsNullOrEmpty(filename))
            {
                filename = "image_" + ToUnixMs(DateTime.UtcNow) + ".png";
            }
            filename = Path.GetFileName(filename);

            Directory.CreateDirectory(OUTPUTS_DIR);
            string filePath = Path.Combine(OUTPUTS_DIR, filename);

            string b64Source = !string.IsNullOrEmpty(base64) ? base64 : (imgUrl != null && imgUrl.StartsWith("data:") ? imgUrl : null);

            if (!string.IsNullOrEmpty(b64Source))
            {
                int commaIdx = b64Source.IndexOf(',');
                string cleanB64 = commaIdx >= 0 ? b64Source.Substring(commaIdx + 1) : b64Source;
                byte[] bytes = Convert.FromBase64String(cleanB64);
                File.WriteAllBytes(filePath, bytes);
            }
            else if (!string.IsNullOrEmpty(imgUrl) && (imgUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || imgUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                byte[] bytes = DownloadBytesSmart(imgUrl);
                File.WriteAllBytes(filePath, bytes);
            }
            else
            {
                throw new Exception("未提供有效的图像数据 (URL 或 Base64)");
            }

            WriteJson(res, 200, "{\"success\":true,\"filename\":\"" + EscapeJson(filename) + "\",\"fullPath\":\"" + EscapeJson(filePath) + "\"}");
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, "{\"error\":\"" + EscapeJson(ex.Message) + "\"}");
        }
    }

    static void HandleOpenOutputs(HttpListenerRequest req, HttpListenerResponse res)
    {
        try
        {
            string body = "";
            if (req.HasEntityBody)
            {
                using (StreamReader reader = new StreamReader(req.InputStream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }
            }
            string filename = ExtractJsonField(body, "filename");
            Directory.CreateDirectory(OUTPUTS_DIR);

            string targetFile = "";
            if (!string.IsNullOrEmpty(filename))
            {
                string safeName = Path.GetFileName(filename);
                string candidate = Path.Combine(OUTPUTS_DIR, safeName);
                if (File.Exists(candidate))
                {
                    targetFile = candidate;
                }
            }

            if (!string.IsNullOrEmpty(targetFile))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + targetFile + "\"")
                {
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo(OUTPUTS_DIR) { UseShellExecute = true });
            }

            WriteJson(res, 200, "{\"success\":true,\"outputsDir\":\"" + EscapeJson(OUTPUTS_DIR) + "\"}");
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, "{\"error\":\"" + EscapeJson(ex.Message) + "\"}");
        }
    }

    static byte[] DownloadBytesSmart(string imgUrl)
    {
        try
        {
            return directHttpClient.GetByteArrayAsync(imgUrl).GetAwaiter().GetResult();
        }
        catch
        {
            return proxyHttpClient.GetByteArrayAsync(imgUrl).GetAwaiter().GetResult();
        }
    }

    static byte[] DownloadGithubFileBytes(string relPath)
    {
        string rawUrl = "https://raw.githubusercontent.com/JONY-YFQ/AI-image-workbench/main/" + relPath + "?t=" + ToUnixMs(DateTime.UtcNow);
        string[] urls = new string[]
        {
            rawUrl,
            "https://gh-proxy.com/" + rawUrl,
            "https://mirror.ghproxy.com/" + rawUrl
        };
        Exception lastErr = null;
        for (int i = 0; i < urls.Length; i++)
        {
            try
            {
                byte[] data = DownloadBytesSmart(urls[i]);
                if (data != null && data.Length > 0) return data;
            }
            catch (Exception ex)
            {
                lastErr = ex;
            }
        }
        throw (lastErr ?? new Exception("无法从 GitHub 下载 " + relPath));
    }

    static string ReadLocalVersionJson()
    {
        string verPath = Path.Combine(BASE_DIR, "version.json");
        if (File.Exists(verPath))
        {
            try { return File.ReadAllText(verPath, Encoding.UTF8); } catch { }
        }
        return "{\"version\":\"MY1.0.8\",\"exeVersion\":\"1.0.8\"}";
    }

    static void HandleCheckUpdate(HttpListenerResponse res)
    {
        try
        {
            string localRaw = ReadLocalVersionJson();
            string localVer = ExtractJsonField(localRaw, "version") ?? "MY1.0.8";

            byte[] remoteBytes = DownloadGithubFileBytes("version.json");
            string remoteRaw = Encoding.UTF8.GetString(remoteBytes);
            string remoteVer = ExtractJsonField(remoteRaw, "version");
            if (string.IsNullOrEmpty(remoteVer))
            {
                throw new Exception("远端 version.json 格式无效");
            }
            string notes = ExtractJsonField(remoteRaw, "notes") ?? "";
            string updatedAt = ExtractJsonField(remoteRaw, "updatedAt") ?? "";
            bool hasUpdate = !string.Equals(localVer.Trim(), remoteVer.Trim(), StringComparison.OrdinalIgnoreCase);

            string json = "{"
                + "\"currentVersion\":\"" + EscapeJson(localVer) + "\","
                + "\"remoteVersion\":\"" + EscapeJson(remoteVer) + "\","
                + "\"hasUpdate\":" + (hasUpdate ? "true" : "false") + ","
                + "\"notes\":\"" + EscapeJson(notes) + "\","
                + "\"updatedAt\":\"" + EscapeJson(updatedAt) + "\""
                + "}";
            WriteJson(res, 200, json);
        }
        catch (Exception ex)
        {
            WriteJson(res, 502, "{\"error\":\"" + EscapeJson("检查更新失败: " + ex.Message) + "\"}");
        }
    }

    static void HandleApplyUpdate(HttpListenerResponse res)
    {
        try
        {
            string localRaw = ReadLocalVersionJson();
            string localExeVer = ExtractJsonField(localRaw, "exeVersion") ?? "1.0.8";

            byte[] remoteVerBytes = DownloadGithubFileBytes("version.json");
            string remoteRaw = Encoding.UTF8.GetString(remoteVerBytes);
            string remoteVer = ExtractJsonField(remoteRaw, "version");
            if (string.IsNullOrEmpty(remoteVer))
            {
                throw new Exception("远端 version.json 无效");
            }
            string remoteExeVer = ExtractJsonField(remoteRaw, "exeVersion") ?? localExeVer;
            string htmlName = ExtractJsonField(remoteRaw, "htmlFile") ?? "AI生图工作台.html";
            string exeName = ExtractJsonField(remoteRaw, "exeFile") ?? "AI生图工作台.exe";

            // 1. 下载并校验新版 HTML
            byte[] htmlBytes = DownloadGithubFileBytes(Uri.EscapeDataString(htmlName));
            string htmlText = Encoding.UTF8.GetString(htmlBytes);
            if (!htmlText.Contains("<!DOCTYPE html>") || !htmlText.Contains("</html>"))
            {
                throw new Exception("下载的 HTML 文件不完整，已取消更新");
            }
            File.WriteAllBytes(Path.Combine(BASE_DIR, htmlName), htmlBytes);

            // 2. 若 EXE 版本号也有更新，则下载新版 EXE 并后台自替换重启
            bool needExeRestart = !string.Equals(localExeVer.Trim(), remoteExeVer.Trim(), StringComparison.OrdinalIgnoreCase);
            if (needExeRestart)
            {
                byte[] exeBytes = DownloadGithubFileBytes(Uri.EscapeDataString(exeName));
                if (exeBytes.Length < 10240 || exeBytes[0] != 0x4D || exeBytes[1] != 0x5A)
                {
                    throw new Exception("下载的 EXE 文件无效，已保留当前程序");
                }
                string exePath = Path.Combine(BASE_DIR, exeName);
                string newExePath = exePath + ".new";
                File.WriteAllBytes(newExePath, exeBytes);
                File.WriteAllBytes(Path.Combine(BASE_DIR, "version.json"), remoteVerBytes);

                WriteJson(res, 200, "{\"success\":true,\"restarted\":true,\"version\":\"" + EscapeJson(remoteVer) + "\"}");

                Thread restartThread = new Thread(() =>
                {
                    Thread.Sleep(500);
                    string cmdArgs = "/c ping -n 2 127.0.0.1 >nul & move /y \"" + newExePath + "\" \"" + exePath + "\" & start \"\" \"" + exePath + "\"";
                    Process.Start(new ProcessStartInfo("cmd.exe", cmdArgs)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    Shutdown();
                });
                restartThread.IsBackground = true;
                restartThread.Start();
                return;
            }

            File.WriteAllBytes(Path.Combine(BASE_DIR, "version.json"), remoteVerBytes);
            WriteJson(res, 200, "{\"success\":true,\"restarted\":false,\"version\":\"" + EscapeJson(remoteVer) + "\"}");
        }
        catch (Exception ex)
        {
            WriteJson(res, 502, "{\"error\":\"" + EscapeJson("应用更新失败: " + ex.Message) + "\"}");
        }
    }

    static void HandleGatewayProxy(HttpListenerRequest req, HttpListenerResponse res)
    {
        string targetUrl = req.Headers["X-Target-Url"];
        if (string.IsNullOrEmpty(targetUrl))
        {
            targetUrl = req.QueryString["url"];
        }
        Uri targetUri;
        if (string.IsNullOrEmpty(targetUrl) || !Uri.TryCreate(targetUrl, UriKind.Absolute, out targetUri) ||
            (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps) ||
            targetUri.IsLoopback)
        {
            res.AddHeader("X-Workbench-Proxy-Error", "1");
            WriteJson(res, 400, "{\"error\":{\"message\":\"无效的目标网关地址\"}}");
            return;
        }

        byte[] bodyBytes = null;
        if (req.HasEntityBody)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                req.InputStream.CopyTo(ms);
                bodyBytes = ms.ToArray();
            }
        }

        string authHeader = req.Headers["Authorization"];
        string reqIdHeader = req.Headers["X-Request-ID"];
        string contentType = string.IsNullOrEmpty(req.ContentType) ? "application/json; charset=utf-8" : req.ContentType;

        HttpResponseMessage upstreamResp = null;
        Exception lastErr = null;
        HttpClient[] clients = new HttpClient[] { directHttpClient, proxyHttpClient };
        for (int i = 0; i < clients.Length; i++)
        {
            try
            {
                using (HttpRequestMessage msg = new HttpRequestMessage(new HttpMethod(req.HttpMethod), targetUri))
                {
                    if (!string.IsNullOrEmpty(authHeader)) msg.Headers.TryAddWithoutValidation("Authorization", authHeader);
                    if (!string.IsNullOrEmpty(reqIdHeader)) msg.Headers.TryAddWithoutValidation("X-Request-ID", reqIdHeader);
                    if (bodyBytes != null && (req.HttpMethod == "POST" || req.HttpMethod == "PUT" || req.HttpMethod == "PATCH"))
                    {
                        ByteArrayContent content = new ByteArrayContent(bodyBytes);
                        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                        msg.Content = content;
                    }
                    upstreamResp = clients[i].SendAsync(msg, HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult();
                    break;
                }
            }
            catch (Exception ex)
            {
                lastErr = ex;
                if (upstreamResp != null)
                {
                    upstreamResp.Dispose();
                    upstreamResp = null;
                }
            }
        }

        if (upstreamResp == null)
        {
            string errMsg = lastErr != null ? (lastErr.InnerException != null ? lastErr.InnerException.Message : lastErr.Message) : "无法连接网关";
            res.AddHeader("X-Workbench-Proxy-Error", "1");
            WriteJson(res, 502, "{\"error\":{\"message\":\"本地网关代理连接失败: " + EscapeJson(errMsg) + "\"}}");
            return;
        }

        using (upstreamResp)
        {
            byte[] respBytes = upstreamResp.Content != null
                ? upstreamResp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                : new byte[0];
            res.StatusCode = (int)upstreamResp.StatusCode;
            if (upstreamResp.Content != null && upstreamResp.Content.Headers.ContentType != null)
            {
                res.ContentType = upstreamResp.Content.Headers.ContentType.ToString();
            }
            else
            {
                res.ContentType = "application/json; charset=utf-8";
            }
            res.ContentLength64 = respBytes.Length;
            if (respBytes.Length > 0)
            {
                res.OutputStream.Write(respBytes, 0, respBytes.Length);
            }
            res.Close();
        }
    }

    static string ExtractJsonField(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return null;
        string keyPattern = "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"";
        Match m = Regex.Match(json, keyPattern);
        if (!m.Success) return null;

        int start = m.Index + m.Length;
        StringBuilder sb = new StringBuilder();
        for (int i = start; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                char next = json[++i];
                if (next == '"') sb.Append('"');
                else if (next == '\\') sb.Append('\\');
                else if (next == '/') sb.Append('/');
                else if (next == 'n') sb.Append('\n');
                else if (next == 'r') sb.Append('\r');
                else if (next == 't') sb.Append('\t');
                else sb.Append(next);
            }
            else if (c == '"')
            {
                break;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    static string EscapeJson(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    static void WriteJson(HttpListenerResponse res, int code, string json)
    {
        res.StatusCode = code;
        res.ContentType = "application/json; charset=utf-8";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.Close();
    }

    static void HandleListOutputs(HttpListenerResponse res)
    {
        try
        {
            Directory.CreateDirectory(OUTPUTS_DIR);
            DirectoryInfo dir = new DirectoryInfo(OUTPUTS_DIR);
            FileInfo[] allFiles = dir.GetFiles();
            var imageFiles = new System.Collections.Generic.List<FileInfo>();
            for (int i = 0; i < allFiles.Length; i++)
            {
                FileInfo fi = allFiles[i];
                if (fi.Length <= 0) continue;
                string ext = fi.Extension.ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp" || ext == ".gif")
                {
                    imageFiles.Add(fi);
                }
            }
            imageFiles.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"files\":[");
            for (int i = 0; i < imageFiles.Count; i++)
            {
                FileInfo fi = imageFiles[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append("\"filename\":\"").Append(EscapeJson(fi.Name)).Append("\",");
                sb.Append("\"url\":\"/outputs/").Append(EscapeJson(Uri.EscapeDataString(fi.Name))).Append("\",");
                sb.Append("\"size\":").Append(fi.Length).Append(",");
                sb.Append("\"timestamp\":").Append(ToUnixMs(fi.LastWriteTimeUtc));
                sb.Append("}");
            }
            sb.Append("]}");
            WriteJson(res, 200, sb.ToString());
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, "{\"error\":\"" + EscapeJson(ex.Message) + "\"}");
        }
    }

    static string GetLocalConfigPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(localAppData, "AI生图工作台");
        return Path.Combine(dir, "config.json");
    }

    static void HandleLocalConfig(HttpListenerRequest req, HttpListenerResponse res)
    {
        try
        {
            string cfgPath = GetLocalConfigPath();
            if (req.HttpMethod == "GET")
            {
                if (!File.Exists(cfgPath))
                {
                    WriteJson(res, 200, "{}");
                    return;
                }
                string content = File.ReadAllText(cfgPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(content)) content = "{}";
                WriteJson(res, 200, content);
                return;
            }
            if (req.HttpMethod == "POST")
            {
                string body;
                using (StreamReader reader = new StreamReader(req.InputStream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                string dir = Path.GetDirectoryName(cfgPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(cfgPath, body, Encoding.UTF8);
                WriteJson(res, 200, "{\"success\":true}");
                return;
            }
            WriteJson(res, 405, "{\"error\":\"Method Not Allowed\"}");
        }
        catch (Exception ex)
        {
            WriteJson(res, 500, "{\"error\":\"" + EscapeJson(ex.Message) + "\"}");
        }
    }

    static long ToUnixMs(DateTime utcTime)
    {
        DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return (long)(utcTime.ToUniversalTime() - epoch).TotalMilliseconds;
    }

    static string GetMimeType(string ext)
    {
        switch ((ext ?? "").ToLowerInvariant())
        {
            case ".html": return "text/html; charset=utf-8";
            case ".js": return "application/javascript; charset=utf-8";
            case ".css": return "text/css; charset=utf-8";
            case ".png": return "image/png";
            case ".jpg":
            case ".jpeg": return "image/jpeg";
            case ".webp": return "image/webp";
            case ".gif": return "image/gif";
            case ".json": return "application/json; charset=utf-8";
            case ".svg": return "image/svg+xml";
            case ".ico": return "image/x-icon";
            default: return "application/octet-stream";
        }
    }
}
