# HackPDM MCP Server

The **HackPDM MCP Server** exposes the full HackPDM Product Data Management client — both its complete Odoo/PDM/SolidWorks/local-vault/filesystem API surface and live WinUI/Uno desktop UI inspection & control — over the **Model Context Protocol (MCP)** (JSON-RPC 2.0, supporting protocol versions `2025-06-18`, `2025-03-26`, and `2024-11-05`).

## Architecture

The implementation spans three parts:

1. **`Src/HackPDM.Mcp.Core`** (`net10.0` library):
   - Zero-extra-dependency MCP JSON-RPC 2.0 server (`McpServer`), newline-delimited **stdio** transport (`McpStdioTransport`), loopback **Streamable HTTP** transport (`McpHttpServer`), bridge discovery (`McpBridgeDiscovery`), and proxy client (`McpRemoteProxyClient`).
   - Full HackPDM API facade (`HackPdmApiService`) covering sessions, profiles, lookups, directory trees, entries, versions, previews, dependency graphs, PDM workflows, search, SolidWorks inspection/editing, local vault files, computer-wide file operations & PWA import, generic Odoo ORM/RPC, settings, AppConfig, and diagnostics.
   - **125 tools**, **13 static resources**, **3 resource templates**, and **6 workflow prompts**.
   - **Permissions, Execution Modes, Presets, Profiles & Live Review Engine** (`McpPermissionManager` / `McpToolCatalog`): 5 execution modes (`auto`, `review`, `review-critical`, `read-only`, `local-only`), 6 built-in presets, persistent custom profiles (`<AppDataPaths.Config>/mcp-permissions.json`), 11 granular capability categories, live human-in-the-loop review queue, and anti-self-escalation guards.
   - Persistent **MCP Activity Log** (`<AppDataPaths.Data>/mcp/activity.jsonl`, with automatic secret redaction) and **Restore Point / Undo Snapshot Manager** (`<AppDataPaths.Data>/mcp/restore-points/`).
   - Cross-platform **SolidWorks file writer** (`SwFileWriter` / `SwStreamWriter` / `OlePropertySet`) supporting custom property editing and dependency path rewriting in both SolidWorks 2015+ (Modern chunked) and SolidWorks ≤2014 (OLE MS-CFB) `.SLDPRT`, `.SLDASM`, and `.SLDDRW` files on Windows, Linux, and macOS without COM.
2. **Embedded Live GUI Bridge & Settings Dashboard inside `Src/HackPDM`** (`net10.0-windows10.0.26100.0` / `net10.0-desktop`):
   - Starts a loopback-only MCP Streamable HTTP server (`127.0.0.1:<ephemeral-port>/mcp`) with minimal/zero setup (auto-started on launch by default, or toggled via the 1-click **Start MCP Server / Stop MCP Server** button; disabled automatically during `HACKPDM_SMOKE_TEST=1` unless `HACKPDM_MCP_ENABLED=1` is set).
   - Writes connection metadata (PID, loopback URL, random per-process Bearer token) to `<AppDataPaths.Data>/mcp-bridge.json` (with `0600` POSIX permissions on Linux/macOS) and cleans it up on exit.
   - Provides a built-in **MCP Server, Permissions & Restore Points** GUI panel (accessible directly via the **MCP Server** item in the left sidebar or **Settings → MCP Server**) with:
     - **1-click Start/Stop & Connection Details**: Live Streamable HTTP URL + **Copy URL**, masked/revealable Bearer Auth Token + **Copy Token**, ready-to-paste **Copy Stdio JSON** (auto-attach) and **Copy HTTP JSON** (live URL + Bearer token), and `mcp-bridge.json` discovery path.
     - **Live Pending AI Action Reviews Queue**: Displays every AI tool awaiting human approval with risk badge (`CRITICAL` / `ACTION`), reason, and redacted arguments — with **Approve Once**, **Approve Session**, and **Deny** buttons (plus optional popup prompt when you are on another page).
     - **AI Permissions, Execution Mode, Presets & Profiles**: Pick any of the 5 execution modes, apply any of the 6 built-in presets, save/delete custom named profiles, and toggle all 11 granular capability categories.
     - **Restore Points & Rollback + Live MCP Activity Log**: Toggle automatic pre-mutation restore points, create manual checkpoints, roll back changes in one click, and inspect/filter/clear the live activity log.
   - Implements `IHackPdmUiBridge` (`LiveGuiClientBridge` + `VisualTreeInspector`) for live XAML visual tree inspection, control querying/clicking/editing, MVVM command invocation, page & drawer navigation, File Manager / Dependency Graph / Search / Status / Theme / Dialog automation, live PNG screenshots, and real-time UI state synchronization.
3. **`Src/HackPDM.McpServer`** (`net10.0` standalone console executable):
   - Runs as a standard **stdio** MCP server (default) or **Streamable HTTP** server (`--http`).
   - In default **`auto`** bridge mode, automatically discovers and proxies tool/resource calls to a running HackPDM desktop GUI when available, or executes all non-UI API tools headlessly in-process when no GUI is running.
   - Supports full CLI permission/preset/profile overrides and management commands (`--show-permissions`, `--list-presets`, `--save-profile`, `--set-active-profile`, `--delete-profile`).

```
AI Client (Claude / Cursor / VS Code / Antigravity / etc.)
   │
   ├─ stdio (default) ──► HackPDM.McpServer (--mode auto|live|headless, --permission-mode / --preset / --permission-profile)
   │                          │
   │                          ├── if GUI running (mcp-bridge.json) ──► HTTP + Bearer ──► HackPDM GUI (McpBridgeHost)
   │                          │                                                            ├─ McpPermissionManager + Live Review Queue
   │                          │                                                            ├─ LiveGuiClientBridge (UI + Visual Tree + Screenshots)
   │                          │                                                            └─ HackPdmApiService ──► Odoo + Local PWA + Disk/SW
   │                          └── else (headless fallback) ──► McpPermissionManager ──────► HackPdmApiService ──► Odoo + Local PWA + Disk/SW
   │
   └─ Streamable HTTP ──► http://127.0.0.1:<port>/mcp  (direct to GUI bridge OR `HackPDM.McpServer --http`)
```

---

## Building

```bash
# Build the standalone MCP server
dotnet build Src/HackPDM.McpServer/HackPDM.McpServer.csproj

# Build the desktop app (Linux/macOS Uno Desktop) with embedded live GUI bridge
dotnet build Src/HackPDM/HackPDM.csproj -f net10.0-desktop

# Build on Windows (WinUI 3)
dotnet build Src/HackPDM/HackPDM.csproj -f net10.0-windows10.0.26100.0
```

---

## Quick-Start From the HackPDM GUI

1. Open HackPDM and click **MCP Server** in the left navigation bar (or **Settings → MCP Server**).
2. Click **Start MCP Server** (if not already auto-started on launch). No manual port, token, or file setup is required — HackPDM automatically binds a loopback port on `127.0.0.1`, generates a 256-bit Bearer token, and publishes `mcp-bridge.json`.
3. Copy your preferred connection details in one click:
   - **Copy Stdio JSON**: Recommended for Claude Desktop, Cursor, VS Code, Windsurf, and Antigravity — automatically attaches to your running HackPDM GUI (and falls back to headless when HackPDM is closed).
   - **Copy HTTP JSON** (or **Copy URL** + **Copy Token**): Connects directly to the live Streamable HTTP endpoint (`http://127.0.0.1:<port>/mcp`) with the `Authorization: Bearer <token>` header pre-populated.
4. Choose your comfort level in the **AI Permissions, Execution Mode, Presets & Profiles** card (defaults safely to **`review-critical`** on first run).

---

## GUI Command Palette, Direct AI Chat & One-Click AI Sign-In (`Ctrl+K`)

Press **`Ctrl+K`** (or **`Ctrl+Shift+P`**, or click the **`>_` Command Palette** icon in the sidebar / **Command Palette / AI Login (Ctrl+K)** in MCP Settings) from anywhere inside HackPDM to open the spotlight Command Palette:

- **`Commands` tab** (prefix `>`): Fuzzy-search and run curated HackPDM API, Vault, SolidWorks, Theme, MCP Safety, Restore Point, and Navigation commands directly (with inline parameter prompts, live JSON output, and 1-click **Open in Window** to pop results into a Dynamic Supplemental Window).
- **`MCP Tools (115)` tab** (prefix `/`): Search all 115 registered MCP tools, edit auto-generated JSON argument skeletons inline, invoke any tool directly against the live embedded MCP server, and inspect results.
- **`Ask AI` tab** (prefix `?` or `@`): Communicate directly with your signed-in AI inside HackPDM. Runs a real multi-turn agentic tool-calling loop directly against HackPDM's live MCP server (respecting active permission modes, logging every tool call, and allowing the AI to guide you, edit themes, or open Dynamic Supplemental Windows).
- **`Connect AI` tab** *(Zero-Pain Login & Auto-Connect)*:
  - **1-Click Browser OAuth Login (OpenRouter PKCE)**: Click **Browser Login (OAuth)** to sign in via Google, GitHub, or email in your browser — HackPDM receives and saves your key automatically over a loopback callback with zero manual copy-pasting, unlocking Claude, GPT-4o, Gemini, and DeepSeek immediately.
  - **1-Click Auto-Detect**: Automatically detects running local AI servers (**Ollama** on `127.0.0.1:11434`, **LM Studio** on `127.0.0.1:1234`) and environment variables (`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, `OPENROUTER_API_KEY`, `GITHUB_TOKEN`).
  - **Direct Native Provider Support (8 Providers)**: OpenRouter, OpenAI, Anthropic Claude (native `/v1/messages`), Google Gemini (native `generateContent`), GitHub Models, Ollama, LM Studio, and any Custom OpenAI-Compatible endpoint (stored owner-only `0600` in `<AppDataPaths.Config>/mcp-ai-providers.json`).
  - **One-Click External AI Client Auto-Connect**: Automatically detects and registers HackPDM into **Claude Desktop**, **Claude Code CLI**, **Cursor**, **VS Code (Copilot MCP)**, **Windsurf**, and **Gemini CLI / Antigravity** (backs up existing JSON to `.hackpdm.bak` and merges/removes only `"hackpdm"`).

---

## Permissions, Execution Modes, Presets & Profiles

Permissions can be configured through **any** of three synchronized surfaces:
- **GUI**: Left sidebar → **MCP Server** → **AI Permissions, Execution Mode, Presets & Profiles** card.
- **Config File**: `<AppDataPaths.Config>/mcp-permissions.json` (shared automatically by the GUI and standalone CLI).
- **CLI**: Flags and environment variables on `HackPDM.McpServer` (either ephemeral runtime overrides for one session or persistent `--save-profile` / `--set-active-profile` commands).

### Execution Modes (5)

| Mode | Wire Name | Behavior |
|---|---|---|
| **Auto** | `auto` | Executes all permitted actions immediately with **no interactive review**. Keep Automatic Restore Points enabled for quick undo. |
| **Review Critical** *(default)* | `review-critical` | Routine/reversible procedures (get latest, checkout, local staging, UI navigation, creating checkpoints) run automatically; **critical operations** (commit/push, logical/permanent delete, raw Odoo writes/RPCs, SolidWorks binary property/dependency edits, external moves/deletes/PWA imports, settings edits, restore rollbacks, UI button clicks/commands) pause for your approval. |
| **Review All Actions** | `review` | Read-only inspection runs freely; **every** mutating action, edit, download, or UI interaction pauses for your explicit approval. |
| **Read-Only** | `read-only` | **Get/read operations only** (both local and remote Odoo/PDM reads); strictly blocks all pushing, writing, editing, deleting, and mutating UI control. |
| **Local-Only** | `local-only` | **Computer-local operations only** (local PWA files, external filesystem, SolidWorks inspection/editing, local config, restore points, local GUI); strictly blocks **all remote Odoo/PDM server RPC operations** (both remote reads and server pushes/deletes). |

> **Argument-aware refinements**: Safe dry-run and local-only invocations are classified accurately:
> - `sw_set_dependencies(dryRun: true)` and `mcp_restore_point_restore(dryRun: true)` are treated as `ReadOnly`.
> - `directory_get_tree(source: "local")` and `odoo_model_schema(fetchServerFields: false)` are treated as local (allowed in `local-only` mode), whereas `directory_get_tree(source: "merged"|"remote")`, `odoo_model_schema(fetchServerFields: true)`, `ui_search_control(execute: true)`, and `ui_dependency_graph_control` calls that fetch/expand from Odoo (`loadEntryId`, `entryIds`, `expandNodeId`, `folderPath`, `directoryId`, `viewMode`) are classified as `IsRemoteRpc: true` and blocked in `local-only` mode.

### Built-In Presets (6)

| Preset Key | Name | Mode | Summary |
|---|---|---|---|
| `read-only` | Read-Only Observer (Get Only) | `read-only` | Vault/Odoo reads + live UI inspection only; zero writes or pushes. |
| `local-only` | Local-Only (No Server RPC) | `local-only` | Local PWA files, computer-wide filesystem, SolidWorks editing, restore points, local GUI; zero Odoo RPCs. |
| `review` | Supervised Assistant (Review All) | `review` | All categories enabled, every mutating action pauses for human approval. |
| `review-critical` | CAD Engineer (Review Critical) | `review-critical` | Default safe preset: routine procedures automatic, critical ops require approval. |
| `local-cad-prep` | Local CAD Prep (No Server Push) | `review-critical` | Permits Odoo reads + local workspace prep + reviewed SolidWorks edits, while strictly disabling `PdmCommitAndPush`, `PdmDeleteAndAdmin`, and `SettingsAndSession`. |
| `auto` | Autonomous (Full Auto) | `auto` | All categories enabled, zero review prompts. |

### Granular Capability Categories (11)

Every one of the 104 MCP tools maps to one of 11 toggleable capability categories:
1. `ReadVaultAndOdoo` — Read directory trees, entries, versions, previews, dependency graphs, search, SolidWorks inspection, settings, logs, and Odoo ORM reads.
2. `PdmWorkingActions` — Get latest, checkout, uncheckout, restore logically deleted entries, download versions.
3. `PdmCommitAndPush` — Commit/check-in to Odoo, upload previews, create remote vault directories.
4. `PdmDeleteAndAdmin` — Logical/permanent deletes and raw `odoo_create`, `odoo_write`, `odoo_unlink`, `odoo_execute` RPCs.
5. `LocalVaultFileEdit` — Write or delete files inside the local PWA vault folder.
6. `ExternalFilesystemAccess` — Browse, mkdir, copy, move, delete, or import files anywhere on disk outside the PWA.
7. `SolidWorksEditing` — Modify SolidWorks custom properties (`sw_set_properties`) and rewrite internal dependency paths (`sw_set_dependencies`).
8. `LiveUiRead` — Live UI state, windows, visual tree, control reads, and PNG screenshots.
9. `LiveUiControl` — Navigate pages, select folders/entries, adjust filters/themes, edit inputs, click controls, invoke commands, respond to dialogs.
10. `SettingsAndSession` — Login/logout, configure Odoo/PWA, load/save `.hp` profiles, edit `settings.json` / `AppConfig.json`, attach/launch GUI.
11. `McpAdminAndRestore` — Create/restore/delete restore points, clear MCP activity log, configure MCP backup settings.

### Configuration File (`mcp-permissions.json`)

Stored at `<AppDataPaths.Config>/mcp-permissions.json` (or overridden via `--permissions-file` / `HACKPDM_MCP_PERMISSIONS_FILE`):

```json
{
  "schemaVersion": 1,
  "activeProfile": "shop-floor-cad",
  "promptDialogWhenHidden": true,
  "customProfiles": {
    "shop-floor-cad": {
      "name": "Shop Floor CAD",
      "key": "shop-floor-cad",
      "description": "Local PWA + SolidWorks editing only; no Odoo RPCs and no external disk writes.",
      "isBuiltInPreset": false,
      "mode": "local-only",
      "reviewTimeoutSeconds": 120,
      "categories": {
        "ReadVaultAndOdoo": true,
        "PdmWorkingActions": false,
        "PdmCommitAndPush": false,
        "PdmDeleteAndAdmin": false,
        "LocalVaultFileEdit": true,
        "ExternalFilesystemAccess": false,
        "SolidWorksEditing": true,
        "LiveUiRead": true,
        "LiveUiControl": true,
        "SettingsAndSession": false,
        "McpAdminAndRestore": true
      },
      "allowedTools": [],
      "deniedTools": [],
      "requireReviewTools": ["local_delete", "sw_set_dependencies"]
    }
  }
}
```

### Live Human-in-the-Loop Review & Anti-Self-Escalation

- When a tool call requires review in `review` or `review-critical` mode:
  - **Live GUI attached**: The request pauses cleanly and appears at the top of the **MCP Server** GUI page under **Pending AI Action Reviews** with **Approve Once**, **Approve Session**, and **Deny** buttons. If you are on a different HackPDM page (and `Popup when on other page` is checked), a confirmation dialog appears so you never miss a request.
  - **Headless mode (no GUI reviewer)**: Returns a structured `review_required` error explaining which tool needs approval and how to approve it via the HackPDM GUI or CLI flags (`--permission-mode auto` or `--allow-tool <name>`).
- **Anti-self-escalation**:
  - MCP AI clients can inspect their own permissions via `mcp_permissions_get`, `mcp_permissions_list_presets`, and `hackpdm://mcp/permissions`, but **no MCP tool can modify permissions or approve reviews**.
  - Generic live UI control tools (`ui_click_control`, `ui_set_control_value`, `ui_invoke_command`) are hard-blocked from targeting any permission, preset, profile, bridge toggle, or Approve/Deny review control — even in `auto` mode.
  - `ui_dialog_respond` is hard-blocked from answering MessageBox dialogs whenever an MCP action review is pending.

---

## Configuring MCP Clients

### Stdio Mode (Recommended — works with both Live GUI & Headless)

Add `HackPDM.McpServer` to your MCP client configuration (e.g., Claude Desktop `claude_desktop_config.json`, Cursor `.cursor/mcp.json`, VS Code `.vscode/mcp.json`, or Antigravity MCP config — or click **Copy Stdio JSON** in HackPDM's MCP Server panel):

```json
{
  "mcpServers": {
    "hackpdm": {
      "command": "dotnet",
      "args": [
        "/path/to/HackPDM/Src/HackPDM.McpServer/bin/Release/net10.0/HackPDM.McpServer.dll",
        "--mode",
        "auto"
      ]
    }
  }
}
```

Add optional runtime permission flags per client (e.g., `"--permission-mode", "read-only"` or `"--preset", "local-only"`).

### Direct Streamable HTTP Mode

Click **Copy HTTP JSON** in HackPDM's **MCP Server** panel while the server is running to copy a ready-to-paste config with your live `http://127.0.0.1:<port>/mcp` URL and `Authorization: Bearer <token>` header, or launch the standalone HTTP server:

```bash
dotnet run --project Src/HackPDM.McpServer -- --http --port 38420 --preset review-critical
```

---

## CLI Options & Environment Variables

### `HackPDM.McpServer` CLI Flags

| Flag | Environment Variable | Default | Description |
|---|---|---|---|
| `--stdio` | — | Enabled | Run newline-delimited JSON-RPC over `stdin`/`stdout` (diagnostics on `stderr`). |
| `--http` | — | Disabled | Run Streamable HTTP server on `127.0.0.1`. |
| `--port <n>` | `HACKPDM_MCP_PORT` | `38420` | TCP port for `--http` (`0` = ephemeral OS-assigned port). |
| `--token <t>` | `HACKPDM_MCP_TOKEN` | Random 256-bit hex | Bearer token for `--http`. |
| `--no-auth` | — | Disabled | Disable Bearer check on loopback `--http` (local dev only). |
| `--mode <m>` | `HACKPDM_MCP_MODE` | `auto` | Bridge attachment mode: `auto` (attach to live GUI if running, else headless), `live`, or `headless`. |
| `--client-url <url>` | `HACKPDM_MCP_URL` | Auto-discover | Explicit running HackPDM GUI bridge URL. |
| `--client-token <t>` | `HACKPDM_MCP_CLIENT_TOKEN` | Auto-discover | Bearer token for `--client-url`. |
| `--data-dir <dir>` | `HACKPDM_DATA_DIR` | OS AppData | Override directory containing `mcp-bridge.json`. |
| `--permission-mode <m>` | `HACKPDM_MCP_PERMISSION_MODE` | Active config (`review-critical`) | Runtime mode override: `auto`, `review`, `review-critical`, `read-only`, or `local-only`. |
| `--preset <name>` / `--permission-profile <name>` | `HACKPDM_MCP_PRESET` / `HACKPDM_MCP_PERMISSION_PROFILE` | Active config | Runtime preset (`read-only`, `local-only`, `review`, `review-critical`, `local-cad-prep`, `auto`) or custom profile name/key. |
| `--permissions-file <path>` | `HACKPDM_MCP_PERMISSIONS_FILE` | `<Config>/mcp-permissions.json` | Override path to `mcp-permissions.json`. |
| `--allow-category <cat[,...]>` | — | — | Enable one or more capability categories for this session (repeatable). |
| `--deny-category <cat[,...]>` | — | — | Disable one or more capability categories for this session (repeatable). |
| `--allow-tool <tool[,...]>` | — | — | Explicitly allow specific tool(s) without review (repeatable; still blocked in `read-only` / `local-only` hard barriers). |
| `--deny-tool <tool[,...]>` | — | — | Explicitly deny specific tool(s) (repeatable). |
| `--review-tool <tool[,...]>` | — | — | Always require human review for specific tool(s) (repeatable). |
| `--review-timeout <sec>` | `HACKPDM_MCP_REVIEW_TIMEOUT` | `120` | Interactive review timeout in seconds (`5..3600`). |
| `--show-permissions` | — | — | Print active effective permissions snapshot as JSON and exit. |
| `--list-presets` | — | — | Print all 5 modes, 6 built-in presets, saved custom profiles, and 11 categories as JSON and exit. |
| `--save-profile <name>` | — | — | Save the effective flags/preset as a persistent custom profile in `mcp-permissions.json` (and activate it), then exit. |
| `--profile-description <text>` | — | — | Optional description paired with `--save-profile`. |
| `--set-active-profile <name>` | — | — | Persist a built-in preset or custom profile as the active profile in `mcp-permissions.json` and exit. |
| `--delete-profile <name>` | — | — | Delete a custom profile from `mcp-permissions.json` and exit. |
| `--reset-theme-defaults` | — | — | Reset `AppConfig.json` Theme back to HackPDM factory defaults (captures automatic restore point) and exit. |
| `--reset-ui-defaults` | — | — | Reset ALL `AppConfig.json` UI & Theme sections to HackPDM factory defaults (captures automatic restore point) and exit. |
| `--profile <path>` | `HACKPDM_PROFILE` | — | Load a HackPDM `.hp` connection profile JSON file on startup (headless). |
| `--auto-login` | `HACKPDM_AUTO_LOGIN=1` | Disabled | Automatically authenticate with Odoo on startup (headless). |

#### CLI Permission & Profile Examples

```bash
# Inspect all modes, built-in presets, custom profiles, and categories
dotnet HackPDM.McpServer.dll --list-presets

# Start a strictly read-only MCP session (no pushes, writes, or edits)
dotnet HackPDM.McpServer.dll --mode auto --permission-mode read-only

# Start a computer-local-only session (zero Odoo/server RPCs)
dotnet HackPDM.McpServer.dll --mode auto --preset local-only

# Create & save a persistent custom profile shared with the HackPDM GUI
dotnet HackPDM.McpServer.dll --preset local-only --deny-category ExternalFilesystemAccess \
  --review-tool sw_set_dependencies --save-profile "Shop Floor CAD"

# Switch active profile persisted in mcp-permissions.json
dotnet HackPDM.McpServer.dll --set-active-profile review-critical
```

### Embedded Live GUI Bridge (`HackPDM` Desktop App)

| Environment Variable / Setting | Default | Description |
|---|---|---|
| `HACKPDM_MCP_ENABLED` / `settings.json: McpBridgeEnabled` | `true` (disabled during `HACKPDM_SMOKE_TEST=1`) | Enable/disable the embedded GUI MCP HTTP bridge. |
| `HACKPDM_MCP_PORT` / `settings.json: McpBridgePort` | `0` (ephemeral) | Pin the embedded bridge to a specific loopback port. |
| `HACKPDM_MCP_TOKEN` | Random 256-bit hex | Override the per-process Bearer token. |
| `HACKPDM_MCP_PERMISSIONS_FILE` | `<AppDataPaths.Config>/mcp-permissions.json` | Override the permissions & custom profiles JSON path. |
| `HACKPDM_MCP_PERMISSION_MODE` / `HACKPDM_MCP_PRESET` | Active config (`review-critical`) | Optional runtime permission override for scripted GUI launches. |
| `settings.json: McpAutoRestorePoints` | `true` | Automatically capture an undoable restore point before mutating file, SolidWorks, or config operations. |
| `settings.json: McpMaxRestorePoints` | `50` | Maximum number of automatic restore points retained before pruning oldest (5..500). |

---

## Security Model

- **Loopback-only binding**: `McpHttpServer` binds strictly to `127.0.0.1` (`IPAddress.Loopback`), never exposing the bridge to the network.
- **Per-process Bearer authentication**: The embedded GUI bridge generates a 256-bit cryptographically random token and compares `Authorization: Bearer ...` using `CryptographicOperations.FixedTimeEquals`.
- **DNS rebinding / browser CSRF defense**: Requests carrying a non-loopback `Origin` header are rejected with `403 Forbidden`.
- **Restricted discovery file**: `mcp-bridge.json` is written atomically and restricted to owner-only (`0600`) permissions on POSIX systems; stale dead-PID discovery files are automatically pruned.
- **Granular permissions, execution modes & interactive review**: Every tool call passes through `McpPermissionManager` before execution; blocked and review-denied calls are recorded in the persistent activity log.
- **Anti-self-escalation**: AI tools cannot alter MCP permissions, toggle the MCP bridge, click Approve/Deny buttons, or answer pending review dialogs.
- **Vault-bounded vs computer-wide file tools**: `local_*` tools stay strictly inside `HackDefaults.Instance.PwaPathAbsolute`; `fs_*` tools operate across the computer (for inspecting and importing external files into the PWA vault) and automatically capture pre-mutation restore points when enabled.
- **Destructive action guards**: `pdm_permanent_delete`, `local_delete`, `fs_delete`, and `odoo_unlink` require an explicit `confirm: true` parameter.
- **Automatic Restore Points & Audit Log**: Mutating file, SolidWorks CAD, and config tools snapshot original files/state before modifying disk, link the `restorePointId` into the persistent JSONL activity log, and take a safety backup before restoring so rollbacks can themselves be undone.
- **Secret redaction**: Passwords, tokens, and keys are redacted from `session_get_info`, `profile_get_current`, `settings_list`, `mcp_log_get` / `activity.jsonl`, pending review argument summaries, and `VisualTreeInspector` (`PasswordBox` values are never included in visual tree dumps).

---

## Complete UI & Theme Customization, Soft/Auto Restart & Factory Defaults

The AI (and user) can independently read, customize, persist in `AppConfig.json`, live-preview, and reset virtually every aspect of HackPDM's interface:

- **Independent Theme Editing (`ui_theme_control` / `hackpdm://ui/theme`)**:
  - Switch `mode` (`Dark` / `Light` / `System`), `palette`, `form`, and `showTreeBranchLines`.
  - Override any single or batch Fluent/system `SolidColorBrush` key (`colors: { "<Key>": "#AARRGGBB" }`) targeting `"active"`, `"dark"`, `"light"`, or `"both"`.
  - Override independent **style & typography** properties (`Theme.Style`): `compactDensity`, `controlCornerRadius`, `overlayCornerRadius`, `panelCornerRadius`, `panelBorderThickness`, `accentPanels`, `panelStrokeColor`, `fontFamily`, `baseFontSize`, `contentPadding`.
  - Override independent **Dependency Graph** properties (`Theme.Graph`): all 13 graph palette colors, all 6 `NodeKind` header colors (`Assembly`, `Part`, `Drawing`, `Pdf`, `Other`, `Deleted`), `headerStyle` (`Band` / `Strip` / `Gradient`), `linkStyle` (`Curve` / `Elbow` / `GlowCurve`), corner radius, border thicknesses, font size, port style/size, shadow depth, grid visibility, and all 7 layout spacing/sizing options (applied **live** with zero restart).
  - Save, load, and delete reusable **Named Custom Themes** (`Theme.CustomThemes`) in `AppConfig.json` — also browsable directly inside **Settings → Theme**.
- **Full `AppConfig.json` UI Configuration (`ui_customize_config`)**:
  - Independently read, edit, save, and reset `OdooEntryColors` (all 11 entry status colors), `ColumnSettings` (`rowHeight`, `fontSize`, grouping, per-column header/width/order/autoSize), `WorkspaceLayout` (all 17 pane docking/sizing/drawer settings or presets), and `WindowSettings` (per-window titles, sizes, positions, presenter kinds — applied live to open windows).
  - Works in **both** Live GUI mode and Headless mode (persisting directly to `AppConfig.json` with automatic MCP restore points).
- **Soft Reload vs Seamless Auto-Restart (`ui_app_restart` or `ui_theme_control(..., autoApplyRestart)`)**:
  - **`soft`**: Rebuilds the root UI in-process so updated corners, borders, compact/roomy density, fonts, columns, entry colors, and layout take full effect immediately — **zero process exit, zero MCP disconnect, zero Odoo logout**, returning to the exact active page.
  - **`full`**: Saves `AppConfig.json` (with automatic restore point), preserves the MCP loopback port, Bearer token, and active page, launches a clean replacement HackPDM process, and automatically re-attaches standalone `HackPDM.McpServer` proxy clients so the AI stays connected seamlessly across Dark ↔ Light base theme switches.
  - **`auto`** *(default)*: Uses `soft` unless a base Dark ↔ Light switch requires `full`.
- **Always-Available Return to Factory Defaults**:
  - **AI MCP**: `ui_theme_control(resetToDefaults: true, save: true)` or `ui_customize_config(resetSection: "all", save: true)` (plus any MCP restore point rollback via `mcp_restore_point_restore`).
  - **User GUI**: **Settings → Theme → Restore Factory Defaults** (plus **Soft Reload UI** / **Restart HackPDM** buttons), or **MCP Server → Reset UI Defaults** (automatically captures a pre-reset restore point first).
  - **Emergency CLI**: `dotnet HackPDM.McpServer.dll --reset-theme-defaults` or `--reset-ui-defaults`.

---

## Complete Tool Reference (110 Tools)

### Client & Bridge Lifecycle (4)
- `client_status` — Runtime mode (`live_gui` / `proxied_to_live_gui` / `headless`), discovered bridge, session info, and live UI summary.
- `client_attach` — Attach to a running HackPDM desktop client (auto-discovered or explicit URL/token).
- `client_detach` — Detach from a live GUI and switch back to headless mode.
- `client_launch` — Launch the HackPDM desktop client with MCP enabled, wait for readiness, and attach.

### Session, Auth & Profiles (10)
- `session_get_info`, `session_configure`, `session_test_connection`, `session_login`, `session_logout`, `session_check_permissions`, `session_reload_lookups`
- `profile_get_current`, `profile_load`, `profile_save`

### Metadata, Directory Tree, Entries, Versions & Dependency Graph (16)
- `metadata_get_lookups`
- `directory_get_tree`, `directory_get_entries`, `directory_get_entry_ids`, `directory_find_path`, `directory_create`, `path_convert`
- `entry_get_details`, `entry_get_history`, `entry_get_relationships`, `entry_get_properties`
- `version_get_info`, `version_get_preview` *(returns inline MCP PNG image)*, `version_upload_preview`, `version_read_contents`
- `dependency_graph_load` — Multi-mode node graph engine (`Dependencies`, `FolderStructure`, `FoldersAndFiles`, `ByProperty`, `SearchFilter`) supporting `Children`, `Parents`, `Both`, and recursive bidirectional **`FullNetwork`** (showing each parent's and child's parents & children), independent depth loading (`maxDepth`, `maxChildDepth`, `maxParentDepth`), independent per-node branch caps (`maxChildBranches`, `maxParentBranches`, `maxNodes`), file-type/extension filters (`extensions`/`fileTypes` e.g. `["sldprt"]`, `["sldasm", "slddrw"]`, `["cad"]`), wildcard name/path/checkout/size/custom property filters, transitive edge bridging (`bridgeFilteredEdges`), root retention (`keepRoots`), optional dependency link overlays (`includeDependencyLinks`), and computed `GraphLayout` coordinates.

### PDM Workflows (10)
- `pdm_get_latest`, `pdm_checkout`, `pdm_uncheckout`, `pdm_commit_preview`, `pdm_commit`
- `pdm_logical_delete`, `pdm_restore_deleted`, `pdm_permanent_delete` *(requires `confirm: true`)*
- `pdm_plan_version_download`, `pdm_download_version`

### Search, Permission-Aware Odoo ORM / JSON-RPC & Direct PostgreSQL (24)
- **Vault Search**: `search_entries`, `search_get_options`
- **Odoo JSON-RPC ORM & Permission-Verified Model Methods (`object.execute_kw` / `object.execute`)**:
  - `odoo_list_models`, `odoo_model_schema` *(works for both mapped HackPDM models and any live server model via `fields_get`)*
  - `odoo_check_model_access` — Verifies the logged-in user's live CRUD permissions (`read`, `write`, `create`, `unlink`) via `check_access_rights` across specific models or discovers accessible models via `ir.model`.
  - `odoo_discover_model_methods` — Lists permitted vs denied JSON-RPC ORM & custom model methods, bound `ir.actions.server` actions, and live `fields_get` metadata for any model.
  - `odoo_read_group` — SQL-like `GROUP BY` / `SUM` / `COUNT` / `AVG` / `MIN` / `MAX` ORM aggregation via `object.execute_kw`.
  - `odoo_call_model_method` — Executes any public Odoo ORM or model API method via `object.execute_kw` (with `ids`, positional `args`, keyword `kwargs`, and `context`) AFTER verifying `check_access_rights` on the model; read-only ORM methods work in `read-only` mode while mutating methods require approval.
  - `odoo_search` *(supports `offset`, `limit`, `order`, `context`)*, `odoo_search_count`, `odoo_read`, `odoo_search_read` *(supports `offset`, `limit`, `order`, `context`)*, `odoo_smart_read`, `odoo_related_browse`
  - `odoo_create`, `odoo_write`, `odoo_unlink` *(requires `confirm: true`)*, `odoo_execute` *(arbitrary model method via `object.execute` / `object.execute_kw` or raw JSON-RPC service call)*
- **Direct Odoo PostgreSQL Database Queries (Npgsql)**:
  - `postgres_status` — Inspects redacted PostgreSQL settings (inheriting Host/Database defaults from HackPDM Odoo settings or `PG*` env vars) and optionally tests live connectivity.
  - `postgres_configure` — Configures Host, Port, Database, Username, Password (stored owner-only `0600` in `mcp-postgres.json`), SSLMode, `allowWriteQueries` (default `false`), timeout, and max rows.
  - `postgres_list_tables`, `postgres_describe_table` — Lists tables with estimated row counts/sizes and inspects columns, foreign keys, and indexes (accepting either `hp_entry` or `hp.entry`).
  - `postgres_query` — Executes parameterized strictly read-only SQL (`SELECT`, `WITH ... SELECT`, `EXPLAIN`, `SHOW`) inside an engine-enforced `BEGIN TRANSACTION READ ONLY` with `SET LOCAL statement_timeout`, blocking multi-statement and write-CTE queries.
  - `postgres_execute` — Executes parameterized modifying/DDL SQL inside an explicit transaction only when `allowWriteQueries=true` AND `confirm=true` (or safely with `dryRun=true` automatic rollback).

### SolidWorks Inspection & Editing, Computer-Wide Files, Local Vault, Settings & Diagnostics (22)
- **SolidWorks CAD (Cross-Platform 2015+ Modern & ≤2014 OLE)**:
  - `sw_inspect_file`, `sw_scan_dependencies`
  - `sw_set_properties` — Add, update, or remove file-level and configuration-level custom properties (`text`, `number`, `yesno`, `date`) inside `.SLDPRT`, `.SLDASM`, `.SLDDRW` files with auto restore point.
  - `sw_set_dependencies` — Rewrite stored dependency paths inside `.SLDPRT`, `.SLDASM`, `.SLDDRW` files via explicit `replacements`, `oldPrefix`/`newPrefix`, or `remapToDirectory` (supports `dryRun: true` and auto restore point).
- **Computer-Wide File Manipulation & External-to-PWA Import**:
  - `fs_list`, `fs_stat`, `fs_mkdir`, `fs_copy`, `fs_move`, `fs_delete` *(requires `confirm: true`, auto restore point)*
  - `fs_import_to_pwa` — Collects external files (plus transitive SolidWorks dependencies), copies or moves them into a target PWA vault folder, automatically rewrites internal SolidWorks dependency paths to point inside the PWA, and captures one atomic restore point.
- **Local Vault (PWA-bounded)**: `local_list_files`, `local_read_file`, `local_write_file`, `local_delete` *(requires `confirm: true`)*, `local_checksum`
- **Settings & Diagnostics**: `settings_list`, `settings_get`, `settings_set`, `app_config_get`, `app_config_save`, `diagnostics_get`

### MCP Activity Log, Restore Points & Permissions Introspection (10)
- `mcp_log_get`, `mcp_log_clear`
- `mcp_restore_point_create`, `mcp_restore_point_list`, `mcp_restore_point_inspect`, `mcp_restore_point_restore` *(supports `dryRun` + automatic pre-restore safety backup)*, `mcp_restore_point_delete`
- `mcp_settings_configure`
- `mcp_permissions_get` *(read-only: active mode, profile, categories, pending reviews)*
- `mcp_permissions_list_presets` *(read-only: all 5 modes, 6 built-in presets, saved custom profiles)*

### Live Desktop GUI Inspection, Control, UI Customization, Guided Actions & Dynamic Windows (29)
- **Window & Visual Tree**: `ui_get_state`, `ui_list_windows`, `ui_visual_tree`, `ui_find_controls`, `ui_get_control`
- **Generic Control Interaction**: `ui_click_control`, `ui_set_control_value`, `ui_invoke_command`
- **Navigation & Screen Automation**: `ui_navigate` *(including `settingsSection: "Mcp"`)*, `ui_file_manager_select_folder`, `ui_file_manager_select_entries`, `ui_file_manager_control`, `ui_dependency_graph_control` *(live control over viewMode, FullNetwork/Children/Parents/Both, maxDepth/maxChildDepth/maxParentDepth, maxChildBranches/maxParentBranches, fileTypes/extensions, name/path/property filters, bridgeFilteredEdges, expandNodeId, resetFilters, zoom/pan/fit & node inspection)*, `ui_search_control`, `ui_status_panel`, `ui_dialog_respond`
- **Dynamic Supplemental AI Windows**:
  - `ui_window_create` — Creates (or replaces) a native HackPDM supplemental window declaratively composed by the AI from 21 structured component types (`section`/`card`, `stack`, `grid`/`columns`, `tabs`, `text`, `markdown`, `kpi_row`, `key_value`, `table` with built-in live search & status pills, `tree`, `list`, `badge_row`, `callout`, `progress`, `code`/`json` with Copy button, `image` from base64/filePath, `divider`, `input`, `checkbox`, `select`, `button_row`). Automatically follows HackPDM's live theme, includes built-in **Copy Summary** (Markdown digest) & **Close**, and registers in `ui_list_windows` / `ui_screenshot`.
  - `ui_window_update` — Live in-place updates without closing/flickering: update title/subtitle/badge/size, replace or append components, or surgically `patches` elements by `id` (KPI tiles, progress bars, callouts, table rows, active tab, text, inputs, visibility).
  - `ui_window_list` — Lists open dynamic supplemental windows, patchable IDs, live user input values, and pending event counts.
  - `ui_window_events` — Reads button clicks, selectable table row clicks, window close events, and current user input values (with optional `waitForEvent: true` to pause until the user interacts).
  - `ui_window_close` — Closes one specific dynamic window or all open dynamic windows (`closeAll: true`).
- **Guided Actions, Attention Animations, Popups & Multi-Step Tours**:
  - `ui_guide_highlight` — Temporary animated highlight (`pulse`, `spotlight`, `bounce`, `shake`, `flash`, `outline`) + optional floating badge & callout card anchored to any control (supports auto-navigation, auto-expiry `durationMs`, and `dismissOnTargetClick`).
  - `ui_guide_popup` — Floating or control-anchored guided action popup card with custom buttons and optional `waitForUser: true` returning the exact button clicked by the user.
  - `ui_guide_tour` — Interactive multi-step guided walkthrough (`start` / `next` / `previous` / `goto` / `status` / `stop`) with per-step auto-navigation, spotlighting, and Back / Next / Finish buttons.
  - `ui_guide_clear` — List (`listOnly: true`) or clear one/all active temporary guided overlays cleanly.
- **Complete Theme, UI Config & Soft/Auto Restart**:
  - `ui_theme_control` *(works live & headless: palette, form, mode, batch/per-key colors, independent style/font/density/corner/border overrides, independent graph overrides, custom theme save/load/delete, factory reset, optional `autoApplyRestart`)*
  - `ui_customize_config` *(works live & headless: `entryColors`, `columns`, `workspaceLayout`, `windowSettings`, per-section or full factory reset, optional `softReload`)*
  - `ui_app_restart` *(soft in-process UI reload or full process auto-restart with preserved connection/navigation and automatic proxy re-attach)*
- **Visual Verification**: `ui_screenshot` *(captures live PNG of any window or named control via `RenderTargetBitmap` and returns an inline MCP image content block)*

---

## Resources & Prompts

### Static Resources (13)
- `hackpdm://status`
- `hackpdm://metadata`
- `hackpdm://models`
- `hackpdm://tree`
- `hackpdm://config/settings`
- `hackpdm://config/app`
- `hackpdm://logs/diagnostics`
- `hackpdm://mcp/log`
- `hackpdm://mcp/restore-points`
- `hackpdm://mcp/permissions`
- `hackpdm://ui/theme`
- `hackpdm://ui/state` *(live GUI)*
- `hackpdm://ui/visual-tree` *(live GUI)*

### Resource Templates (3)
- `hackpdm://directory/{directoryId}/entries`
- `hackpdm://entry/{entryId}/details`
- `hackpdm://model/{modelName}/schema`

### Guided Workflow Prompts (6)
- `explore-vault` — Inspect connection, directory tree, and folder entries.
- `commit-changes` — Safely preview and check in modified/new local files and SolidWorks dependencies.
- `diagnose-hackpdm` — Run a full health check across session, connection, settings, logs, and live UI.
- `automate-ui` — Step-by-step live HackPDM desktop UI inspection and control workflow.
- `guide-user` — Visually guide the user inside HackPDM with temporary animations, spotlights, interactive popups, and multi-step tours.
- `present-report` — Gather HackPDM data and format it into a rich interactive supplemental dynamic window via `ui_window_create` / `ui_window_update`.
