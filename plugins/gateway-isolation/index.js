const ISOLATION_ENVIRONMENT_VARIABLE = "CLAWCTL_GATEWAY_ISOLATION";
const STATUS_PATH = "/plugins/gateway-isolation/status";

export function readGatewayIsolationMode(env) {
  const value = env[ISOLATION_ENVIRONMENT_VARIABLE];
  return value === "enabled" || value === "disabled" ? value : null;
}

export function renderGatewayIsolationPage(mode) {
  if (mode !== "enabled" && mode !== "disabled") {
    throw new TypeError("Gateway isolation mode must be enabled or disabled.");
  }

  const enabled = mode === "enabled";
  const status = enabled ? "Enabled" : "Disabled";
  const command = `clawctl gateway-isolation ${enabled ? "disable" : "enable"}`;
  const tone = enabled ? "ok" : "warn";

  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Gateway Isolation</title>
  <style>
    :root {
      color-scheme: light dark;
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      --bg: #ffffff;
      --card: #f6f8fa;
      --border: #d0d7de;
      --text: #1f2328;
      --muted: #59636e;
      --ok-bg: #dafbe1;
      --ok-text: #116329;
      --warn-bg: #fff8c5;
      --warn-text: #7d4e00;
      --button-bg: #f6f8fa;
    }
    @media (prefers-color-scheme: dark) {
      :root {
        --bg: #0d1117;
        --card: #161b22;
        --border: #30363d;
        --text: #f0f6fc;
        --muted: #8b949e;
        --ok-bg: #12261e;
        --ok-text: #56d364;
        --warn-bg: #2e240d;
        --warn-text: #e3b341;
        --button-bg: #21262d;
      }
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font-size: 14px;
    }
    main {
      max-width: 880px;
      margin: 0 auto;
      padding: 24px;
    }
    h1 {
      margin: 0 0 8px;
      font-size: 20px;
      font-weight: 650;
    }
    .intro {
      margin: 0 0 20px;
      color: var(--muted);
      line-height: 1.5;
    }
    .settings-section {
      overflow: hidden;
      border: 1px solid var(--border);
      border-radius: 10px;
      background: var(--card);
    }
    .settings-row {
      display: grid;
      grid-template-columns: minmax(180px, 1fr) minmax(240px, 1.3fr);
      gap: 20px;
      align-items: center;
      padding: 18px;
    }
    .settings-row + .settings-row { border-top: 1px solid var(--border); }
    .settings-row--stacked { align-items: start; }
    .settings-row__title { font-weight: 600; }
    .settings-row__description {
      margin-top: 5px;
      color: var(--muted);
      line-height: 1.45;
    }
    .settings-row__control { justify-self: end; min-width: 0; }
    .status {
      display: inline-flex;
      align-items: center;
      gap: 7px;
      border-radius: 999px;
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
    .status--warn { color: var(--warn-text); background: var(--warn-bg); }
    .command {
      display: flex;
      min-width: 0;
      align-items: center;
      gap: 8px;
      border: 1px solid var(--border);
      border-radius: 7px;
      padding: 8px 9px;
      background: var(--bg);
    }
    code {
      min-width: 0;
      overflow-wrap: anywhere;
      font-family: ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
      font-size: 13px;
    }
    button {
      flex: none;
      border: 1px solid var(--border);
      border-radius: 6px;
      padding: 5px 9px;
      background: var(--button-bg);
      color: var(--text);
      cursor: pointer;
      font: inherit;
    }
    .copy-status {
      margin-top: 6px;
      color: var(--muted);
      font-size: 12px;
    }
    button:focus-visible { outline: 2px solid #58a6ff; outline-offset: 2px; }
    @media (max-width: 620px) {
      main { padding: 16px; }
      .settings-row { grid-template-columns: 1fr; gap: 12px; }
      .settings-row__control { justify-self: stretch; }
      .status { width: fit-content; }
    }
  </style>
</head>
<body>
  <main>
    <h1>Gateway Isolation</h1>
    <p class="intro">Diagnostic launch mode reported by the Windows launcher.</p>
    <section class="settings-section" aria-label="Gateway Isolation">
      <div class="settings-row">
        <div class="settings-row__title">Reported Gateway Isolation</div>
        <div class="settings-row__control">
          <span class="status status--${tone}">${status}</span>
        </div>
      </div>
      <div class="settings-row settings-row--stacked">
        <div>
          <div class="settings-row__title">Change with CLI</div>
          <div class="settings-row__description">Run from the signed-in user session on the Gateway host.</div>
        </div>
        <div class="settings-row__control command">
          <code id="isolation-command">${command}</code>
          <button id="copy-command" type="button" aria-label="Copy command">Copy</button>
        </div>
        <div id="copy-status" class="copy-status" role="status" aria-live="polite"></div>
      </div>
    </section>
  </main>
  <script>
    const button = document.getElementById("copy-command");
    const command = document.getElementById("isolation-command");
    const status = document.getElementById("copy-status");
    button.addEventListener("click", async () => {
      const value = command.textContent;
      let copied = false;
      try {
        await navigator.clipboard.writeText(value);
        copied = true;
      } catch {
        const selection = window.getSelection();
        const range = document.createRange();
        range.selectNodeContents(command);
        selection.removeAllRanges();
        selection.addRange(range);
        copied = document.execCommand("copy");
        if (copied) {
          selection.removeAllRanges();
        }
      }
      if (copied) {
        button.textContent = "Copied";
        status.textContent = "";
      } else {
        button.textContent = "Selected";
        status.textContent = "Copy the selected command manually.";
      }
    });
  </script>
</body>
</html>`;
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
    name: "Gateway Isolation",
    description: "Reports the Windows launch mode selected for the running Gateway.",
    register(api) {
      api.session.controls.registerControlUiDescriptor({
        surface: "tab",
        id: "gateway-isolation",
        label: "Gateway Isolation",
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
              "<!doctype html><title>Gateway Isolation unavailable</title><p>The Windows launcher did not provide a valid Gateway isolation mode.</p>",
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
