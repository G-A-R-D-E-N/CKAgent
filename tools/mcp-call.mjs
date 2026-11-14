// Constraint: the retry-and-reveal lives above the plugin pipe, so a probe must drive this over the protocol.
import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

// Constraint: the arguments must arrive in a file, because PowerShell drops JSON quotes for native commands.
const [, , dll, tool, argsFile = "", game = "", timeoutMs = "20000", extra = ""] = process.argv;
if (!dll || !tool) {
  console.error("usage: node mcp-call.mjs <ckagent.dll|ckagent.exe> <tool> [args.json] [game] [timeout-ms] [extra-args]");
  process.exit(2);
}

let toolArguments = {};
if (argsFile) {
  try {
    const text = readFileSync(argsFile, "utf8").replace(/^\uFEFF/, "");
    toolArguments = text.trim() ? JSON.parse(text) : {};
  } catch (error) {
    console.log(JSON.stringify({ probeError: `the args file could not be read: ${error.message}` }));
    process.exit(2);
  }
}

// Invariant: an .exe is the apphost itself, and its integrity level is what a low-client probe is measuring.
const hostArgs = [
  ...(game ? ["--game", game] : []),
  ...(extra ? extra.split(" ").filter(Boolean) : []),
  "--state",
  join(tmpdir(), "ckagent-probe"),
];

const child = dll.toLowerCase().endsWith(".exe")
  ? spawn(dll, hostArgs, { stdio: ["pipe", "pipe", "inherit"] })
  : spawn("dotnet", [dll, ...hostArgs], { stdio: ["pipe", "pipe", "inherit"] });

let buffer = "";
const pending = new Map();

const send = (message) =>
  new Promise((resolve) => {
    // Invariant: a notification carries no id, so waiting for its reply would hang the probe.
    if (message.id === undefined) {
      child.stdin.write(JSON.stringify({ jsonrpc: "2.0", ...message }) + "\n");
      resolve(null);
      return;
    }
    pending.set(message.id, resolve);
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", ...message }) + "\n");
  });

child.stdout.on("data", (chunk) => {
  buffer += chunk.toString("utf8");
  let newline;
  while ((newline = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (!line) continue;
    let message;
    try {
      message = JSON.parse(line);
    } catch {
      continue;
    }
    const resolve = pending.get(message.id);
    if (resolve) {
      pending.delete(message.id);
      resolve(message);
    }
  }
});

const bail = (code, text) => {
  console.log(JSON.stringify({ probeError: text }));
  child.kill();
  process.exit(code);
};

const timer = setTimeout(() => bail(3, `no reply within ${timeoutMs} ms`), Number(timeoutMs));

await send({
  id: 1,
  method: "initialize",
  params: { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "ckagent-probe", version: "1.0.0" } },
});
await send({ method: "notifications/initialized" });

const reply = await send({ id: 2, method: "tools/call", params: { name: tool, arguments: toolArguments } });
clearTimeout(timer);

// Constraint: the tool result must come from content[0].text; a protocol error is reported as-is.
if (reply.error) bail(1, JSON.stringify(reply.error));

const text = reply.result?.content?.[0]?.text ?? "";
console.log(text || JSON.stringify(reply.result ?? {}));
child.kill();
process.exit(0);
