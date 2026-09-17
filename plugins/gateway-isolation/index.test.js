import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";
import vm from "node:vm";
import {
  createGatewayIsolationPlugin,
  readGatewayIsolationMode,
  renderGatewayIsolationPage,
} from "./index.js";

test("ships disabled by default while retaining explicit startup activation", () => {
  const manifest = JSON.parse(
    fs.readFileSync(new URL("./openclaw.plugin.json", import.meta.url), "utf8"),
  );
  assert.equal(manifest.enabledByDefault, false);
  assert.equal(manifest.enabledByDefaultOnPlatforms, undefined);
  assert.equal(manifest.activation.onStartup, true);
});

function registerPlugin(mode) {
  const descriptors = [];
  const routes = [];
  const plugin = createGatewayIsolationPlugin({
    CLAWCTL_GATEWAY_ISOLATION: mode,
  });
  plugin.register({
    session: {
      controls: {
        registerControlUiDescriptor(descriptor) {
          descriptors.push(descriptor);
        },
      },
    },
    registerHttpRoute(route) {
      routes.push(route);
    },
  });
  assert.equal(descriptors.length, 1);
  assert.equal(routes.length, 1);
  return { descriptors, routes };
}

function invokeRoute(route) {
  const result = {
    body: "",
    headers: {},
    statusCode: 0,
  };
  const handled = route.handler(
    {},
    {
      writeHead(statusCode, headers) {
        result.statusCode = statusCode;
        result.headers = headers;
      },
      end(body) {
        result.body = body;
      },
    },
  );
  assert.equal(handled, true);
  return result;
}

function runThemeBridge(html) {
  const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)];
  assert.equal(scripts.length, 1);
  const properties = new Map();
  const root = {
    dataset: {},
    style: {
      colorScheme: "",
      setProperty(name, value) {
        properties.set(name, value);
      },
    },
  };
  const parent = {};
  let listener;
  const context = {
    document: { documentElement: root },
    getComputedStyle() {
      return {
        getPropertyValue(name) {
          return properties.get(name) ?? "";
        },
      };
    },
    window: {
      parent,
      addEventListener(type, callback) {
        if (type === "message") listener = callback;
      },
    },
  };
  vm.runInNewContext(scripts[0][1], context);
  assert.equal(typeof listener, "function");
  return { listener, parent, properties, root };
}

const invalidModes = [
  undefined, null, "", "disabled", "invalid", "ENABLED", " enabled ",
  "1", true, 1, "<script>alert(1)</script>",
];

function assertInformationalPage(html) {
  assert.match(html, /<title>Windows Launcher<\/title>/);
  assert.match(html, /<h1>Windows Launcher<\/h1>/);
  assert.match(html, /<dl[^>]+aria-label="Windows Launcher status"/);
  assert.match(html, /<dt>Gateway<\/dt>\s*<dd><span class="status status--ok">Running<\/span><\/dd>/);
  assert.doesNotMatch(
    html,
    /<button|<input|<select|<form|<code|clipboard|execCommand|getSelection|createRange|aria-live|copy-status|Change with CLI|clawctl|--no-isolation|Disabled|Not running|--button-bg|--font-mono|--focus/i,
  );
}

function assertHeaders(response) {
  assert.deepEqual(response.headers, {
    "Cache-Control": "no-store",
    "Content-Security-Policy":
      "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-ancestors 'self'",
    "Content-Type": "text/html; charset=utf-8",
    "Referrer-Policy": "no-referrer",
    "X-Content-Type-Options": "nosniff",
  });
}

test("accepts only the exact enabled launcher report", () => {
  assert.equal(readGatewayIsolationMode({ CLAWCTL_GATEWAY_ISOLATION: "enabled" }), "enabled");
  for (const value of invalidModes) {
    assert.equal(readGatewayIsolationMode({ CLAWCTL_GATEWAY_ISOLATION: value }), null);
  }
});

test("does not use session-routing preference as isolation evidence", () => {
  for (const mode of invalidModes) {
    assert.equal(readGatewayIsolationMode({
      CLAWCTL_GATEWAY_ISOLATION: mode,
      OPENCLAW_SESSION: "1",
    }), null);
  }
  assert.equal(readGatewayIsolationMode({
    CLAWCTL_GATEWAY_ISOLATION: "enabled",
    get OPENCLAW_SESSION() {
      throw new Error("Routing preference must not be read.");
    },
  }), "enabled");
});

test("renders only informational running and active status", () => {
  const html = renderGatewayIsolationPage("enabled");
  assertInformationalPage(html);
  assert.match(html, /<dt>Isolation<\/dt>\s*<dd><span class="status status--ok">Active<\/span><\/dd>/);
  assert.match(html, /This Gateway is running in Windows isolation\./);
  assert.doesNotMatch(html, /Invalid|unavailable/);
});

test("applies recognized host theme tokens from the parent frame", () => {
  const bridge = runThemeBridge(renderGatewayIsolationPage("enabled"));
  bridge.listener({
    source: bridge.parent,
    data: {
      type: "openclaw:widget-theme",
      mode: "dark",
      tokens: {
        surface: "#101010",
        card: "#202020",
        elevated: "#303030",
        text: "#fefefe",
        muted: "#aaaaaa",
        border: "#404040",
        "border-strong": "#505050",
        accent: "#55aaff",
        ok: "#44cc77",
        warn: "#e0a020",
        radius: "14px",
        "radius-full": "9999px",
        "font-body": "Georgia, serif",
        "font-mono": "Consolas, monospace",
        "text-strong": "#ffffff",
      },
    },
  });

  assert.equal(bridge.root.dataset.themeMode, "dark");
  assert.equal(bridge.root.style.colorScheme, "dark");
  assert.equal(bridge.properties.get("--bg"), "#101010");
  assert.equal(bridge.properties.get("--card"), "#202020");
  assert.equal(bridge.properties.get("--text"), "#fefefe");
  assert.equal(bridge.properties.get("--text-strong"), "#ffffff");
  assert.equal(bridge.properties.get("--radius"), "14px");
  assert.equal(bridge.properties.get("--radius-full"), "9999px");
  assert.equal(bridge.properties.get("--font-body"), "Georgia, serif");
  assert.match(bridge.properties.get("--ok-bg"), /#44cc77 18%, #202020/);
  for (const unused of ["--button-bg", "--focus", "--font-mono", "--warn-text", "--warn-bg"]) {
    assert.equal(bridge.properties.has(unused), false);
  }

  bridge.listener({
    source: bridge.parent,
    data: {
      type: "openclaw:widget-theme",
      mode: "light",
      tokens: { surface: "#fafafa", card: "#ffffff", ok: "#116329" },
    },
  });
  assert.equal(bridge.root.dataset.themeMode, "light");
  assert.equal(bridge.root.style.colorScheme, "light");
  assert.equal(bridge.properties.get("--bg"), "#fafafa");
  assert.match(bridge.properties.get("--ok-bg"), /#116329 18%, #ffffff/);
});

test("ignores theme messages from other frames and malformed host values", () => {
  const bridge = runThemeBridge(renderGatewayIsolationPage("enabled"));
  for (const event of [
    {
      source: {},
      data: { type: "openclaw:widget-theme", mode: "light", tokens: { surface: "#fff" } },
    },
    {
      source: bridge.parent,
      data: { type: "other", mode: "light", tokens: { surface: "#fff" } },
    },
    {
      source: bridge.parent,
      data: { type: "openclaw:widget-theme", mode: "sepia", tokens: { surface: "#fff" } },
    },
    {
      source: bridge.parent,
      data: { type: "openclaw:widget-theme", mode: "light", tokens: null },
    },
    {
      source: bridge.parent,
      data: { type: "openclaw:widget-theme", mode: "light", tokens: [] },
    },
  ]) {
    bridge.listener(event);
  }
  assert.equal(bridge.root.style.colorScheme, "");
  assert.deepEqual([...bridge.properties], []);
  bridge.listener({
    source: bridge.parent,
    data: {
      type: "openclaw:widget-theme",
      mode: "light",
      tokens: {
        surface: "",
        card: " ",
        text: 123,
        muted: "x".repeat(257),
        radius: null,
        unknown: "red",
      },
    },
  });
  assert.deepEqual([...bridge.properties], []);
});

test("registers one read-only Control tab and one authenticated sandbox route", () => {
  const { descriptors, routes } = registerPlugin("enabled");
  assert.deepEqual(descriptors[0], {
    surface: "tab",
    id: "gateway-isolation",
    label: "Windows Launcher",
    description: "Read-only Windows Gateway isolation status.",
    icon: "shield-check",
    group: "control",
    order: 20,
    path: "/plugins/gateway-isolation/status",
    requiredScopes: ["operator.read"],
  });
  assert.equal(routes[0].path, descriptors[0].path);
  assert.equal(routes[0].auth, "gateway");
  assert.equal(routes[0].match, "exact");

  const response = invokeRoute(routes[0]);
  assert.equal(response.statusCode, 200);
  assertHeaders(response);
  assertInformationalPage(response.body);
  assert.match(response.body, />Active</);
});

for (const initial of ["enabled", ...invalidModes]) {
  test(`reads ${JSON.stringify(initial) ?? "missing"} exactly once and keeps every response stable`, () => {
    let reads = 0;
    let value = initial;
    const plugin = createGatewayIsolationPlugin({
      get CLAWCTL_GATEWAY_ISOLATION() {
        reads++;
        return value;
      },
    });
    assert.equal(reads, 1);
    value = initial === "enabled" ? "disabled" : "enabled";
    const routes = [];
    plugin.register({
      session: { controls: { registerControlUiDescriptor() {} } },
      registerHttpRoute(route) {
        routes.push(route);
      },
    });

    const first = invokeRoute(routes[0]);
    assert.equal(first.statusCode, initial === "enabled" ? 200 : 503);
    assert.match(first.body, initial === "enabled" ? />Active</ : />Invalid</);
    assertInformationalPage(first.body);
    assertHeaders(first);
    for (value of ["enabled", "disabled", "invalid", undefined]) {
      assert.deepEqual(invokeRoute(routes[0]), first);
    }
    assert.equal(reads, 1);
  });
}

for (const mode of invalidModes) {
  const label = JSON.stringify(mode) ?? "missing";
  test(`fails closed for ${label} with neutral invalid status and no mutation guidance`, () => {
    const { routes } = registerPlugin(mode);
    const response = invokeRoute(routes[0]);
    assert.equal(response.statusCode, 503);
    assertInformationalPage(response.body);
    assert.match(response.body, /Isolation status is unavailable\./);
    assert.match(response.body, /did not provide a valid isolation report/);
    assert.match(response.body, /<dt>Isolation<\/dt>\s*<dd><span class="status status--neutral">Invalid<\/span><\/dd>/);
    assert.doesNotMatch(
      response.body,
      />Active<|status--warn|<script>alert/,
    );
    const bridge = runThemeBridge(response.body);
    bridge.listener({
      source: bridge.parent,
      data: {
        type: "openclaw:widget-theme",
        mode: "dark",
        tokens: { surface: "#101010", text: "#fefefe", muted: "#aaaaaa" },
      },
    });
    assert.equal(bridge.root.style.colorScheme, "dark");
    assert.equal(bridge.properties.get("--bg"), "#101010");
    assert.equal(bridge.properties.get("--text"), "#fefefe");
    assert.equal(bridge.properties.get("--muted"), "#aaaaaa");
    assertHeaders(response);
  });
  test(`renderer rejects ${label}`, () => {
    assert.throws(() => renderGatewayIsolationPage(mode), TypeError);
  });
}
