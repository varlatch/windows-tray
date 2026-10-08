#!/usr/bin/env node
// A stand-in for the varlatch CLI, for the tests and for trying the app
// without a server. It works in %FAKE_VARLATCH_DIR% (default: the home
// directory):
//
//   calls.log    every call, one line each
//   env.log      VARLATCH_NO_BROWSER as each browser sign-in saw it
//   status.json  what `status --json` prints (no sessions when missing)
//   probe.json   what `status --probe --json` prints (status.json when missing)
//   fake.json    variables read on every call, so a running app's fake can
//                change behavior without a restart: {"FAKE_LOGIN_RC": "1"}
//
// and these variables, from fake.json or the environment, choose how things
// end:
//
//   FAKE_VERSION      what --version reports (default 0.15.1)
//   FAKE_NO_STATUS    set: act like a CLI from before `status`
//   FAKE_LOGIN_SLEEP  seconds a browser sign-in waits (default 0)
//   FAKE_LOGIN_RC     its exit status (default 0)
//   FAKE_REVOKE_FAIL  set: a sign-in reports that the old credential stayed
//   FAKE_START_FAIL   set: `login --start` is refused
//   FAKE_WAIT         approve | pending-once | deny | expire | hang
//   FAKE_LOGOUT_FAIL  set: logout fails
"use strict";
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const dir = process.env.FAKE_VARLATCH_DIR || os.homedir();
const vars = { ...process.env };
try {
  Object.assign(vars, JSON.parse(fs.readFileSync(path.join(dir, "fake.json"), "utf8")));
} catch {
  // No fake.json: the environment alone.
}
const set = (name) => Boolean(vars[name]);
const args = process.argv.slice(2);
fs.appendFileSync(path.join(dir, "calls.log"), `cli ${args.join(" ")}\n`);

const out = (text) => process.stdout.write(text + "\n");
const err = (text) => process.stderr.write(text + "\n");
const sleep = (seconds) => new Promise((resolve) => setTimeout(resolve, seconds * 1000));
const read = (file) => {
  try {
    return fs.readFileSync(path.join(dir, file), "utf8");
  } catch {
    return null;
  }
};

const at = args.indexOf("--server");
const server = at >= 0 ? args[at + 1] || "" : "";

async function main() {
  switch (args[0]) {
    case "--version":
      out(`varlatch ${vars.FAKE_VERSION || "0.15.1"} (migration 26)`);
      return 0;
    case "--help":
      out("Usage: varlatch <command>");
      return 0;
    case "status": {
      if (set("FAKE_NO_STATUS")) {
        err("Usage: varlatch <command> [options]");
        return 64;
      }
      const probe = args[1] === "--probe" ? read("probe.json") : null;
      process.stdout.write(probe ?? read("status.json") ?? '{"version":1,"servers":[],"repo":null}\n');
      return 0;
    }
    case "logout":
      if (set("FAKE_LOGOUT_FAIL")) {
        err(`varlatch: No stored credential for ${server}`);
        return 1;
      }
      out(`${server}: credential revoked and removed.`);
      return 0;
    case "login":
      break;
    default:
      err(`varlatch: unknown command ${args[0] || ""}`);
      return 64;
  }

  const mode = args.includes("--start") ? "start" : args.includes("--wait") ? "wait" : "browser";
  if (mode === "start") {
    if (set("FAKE_START_FAIL")) {
      err("varlatch login: the server refused to start a sign-in (NOT_FOUND)");
      return 1;
    }
    const expires = new Date(Date.now() + 10 * 60 * 1000).toISOString();
    out(JSON.stringify({ verificationUri: `${server || "https://vl.example.com"}/device`, userCode: "BCDF-GHJK", expiresAt: expires }));
    return 0;
  }
  if (mode === "wait") {
    const n = Number(read("wait-count") || 0) + 1;
    fs.writeFileSync(path.join(dir, "wait-count"), String(n));
    switch (vars.FAKE_WAIT || "approve") {
      case "pending-once":
        if (n === 1) {
          await sleep(1);
          out('{"state":"pending"}');
          err("varlatch login: not approved yet.");
          return 75;
        }
        break;
      case "deny":
        out('{"state":"denied"}');
        err("varlatch login: the sign-in was denied in the browser. Nothing was stored.");
        return 77;
      case "expire":
        out('{"state":"expired"}');
        err("varlatch login: the sign-in expired before it was approved and collected (a code lasts 10 minutes). Nothing was stored.");
        return 77;
      case "hang":
        await sleep(600);
        return 75;
    }
    out('{"state":"signed-in","credentialId":"crd_test","expiresAt":"2099-01-01T12:00:00Z"}');
    return 0;
  }

  fs.appendFileSync(path.join(dir, "env.log"), `VARLATCH_NO_BROWSER=${process.env.VARLATCH_NO_BROWSER ?? ""}\n`);
  out("Complete passkey sign-in in your browser:");
  out(`  ${server || "https://vl.example.com"}/enroll?callback=http%3A%2F%2F127.0.0.1%3A49152%2F`);
  await sleep(Number(vars.FAKE_LOGIN_SLEEP || 0));
  const rc = Number(vars.FAKE_LOGIN_RC || 0);
  if (rc !== 0) {
    err("varlatch login: browser sign-in failed: no passkey");
    return rc;
  }
  out("Issued CLI credential (expires 2099-01-01T12:00:00Z).");
  out(`Logged in to ${server || "https://vl.example.com"} (server 0.15.1, API v1).`);
  if (set("FAKE_REVOKE_FAIL")) err("varlatch: previous credential not revoked (network error); it may remain live.");
  return 0;
}

main().then((code) => {
  process.exitCode = code;
});
