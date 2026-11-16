const http = require("http");
const https = require("https");

const LISTEN_HOST = "127.0.0.1";
const LISTEN_PORT = 8787;
const UPSTREAM_ORIGIN = "https://maas.qianwenaiapi.com";
const UPSTREAM_PREFIX = "/compatible-mode/v1";

function writeCors(res, origin = "*") {
  res.setHeader("Access-Control-Allow-Origin", origin || "*");
  res.setHeader("Access-Control-Allow-Headers", "Authorization, Content-Type, X-Requested-With");
  res.setHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
  res.setHeader("Access-Control-Max-Age", "600");
}

function reject(res, status, message) {
  writeCors(res);
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8" });
  res.end(JSON.stringify({ error: { message } }));
}

const server = http.createServer((req, res) => {
  const origin = req.headers.origin || "*";
  writeCors(res, origin);

  if (req.method === "OPTIONS") {
    res.writeHead(204);
    res.end();
    return;
  }

  if (!["GET", "POST"].includes(req.method)) {
    reject(res, 405, "仅支持 GET、POST 和 OPTIONS 请求。");
    return;
  }

  const requestUrl = new URL(req.url, `http://${req.headers.host}`);
  if (!requestUrl.pathname.startsWith(UPSTREAM_PREFIX)) {
    reject(res, 404, `代理只转发 ${UPSTREAM_PREFIX} 下的千问兼容接口。`);
    return;
  }

  const upstreamUrl = new URL(requestUrl.pathname + requestUrl.search, UPSTREAM_ORIGIN);
  const headers = { ...req.headers };
  delete headers.host;
  delete headers.origin;
  delete headers.referer;
  delete headers.connection;
  headers["accept-encoding"] = "identity";

  const upstreamRequest = https.request(upstreamUrl, {
    method: req.method,
    headers,
    timeout: 180000
  }, upstreamResponse => {
    res.writeHead(upstreamResponse.statusCode || 502, upstreamResponse.headers);
    upstreamResponse.pipe(res);
  });

  upstreamRequest.on("timeout", () => upstreamRequest.destroy(new Error("千问上游请求超时")));
  upstreamRequest.on("error", error => {
    if (res.headersSent) {
      res.destroy(error);
      return;
    }
    reject(res, 502, `千问上游连接失败：${error.code ? `${error.code}，` : ""}${error.message}`);
  });
  req.pipe(upstreamRequest);
});

server.on("error", error => {
  if (error.code === "EADDRINUSE") {
    console.log(`Qwen local proxy is already running at http://${LISTEN_HOST}:${LISTEN_PORT}${UPSTREAM_PREFIX}`);
    process.exitCode = 0;
    return;
  }
  console.error(`Qwen local proxy failed to start: ${error.code || "ERROR"} ${error.message}`);
  process.exitCode = 1;
});

server.listen(LISTEN_PORT, LISTEN_HOST, () => {
  console.log(`千问本地代理已启动：http://${LISTEN_HOST}:${LISTEN_PORT}${UPSTREAM_PREFIX}`);
});
