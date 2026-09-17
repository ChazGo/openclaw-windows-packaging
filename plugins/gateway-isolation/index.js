const ISOLATION_ENVIRONMENT_VARIABLE = "CLAWCTL_GATEWAY_ISOLATION";
const STATUS_PATH = "/plugins/gateway-isolation/status";
const THEME_MESSAGE_TYPE = "openclaw:widget-theme";
const THEME_BRIDGE_SCRIPT = `<script>
  const themeTokenProperties = {
    surface: "--bg",
    card: "--card",
    text: "--text",
    "text-strong": "--text-strong",
    muted: "--muted",
    border: "--border",
    ok: "--ok-text",
    radius: "--radius",
    "radius-full": "--radius-full",
    "font-body": "--font-body",
  };
  window.addEventListener("message", (event) => {
    if (event.source !== window.parent) return;
    const message = event.data;
    if (
      !message ||
      message.type !== "${THEME_MESSAGE_TYPE}" ||
      (message.mode !== "light" && message.mode !== "dark") ||
      !message.tokens ||
      typeof message.tokens !== "object" ||
      Array.isArray(message.tokens)
    ) return;
    const root = document.documentElement;
    root.dataset.themeMode = message.mode;
    root.style.colorScheme = message.mode;
    for (const [token, property] of Object.entries(themeTokenProperties)) {
      const value = message.tokens[token];
      if (typeof value === "string" && value.trim() && value.length <= 256) {
        root.style.setProperty(property, value);
      }
    }
    const styles = getComputedStyle(root);
    const card = styles.getPropertyValue("--card").trim();
    const ok = styles.getPropertyValue("--ok-text").trim();
    if (card && ok) {
      root.style.setProperty("--ok-bg", "color-mix(in srgb, " + ok + " 18%, " + card + ")");
    }
  });
</script>`;

export function readGatewayIsolationMode(env) {
  const value = env[ISOLATION_ENVIRONMENT_VARIABLE];
  return value === "enabled" ? value : null;
}

export function renderGatewayIsolationPage(mode) {
  if (mode !== "enabled") {
    throw new TypeError("Gateway isolation mode must be enabled.");
  }

  return renderStatusPage(true);
}

function renderStatusPage(enabled) {
  const description = enabled
    ? "This Gateway is running in Windows isolation."
    : "Isolation status is unavailable. The Windows launcher did not provide a valid isolation report.";
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Windows Launcher</title>
  <style>
    :root {
      color-scheme: light dark;
      --font-body: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      --radius: 10px;
      --radius-full: 9999px;
      --bg: #ffffff;
      --card: #f6f8fa;
      --border: #d0d7de;
      --text: #1f2328;
      --text-strong: #1f2328;
      --muted: #59636e;
      --ok-bg: #dafbe1;
      --ok-text: #116329;
    }
    @media (prefers-color-scheme: dark) {
      :root {
        --bg: #0d1117;
        --card: #161b22;
        --border: #30363d;
        --text: #f0f6fc;
        --text-strong: #f0f6fc;
        --muted: #8b949e;
        --ok-bg: #12261e;
        --ok-text: #56d364;
      }
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font-family: var(--font-body);
      font-size: 14px;
    }
    main {
      max-width: 880px;
      margin: 0 auto;
      padding: 24px;
    }
    h1 {
      margin: 0 0 8px;
      color: var(--text-strong);
      font-size: 20px;
      font-weight: 650;
    }
    .intro {
      margin: 0 0 20px;
      color: var(--muted);
      line-height: 1.5;
    }
    .status-section {
      overflow: hidden;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--card);
    }
    .status-row {
      display: grid;
      grid-template-columns: minmax(180px, 1fr) minmax(240px, 1.3fr);
      gap: 20px;
      align-items: center;
      padding: 18px;
    }
    .status-row + .status-row { border-top: 1px solid var(--border); }
    dt { color: var(--text-strong); font-weight: 600; }
    dd { margin: 0; justify-self: end; min-width: 0; }
    .status {
      display: inline-flex;
      align-items: center;
      gap: 7px;
      border-radius: var(--radius-full);
      padding: 5px 10px;
      font-weight: 600;
    }
    .status::before {
      width: 7px;
      height: 7px;
      border-radius: 50%;
      background: currentColor;
      content: "";
    }
    .status--ok { color: var(--ok-text); background: var(--ok-bg); }
    .status--neutral { color: var(--muted); }
    @media (max-width: 620px) {
      main { padding: 16px; }
      .status-row { grid-template-columns: 1fr; gap: 12px; }
      dd { justify-self: start; }
      .status { width: fit-content; }
    }
  </style>
</head>
<body>
  <main>
    <h1>Windows Launcher</h1>
    <p class="intro">${description}</p>
    <dl class="status-section" aria-label="Windows Launcher status">
      <div class="status-row">
        <dt>Gateway</dt>
        <dd><span class="status status--ok">Running</span></dd>
      </div>
      <div class="status-row">
        <dt>Isolation</dt>
        <dd><span class="status status--${enabled ? "ok" : "neutral"}">${enabled ? "Active" : "Invalid"}</span></dd>
      </div>
    </dl>
  </main>
  ${THEME_BRIDGE_SCRIPT}
</body>
</html>`;
}

function renderGatewayIsolationUnavailablePage() {
  return renderStatusPage(false);
}

function writeHtmlResponse(response, statusCode, html) {
  response.writeHead(statusCode, {
    "Cache-Control": "no-store",
    "Content-Security-Policy":
      "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-ancestors 'self'",
    "Content-Type": "text/html; charset=utf-8",
    "Referrer-Policy": "no-referrer",
    "X-Content-Type-Options": "nosniff",
  });
  response.end(html);
}

export function createGatewayIsolationPlugin(env = process.env) {
  const launchMode = readGatewayIsolationMode(env);

  return {
    id: "gateway-isolation",
    name: "Windows Launcher",
    description: "Reports the Windows launch mode selected for the running Gateway.",
    register(api) {
      api.session.controls.registerControlUiDescriptor({
        surface: "tab",
        id: "gateway-isolation",
        label: "Windows Launcher",
        description: "Read-only Windows Gateway isolation status.",
        icon: "shield-check",
        group: "control",
        order: 20,
        path: STATUS_PATH,
        requiredScopes: ["operator.read"],
      });
      api.registerHttpRoute({
        path: STATUS_PATH,
        auth: "gateway",
        match: "exact",
        handler(_request, response) {
          if (!launchMode) {
            writeHtmlResponse(
              response,
              503,
              renderGatewayIsolationUnavailablePage(),
            );
            return true;
          }
          writeHtmlResponse(response, 200, renderGatewayIsolationPage(launchMode));
          return true;
        },
      });
    },
  };
}

export default createGatewayIsolationPlugin();
