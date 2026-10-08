# Changelog

## 0.1.0 (2026-10-08)

First release, installed with
`irm https://github.com/varlatch/windows-tray/releases/latest/download/install.ps1 | iex`.

- **Sessions in the notification area**, read offline from
  `varlatch status --json` every 30 seconds: the Varlatch mark in the
  taskbar's own monochrome, with an amber badge while a credential is
  expiring and a red one when signed out, expired, or the CLI cannot be
  used. The panel lists each server with a live countdown; a tooltip shows
  the same. A right click has the usual menu.
- One notification for each move into *expiring* or *expired*, with
  **Renew now** or **Log in**, and **Another device**.
- **Actions:** sign in, renew, and log out per server; **Verify**
  (`varlatch status --probe`) with the results on the rows; **Open
  dashboard**. A sign-in runs without a terminal window: the app opens the
  sign-in page in your browser, the panel shows it waiting, with **Open
  link** and **Cancel**, and the result arrives as a notification. Quitting
  the app cancels it, and so does the app ending in any other way.
- **Sign in from another device** (CLI 0.14.0 or newer), for when the
  browser here has no passkey: the panel shows an address, a code, and a QR
  code of the address.
- **Session length:** 1 to 24 hours, or the server's default of 12, for
  every sign-in the app starts.
- After a full logout, **Log in** targets the last server used.
- **First run:** without the CLI, **Install in Terminal** installs the
  release CLI at the version your server runs, checked against
  `SHA256SUMS` (and its signature, with cosign), with Node.js from winget
  when it is missing, and offers the first sign-in. With no server known,
  the panel asks for the server's address. **Add server** signs in to one
  more. While Windows keeps the icon in the overflow menu, the panel says
  how to keep it in view.
- **Finds the CLI** where its Windows install puts it, or on the `PATH` as
  the registry has it now, and runs the release CLI with Node.js directly,
  also behind a `varlatch.cmd` shim.
- **CLI updates** (opt-in): an anonymous check for new releases, at most
  once an hour, with **Release notes** and **Update in Terminal**
  (`varlatch self-update`, from an administrator PowerShell when needed),
  and one notification per release.
- **Opens at login** (an installed copy), and respects being turned off in
  Settings > Apps > Startup.
- Settings: session length, check interval, expiry notifications, showing
  the icon while logged out, localhost servers, the CLI path, and release
  checks.
- Installs per user, without administrator rights, from a self-contained
  archive for x64 or Arm64, checked against `SHA256SUMS`; uninstalls from
  Settings > Apps.
