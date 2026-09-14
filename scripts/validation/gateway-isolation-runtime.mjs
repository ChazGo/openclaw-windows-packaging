import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawn } from "node:child_process";
import { pathToFileURL } from "node:url";

const [
  layoutArgument,
  nodeArgument,
  outputArgument,
  coreCommit,
  packagingCommit,
] = process.argv.slice(2);
assert.ok(
  layoutArgument &&
    nodeArgument &&
    outputArgument &&
    /^[0-9a-f]{40}$/.test(coreCommit ?? "") &&
    /^[0-9a-f]{40}$/.test(packagingCommit ?? ""),
  "Usage: node gateway-isolation-runtime.mjs <layout> <node.exe> <output> <core-commit> <packaging-commit>",
);

const layout = path.resolve(layoutArgument);
const node = path.resolve(nodeArgument);
const output = path.resolve(outputArgument);
const pluginPath = "/plugins/gateway-isolation/status";
const temp = fs.mkdtempSync(path.join(os.tmpdir(), "windows-launcher-proof-"));
const token = crypto.randomBytes(32).toString("hex");
const auth = { Authorization: `Bearer ${token}` };
const build = JSON.parse(
  fs.readFileSync(path.join(layout, "app", "dist", "build-info.json"), "utf8"),
);
const { chromium } = await import(
  pathToFileURL(
    path.resolve(layout, "app", "node_modules", "playwright-core", "index.mjs"),
  ).href
);

const customThemeTokenNames = [
  "bg",
  "bg-accent",
  "bg-elevated",
  "bg-hover",
  "bg-muted",
  "bg-content",
  "card",
  "card-foreground",
  "card-highlight",
  "popover",
  "popover-foreground",
  "panel",
  "panel-strong",
  "panel-hover",
  "chrome",
  "chrome-strong",
  "text",
  "text-strong",
  "chat-text",
  "muted",
  "muted-strong",
  "muted-foreground",
  "border",
  "border-strong",
  "border-hover",
  "input",
  "ring",
  "accent",
  "accent-hover",
  "accent-muted",
  "accent-subtle",
  "accent-foreground",
  "accent-glow",
  "primary",
  "primary-foreground",
  "secondary",
  "secondary-foreground",
  "accent-2",
  "accent-2-muted",
  "accent-2-subtle",
  "destructive",
  "destructive-foreground",
  "danger",
  "danger-muted",
  "danger-subtle",
  "focus",
  "focus-ring",
  "focus-glow",
  "font-body",
  "font-display",
  "mono",
  "grid-line",
];

function customMode(defaultColor, overrides) {
  const tokens = Object.fromEntries(
    customThemeTokenNames.map((name) => [name, defaultColor]),
  );
  Object.assign(tokens, {
    "font-body": "Georgia, serif",
    "font-display": "Georgia, serif",
    mono: "Courier New, monospace",
    ...overrides,
  });
  return tokens;
}

const customTheme = {
  sourceUrl: "https://tweakcn.com/themes/windows-launcher-proof",
  themeId: "windows-launcher-proof",
  label: "Windows Launcher Proof",
  importedAt: "2026-09-14T00:00:00.000Z",
  light: customMode("#fff4d6", {
    bg: "#fff4d6",
    "bg-elevated": "#ffd166",
    card: "#ffe3a3",
    text: "#342400",
    "text-strong": "#1f1300",
    muted: "#765b23",
    border: "#b7791f",
    "border-strong": "#7c4f00",
    accent: "#7b2cbf",
    primary: "#7b2cbf",
    "primary-foreground": "#ffffff",
    ok: "#006d3c",
    warn: "#9c4a00",
  }),
  dark: customMode("#151026", {
    bg: "#151026",
    "bg-elevated": "#3f2a63",
    card: "#281d45",
    text: "#f8efff",
    "text-strong": "#ffffff",
    muted: "#c5aad8",
    border: "#7b5aa6",
    "border-strong": "#ad86d8",
    accent: "#ff4fd8",
    primary: "#ff4fd8",
    "primary-foreground": "#201020",
    ok: "#70e1a1",
    warn: "#ffb347",
  }),
};

const tokenMapping = {
  "--bg": "--bg",
  "--card": "--card",
  "--button-bg": "--bg-elevated",
  "--text": "--text",
  "--text-strong": "--text-strong",
  "--muted": "--muted",
  "--border": "--border",
  "--border-strong": "--border-strong",
  "--focus": "--accent",
  "--ok-text": "--ok",
  "--warn-text": "--warn",
  "--radius": "--radius",
  "--radius-full": "--radius-full",
  "--font-body": "--font-body",
  "--font-mono": "--mono",
};

const pause = (milliseconds) =>
  new Promise((resolve) => setTimeout(resolve, milliseconds));

async function until(check, label, timeout = 45_000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    if (await check()) {
      return;
    }
    await pause(200);
  }
  throw new Error(`Timed out: ${label}`);
}

async function reservePort() {
  return await new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      assert.equal(typeof address, "object");
      const port = address.port;
      server.close(() => resolve(port));
    });
  });
}

async function assertPortFree(port) {
  await new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once("error", reject);
    server.listen(port, "127.0.0.1", () => server.close(resolve));
  });
}

function sha256(file) {
  return crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex");
}

function publicEnvironment(browserVersion) {
  return {
    os: "Windows x64",
    node: process.version,
    browser: browserVersion,
    layout: "expanded application",
  };
}

async function setTheme(page, theme, mode) {
  await page.evaluate(
    ({ customTheme, mode, theme }) => {
      const key = Object.keys(localStorage).find((candidate) =>
        candidate.startsWith("openclaw.control.settings.v1:"),
      );
      if (!key) {
        throw new Error("Control UI settings key was not created.");
      }
      const settings = JSON.parse(localStorage.getItem(key));
      settings.theme = theme;
      settings.themeMode = mode;
      if (theme === "custom") {
        settings.customTheme = customTheme;
      }
      const value = JSON.stringify(settings);
      localStorage.setItem(key, value);
      window.dispatchEvent(new StorageEvent("storage", { key, newValue: value }));
    },
    { customTheme, mode, theme },
  );
}

async function themeSnapshot(page, frame, expectedTheme, expectedMode) {
  await until(
    async () =>
      (await page.locator("html").getAttribute("data-theme")) === expectedTheme &&
      (await page.locator("html").getAttribute("data-theme-mode")) === expectedMode,
    `${expectedTheme} ${expectedMode} host theme`,
  );
  await until(
    async () =>
      (await frame.locator("html").getAttribute("data-theme-mode")) === expectedMode,
    `${expectedTheme} ${expectedMode} plugin theme`,
  );

  const host = await page.evaluate((mapping) => {
    const styles = getComputedStyle(document.documentElement);
    return Object.fromEntries(
      Object.values(mapping).map((property) => [
        property,
        styles.getPropertyValue(property).trim(),
      ]),
    );
  }, tokenMapping);
  const plugin = await frame.evaluate((mapping) => {
    const styles = getComputedStyle(document.documentElement);
    return {
      mode: document.documentElement.dataset.themeMode,
      values: Object.fromEntries(
        Object.keys(mapping).map((property) => [
          property,
          styles.getPropertyValue(property).trim(),
        ]),
      ),
      h1: document.querySelector("h1")?.textContent,
      status: document.querySelector(".status")?.textContent,
      row: document.querySelector(".settings-row__title")?.textContent,
      mutationGuidance:
        document.body.textContent.includes("Change with CLI") ||
        document.body.textContent.includes("clawctl gateway-isolation"),
    };
  }, tokenMapping);

  for (const [pluginProperty, hostProperty] of Object.entries(tokenMapping)) {
    assert.ok(host[hostProperty], `Host did not publish ${hostProperty}.`);
    assert.equal(
      plugin.values[pluginProperty],
      host[hostProperty],
      `${pluginProperty} did not match ${hostProperty}.`,
    );
  }
  assert.equal(plugin.mode, expectedMode);
  assert.equal(plugin.h1, "Windows Launcher");
  assert.equal(plugin.status, "Disabled");
  assert.equal(plugin.row, "Reported Gateway Isolation");
  assert.equal(plugin.mutationGuidance, false);
  return { hostTheme: expectedTheme, mode: expectedMode, tokens: plugin.values };
}

const port = await reservePort();
const base = `http://127.0.0.1:${port}`;
const route = `${base}${pluginPath}`;
const profile = path.join(temp, "profile");
fs.mkdirSync(profile);
fs.mkdirSync(output, { recursive: true });
fs.writeFileSync(
  path.join(profile, "openclaw.json"),
  JSON.stringify({
    gateway: {
      mode: "local",
      port,
      bind: "loopback",
      auth: { mode: "token" },
      controlUi: { allowedOrigins: [base] },
    },
    agents: { defaults: { workspace: path.join(profile, "workspace") } },
    browser: { enabled: false },
    discovery: { mdns: { mode: "off" } },
    logging: { file: path.join(profile, "gateway.log") },
  }),
);

const env = Object.fromEntries(
  Object.entries(process.env).filter(
    ([key]) => !/^(OPENCLAW_|CLAWCTL_GATEWAY_ISOLATION$)/i.test(key),
  ),
);
const pathKey = Object.keys(env).find((key) => key.toLowerCase() === "path") ?? "PATH";
env[pathKey] = `${path.dirname(node)};${env[pathKey] ?? ""}`;
Object.assign(env, {
  OPENCLAW_STATE_DIR: profile,
  OPENCLAW_CONFIG_PATH: path.join(profile, "openclaw.json"),
  OPENCLAW_GATEWAY_TOKEN: token,
  OPENCLAW_SUPERVISOR_MODE: "external",
  OPENCLAW_SERVICE_REPAIR_POLICY: "external",
  OPENCLAW_NO_AUTO_UPDATE: "1",
  CLAWCTL_GATEWAY_ISOLATION: "enabled",
});

const child = spawn(
  path.join(layout, "openclaw.exe"),
  ["gateway", "run", "--port", String(port), "--bind", "loopback", "--auth", "token"],
  { env, cwd: profile, stdio: ["ignore", "pipe", "pipe"], windowsHide: true },
);
let logs = "";
child.stdout.on("data", (chunk) => {
  logs += chunk;
});
child.stderr.on("data", (chunk) => {
  logs += chunk;
});

const browser = await chromium.launch({ channel: "msedge", headless: true });
const context = await browser.newContext({
  viewport: { width: 1440, height: 1080 },
  deviceScaleFactor: 1,
});
const page = await context.newPage();
let hello;
const rpcErrors = [];
page.on("websocket", (socket) =>
  socket.on("framereceived", ({ payload }) => {
    const frame = JSON.parse(payload.toString());
    if (frame.payload?.type === "hello-ok") {
      hello = frame.payload.server;
    }
    if (frame.error) {
      rpcErrors.push(frame.error.code);
    }
  }),
);
const visibleFrameRequests = [];
page.on("request", (request) => {
  const url = new URL(request.url());
  if (
    request.isNavigationRequest() &&
    url.pathname === pluginPath &&
    url.search === ""
  ) {
    visibleFrameRequests.push(request.url());
  }
});

try {
  await until(async () => {
    if (child.exitCode !== null) {
      throw new Error(`Gateway exited with ${child.exitCode}: ${logs}`);
    }
    try {
      return (await fetch(`${base}/healthz`)).ok;
    } catch (error) {
      if (error.cause?.code !== "ECONNREFUSED") {
        throw error;
      }
      return false;
    }
  }, "Gateway readiness");

  let canonicalBody;
  const httpResults = [];
  for (const method of ["GET", "HEAD"]) {
    for (const access of ["anonymous", "wrong-token", "authenticated"]) {
      const response = await fetch(route, {
        method,
        headers:
          access === "authenticated"
            ? auth
            : access === "wrong-token"
              ? { Authorization: "Bearer wrong-token" }
              : {},
      });
      assert.equal(response.status, access === "authenticated" ? 200 : 401);
      const body = await response.text();
      if (method === "HEAD") {
        assert.equal(body, "");
      }
      if (access === "authenticated") {
        assert.equal(response.headers.get("cache-control"), "no-store");
        assert.equal(response.headers.get("x-content-type-options"), "nosniff");
        assert.equal(response.headers.get("referrer-policy"), "no-referrer");
        assert.match(
          response.headers.get("content-security-policy"),
          /frame-ancestors 'self'/,
        );
        if (method === "GET") {
          canonicalBody = body;
          assert.match(body, />Disabled</);
          assert.doesNotMatch(
            body,
            /Change with CLI|clipboard|copy-command|clawctl gateway-isolation/,
          );
        }
      }
      httpResults.push({ method, access, status: response.status });
    }
  }
  for (const method of ["POST", "PUT", "PATCH", "DELETE"]) {
    const response = await fetch(route, { method, headers: auth });
    assert.equal(response.status, 200);
    assert.equal(await response.text(), canonicalBody);
    httpResults.push({
      method,
      access: "authenticated",
      status: response.status,
      effect: "same read-only response",
    });
  }

  await page.goto(`${base}/#token=${token}`, { waitUntil: "domcontentloaded" });
  await until(() => Boolean(hello), "authenticated hello-ok");
  assert.equal(hello.buildId, build.buildId);
  assert.equal(hello.controlUiBuildSource, "bundled");
  await pause(2_000);
  const back = page.getByRole("button", { name: /Back to app/ });
  if (await back.count()) {
    await back.click();
  }
  const tab = page.getByText("Windows Launcher", { exact: true });
  await tab.waitFor({ state: "visible" });
  await tab.click();

  const iframe = page.locator("iframe.plugin-tab-embed__frame");
  await iframe.waitFor({ state: "visible" });
  assert.equal(await iframe.getAttribute("sandbox"), "allow-scripts");
  assert.equal((await iframe.getAttribute("sandbox")).includes("allow-same-origin"), false);
  const pluginFrame = page.frames().find((candidate) => {
    try {
      return new URL(candidate.url()).pathname === pluginPath;
    } catch {
      return false;
    }
  });
  assert.ok(pluginFrame, "Plugin frame did not mount.");
  await pluginFrame
    .getByText("Reported Gateway Isolation", { exact: true })
    .waitFor({ state: "visible" });
  assert.equal(visibleFrameRequests.length, 1);

  let postMountNavigations = 0;
  page.on("framenavigated", (frame) => {
    if (frame === pluginFrame) {
      postMountNavigations++;
    }
  });

  const themes = [
    {
      id: "claw-dark",
      theme: "claw",
      mode: "dark",
      resolved: "dark",
      screenshot: "claw-dark.png",
    },
    {
      id: "claw-light",
      theme: "claw",
      mode: "light",
      resolved: "light",
      screenshot: "claw-light.png",
    },
    {
      id: "custom-dark",
      theme: "custom",
      mode: "dark",
      resolved: "custom",
      screenshot: "custom-dark.png",
    },
    {
      id: "custom-light",
      theme: "custom",
      mode: "light",
      resolved: "custom-light",
      screenshot: "custom-light.png",
    },
  ];
  const themeResults = [];
  for (const theme of themes) {
    await setTheme(page, theme.theme, theme.mode);
    const snapshot = await themeSnapshot(
      page,
      pluginFrame,
      theme.resolved,
      theme.mode,
    );
    assert.equal(
      page.frames().find((candidate) => candidate === pluginFrame),
      pluginFrame,
    );
    assert.equal(visibleFrameRequests.length, 1);
    assert.equal(postMountNavigations, 0);
    await page.screenshot({
      path: path.join(output, theme.screenshot),
      fullPage: true,
    });
    themeResults.push({
      id: theme.id,
      family: theme.theme === "custom" ? customTheme.label : "Claw",
      importedCustomTheme: theme.theme === "custom",
      screenshot: theme.screenshot,
      ...snapshot,
    });
  }

  assert.deepEqual(rpcErrors, []);
  const result = {
    schemaVersion: 1,
    packagingCommit,
    corePullRequest: "openclaw/openclaw#145409",
    coreCommit,
    build: {
      version: hello.version,
      buildId: hello.buildId,
      controlUiBuildSource: hello.controlUiBuildSource,
    },
    environment: publicEnvironment(await browser.version()),
    sha256: {
      launcher: sha256(path.join(layout, "openclaw.exe")),
      node: sha256(node),
      runtimeEntry: sha256(path.join(layout, "app", "openclaw.mjs")),
      plugin: sha256(
        path.join(
          layout,
          "app",
          "dist",
          "extensions",
          "gateway-isolation",
          "index.js",
        ),
      ),
    },
    launcher: {
      inheritedIsolationInput: "enabled",
      reportedIsolationMode: "disabled",
      provesLauncherOverridesInheritedInput: true,
    },
    route: {
      path: pluginPath,
      httpResults,
      authenticated: true,
      readOnly: true,
      responseHardening: true,
    },
    frame: {
      sandbox: "allow-scripts",
      allowSameOrigin: false,
      visibleRequests: visibleFrameRequests.length,
      postMountNavigations,
      sameFrameAcrossLiveThemeSwitches: true,
    },
    themes: themeResults,
    unsupportedCliGuidanceAbsent: true,
    passed: true,
  };
  const json = `${JSON.stringify(result, null, 2)}\n`;
  assert.doesNotMatch(json, /[A-Za-z]:[\\/]/);
  assert.equal(json.includes(os.userInfo().username), false);
  fs.writeFileSync(path.join(output, "runtime-results.json"), json);
  fs.rmSync(temp, { recursive: true, maxRetries: 10, retryDelay: 200 });
  console.log(
    `Windows Launcher runtime proof passed: ${themes.length} themes, one frame request, no reloads.`,
  );
} catch (error) {
  fs.writeFileSync(path.join(temp, "failure.log"), logs);
  await page.screenshot({
    path: path.join(temp, "failure.png"),
    fullPage: true,
  });
  throw error;
} finally {
  await page.close();
  await context.close();
  await browser.close();
  if (child.exitCode === null) {
    execFileSync("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"], {
      stdio: "ignore",
    });
  }
  await until(
    () => child.exitCode !== null || child.signalCode !== null,
    "owned process exit",
  );
  await assertPortFree(port);
}
