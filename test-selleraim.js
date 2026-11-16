/**
 * Seller Aim 模型网关连通性自检脚本
 * 遵循 https://api.model.selleraim.com/skill.md 规范
 * 
 * 运行方式：
 *   node test-selleraim.js <你的 API Key>
 * 或设置环境变量后运行：
 *   $env:MG_API_KEY="my-xxxx"; node test-selleraim.js
 */

const fs = require("fs");
const path = require("path");

function loadEnvFile() {
  const envPath = path.join(__dirname, ".env");
  if (fs.existsSync(envPath)) {
    const lines = fs.readFileSync(envPath, "utf-8").split(/\r?\n/);
    for (const line of lines) {
      const trimmed = line.trim();
      if (!trimmed || trimmed.startsWith("#")) continue;
      const idx = trimmed.indexOf("=");
      if (idx > 0) {
        const key = trimmed.slice(0, idx).trim();
        const val = trimmed.slice(idx + 1).trim().replace(/^['"]|['"]$/g, "");
        if (!process.env[key]) {
          process.env[key] = val;
        }
      }
    }
  }
}

loadEnvFile();

const BASE_URL = (process.env.MG_BASE_URL || "https://api.model.selleraim.com").replace(/\/+$/, "");
const API_KEY = process.argv[2] || process.env.MG_API_KEY || "";

async function main() {
  console.log("==========================================");
  console.log("Seller Aim 模型网关连通性自检");
  console.log(`网关地址: ${BASE_URL}`);
  console.log("==========================================\n");

  if (!API_KEY || API_KEY === "my-xxxxxxxx") {
    console.error("【提示】未检测到有效的 API Key。");
    console.error("使用方法：");
    console.error("  1. 在 .env 文件或环境变量中配置 MG_API_KEY=my-xxxx");
    console.error("  2. 或者直接命令行传参：node test-selleraim.js my-xxxx\n");
    process.exit(1);
  }

  const maskedKey = API_KEY.length > 8 ? `${API_KEY.slice(0, 4)}...${API_KEY.slice(-4)}` : "***";
  console.log(`使用密钥: ${maskedKey}`);
  console.log("正在拉取模型清单 (GET /v1/models)...");

  try {
    const modelsRes = await fetch(`${BASE_URL}/v1/models`, {
      method: "GET",
      headers: {
        Authorization: `Bearer ${API_KEY}`,
        "X-Request-ID": `check-models-${Date.now()}`
      }
    });

    const requestId = modelsRes.headers.get("x-request-id") || "(无)";
    console.log(`HTTP 状态: ${modelsRes.status} ${modelsRes.statusText}`);
    console.log(`X-Request-ID: ${requestId}`);

    const modelsData = await modelsRes.json();
    if (!modelsRes.ok) {
      console.error("\n拉取模型失败:", modelsData?.error?.message || modelsData);
      process.exit(1);
    }

    const models = Array.isArray(modelsData?.data) ? modelsData.data : [];
    console.log(`\n成功获取 ${models.length} 个可用模型:`);

    const imageModels = models.filter(m => m.kind === "image");
    const chatModels = models.filter(m => m.kind === "chat");
    const otherModels = models.filter(m => m.kind !== "image" && m.kind !== "chat");

    if (imageModels.length) {
      console.log("\n[生图模型 (kind: image)]");
      imageModels.forEach(m => console.log(`  - ${m.id} (${m.name || "未命名"})`));
    } else {
      console.log("\n[生图模型]: 暂无标注为 image 的模型，将展示其他可用模型");
    }

    if (chatModels.length) {
      console.log("\n[对话模型 (kind: chat)]");
      chatModels.slice(0, 8).forEach(m => console.log(`  - ${m.id} (${m.name || "未命名"})`));
      if (chatModels.length > 8) console.log(`  ... 另有 ${chatModels.length - 8} 个对话模型`);
    }

    if (otherModels.length) {
      console.log(`\n[其他模态模型]: 共 ${otherModels.length} 个`);
    }

    // 挑选一个对话模型测试连通性
    const testChatModel = chatModels[0]?.id || "qwen3.8-flash";
    console.log(`\n------------------------------------------`);
    console.log(`正在进行基础对话连通性测试 (模型: ${testChatModel})...`);

    const chatRes = await fetch(`${BASE_URL}/v1/chat/completions`, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${API_KEY}`,
        "Content-Type": "application/json",
        "X-Request-ID": `check-chat-${Date.now()}`
      },
      body: JSON.stringify({
        model: testChatModel,
        messages: [{ role: "user", content: "你好" }]
      })
    });

    const chatReqId = chatRes.headers.get("x-request-id") || "(无)";
    const cost = chatRes.headers.get("x-mg-cost") || "";
    const currency = chatRes.headers.get("x-mg-cost-currency") || "CNY";

    console.log(`HTTP 状态: ${chatRes.status} ${chatRes.statusText}`);
    console.log(`X-Request-ID: ${chatReqId}`);
    if (cost) console.log(`网关计费 (X-MG-Cost): ${cost} ${currency}`);

    const chatData = await chatRes.json();
    if (chatRes.ok) {
      const reply = chatData.choices?.[0]?.message?.content || "(无返回内容)";
      console.log(`模型回复: ${reply.trim().slice(0, 100)}...`);
      console.log("\n【恭喜】Seller Aim 网关连接自检完全通过！");
      console.log("您现在可以在「临时生图.html」中选择「Seller Aim (模型网关)」使用！");
    } else {
      console.log("对话测试响应:", chatData?.error?.message || chatData);
    }
  } catch (err) {
    console.error("连接发生异常:", err.message);
  }
}

main();
