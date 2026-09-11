import assert from "node:assert/strict";
import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { renderGatewayIsolationPage } from "../../plugins/gateway-isolation/index.js";

const [layout, outputArgument] = process.argv.slice(2);
assert.ok(layout && outputArgument, "Usage: node gateway-isolation-copy.mjs <layout> <output>");
const output = path.resolve(outputArgument);
const { chromium } = await import(pathToFileURL(
  path.resolve(layout, "app", "node_modules", "playwright-core", "index.mjs")).href);
const server = http.createServer((request, response) => {
  const mode = request.url === "/enabled" ? "enabled" : "disabled";
  response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
  response.end(renderGatewayIsolationPage(mode));
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
const base = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ channel: "msedge", headless: true });
const results = [];
try {
  for (const mode of ["enabled", "disabled"]) {
    for (const capability of ["clipboard-api", "legacy-fallback", "manual-selection"]) {
      const page = await browser.newPage();
      try {
        await page.addInitScript(capability => {
          window.copyCalls = [];
          Object.defineProperty(navigator, "clipboard", {
            configurable: true,
            value: { writeText: async value => {
              window.copyCalls.push({ method: "clipboard.writeText", value });
              if (capability !== "clipboard-api") throw new DOMException("Fixture denial", "NotAllowedError");
            } },
          });
          document.execCommand = command => {
            window.copyCalls.push({ method: "execCommand", command, selection: window.getSelection().toString() });
            return capability === "legacy-fallback";
          };
        }, capability);
        await page.goto(`${base}/${mode}`);
        await page.getByRole("button", { name: "Copy command" }).click();
        const manual = capability === "manual-selection";
        const command = `clawctl gateway-isolation ${mode === "enabled" ? "disable" : "enable"}`;
        await page.getByText(manual ? "Selected" : "Copied", { exact: true }).waitFor();
        const calls = await page.evaluate(() => window.copyCalls);
        assert.equal(calls[0].value, command);
        assert.equal(calls.length, capability === "clipboard-api" ? 1 : 2);
        if (calls.length === 2) {
          assert.equal(calls[1].command, "copy");
          assert.equal(calls[1].selection, command);
        }
        assert.equal(await page.locator("#copy-status").innerText(),
          manual ? "Copy the selected command manually." : "");
        assert.equal(await page.evaluate(() => window.getSelection().toString()), manual ? command : "");
        results.push({ mode, capabilityFixture: capability, command,
          label: manual ? "Selected" : "Copied", calls, passed: true });
        console.log(`PASS ${mode}/${capability}: ${manual ? "manual text remains selected" : "copy success"}`);
      } finally {
        await page.close();
      }
    }
  }
  fs.mkdirSync(output, { recursive: true });
  fs.writeFileSync(path.join(output, "copy-fixtures.json"), JSON.stringify({
    kind: "Browser capability fixtures against the actual rendered plugin page",
    completedAt: new Date().toISOString(), cases: results,
  }, null, 2) + "\n");
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
