const http = require("http");
const fs = require("fs");
const os = require("os");
const path = require("path");

const PORT = 8787;
const OUTPUTS_DIR = path.join(__dirname, "outputs");
if (!fs.existsSync(OUTPUTS_DIR)) {
  fs.mkdirSync(OUTPUTS_DIR, { recursive: true });
}

function getLocalConfigPath() {
  const baseDir = process.env.LOCALAPPDATA || os.homedir();
  return path.join(baseDir, "AI生图工作台", "config.json");
}

function setCors(res) {
  res.setHeader("Access-Control-Allow-Origin", "*");
  res.setHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
  res.setHeader("Access-Control-Allow-Headers", "Content-Type, Authorization, X-Requested-With, X-Request-ID, X-Target-Url");
  res.setHeader("Access-Control-Expose-Headers", "X-Workbench-Proxy-Error");
}

const server = http.createServer(async (req, res) => {
  setCors(res);
  if (req.method === "OPTIONS") {
    res.writeHead(204);
    res.end();
    return;
  }

  let reqUrl;
  try {
    reqUrl = new URL(req.url, `http://${req.headers.host || "127.0.0.1:8787"}`);
  } catch (e) {
    res.writeHead(400, { "Content-Type": "text/plain; charset=utf-8" });
    res.end("Bad Request");
    return;
  }

  // 健康检测
  if (reqUrl.pathname === "/api/ping") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ status: "ok", outputsDir: OUTPUTS_DIR }));
    return;
  }

  // 本地直连网关代理（绕过浏览器/系统代理超时与干扰）
  if (reqUrl.pathname === "/api/proxy") {
    const targetUrl = req.headers["x-target-url"] || reqUrl.searchParams.get("url") || "";
    let parsed;
    try {
      parsed = new URL(targetUrl);
    } catch (e) {}
    if (!parsed || !/^https?:$/.test(parsed.protocol) || ["127.0.0.1", "localhost", "::1"].includes(parsed.hostname)) {
      res.setHeader("X-Workbench-Proxy-Error", "1");
      res.writeHead(400, { "Content-Type": "application/json; charset=utf-8" });
      res.end(JSON.stringify({ error: { message: "无效的目标网关地址" } }));
      return;
    }
    const chunks = [];
    req.on("data", chunk => chunks.push(chunk));
    req.on("end", async () => {
      try {
        const headers = {};
        if (req.headers["authorization"]) headers["Authorization"] = req.headers["authorization"];
        if (req.headers["x-request-id"]) headers["X-Request-ID"] = req.headers["x-request-id"];
        if (req.headers["content-type"]) headers["Content-Type"] = req.headers["content-type"];
        const hasBody = chunks.length > 0 && ["POST", "PUT", "PATCH"].includes(req.method);
        const upstream = await fetch(parsed.toString(), {
          method: req.method,
          headers,
          body: hasBody ? Buffer.concat(chunks) : undefined,
          signal: AbortSignal.timeout(600000)
        });
        const buf = Buffer.from(await upstream.arrayBuffer());
        res.writeHead(upstream.status, {
          "Content-Type": upstream.headers.get("content-type") || "application/json; charset=utf-8"
        });
        res.end(buf);
      } catch (err) {
        res.setHeader("X-Workbench-Proxy-Error", "1");
        res.writeHead(502, { "Content-Type": "application/json; charset=utf-8" });
        res.end(JSON.stringify({ error: { message: "本地网关代理连接失败: " + err.message } }));
      }
    });
    return;
  }

  // 扫描本地 outputs 文件夹历史图片
  if (reqUrl.pathname === "/api/outputs" && req.method === "GET") {
    try {
      fs.mkdirSync(OUTPUTS_DIR, { recursive: true });
      const exts = new Set([".png", ".jpg", ".jpeg", ".webp", ".gif"]);
      const files = fs.readdirSync(OUTPUTS_DIR)
        .map(name => {
          const full = path.join(OUTPUTS_DIR, name);
          const stat = fs.statSync(full);
          return { name, stat, ext: path.extname(name).toLowerCase() };
        })
        .filter(item => item.stat.isFile() && item.stat.size > 0 && exts.has(item.ext))
        .sort((a, b) => b.stat.mtimeMs - a.stat.mtimeMs)
        .map(item => ({
          filename: item.name,
          url: "/outputs/" + encodeURIComponent(item.name),
          size: item.stat.size,
          timestamp: Math.floor(item.stat.mtimeMs)
        }));
      res.writeHead(200, { "Content-Type": "application/json; charset=utf-8" });
      res.end(JSON.stringify({ files }));
    } catch (err) {
      res.writeHead(500, { "Content-Type": "application/json; charset=utf-8" });
      res.end(JSON.stringify({ error: err.message }));
    }
    return;
  }

  // 本机配置读写 (%LOCALAPPDATA%\AI生图工作台\config.json)
  if (reqUrl.pathname === "/api/config") {
    const cfgPath = getLocalConfigPath();
    if (req.method === "GET") {
      try {
        const raw = fs.existsSync(cfgPath) ? fs.readFileSync(cfgPath, "utf-8") : "{}";
        res.writeHead(200, { "Content-Type": "application/json; charset=utf-8" });
        res.end(raw.trim() || "{}");
      } catch (err) {
        res.writeHead(500, { "Content-Type": "application/json; charset=utf-8" });
        res.end(JSON.stringify({ error: err.message }));
      }
      return;
    }
    if (req.method === "POST") {
      const chunks = [];
      req.on("data", chunk => chunks.push(chunk));
      req.on("end", () => {
        try {
          const body = Buffer.concat(chunks).toString("utf-8").trim() || "{}";
          fs.mkdirSync(path.dirname(cfgPath), { recursive: true });
          fs.writeFileSync(cfgPath, body, "utf-8");
          res.writeHead(200, { "Content-Type": "application/json; charset=utf-8" });
          res.end(JSON.stringify({ success: true }));
        } catch (err) {
          res.writeHead(500, { "Content-Type": "application/json; charset=utf-8" });
          res.end(JSON.stringify({ error: err.message }));
        }
      });
      return;
    }
  }

  // 图像静默无感直存本地 outputs 目录
  if (reqUrl.pathname === "/api/save-image" && req.method === "POST") {
    const chunks = [];
    req.on("data", chunk => chunks.push(chunk));
    req.on("end", async () => {
      try {
        const rawBody = Buffer.concat(chunks).toString("utf-8");
        let { filename, url: imgUrl, base64, data } = JSON.parse(rawBody);
        const safeName = path.basename(filename || `image_${Date.now()}.png`);
        fs.mkdirSync(OUTPUTS_DIR, { recursive: true });
        const filePath = path.join(OUTPUTS_DIR, safeName);
        const rawB64 = base64 || data;

        if (rawB64 || (imgUrl && imgUrl.startsWith("data:"))) {
          const b64Source = rawB64 || imgUrl;
          const cleanB64 = b64Source.replace(/^data:image\/\w+;base64,/, "").replace(/^data:[^;]+;base64,/, "");
          fs.writeFileSync(filePath, Buffer.from(cleanB64, "base64"));
        } else if (imgUrl && /^https?:\/\//i.test(imgUrl)) {
          const resp = await fetch(imgUrl, { signal: AbortSignal.timeout(60000) });
          if (!resp.ok) throw new Error(`下载原图失败: HTTP ${resp.status}`);
          const buffer = Buffer.from(await resp.arrayBuffer());
          fs.writeFileSync(filePath, buffer);
        } else {
          throw new Error("未提供有效的图像数据 (URL 或 Base64)");
        }

        console.log(`[AutoSave] 图像已直接保存至: ${filePath}`);
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ success: true, filename: safeName, fullPath: filePath }));
      } catch (err) {
        console.error("[AutoSave Error]", err.message);
        res.writeHead(500, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: err.message }));
      }
    });
    return;
  }

  // 静态页面访问支持 (优先 AI生图工作台.html)
  let requestPath = decodeURIComponent(reqUrl.pathname);
  if (requestPath === "/" || requestPath === "/index.html") {
    requestPath = fs.existsSync(path.join(__dirname, "AI生图工作台.html"))
      ? "AI生图工作台.html"
      : "临时生图.html";
  }

  let filePath = path.join(__dirname, requestPath);
  if (fs.existsSync(filePath) && fs.statSync(filePath).isFile()) {
    const ext = path.extname(filePath).toLowerCase();
    const mimeMap = {
      ".html": "text/html; charset=utf-8",
      ".js": "application/javascript; charset=utf-8",
      ".css": "text/css; charset=utf-8",
      ".png": "image/png",
      ".jpg": "image/jpeg",
      ".jpeg": "image/jpeg",
      ".webp": "image/webp",
      ".json": "application/json; charset=utf-8",
      ".svg": "image/svg+xml"
    };
    res.writeHead(200, { "Content-Type": mimeMap[ext] || "application/octet-stream" });
    fs.createReadStream(filePath).pipe(res);
    return;
  }

  res.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
  res.end("页面未找到");
});

server.listen(PORT, "127.0.0.1", () => {
  console.log(`AI 生图本地存储与代理服务已在 http://127.0.0.1:${PORT} 运行`);
});
