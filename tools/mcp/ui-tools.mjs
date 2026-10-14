// BRIDGEUI1: MCP tools for focus-free UI acceptance tests (docs/bridge-ops-ui.md, "UI input without focus").
// Each tool wraps one client bridge op: ui_input, ui_read, nav, display, screenshot. The registration is separate from
// server.mjs so the schemas and the argument mapping can be tested without a game (test/ui-tools.test.mjs).

const TARGET_HELP =
  "A target is row (a cell row key from mods_ui widget_read, e.g. demo-table:row0) with optional cell (e.g. e:reserve), " +
  "or panel (a shown panel's title), or x and y (screen pixels, origin bottom-left). fx / fy (0..1, default 0.5) pick the " +
  "point inside the target's rect, dx / dy add pixels.";

function targetSchema(z) {
  return {
    row: z.string().optional().describe("Cell row key (mods_ui widget_read / ui_read widget)."),
    cell: z.string().optional().describe("A cell key of that row (e.g. e:reserve, x0, grip, c:total)."),
    panel: z.string().optional().describe("A shown panel's title (ui_read panels)."),
    x: z.number().optional().describe("Screen x in pixels, origin left."),
    y: z.number().optional().describe("Screen y in pixels, origin bottom."),
    fx: z.number().min(0).max(1).optional().describe("0..1 across the target from its left. Default 0.5."),
    fy: z.number().min(0).max(1).optional().describe("0..1 up the target from its bottom. Default 0.5."),
    dx: z.number().optional().describe("Pixels added to x."),
    dy: z.number().optional().describe("Pixels added to y."),
  };
}

/** Drop undefined values so the bridge sees only what the caller gave. */
export function compact(obj) {
  const out = {};
  for (const [k, v] of Object.entries(obj ?? {})) if (v !== undefined && v !== null) out[k] = v;
  return out;
}

/** The bridge timeout for one ui_input call: frames run at the game's rate, so long drags and typing need more. */
export function uiInputTimeout(args) {
  const frames = (args.settleFrames ?? 3) + (args.holdFrames ?? 3) + (args.steps ?? 12) + 2 * Math.abs(args.notches ?? 0);
  const typing = (args.text?.length ?? 0) * 4;
  return Math.min(120000, Math.max(25000, (frames + typing) * 60 + 15000));
}

export function registerUiTools(server, { z, bridge, text, fail }) {
  const target = targetSchema(z);

  server.tool(
    "progenitor_ui_input",
    "Client only. Drive the Progenitor UI kit without OS input and without the game window's focus: hover (tooltips, " +
      "hover states, term markers), click with any button and Shift/Ctrl (chip x, buttons, column headers for multi-key " +
      "sort, range select), drag (ordered-list grips, scroll bars), the mouse wheel (through the kit's wheel claims and " +
      "the hovered panel, so precedence is tested), UI-layer keys (Escape, Enter, Tab, editing keys: the framework's " +
      "Escape and Enter handling and the focused field, never a keybind; use keybind_press for binds), typing into the " +
      "focused kit field, and giving a field (a cell-row value box, a search box) the keyboard. The synthetic pointer " +
      "replaces the mouse for the kit's own reads and sends pointer events through the game's EventSystem raycast. A " +
      "lease (leaseMs, default 30 s) is renewed by every call; it ends on action=release, release:true, timeout, the " +
      "player touching the real mouse or keyboard, a Core swap or Dev unload. The game itself (its pause menu, camera) " +
      "does not see these events. " + TARGET_HELP,
    {
      action: z.enum(["move", "hover", "click", "drag", "up", "wheel", "key", "type", "focus", "status", "release"]).describe(
        "move/hover: pointer to the target. click: press and release there. drag: press at the target, move to `to`, release " +
          "(keepDown: stay pressed there; up lets go later, so a capture can show the held state). " +
          "wheel: notches over the target. key: one UI-layer key tap. type: text into the focused kit field. focus: give the " +
          "kit field at the target the keyboard. status: lease, hovered chain, recent events. release: end the lease."),
      ...target,
      to: z.object(target).optional().describe("drag only: where to release (same fields as the target)."),
      button: z.enum(["left", "right", "middle"]).optional().describe("click/drag. Default left."),
      modifiers: z.array(z.enum(["shift", "ctrl"])).optional().describe("click/drag/move: held for the action (the kit's click modifiers)."),
      settleFrames: z.number().int().min(0).max(120).optional().describe("Frames the pointer rests on the target before a press. Default 3."),
      holdFrames: z.number().int().min(1).max(600).optional().describe("click: frames the button stays down. Default 3."),
      steps: z.number().int().min(1).max(600).optional().describe("drag: frames (one move each). Default 12."),
      keepDown: z.boolean().optional().describe("drag: leave the button held at `to`; finish with action=up."),
      notches: z.number().int().min(-20).max(20).optional().describe("wheel: notches, positive up (toward the start of a list)."),
      key: z.string().optional().describe("key: Escape, Enter, Tab, Backspace, Delete, Left, Right, Up, Down, Home, End, Space, or a letter."),
      frames: z.number().int().min(1).max(120).optional().describe("key: frames the key is held. Default 2."),
      text: z.string().max(2000).optional().describe("type: the text."),
      leaseMs: z.number().int().min(500).max(300000).optional().describe("Lease length from this call. Default 30000."),
      release: z.boolean().optional().describe("End the lease when this action finishes (the pointer leaves: hovers end)."),
    },
    async (args) => {
      try {
        const a = compact(args);
        if (a.action === "hover") a.action = "move";
        if (a.to) a.to = compact(a.to);
        return text(await bridge("client", "ui_input", a, uiInputTimeout(a)));
      } catch (e) { return fail(e); }
    });

  server.tool(
    "progenitor_ui_read",
    "Client only, read only (no queue slot needed). UI acceptance readback: state (the synthetic pointer's hovered chain, " +
      "top hit, press, drag, selected object and recent pointer events), focus (the kit field with the keyboard, its text, " +
      "and the controller focus), tooltip (the kit tooltip's title, body and rect), widgets (every live widget's key and " +
      "type), widget (key: its own readback: a data table's sort keys, visible slots, pending edits, hidden columns; chips " +
      "and their lines; a master-detail's tops), panels (every shown panel's title, capture mode and rect), tips (the tip " +
      "card, its rect, its anchor panel's rect and the offset between them, on-screen), terms (plugin: explained words, " +
      "marker visibility and hover counts), target (the point ui_input would act on).",
    {
      what: z.enum(["state", "focus", "tooltip", "widgets", "widget", "panels", "tips", "terms", "target"]).describe("What to read."),
      key: z.string().optional().describe("widget: the widget key (from what=widgets)."),
      plugin: z.string().optional().describe("terms: the plugin GUID."),
      ...target,
    },
    async (args) => { try { return text(await bridge("client", "ui_read", compact(args))); } catch (e) { return fail(e); } });

  server.tool(
    "progenitor_nav",
    "Client only. NAV1 controller navigation. state: where the focus is. press: one step through the model. hold: buttons " +
      "held through the framework's own per-frame navigation sample for ms (held directions repeat), no device. stick: the " +
      "right stick's vertical axis for ms (scrolls the focused list). focus: put the focus on a visible cell row or one of " +
      "its cells (into a content pane, or a row past the first screen). pad / pad_remove: a virtual gamepad through the " +
      "Input System (background:true when the window is unfocused).",
    {
      action: z.enum(["state", "press", "hold", "stick", "focus", "pad", "pad_remove"]).describe("What to do."),
      button: z.string().optional().describe("press/pad: up, down, left, right, a, b, x, lt, rt. hold: one or several joined by +."),
      ms: z.number().int().min(1).max(5000).optional().describe("hold/stick/pad: how long. Default 120."),
      y: z.number().min(-1).max(1).optional().describe("stick: -1..1, positive up."),
      row: z.string().optional().describe("focus: the cell row key."),
      cell: z.string().optional().describe("focus: a cell key of that row (empty: the row)."),
      background: z.boolean().optional().describe("pad only."),
    },
    async (args) => { try { return text(await bridge("client", "nav", compact(args))); } catch (e) { return fail(e); } });

  server.tool(
    "progenitor_display",
    "Client only. The game window's resolution and window mode through the game's own graphics path " +
      "(GraphicsSettingsManager.SetResolution). status reads the window, the monitor and the game's saved graphics fields; " +
      "set changes them (this can change the user's saved graphics settings); restore returns to the values before the " +
      "first set of this Dev generation. Always restore when done. The monitor bounds what a window can show: for a 16:9 " +
      "layout larger than the monitor, set a smaller 16:9 window and use progenitor_screenshot superSize.",
    {
      action: z.enum(["status", "set", "restore"]).describe("What to do."),
      width: z.number().int().min(640).max(16384).optional().describe("set: width in pixels."),
      height: z.number().int().min(360).max(16384).optional().describe("set: height in pixels."),
      mode: z.enum(["windowed", "fullscreenwindow", "exclusive", "maximized"]).optional().describe("set: window mode. Default windowed."),
    },
    async (args) => { try { return text(await bridge("client", "display", compact(args), 30000)); } catch (e) { return fail(e); } });

  server.tool(
    "progenitor_screenshot",
    "Client only, read only. A focus-free capture made by the game itself (ScreenCapture.CaptureScreenshot): the frame it " +
      "renders, UI included, at the window's resolution or superSize times it. Optional region: rect {x, y, w, h} (screen " +
      "pixels, origin bottom-left), or row (and cell) or panel, with pad; the crop is written beside the file as " +
      "<name>-region.png. A minimized window renders nothing. Returns the path and the image size.",
    {
      path: z.string().optional().describe("A .png path on the game machine. Default BepInEx/progenitor-shots/<utc time>.png."),
      superSize: z.number().int().min(1).max(4).optional().describe("Render at this multiple of the window size. Default 1."),
      rect: z.object({ x: z.number(), y: z.number(), w: z.number(), h: z.number() }).optional().describe("Region in screen pixels."),
      row: z.string().optional().describe("Region: a cell row key."),
      cell: z.string().optional().describe("Region: a cell of that row."),
      panel: z.string().optional().describe("Region: a shown panel's title."),
      pad: z.number().min(0).max(2000).optional().describe("Pixels around the region."),
    },
    async (args) => { try { return text(await bridge("client", "screenshot", compact(args), 30000)); } catch (e) { return fail(e); } });
}
