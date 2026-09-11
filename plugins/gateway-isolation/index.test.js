import assert from "node:assert/strict";
import test from "node:test";
import {
  createGatewayIsolationPlugin,
  readGatewayIsolationMode,
  renderGatewayIsolationPage,
} from "./index.js";

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

test("accepts only the exact launcher isolation values", () => {
  assert.equal(readGatewayIsolationMode({ CLAWCTL_GATEWAY_ISOLATION: "enabled" }), "enabled");
  assert.equal(readGatewayIsolationMode({ CLAWCTL_GATEWAY_ISOLATION: "disabled" }), "disabled");
  for (const value of [undefined, "", "ENABLED", "unknown"]) {
    assert.equal(readGatewayIsolationMode({ CLAWCTL_GATEWAY_ISOLATION: value }), null);
  }
});

for (const expected of [
  {
    mode: "enabled",
    status: "Enabled",
    command: "clawctl gateway-isolation disable",
    tone: "status--ok",
  },
  {
    mode: "disabled",
    status: "Disabled",
    command: "clawctl gateway-isolation enable",
    tone: "status--warn",
  },
]) {
  test(`renders the exact ${expected.mode} read-only status`, () => {
    const html = renderGatewayIsolationPage(expected.mode);
    assert.match(html, /Gateway Isolation/);
    assert.match(html, /Reported Gateway Isolation/);
    assert.match(html, new RegExp(`>${expected.status}<`));
    assert.match(html, /Change with CLI/);
    assert.match(html, /Run from the signed-in user session on the Gateway host\./);
    assert.match(html, new RegExp(expected.command));
    assert.match(html, new RegExp(expected.tone));
    assert.match(html, /aria-label="Copy command"/);
    assert.match(html, /Copy the selected command manually\./);
    assert.match(html, /copied = document\.execCommand\("copy"\)/);
    assert.doesNotMatch(html, /next manual Gateway restart/i);
  });
}

test("registers one read-only Control tab and one authenticated sandbox route", () => {
  const { descriptors, routes } = registerPlugin("enabled");
  assert.deepEqual(descriptors[0], {
    surface: "tab",
    id: "gateway-isolation",
    label: "Gateway Isolation",
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
  assert.equal(response.headers["Cache-Control"], "no-store");
  assert.match(response.headers["Content-Security-Policy"], /frame-ancestors 'self'/);
  assert.match(response.body, />Enabled</);
});

for (const initial of ["enabled", "disabled", undefined, "", "invalid", "ENABLED", " enabled "]) {
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
    assert.equal(first.statusCode, initial === "enabled" || initial === "disabled" ? 200 : 503);
    if (initial === "enabled") assert.match(first.body, />Enabled</);
    if (initial === "disabled") assert.match(first.body, />Disabled</);
    for (value of ["enabled", "disabled", "invalid", undefined]) {
      assert.deepEqual(invokeRoute(routes[0]), first);
    }
    assert.equal(reads, 1);
  });
}

for (const mode of [undefined, "", "invalid", "ENABLED", " enabled "]) {
  const label = JSON.stringify(mode) ?? "missing";
  test(`fails closed for ${label} with no status, command, or copy control`, () => {
    const { routes } = registerPlugin(mode);
    const response = invokeRoute(routes[0]);
    assert.equal(response.statusCode, 503);
    assert.match(response.body, /did not provide a valid Gateway isolation mode/);
    assert.doesNotMatch(response.body, /status--(?:ok|warn)|isolation-command|<button/);
    assert.equal(response.headers["Cache-Control"], "no-store");
    assert.equal(response.headers["X-Content-Type-Options"], "nosniff");
    assert.equal(response.headers["Referrer-Policy"], "no-referrer");
  });
  test(`renderer rejects ${label}`, () => {
    assert.throws(() => renderGatewayIsolationPage(mode), TypeError);
  });
}
