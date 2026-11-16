const { execFileSync } = require("child_process");

try {
  execFileSync("powershell.exe", [
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-Command",
    "$connections = Get-NetTCPConnection -LocalPort 8787 -State Listen -ErrorAction SilentlyContinue; " +
      "if ($connections) { $connections | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue } }"
  ], { stdio: "ignore" });
} catch {}

setTimeout(() => {
  require("./qwen-proxy.js");
}, 500);
