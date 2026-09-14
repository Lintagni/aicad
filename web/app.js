"use strict";

// The token is injected into the page by the server. It goes in a header, not
// a URL, so it never lands in history, and the custom header makes the browser
// refuse cross-site calls to the local server.
var TOKEN = window.AICAD_TOKEN;

function $(id) { return document.getElementById(id); }

var el = {
  status: $("status"),
  statusLabel: $("status").querySelector(".label"),
  alertConnection: $("alertConnection"),
  alertEngine: $("alertEngine"),
  engineMessage: $("engineMessage"),
  fixEngine: $("fixEngine"),

  sidebar: $("sidebar"),
  history: $("history"),
  historyEmpty: $("historyEmpty"),
  statEngine: $("statEngine"),
  statDrawing: $("statDrawing"),
  statRefs: $("statRefs"),

  scroll: $("scroll"),
  idle: $("idle"),
  examples: $("examples"),
  messages: $("messages"),

  busy: $("busy"),
  echo: $("echo"),
  steps: $("steps"),

  result: $("result"),
  resultTitle: $("resultTitle"),
  resultSummary: $("resultSummary"),
  resultFacts: $("resultFacts"),
  resultParams: $("resultParams"),
  resultObjects: $("resultObjects"),
  paramPane: $("paramPane"),
  objectPane: $("objectPane"),
  undo: $("undo"),
  regenerate: $("regenerate"),

  input: $("input"),
  send: $("send"),
  fields: $("fields"),
  toggleFields: $("toggleFields"),
  mode2d: $("mode2d"),
  mode3d: $("mode3d"),
  updatePrevious: $("updatePrevious"),
  pickPoint: $("pickPoint"),

  logpane: $("logpane"),
  log: $("log"),
  logEmpty: $("logEmpty"),
  logCount: $("logCount"),
  follow: $("follow"),
  grip: $("grip"),

  toggleConnection: $("toggleConnection")
};

// ------------------------------------------------------------------- api

function api(path, body) {
  return fetch(path, {
    method: body ? "POST" : "GET",
    headers: { "Content-Type": "application/json", "X-AiCad-Token": TOKEN },
    body: body ? JSON.stringify(body) : undefined
  }).then(function (r) {
    if (!r.ok) throw new Error("HTTP " + r.status);
    return r.json();
  });
}

function action(name, extra) {
  var body = { action: name };
  if (extra) for (var k in extra) if (extra.hasOwnProperty(k)) body[k] = extra[k];
  return api("/api/action", body).then(function (r) {
    // Failures used to go only to the console, which made a refused install
    // look like a successful one.
    if (r && r.ok === false && r.error) alert(r.error);
    invalidate();
    return refresh().then(function () { return r; });
  });
}

// ------------------------------------------------------------ local state

// Things the server has no opinion about: which pane filter is showing, how
// wide the log is, what the user typed but has not sent.
var ui = {
  mode: "2d",
  showFields: false,
  logLevel: "all",
  follow: true,
  logWidth: 400,
  values: {}          // field key -> typed value, per mode
};

function loadUi() {
  try {
    var saved = JSON.parse(localStorage.getItem("aicad.ui") || "{}");
    for (var k in saved) if (saved.hasOwnProperty(k) && ui.hasOwnProperty(k)) ui[k] = saved[k];
  } catch (e) { /* first run, or storage blocked */ }
}

function saveUi() {
  try { localStorage.setItem("aicad.ui", JSON.stringify(ui)); } catch (e) { }
}

var busy = false;
var lastRender = "";

function invalidate() { lastRender = ""; }

// --------------------------------------------------------------- fields

// The parameters the composer offers. They are blank by default: a value shown
// here is stated to the model as fixed, so a pre-filled box would silently
// constrain every drawing. The placeholder shows the shape of an answer.
var FIELD_SETS = {
  "2d": [
    { key: "standard", label: "STANDARD", placeholder: "IEC 60617" },
    { key: "scale", label: "SCALE", placeholder: "1:1" },
    { key: "layer", label: "LAYER", placeholder: "E-SLD" },
    { key: "text", label: "TEXT HEIGHT", placeholder: "2.5 mm" }
  ],
  "3d": [
    { key: "units", label: "UNITS", placeholder: "mm" },
    { key: "size", label: "OVERALL SIZE", placeholder: "800 x 600 x 250" },
    { key: "layer", label: "LAYER", placeholder: "M-MODEL" },
    { key: "material", label: "MATERIAL", placeholder: "1.5 mm steel" }
  ]
};

function fieldKey(key) { return ui.mode + ":" + key; }

function currentFields() {
  var set = FIELD_SETS[ui.mode] || [];
  var out = [];
  for (var i = 0; i < set.length; i++) {
    var value = (ui.values[fieldKey(set[i].key)] || "").trim();
    if (!value) continue;
    out.push({ key: set[i].key, label: set[i].label, value: value });
  }
  return out;
}

function renderFields() {
  el.fields.innerHTML = "";
  var set = FIELD_SETS[ui.mode] || [];

  for (var i = 0; i < set.length; i++) {
    (function (spec) {
      var label = document.createElement("label");

      var name = document.createElement("span");
      name.className = "name";
      name.textContent = spec.label;
      label.appendChild(name);

      var input = document.createElement("input");
      input.type = "text";
      input.placeholder = spec.placeholder;
      input.value = ui.values[fieldKey(spec.key)] || "";
      input.addEventListener("input", function () {
        ui.values[fieldKey(spec.key)] = input.value;
        saveUi();
      });
      label.appendChild(input);

      el.fields.appendChild(label);
    })(set[i]);
  }

  el.fields.hidden = !ui.showFields;
  el.toggleFields.textContent = ui.showFields ? "Hide parameters" : "Add parameters";
}

// -------------------------------------------------------------- examples

var EXAMPLES = [
  {
    mode: "2d", tag: "2D · SLD",
    text: "Single line diagram, 250 kVA source, 400 A main MCCB, four 100 A outgoing ways, IEC symbols"
  },
  {
    mode: "2d", tag: "2D · LADDER",
    text: "Star-delta starter control circuit as a ladder diagram"
  },
  {
    mode: "3d", tag: "3D · ENCLOSURE",
    text: "800 x 600 x 250 enclosure with 3 DIN rails and a gland plate"
  },
  {
    mode: "3d", tag: "3D · ASSEMBLY",
    text: "6-axis robot cell, 1.8 m reach, fenced 3 x 3 m, conveyor infeed"
  }
];

function renderExamples() {
  el.examples.innerHTML = "";
  for (var i = 0; i < EXAMPLES.length; i++) {
    (function (ex) {
      var card = document.createElement("button");
      card.type = "button";
      card.className = "example" + (ex.mode === "3d" ? " d3" : "");

      var tag = document.createElement("div");
      tag.className = "tag";
      tag.textContent = ex.tag;
      card.appendChild(tag);

      var text = document.createElement("div");
      text.className = "text";
      text.textContent = ex.text;
      card.appendChild(text);

      card.addEventListener("click", function () {
        setMode(ex.mode);
        el.input.value = ex.text;
        autoGrow();
        el.input.focus();
      });

      el.examples.appendChild(card);
    })(EXAMPLES[i]);
  }
}

// ------------------------------------------------------------- rendering

function render(state) {
  // Re-rendering only on change keeps scroll position and text selection.
  var signature = JSON.stringify(state);
  if (signature === lastRender) return;
  var wasAtBottom = nearBottom();
  lastRender = signature;

  busy = state.busy;

  renderStatus(state);
  renderAlerts(state);
  renderStats(state);
  renderHistory(state);
  renderMessages(state.messages);
  renderBusy(state);
  renderResult(state);
  renderLog(state.activity);

  var started = state.messages.length > 0 || state.busy;
  el.idle.hidden = started;

  el.send.disabled = busy || !state.connected;
  el.send.textContent = busy ? "Drawing…" : "Draw";
  el.updatePrevious.disabled = !state.canReplace;

  if (wasAtBottom) scrollToBottom();
}

function renderStatus(state) {
  var s = el.status;
  s.className = "";

  if (!state.connected) {
    el.statusLabel.textContent = state.detached ? "Detached" : "Not connected";
  } else if (!state.hasDrawing) {
    // Connected but with no drawing open is a distinct state: commands cannot
    // run against AutoCAD's Start tab.
    s.className = "warn";
    el.statusLabel.textContent = "Connected · no drawing open";
  } else {
    s.className = "on";
    el.statusLabel.textContent = "Connected · " + (state.drawingName || state.caption || "AutoCAD");
  }

  el.toggleConnection.textContent = state.connected ? "Disconnect" : "Reconnect";
}

function renderAlerts(state) {
  // Detaching was deliberate, so it is not something to warn about.
  el.alertConnection.hidden = state.connected || state.detached;

  // Two different problems: the file is behind, or AutoCAD has not reloaded it.
  var show = state.engineStale || state.engineNeedsRestart;
  el.alertEngine.hidden = !show;
  if (show) {
    if (state.engineStale) {
      el.engineMessage.textContent =
        "The drawing engine inside AutoCAD is out of date. Close AutoCAD, update it, then reopen AutoCAD.";
      el.fixEngine.hidden = false;
    } else {
      el.engineMessage.textContent =
        "AutoCAD is still running an older engine. Restart AutoCAD to load the new one — " +
        "until then, newer drawing features are unavailable.";
      el.fixEngine.hidden = true;
    }
  }
}

function renderStats(state) {
  var engine = state.engineVersion || "—";
  if (state.engineStale) engine += " · out of date";
  else if (state.engineNeedsRestart) engine += " · restart";
  else if (state.engineVersion) engine += " · current";
  el.statEngine.textContent = engine;
  el.statEngine.title = engine;

  var drawing = state.drawingName || (state.connected ? (state.caption || "AutoCAD") : "not connected");
  el.statDrawing.textContent = drawing;
  el.statDrawing.title = drawing;

  // Indexing is slow and freezes AutoCAD, so it is said out loud here rather
  // than only inside the References window.
  var refs;
  if (state.indexing) {
    refs = state.indexingTotal > 0
      ? "reading " + (state.indexingDone + 1) + "/" + state.indexingTotal
      : "reading…";
    el.statRefs.title = "Reading " + (state.indexingFile || "reference drawings");
  } else {
    var total = state.referenceCount || 0;
    var pending = state.referencePending || 0;
    refs = pending > 0
      ? (total - pending) + " of " + total + " read"
      : total + " indexed";
    el.statRefs.title = refs;
  }
  el.statRefs.textContent = refs;
}

function renderHistory(state) {
  el.history.innerHTML = "";
  var chats = state.chats || [];
  el.historyEmpty.hidden = chats.length > 0;

  for (var i = 0; i < chats.length; i++) {
    (function (chat) {
      var row = document.createElement("div");
      row.className = "chat" + (chat.id === state.chatId ? " active" : "");

      var grow = document.createElement("div");
      grow.className = "grow";

      var title = document.createElement("div");
      title.className = "title";
      title.textContent = chat.title;
      title.title = chat.title;
      grow.appendChild(title);

      var meta = document.createElement("div");
      meta.className = "meta";
      meta.appendChild(span(chat.kind));
      meta.appendChild(span("·"));
      meta.appendChild(span(chat.when));
      grow.appendChild(meta);

      row.appendChild(grow);

      var del = document.createElement("button");
      del.type = "button";
      del.className = "del";
      del.textContent = "✕";
      del.title = "Delete this drawing";
      del.addEventListener("click", function (e) {
        // Stops the row's own handler from opening what we are deleting.
        e.stopPropagation();
        if (busy) return;
        action("deletechatid", { id: chat.id });
      });
      row.appendChild(del);

      row.addEventListener("click", function () {
        if (busy || chat.id === state.chatId) return;
        api("/api/openchat", { id: chat.id }).then(function () {
          invalidate();
          refresh();
        });
      });

      el.history.appendChild(row);
    })(chats[i]);
  }
}

// True when the most recent message of that role says exactly this.
function endsWith(messages, role, text) {
  if (!text) return false;
  for (var i = (messages || []).length - 1; i >= 0; i--) {
    if (messages[i].role !== role) continue;
    return messages[i].text.trim() === text.trim();
  }
  return false;
}

function span(text) {
  var s = document.createElement("span");
  s.textContent = text;
  return s;
}

function renderMessages(messages) {
  el.messages.innerHTML = "";
  for (var i = 0; i < messages.length; i++) {
    var m = messages[i];
    var wrap = document.createElement("div");
    wrap.className = "msg " + m.role;

    if (m.role === "user" || m.role === "assistant") {
      var who = document.createElement("div");
      who.className = "who";
      who.textContent = m.role === "user" ? "YOU" : "ASSISTANT";
      wrap.appendChild(who);
    }

    var body = document.createElement("div");
    body.className = "body";
    body.textContent = m.text;
    wrap.appendChild(body);

    el.messages.appendChild(wrap);
  }
}

function renderBusy(state) {
  el.busy.hidden = !state.busy;
  if (!state.busy) return;

  // The transcript above already ends with the request, so echoing it here
  // would print the same sentence twice.
  el.echo.textContent = state.lastPrompt || "";
  el.echo.hidden = endsWith(state.messages, "user", state.lastPrompt);

  el.steps.innerHTML = "";
  var steps = state.steps || [];
  for (var i = 0; i < steps.length; i++) {
    var row = document.createElement("div");
    row.className = "step " +
      (i < state.stepIndex ? "done" : (i === state.stepIndex ? "now" : ""));

    var ring = document.createElement("div");
    ring.className = "ring";
    row.appendChild(ring);

    var label = document.createElement("div");
    label.className = "label";
    label.textContent = steps[i].label;
    row.appendChild(label);

    var gap = document.createElement("div");
    gap.className = "spacer";
    row.appendChild(gap);

    var note = document.createElement("div");
    note.className = "note";
    note.textContent = steps[i].note || "";
    row.appendChild(note);

    el.steps.appendChild(row);
  }

  $("cancel").textContent = state.cancelling ? "Cancelling…" : "Cancel";
  $("cancel").disabled = state.cancelling;
}

function renderResult(state) {
  var r = state.result;
  el.result.hidden = !r || state.busy;
  if (!r || state.busy) return;

  el.resultTitle.textContent = r.drawing
    ? "Drawn into " + r.drawing
    : "Drawn into AutoCAD";

  // When the model left a note it is already in the transcript as its reply;
  // repeating it under the heading says nothing new.
  el.resultSummary.textContent = r.summary || "";
  el.resultSummary.hidden = !r.summary || endsWith(state.messages, "assistant", r.summary);

  el.resultFacts.innerHTML = "";
  for (var i = 0; i < (r.facts || []).length; i++) {
    var chip = document.createElement("div");
    chip.className = "fact";
    chip.textContent = r.facts[i];
    el.resultFacts.appendChild(chip);
  }

  // Parameters shown here are the ones that produced this drawing. Editing one
  // writes it back to the composer, so Regenerate redraws with the new value.
  var params = r.params || [];
  el.paramPane.hidden = params.length === 0;
  el.resultParams.innerHTML = "";
  for (var j = 0; j < params.length; j++) {
    (function (p) {
      var row = document.createElement("div");
      row.className = "paramRow";

      var label = document.createElement("div");
      label.className = "label";
      label.textContent = p.label || p.key;
      row.appendChild(label);

      var input = document.createElement("input");
      input.type = "text";
      input.value = p.value;
      input.addEventListener("change", function () {
        ui.values[fieldKey(p.key)] = input.value;
        saveUi();
        renderFields();
      });
      row.appendChild(input);

      el.resultParams.appendChild(row);
    })(params[j]);
  }

  var objects = r.objects || [];
  el.objectPane.hidden = objects.length === 0;
  el.resultObjects.innerHTML = "";
  for (var k = 0; k < objects.length; k++) {
    var line = document.createElement("div");
    var name = document.createElement("span");
    name.className = "name";
    name.textContent = objects[k].name;
    var count = document.createElement("span");
    count.className = "count";
    count.textContent = objects[k].count;
    line.appendChild(name);
    line.appendChild(count);
    el.resultObjects.appendChild(line);
  }

  el.undo.disabled = !state.canUndo;
  el.regenerate.disabled = !state.canRegenerate || !state.connected;
}

var LEVEL_LABEL = { info: "INFO", ok: "OK", warn: "WARN", cmd: "CMD", error: "ERROR" };

function renderLog(entries) {
  entries = entries || [];
  el.logCount.textContent = entries.length + (entries.length === 1 ? " entry" : " entries");

  var visible = [];
  for (var i = 0; i < entries.length; i++) {
    var level = entries[i].level || "info";
    if (ui.logLevel === "all" ||
        (ui.logLevel === "issues" ? (level === "warn" || level === "error") : level === ui.logLevel)) {
      visible.push(entries[i]);
    }
  }

  el.logEmpty.hidden = visible.length > 0;
  el.log.innerHTML = "";

  for (var j = 0; j < visible.length; j++) {
    var e = visible[j];
    var line = document.createElement("div");
    line.className = "line " + (e.level || "info");

    var t = document.createElement("span");
    t.className = "t";
    t.textContent = e.time;
    line.appendChild(t);

    var lvl = document.createElement("span");
    lvl.className = "lvl";
    lvl.textContent = LEVEL_LABEL[e.level] || "INFO";
    line.appendChild(lvl);

    var text = document.createElement("span");
    text.className = "text";
    text.textContent = e.text;
    line.appendChild(text);

    el.log.appendChild(line);
  }

  if (ui.follow) el.log.scrollTop = el.log.scrollHeight;
}

function nearBottom() {
  var t = el.scroll;
  return t.scrollTop + t.clientHeight >= t.scrollHeight - 60;
}

function scrollToBottom() { el.scroll.scrollTop = el.scroll.scrollHeight; }

// --------------------------------------------------------------- actions

function refresh() {
  return api("/api/state").then(render).catch(function () {
    el.status.className = "";
    el.statusLabel.textContent = "Server not responding";
  });
}

function send() {
  var text = el.input.value.trim();
  if (!text || busy) return;

  el.input.value = "";
  autoGrow();
  busy = true;
  el.send.disabled = true;
  el.send.textContent = "Drawing…";

  api("/api/send", {
    message: text,
    mode: ui.mode,
    fields: currentFields(),
    pickPoint: el.pickPoint.checked,
    updatePrevious: el.updatePrevious.checked && !el.updatePrevious.disabled
  }).then(function () {
    invalidate();
    return refresh();
  });
}

function setMode(mode) {
  ui.mode = mode === "3d" ? "3d" : "2d";
  saveUi();
  el.mode2d.className = "mode" + (ui.mode === "2d" ? " on" : "");
  el.mode3d.className = "mode" + (ui.mode === "3d" ? " on" : "");
  renderFields();
}

function autoGrow() {
  el.input.style.height = "auto";
  el.input.style.height = Math.min(el.input.scrollHeight, 220) + "px";
}

// ----------------------------------------------------------------- wiring

el.send.addEventListener("click", send);
$("newChat").addEventListener("click", function () { action("newchat"); });
el.input.addEventListener("input", autoGrow);

el.input.addEventListener("keydown", function (e) {
  if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
    e.preventDefault();
    send();
  }
});

el.mode2d.addEventListener("click", function () { setMode("2d"); });
el.mode3d.addEventListener("click", function () { setMode("3d"); });

el.toggleFields.addEventListener("click", function () {
  ui.showFields = !ui.showFields;
  saveUi();
  renderFields();
});

$("attach").addEventListener("click", openRefs);

$("clearHistory").addEventListener("click", function () {
  if (busy) return;
  if (!confirm("Delete every saved drawing from the history? This cannot be undone.")) return;

  // No bulk endpoint: deleting them one by one is what the button means, and
  // keeps a single definition of what deleting a chat does.
  api("/api/state").then(function (state) {
    var chain = Promise.resolve();
    (state.chats || []).forEach(function (chat) {
      chain = chain.then(function () {
        return api("/api/action", { action: "deletechatid", id: chat.id });
      });
    });
    return chain;
  }).then(function () {
    invalidate();
    refresh();
  });
});

$("cancel").addEventListener("click", function () { action("cancel"); });
$("keep").addEventListener("click", function () {
  // Keeping is the default - the geometry is already in the drawing - so this
  // just clears the panel and readies the composer for the next request.
  el.result.hidden = true;
  el.input.focus();
});
$("regenerate").addEventListener("click", function () {
  if (busy) return;
  action("regenerate");
});
$("undo").addEventListener("click", function () {
  if (busy) return;
  if (!confirm("Remove everything the last generation drew?")) return;
  action("undo");
});

el.toggleConnection.addEventListener("click", function () {
  api("/api/state").then(function (state) {
    action(state.connected ? "disconnect" : "connect");
  });
});

$("troubleshoot").addEventListener("click", function () {
  api("/api/state").then(function (state) {
    $("helpDrawing").textContent = !state.connected
      ? "not connected"
      : (state.hasDrawing ? "yes" : "no drawing open");
    $("helpBackdrop").hidden = false;
  });
});
$("helpClose").addEventListener("click", function () { $("helpBackdrop").hidden = true; });

el.fixEngine.addEventListener("click", function () {
  action("setup").then(function () {
    return api("/api/state");
  }).then(function (s) {
    if (!s.engineStale) alert("Engine updated. Reopen AutoCAD to load it.");
  });
});

var dataActions = document.querySelectorAll("[data-action]");
for (var a = 0; a < dataActions.length; a++) {
  dataActions[a].addEventListener("click", function (e) {
    action(e.currentTarget.dataset.action);
  });
}

// ----- narrow layouts: the side panels become overlays -----

$("toggleNav").addEventListener("click", function () {
  el.sidebar.classList.toggle("forced");
  el.logpane.classList.remove("forced");
});
$("toggleLog").addEventListener("click", function () {
  el.logpane.classList.toggle("forced");
  el.sidebar.classList.remove("forced");
});

// ----- log pane -----

var filters = document.querySelectorAll(".filter");
for (var f = 0; f < filters.length; f++) {
  filters[f].addEventListener("click", function (e) {
    ui.logLevel = e.currentTarget.dataset.level;
    saveUi();
    for (var i = 0; i < filters.length; i++)
      filters[i].className = "filter" + (filters[i].dataset.level === ui.logLevel ? " on" : "");
    invalidate();
    refresh();
  });
}

el.follow.addEventListener("change", function () {
  ui.follow = el.follow.checked;
  saveUi();
  if (ui.follow) el.log.scrollTop = el.log.scrollHeight;
});

$("copyLog").addEventListener("click", function () {
  api("/api/state").then(function (state) {
    var text = (state.activity || []).map(function (e) {
      return e.time + "  " + (LEVEL_LABEL[e.level] || "INFO") + "  " + e.text;
    }).join("\n");

    if (navigator.clipboard) {
      navigator.clipboard.writeText(text).then(flashCopied, flashCopied);
    } else {
      flashCopied();
    }
  });
});

function flashCopied() {
  var b = $("copyLog");
  b.textContent = "Copied";
  setTimeout(function () { b.textContent = "Copy"; }, 1200);
}

$("clearLog").addEventListener("click", function () { action("clearlog"); });

// Dragging the grip resizes the log pane. Bounds stop it from eating the
// conversation or collapsing to nothing.
var drag = null;

el.grip.addEventListener("mousedown", function (e) {
  e.preventDefault();
  drag = { x: e.clientX, w: el.logpane.offsetWidth };
  el.grip.classList.add("dragging");
  document.body.style.userSelect = "none";
});

window.addEventListener("mousemove", function (e) {
  if (!drag) return;
  var w = Math.min(720, Math.max(260, drag.w + (drag.x - e.clientX)));
  ui.logWidth = w;
  el.logpane.style.flex = "0 0 " + w + "px";
  el.logpane.style.width = w + "px";
});

window.addEventListener("mouseup", function () {
  if (!drag) return;
  drag = null;
  el.grip.classList.remove("dragging");
  document.body.style.userSelect = "";
  saveUi();
});

// ---------------------------------------------------------------- polling

function tick() {
  refresh().then(function () {
    setTimeout(tick, busy ? 600 : 2500);
  });
}

// ---------------------------------------------------------------- settings

var cfg = {
  backdrop: $("settingsBackdrop"),
  provider: $("cfgProvider"),
  key: $("cfgKey"),
  keyState: $("keyState"),
  baseUrl: $("cfgBaseUrl"),
  model: $("cfgModel"),
  modelState: $("modelState"),
  fallback: $("cfgFallback"),
  maxTokens: $("cfgMaxTokens"),
  thinking: $("cfgThinking"),
  extra: $("cfgExtra"),
  path: $("cfgPath")
};

var loaded = null;

function providerFamily() {
  var p = cfg.provider.value;
  if (p === "gemini") return "gemini";
  if (p === "claude") return "claude";
  return "server";
}

function modelFieldFor(family) {
  if (family === "gemini") return "geminiModel";
  if (family === "claude") return "claudeModel";
  return "openAiModel";
}

// Fallbacks are per provider: a Gemini list left in the box after switching to
// Claude would send Gemini names to Anthropic, which can only fail.
function fallbackFieldFor(family) {
  if (family === "gemini") return "geminiFallbackModels";
  if (family === "claude") return "claudeFallbackModels";
  return "openAiFallbackModels";
}

function keyFieldFor(family) {
  if (family === "gemini") return "geminiApiKey";
  if (family === "claude") return "claudeApiKey";
  return "openAiApiKey";
}

// Which provider the dialog is currently showing, so an edit to the fallback
// box can be kept when the user switches away and back.
var shownFamily = null;

function syncProviderFields() {
  var family = providerFamily();

  if (loaded && shownFamily && shownFamily !== family)
    loaded[fallbackFieldFor(shownFamily)] = cfg.fallback.value.trim();
  shownFamily = family;
  var serverRows = document.querySelectorAll(".serverOnly");
  for (var i = 0; i < serverRows.length; i++)
    serverRows[i].style.display = family === "server" ? "" : "none";

  if (!loaded) return;

  // The stored model is the starting point; the full list replaces it as soon
  // as the provider answers. Showing one option and a button the user has to
  // find first reads as "there is nothing to choose from".
  var known = modelCache[family];
  setModelOptions(known || [loaded[modelFieldFor(family)]].filter(Boolean),
                  loaded[modelFieldFor(family)]);
  cfg.fallback.value = loaded[fallbackFieldFor(family)] || "";

  if (!known) autoLoadModels(family);

  var has = family === "gemini" ? loaded.hasGeminiKey
          : family === "claude" ? loaded.hasClaudeKey
          : loaded.hasOpenAiKey;

  // An empty box reads as "the key is gone", so say plainly that it is not.
  if (has) {
    cfg.key.placeholder = "••••••••  key already saved";
    cfg.keyState.textContent = "Your key is saved. Leave this blank unless you are replacing it.";
    cfg.keyState.className = "note good";
  } else {
    cfg.key.placeholder = "paste your API key here";
    cfg.keyState.textContent = "No key stored for this provider yet.";
    cfg.keyState.className = "note";
  }
}

// Model lists per provider family, so reopening the dialog or flicking between
// providers does not re-ask the API every time.
var modelCache = {};

/// Fills the dropdown in the background. Silent on failure: the user has not
/// asked for anything yet, so an error here is noise - the stored model stays
/// selectable and the explicit button reports properly.
function autoLoadModels(family) {
  var has = family === "gemini" ? loaded.hasGeminiKey
          : family === "claude" ? loaded.hasClaudeKey
          : loaded.hasOpenAiKey;
  if (!has) return;

  cfg.modelState.textContent = "Loading the model list…";
  cfg.modelState.className = "note";

  api("/api/models").then(function (r) {
    // The user may have switched provider while this was in flight.
    if (providerFamily() !== family) return;

    if (!r.ok || !r.models || r.models.length === 0) {
      cfg.modelState.textContent = r.error || "";
      cfg.modelState.className = "note";
      return;
    }

    modelCache[family] = r.models;
    setModelOptions(r.models, cfg.model.value || loaded[modelFieldFor(family)]);
    cfg.modelState.textContent = r.models.length + " models available.";
    cfg.modelState.className = "note good";
  }).catch(function () {
    if (providerFamily() === family) cfg.modelState.textContent = "";
  });
}

function setModelOptions(names, selected) {
  cfg.model.innerHTML = "";
  if (!names.length) names = selected ? [selected] : [];
  for (var i = 0; i < names.length; i++) {
    var o = document.createElement("option");
    o.value = names[i];
    o.textContent = names[i];
    cfg.model.appendChild(o);
  }
  if (selected && names.indexOf(selected) === -1) {
    var cur = document.createElement("option");
    cur.value = selected;
    cur.textContent = selected;
    cfg.model.insertBefore(cur, cfg.model.firstChild);
  }
  if (selected) cfg.model.value = selected;
}

function openSettings() {
  Promise.all([api("/api/config"), api("/api/state")]).then(function (both) {
    var c = both[0], state = both[1];
    loaded = c;

    cfg.provider.value = c.provider || "gemini";
    cfg.baseUrl.value = c.openAiBaseUrl || "";
    cfg.fallback.value = c.fallbackModels || "";
    cfg.maxTokens.value = c.maxTokens;
    cfg.thinking.value = c.thinkingBudget;
    cfg.extra.value = c.extraInstructions || "";
    cfg.key.value = "";
    cfg.path.textContent = "Settings are stored in " + c.path;
    cfg.modelState.textContent = "";
    cfg.modelState.className = "note";
    shownFamily = null;

    // The read-only status rows, from live state rather than guesswork.
    var acad = $("setAcad");
    acad.textContent = state.connected
      ? (state.caption || "running")
      : (state.detached ? "detached" : "not running");
    acad.className = "value " + (state.connected ? "good" : "bad");

    var engine = $("setEngine");
    engine.textContent = (state.engineVersion || "unknown") +
      (state.engineStale ? " · out of date"
        : state.engineNeedsRestart ? " · restart AutoCAD" : " · current");
    engine.className = "value " +
      (state.engineStale || state.engineNeedsRestart ? "bad" : "good");

    $("setRefs").textContent = (state.referenceCount || 0) + " drawings";

    syncProviderFields();
    cfg.backdrop.hidden = false;
  });
}

function saveSettings() {
  var family = providerFamily();
  var body = {
    provider: cfg.provider.value,
    openAiBaseUrl: cfg.baseUrl.value.trim(),
    fallbackModels: cfg.fallback.value.trim(),
    maxTokens: parseInt(cfg.maxTokens.value, 10) || 32000,
    thinkingBudget: parseInt(cfg.thinking.value, 10),
    extraInstructions: cfg.extra.value
  };
  body[modelFieldFor(family)] = cfg.model.value;
  if (loaded) loaded[fallbackFieldFor(family)] = cfg.fallback.value.trim();
  // Only send a key when one was typed; blank keeps whatever is stored.
  if (cfg.key.value.trim()) body[keyFieldFor(family)] = cfg.key.value.trim();

  api("/api/config", body).then(function (r) {
    if (r && r.ok === false) { alert("Could not save: " + (r.error || "unknown error")); return; }
    cfg.backdrop.hidden = true;
    invalidate();
    refresh();
  });
}

// Saving first means the server queries with the key just typed.
function loadModels() {
  cfg.modelState.textContent = "Asking the provider…";
  cfg.modelState.className = "note";

  var family = providerFamily();
  var body = { provider: cfg.provider.value, openAiBaseUrl: cfg.baseUrl.value.trim() };
  if (cfg.key.value.trim()) body[keyFieldFor(family)] = cfg.key.value.trim();

  api("/api/config", body)
    .then(function () { return api("/api/models"); })
    .then(function (r) {
      if (!r.ok) {
        cfg.modelState.textContent = r.error || "Could not list models.";
        cfg.modelState.className = "note bad";
        return;
      }
      modelCache[providerFamily()] = r.models;
      setModelOptions(r.models, cfg.model.value);
      cfg.modelState.textContent = r.models.length + " models available.";
      cfg.modelState.className = "note good";
    })
    .catch(function () {
      cfg.modelState.textContent = "Could not reach the provider.";
      cfg.modelState.className = "note bad";
    });
}

$("openSettings").addEventListener("click", openSettings);
$("settingsCancel").addEventListener("click", function () { cfg.backdrop.hidden = true; });
$("settingsClose").addEventListener("click", function () { cfg.backdrop.hidden = true; });
$("settingsSave").addEventListener("click", saveSettings);
$("loadModels").addEventListener("click", loadModels);
cfg.provider.addEventListener("change", syncProviderFields);

// -------------------------------------------------------------- references

function openRefs() {
  $("refsBackdrop").hidden = false;
  loadRefs();
}

// While AutoCAD is reading drawings the list keeps refreshing itself, because
// a large file takes minutes and a static list looks like nothing is happening.
var refTimer = null;

function stopRefPolling() {
  if (refTimer) { clearTimeout(refTimer); refTimer = null; }
}

var REF_STATE_LABEL = {
  indexed: "indexed",
  pending: "waiting",
  reading: "reading…",
  failed: "failed"
};

function loadRefs() {
  stopRefPolling();

  api("/api/references").then(function (r) {
    $("refFolder").textContent = r.folder || "";
    $("refFolder").title = r.folder || "";

    var files = r.files || [];
    var list = $("refList");
    list.innerHTML = "";
    $("refEmpty").hidden = files.length > 0;
    list.hidden = files.length === 0;

    for (var i = 0; i < files.length; i++) {
      var f = files[i];

      var row = document.createElement("div");
      row.className = "ref";

      var thumb = document.createElement("div");
      thumb.className = "thumb";
      row.appendChild(thumb);

      var grow = document.createElement("div");
      grow.className = "grow";

      var name = document.createElement("div");
      name.className = "name";
      name.textContent = f.name;
      name.title = f.name;
      grow.appendChild(name);

      var meta = document.createElement("div");
      meta.className = "meta";
      // A drawing that could not be read says why, in place of its size.
      meta.textContent = f.state === "failed" && f.reason
        ? f.meta + " · " + f.reason
        : f.meta;
      meta.title = meta.textContent;
      grow.appendChild(meta);

      row.appendChild(grow);

      var state = document.createElement("div");
      state.className = "state " + f.state;
      state.textContent = REF_STATE_LABEL[f.state] || f.state;
      row.appendChild(state);

      list.appendChild(row);
    }

    renderRefStatus(r);

    // Keep watching only while there is something to watch.
    if (r.indexing && !$("refsBackdrop").hidden) refTimer = setTimeout(loadRefs, 3000);
  });
}

function renderRefStatus(r) {
  var status = $("refStatus");
  var retry = $("refRetry");
  var button = $("refReindex");

  retry.hidden = !(r.failed > 0);

  if (r.indexing) {
    var of = r.progressTotal > 0
      ? " (" + (r.progressDone + 1) + " of " + r.progressTotal + ")"
      : "";
    status.textContent = "Reading " + (r.current || "drawings") + of +
      " — AutoCAD is busy until this finishes. You can close this window; it keeps going.";
    status.className = "note working";
    button.disabled = true;
    button.textContent = "Reading…";
    return;
  }

  button.disabled = false;
  button.textContent = "Reindex";

  var waiting = 0;
  for (var i = 0; i < (r.files || []).length; i++)
    if (r.files[i].state === "pending") waiting++;

  if (waiting > 0) {
    status.textContent = waiting + (waiting === 1 ? " drawing is" : " drawings are") +
      " waiting to be read. Large files take a few minutes each; drawing is not held up by it.";
    status.className = "note";
  } else if (r.failed > 0) {
    status.textContent = r.failed + (r.failed === 1 ? " drawing could" : " drawings could") +
      " not be read and will be skipped. Editing one makes it try again.";
    status.className = "note bad";
  } else {
    status.textContent = "Indexed drawings teach the assistant your symbol library, " +
      "layer names and title block.";
    status.className = "note";
  }
}

$("openRefs").addEventListener("click", openRefs);
$("refsClose").addEventListener("click", function () {
  stopRefPolling();
  $("refsBackdrop").hidden = true;
});
$("refAdd").addEventListener("click", function () { action("openreferences"); });

$("refReindex").addEventListener("click", function () {
  $("refReindex").disabled = true;
  $("refReindex").textContent = "Reading…";
  // The server starts the import and reports progress; the list polls for it
  // rather than guessing how long to wait.
  action("syncreferences").then(function () { setTimeout(loadRefs, 600); });
});

$("refRetry").addEventListener("click", function () {
  $("refRetry").disabled = true;
  action("retryreferences").then(function () {
    $("refRetry").disabled = false;
    setTimeout(loadRefs, 600);
  });
});

// Clicking the dimmed area outside a sheet closes it.
var backdrops = document.querySelectorAll(".backdrop");
for (var b = 0; b < backdrops.length; b++) {
  backdrops[b].addEventListener("click", function (e) {
    if (e.target === e.currentTarget) e.currentTarget.hidden = true;
  });
}

document.addEventListener("keydown", function (e) {
  if (e.key !== "Escape") return;
  for (var i = 0; i < backdrops.length; i++) backdrops[i].hidden = true;
});

// ------------------------------------------------------------------ start

loadUi();
setMode(ui.mode);
renderExamples();
renderFields();

el.follow.checked = ui.follow;
el.logpane.style.flex = "0 0 " + ui.logWidth + "px";
el.logpane.style.width = ui.logWidth + "px";

var startFilters = document.querySelectorAll(".filter");
for (var g = 0; g < startFilters.length; g++)
  startFilters[g].className = "filter" + (startFilters[g].dataset.level === ui.logLevel ? " on" : "");

refresh().then(scrollToBottom);
tick();
el.input.focus();
