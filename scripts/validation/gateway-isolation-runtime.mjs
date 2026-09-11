import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { spawn, execFileSync } from "node:child_process";
import { pathToFileURL } from "node:url";

// Supply a published launcher beside app/, a compatible node.exe, and an evidence directory.
const [layoutArgument, nodeArgument, outputArgument] = process.argv.slice(2);
assert.ok(layoutArgument && nodeArgument && outputArgument,
  "Usage: node gateway-isolation-runtime.mjs <layout> <node.exe> <output>");
const layout = path.resolve(layoutArgument);
const node = path.resolve(nodeArgument);
const output = path.resolve(outputArgument);
const { chromium } = await import(pathToFileURL(
  path.join(layout, "app", "node_modules", "playwright-core", "index.mjs")).href);
const port = 19428;
const base = `http://127.0.0.1:${port}`;
const route = `${base}/plugins/gateway-isolation/status`;
const temp = fs.mkdtempSync(path.join(os.tmpdir(), "gateway-isolation-proof-"));
fs.mkdirSync(output, { recursive: true });
const token = crypto.randomBytes(32).toString("hex");
const auth = { Authorization: ["Bearer", token].join(" ") };
const build = JSON.parse(fs.readFileSync(path.join(layout, "app", "dist", "build-info.json"), "utf8"));
const cases = [
  { id: "launcher-disabled", launch: "NativeAOT launcher", input: "enabled", mode: "disabled" },
  { id: "fixture-enabled", launch: "Node with launcher-input fixture", input: "enabled", mode: "enabled" },
  { id: "fixture-disabled", launch: "Node with launcher-input fixture", input: "disabled", mode: "disabled" },
  { id: "fixture-missing", launch: "Node with launcher-input fixture", mode: null },
  { id: "fixture-invalid", launch: "Node with launcher-input fixture", input: "invalid", mode: null },
  { id: "fixture-uppercase", launch: "Node with launcher-input fixture", input: "ENABLED", mode: null },
  { id: "fixture-empty", launch: "Node with launcher-input fixture", input: "", mode: null },
  { id: "fixture-whitespace", launch: "Node with launcher-input fixture", input: " enabled ", mode: null },
];
const report = { source: "9aa1df286c2b6fd59b4c101201ddae0ae6000324", build,
  environment: { os: "Windows x64", node: process.version, layout: "expanded application", port },
  cases: [] };
report.sha256 = Object.fromEntries([
  ["launcher", path.join(layout, "openclaw.exe")],
  ["node", node],
  ["runtimeEntry", path.join(layout, "app", "openclaw.mjs")],
  ["plugin", path.join(layout, "app", "dist", "extensions", "gateway-isolation", "index.js")],
].map(([name, file]) => [name, crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex")]));
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(check, label, timeout = 45000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    if (await check()) return;
    await pause(200);
  }
  throw new Error(`Timed out: ${label}`);
}
async function freePort() {
  await new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once("error", reject);
    server.listen(port, "127.0.0.1", () => server.close(resolve));
  });
}
const browser = await chromium.launch({ channel: "msedge", headless: true });
const context = await browser.newContext({
  viewport: { width: 1440, height: 1080 }, deviceScaleFactor: 1,
  permissions: ["clipboard-read", "clipboard-write"],
});
report.environment.browser = await browser.version();
try {
  for (const scenario of cases) {
    await freePort();
    const profile = path.join(temp, scenario.id);
    fs.mkdirSync(profile);
    fs.writeFileSync(path.join(profile, "openclaw.json"), JSON.stringify({
      gateway: { mode: "local", port, bind: "loopback", auth: { mode: "token" },
        controlUi: { allowedOrigins: [base] } },
      agents: { defaults: { workspace: path.join(profile, "workspace") } },
      browser: { enabled: false }, discovery: { mdns: { mode: "off" } },
      logging: { file: path.join(profile, "gateway.log") },
    }));
    const env = Object.fromEntries(Object.entries(process.env)
      .filter(([key]) => !/^(OPENCLAW_|CLAWCTL_GATEWAY_ISOLATION$)/i.test(key)));
    const pathKey = Object.keys(env).find(key => key.toLowerCase() === "path") ?? "PATH";
    env[pathKey] = `${path.dirname(node)};${env[pathKey] ?? ""}`;
    Object.assign(env, {
      OPENCLAW_STATE_DIR: profile, OPENCLAW_CONFIG_PATH: path.join(profile, "openclaw.json"),
      OPENCLAW_GATEWAY_TOKEN: token, OPENCLAW_SUPERVISOR_MODE: "external",
      OPENCLAW_SERVICE_REPAIR_POLICY: "external", OPENCLAW_NO_AUTO_UPDATE: "1",
    });
    if (scenario.input !== undefined) env.CLAWCTL_GATEWAY_ISOLATION = scenario.input;
    const launcher = scenario.id === "launcher-disabled";
    const args = ["gateway", "run", "--port", String(port), "--bind", "loopback",
      "--auth", "token", "--ws-log", "compact"];
    if (!launcher) args.unshift(path.join(layout, "app", "openclaw.mjs"));
    const child = spawn(launcher ? path.join(layout, "openclaw.exe") : node, args,
      { env, cwd: profile, stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
    let logs = "";
    child.stdout.on("data", chunk => { logs += chunk; });
    child.stderr.on("data", chunk => { logs += chunk; });
    const page = await context.newPage();
    let hello;
    const rpcErrors = [];
    page.on("websocket", ws => ws.on("framereceived", ({ payload }) => {
      const frame = JSON.parse(payload.toString());
      if (frame.payload?.type === "hello-ok") hello = frame.payload.server;
      if (frame.error) rpcErrors.push(frame.error.code);
    }));
    const result = { id: scenario.id, launch: scenario.launch,
      input: scenario.input ?? "(missing)", expectedMode: scenario.mode,
      http: [], assertions: [] };
    try {
      await until(async () => {
        if (child.exitCode !== null) throw new Error(`Gateway exited: ${child.exitCode}`);
        try { return (await fetch(`${base}/healthz`)).ok; }
        catch (error) {
          if (error.cause?.code !== "ECONNREFUSED") throw error;
          return false;
        }
      }, `${scenario.id} readiness`);
      const status = scenario.mode ? 200 : 503;
      let canonicalBody;
      for (const method of ["GET", "HEAD"]) {
        for (const access of ["anonymous", "wrong-token", "authenticated"]) {
          const response = await fetch(route, { method, headers:
            access === "authenticated" ? auth :
              access === "wrong-token" ? { Authorization: "Bearer invalid-test-token" } : {} });
          assert.equal(response.status, access === "authenticated" ? status : 401);
          const body = await response.text();
          if (method === "HEAD") assert.equal(body, "");
          if (access === "authenticated") {
            assert.equal(response.headers.get("cache-control"), "no-store");
            assert.equal(response.headers.get("x-content-type-options"), "nosniff");
            assert.equal(response.headers.get("referrer-policy"), "no-referrer");
            assert.match(response.headers.get("content-security-policy"), /frame-ancestors 'self'/);
            if (method === "GET") {
              canonicalBody = body;
              if (scenario.mode) assert.match(body, new RegExp(`>${scenario.mode === "enabled" ? "Enabled" : "Disabled"}<`));
              else {
                assert.match(body, /did not provide a valid Gateway isolation mode/);
                assert.doesNotMatch(body, /status--(?:ok|warn)|id="isolation-command"/);
              }
            }
          }
          result.http.push({ method, access, status: response.status });
        }
      }
      // This handler is read-only, not GET-only. Probe methods without assuming a 405 contract.
      for (const method of ["POST", "PUT", "PATCH", "DELETE"]) {
        const response = await fetch(route, { method, headers: auth });
        assert.equal(response.status, status);
        assert.equal(await response.text(), canonicalBody);
        result.http.push({ method, access: "authenticated", status: response.status,
          effect: "same read-only response" });
      }
      const stable = await fetch(route, { headers: auth });
      assert.equal(stable.status, status);
      assert.equal(await stable.text(), canonicalBody);
      result.assertions.push("GET/HEAD auth denial and valid auth", "response hardening headers",
        "write-method probes leave status unchanged");
      await page.goto(`${base}/#token=${token}`, { waitUntil: "domcontentloaded" });
      await until(() => Boolean(hello), "authenticated hello-ok");
      assert.equal(hello.buildId, build.buildId);
      assert.equal(hello.controlUiBuildSource, "bundled");
      await pause(3000);
      const back = page.getByRole("button", { name: /Back to app/ });
      if (await back.count()) await back.click();
      const tab = page.getByText("Gateway Isolation", { exact: true });
      await tab.waitFor({ state: "visible" });
      await tab.click();
      if (scenario.mode) {
        const iframe = page.locator("iframe");
        await iframe.waitFor({ state: "visible" });
        assert.equal(await iframe.getAttribute("sandbox"), "allow-scripts");
        const frame = page.frameLocator("iframe");
        await frame.getByText("Reported Gateway Isolation", { exact: true }).waitFor();
        const enabled = scenario.mode === "enabled";
        assert.equal(await frame.locator(".status").innerText(), enabled ? "Enabled" : "Disabled");
        assert.equal(await frame.locator(".status").getAttribute("class"),
          `status status--${enabled ? "ok" : "warn"}`);
        const command = `clawctl gateway-isolation ${enabled ? "disable" : "enable"}`;
        assert.equal(await frame.locator("#isolation-command").innerText(), command);
        await frame.getByRole("button", { name: "Copy command" }).click();
        await until(async () => await frame.locator("#copy-command").innerText() === "Copied", "copy completion");
        assert.equal(await page.evaluate(() => navigator.clipboard.readText()), command);
        result.ui = { state: enabled ? "Enabled" : "Disabled", tone: enabled ? "ok" : "warn",
          command, sandbox: "allow-scripts", copy: "exact clipboard text verified" };
        result.assertions.push("visible sidebar tab", "sandboxed iframe", "exact status/tone/inverse CLI",
          "Copy writes exact command to clipboard");
      } else {
        const iframe = page.locator("iframe");
        await iframe.waitFor({ state: "visible" });
        assert.equal(await iframe.getAttribute("sandbox"), "allow-scripts");
        const frame = page.frameLocator("iframe");
        await frame.getByText("The Windows launcher did not provide a valid Gateway isolation mode.",
          { exact: true }).waitFor();
        const text = await frame.locator("body").innerText();
        assert.match(text, /did not provide a valid Gateway isolation mode/);
        assert.doesNotMatch(text, /Reported Gateway Isolation|clawctl gateway-isolation (?:enable|disable)/);
        assert.equal(await frame.locator(".status, #isolation-command, button").count(), 0);
        result.ui = { status: "invalid launcher mode message", staleStatus: false, sandbox: "allow-scripts" };
        result.assertions.push("HTTP 503 fails closed", "Control UI error without stale status or command");
      }
      assert.ok(!page.url().includes(token));
      assert.deepEqual(rpcErrors, []);
      result.hello = { version: hello.version, buildId: hello.buildId,
        controlUiBuildSource: hello.controlUiBuildSource };
      const screenshot = `${scenario.id}.png`;
      await page.screenshot({ path: path.join(output, screenshot), fullPage: true });
      result.screenshot = screenshot;
      result.passed = true;
      console.log(`PASS ${scenario.id}: ${result.http.length} HTTP assertions; ${result.assertions.join("; ")}`);
    } catch (error) {
      fs.writeFileSync(path.join(temp, `${scenario.id}-failure.log`), logs);
      await page.screenshot({ path: path.join(temp, `${scenario.id}-failure.png`), fullPage: true });
      throw error;
    } finally {
      await page.close();
      if (child.exitCode === null) execFileSync("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"],
        { stdio: "ignore" });
      await until(() => child.exitCode !== null || child.signalCode !== null, "owned process exit");
      await freePort();
    }
    report.cases.push(result);
    fs.writeFileSync(path.join(output, "runtime-matrix.json"), JSON.stringify(report, null, 2) + "\n");
  }
  report.completedAt = new Date().toISOString();
  report.cleanup = "All validation-owned process trees stopped; port verified free";
  fs.writeFileSync(path.join(output, "runtime-matrix.json"), JSON.stringify(report, null, 2) + "\n");
  fs.rmSync(temp, { recursive: true });
} finally {
  await browser.close();
}
