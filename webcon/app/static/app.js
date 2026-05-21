(() => {
  const $ = (sel, root = document) => root.querySelector(sel);

  // Pane refs
  const authPane = $("#auth");
  const dashPane = $("#dashboard");
  const consolePane = $("#console");

  // Console refs
  const logEl = $("#log");
  const statusEl = $("#status");
  const cmdInput = $("#cmd");
  const serverLabel = $("#server-label");

  // Dashboard refs
  const cardsEl = $("#cards");
  const bcLog = $("#bc-log");
  const bcCmd = $("#bc-cmd");

  let ws = null;
  let history = JSON.parse(sessionStorage.getItem("history") || "[]");
  let historyIdx = history.length;

  // Decide view from URL.
  // "/"                  → dashboard
  // "/server/<id>"       → console for that server
  function currentRoute() {
    const m = location.pathname.match(/^\/server\/([^\/]+)\/?$/);
    if (m) return { kind: "console", id: decodeURIComponent(m[1]) };
    return { kind: "dashboard" };
  }

  // ----- generic helpers --------------------------------------------------

  function appendLineTo(targetEl, kind, text) {
    const atBottom = targetEl.scrollTop + targetEl.clientHeight >= targetEl.scrollHeight - 20;
    for (const raw of String(text).split("\n")) {
      if (!raw.trim()) continue;
      const span = document.createElement("span");
      span.className = `line ${kind}`;
      span.textContent = raw;
      targetEl.appendChild(span);
    }
    if (atBottom) targetEl.scrollTop = targetEl.scrollHeight;
  }

  function setStatus(connected) {
    statusEl.classList.toggle("pill-connected", connected);
    statusEl.classList.toggle("pill-disconnected", !connected);
    // keep legacy classes for any external hooks / older CSS
    statusEl.classList.toggle("connected", connected);
    statusEl.classList.toggle("disconnected", !connected);
    statusEl.innerHTML =
      '<span class="pill-dot" aria-hidden="true"></span><span>' +
      (connected ? "CONNECTED" : "DISCONNECTED") +
      '</span>';
  }

  function getToken() {
    return sessionStorage.getItem("token") || "";
  }

  function wsBase() {
    return (location.protocol === "https:" ? "wss" : "ws") + "://" + location.host;
  }

  // ----- workshop input wiring (shared logic) -----------------------------

  function wireWorkshop(inputEl, btnEl, sendFn) {
    const refresh = () => {
      const v = inputEl.value.trim();
      btnEl.disabled = !/^\d+$/.test(v);
      inputEl.classList.toggle("invalid", v.length > 0 && !/^\d+$/.test(v));
    };
    inputEl.addEventListener("input", refresh);
    inputEl.addEventListener("keydown", (ev) => {
      if (ev.key === "Enter" && !btnEl.disabled) {
        ev.preventDefault();
        btnEl.click();
      }
    });
    btnEl.addEventListener("click", () => {
      const v = inputEl.value.trim();
      if (!/^\d+$/.test(v)) return;
      sendFn(`host_workshop_map ${v}`);
      inputEl.value = "";
      refresh();
    });
    refresh();
  }

  // ----- console view -----------------------------------------------------

  function connectConsole(serverId) {
    const token = getToken();
    const url = `${wsBase()}/ws?token=${encodeURIComponent(token)}&server=${encodeURIComponent(serverId)}`;
    ws = new WebSocket(url);

    ws.addEventListener("open", () => {
      setStatus(true);
      cmdInput.focus();
    });

    ws.addEventListener("message", (ev) => {
      let msg;
      try { msg = JSON.parse(ev.data); } catch { return; }
      if (typeof msg.text !== "string") return;
      appendLineTo(logEl, msg.type || "rcon", msg.text);
    });

    ws.addEventListener("close", (ev) => {
      setStatus(false);
      if (ev.code === 4401) {
        sessionStorage.removeItem("token");
        showAuth();
        alert("Token rejected.");
      } else if (ev.code === 4404) {
        appendLineTo(logEl, "error", "[unknown server id]");
      } else {
        appendLineTo(logEl, "error", "[disconnected — refresh to reconnect]");
      }
    });

    ws.addEventListener("error", () => setStatus(false));
  }

  function sendCmd(cmd) {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({ cmd }));
    if (history[history.length - 1] !== cmd) {
      history.push(cmd);
      if (history.length > 200) history.shift();
      sessionStorage.setItem("history", JSON.stringify(history));
    }
    historyIdx = history.length;
  }

  function setupConsole(serverId) {
    serverLabel.textContent = serverId;
    const idLine = document.getElementById("server-id-line");
    if (idLine) idLine.textContent = "#" + serverId;
    document.title = `CS2 WebCon — ${serverId}`;
    // initialize status pill with proper DOM (dot + label)
    setStatus(false);
    // best-effort label enrichment via /api/servers (fire-and-forget,
    // doesn't block listener wiring or the WS connect below)
    (async () => {
      try {
        const token = getToken();
        const resp = await fetch(`/api/servers?token=${encodeURIComponent(token)}`);
        if (!resp.ok) return;
        const list = await resp.json();
        const found = (list || []).find((s) => s.id === serverId);
        if (found) {
          serverLabel.textContent = found.label || serverId;
          if (idLine) idLine.textContent = "#" + (found.id || serverId);
          document.title = `CS2 WebCon — ${found.label || serverId}`;
        }
      } catch (_) { /* ignore — fall back to id */ }
    })();
    // Wire console-only handlers (idempotent across navigations would matter on
    // a real SPA, but here we do a hard page change for back-to-dash; the
    // dashboard link is a plain anchor, so the page reloads each time.)
    $("#input-form").addEventListener("submit", (ev) => {
      ev.preventDefault();
      const cmd = cmdInput.value.trim();
      if (!cmd) return;
      sendCmd(cmd);
      cmdInput.value = "";
    });
    cmdInput.addEventListener("keydown", (ev) => {
      if (ev.key === "ArrowUp") {
        if (historyIdx > 0) {
          historyIdx--;
          cmdInput.value = history[historyIdx] ?? "";
          setTimeout(() => cmdInput.setSelectionRange(cmdInput.value.length, cmdInput.value.length), 0);
        }
        ev.preventDefault();
      } else if (ev.key === "ArrowDown") {
        if (historyIdx < history.length) {
          historyIdx++;
          cmdInput.value = history[historyIdx] ?? "";
        }
        ev.preventDefault();
      }
    });
    document.querySelectorAll("#console .quick button[data-cmd]").forEach((btn) => {
      btn.addEventListener("click", () => {
        sendCmd(btn.dataset.cmd);
        cmdInput.focus();
      });
    });
    $("#clear").addEventListener("click", () => { logEl.innerHTML = ""; });
    wireWorkshop($("#ws-workshop"), $("#ws-workshop-btn"), sendCmd);
    consolePane.classList.remove("hidden");
    connectConsole(serverId);
  }

  // ----- dashboard view ---------------------------------------------------

  async function loadServers() {
    const token = getToken();
    // fetch /api/status + /api/servers in parallel; /api/status gives connect
    // state + label, /api/servers gives the configuration fields (host/port/log).
    const [statusResp, configResp] = await Promise.all([
      fetch(`/api/status?token=${encodeURIComponent(token)}`),
      fetch(`/api/servers?token=${encodeURIComponent(token)}`),
    ]);
    if (statusResp.status === 401 || configResp.status === 401) {
      sessionStorage.removeItem("token");
      showAuth();
      return [];
    }
    if (!statusResp.ok) {
      cardsEl.innerHTML = `<div class="cards-empty"><span class="cards-empty-title">failed to load servers (HTTP ${statusResp.status})</span></div>`;
      return [];
    }
    const statusList = await statusResp.json();
    let configById = {};
    if (configResp.ok) {
      try {
        const configList = await configResp.json();
        for (const c of configList) configById[c.id] = c;
      } catch (_) { /* ignore */ }
    }
    return statusList.map((s) => ({ ...(configById[s.id] || {}), ...s }));
  }

  function renderCards(servers) {
    // update servers-count meta in the section header
    const countEl = document.getElementById("servers-count");
    if (countEl) {
      const n = servers.length;
      countEl.textContent = n === 0
        ? "// 0 NODES"
        : `// ${n} NODE${n === 1 ? "" : "S"}`;
    }

    cardsEl.innerHTML = "";
    cardsEl.className = "cards"; // reset, in case empty state changed it

    if (!servers.length) {
      const empty = document.createElement("div");
      empty.className = "cards-empty";
      empty.innerHTML = `
        <svg class="icon" aria-hidden="true"><use href="#i-server-off"/></svg>
        <span class="cards-empty-title">No servers configured</span>
        <span class="cards-empty-sub">Click <strong>Add Server</strong> in the top bar to register your first CS2 RCON endpoint.</span>
      `;
      cardsEl.appendChild(empty);
      return;
    }

    for (const s of servers) {
      const tile = document.createElement("article");
      tile.className = "tile";
      const pillCls = s.connected ? "pill-connected" : "pill-disconnected";
      const pillTxt = s.connected ? "CONNECTED" : "DISCONNECTED";
      const hostPort = (s.rcon_host && s.rcon_port)
        ? `${s.rcon_host}:${s.rcon_port}`
        : "—";
      const logCt = s.log_container || "—";
      tile.innerHTML = `
        <div class="tile-head">
          <span class="tile-label">${escapeHtml(s.label || s.id)}</span>
          <span class="pill ${pillCls}">
            <span class="pill-dot" aria-hidden="true"></span>
            <span>${pillTxt}</span>
          </span>
        </div>
        <div class="tile-id">#${escapeHtml(s.id)}</div>
        <div class="tile-metrics">
          <div class="metric">
            <span class="metric-key">RCON</span>
            <span class="metric-val" title="${escapeHtml(hostPort)}">${escapeHtml(hostPort)}</span>
          </div>
          <div class="metric">
            <span class="metric-key">LOG</span>
            <span class="metric-val" title="${escapeHtml(logCt)}">${escapeHtml(logCt)}</span>
          </div>
        </div>
        ${s.detail ? `
          <div class="tile-detail">
            <svg class="icon" aria-hidden="true"><use href="#i-warning"/></svg>
            <span>${escapeHtml(s.detail)}</span>
          </div>` : ""}
        <div class="tile-actions">
          <a class="btn btn-primary btn-open" href="/server/${encodeURIComponent(s.id)}">
            <span>OPEN</span>
            <svg class="icon" aria-hidden="true"><use href="#i-chevron-right"/></svg>
          </a>
          <button class="icon-btn" data-act="edit"   data-id="${escapeHtml(s.id)}" aria-label="Edit ${escapeHtml(s.id)}" title="Edit">
            <svg class="icon" aria-hidden="true"><use href="#i-pencil"/></svg>
          </button>
          <button class="icon-btn" data-act="delete" data-id="${escapeHtml(s.id)}" aria-label="Delete ${escapeHtml(s.id)}" title="Delete">
            <svg class="icon" aria-hidden="true"><use href="#i-trash"/></svg>
          </button>
        </div>
      `;
      cardsEl.appendChild(tile);
    }
    cardsEl.querySelectorAll(".icon-btn[data-act=edit]").forEach((b) => {
      b.addEventListener("click", () => openServerModal("edit", b.dataset.id));
    });
    cardsEl.querySelectorAll(".icon-btn[data-act=delete]").forEach((b) => {
      b.addEventListener("click", () => deleteServer(b.dataset.id));
    });
  }

  // ----- server CRUD modal ------------------------------------------------

  const modalEl = $("#server-modal");
  const modalTitle = $("#server-modal-title");
  const modalError = $("#server-modal-error");
  const modalForm = $("#server-form");
  const modalPwdHint = $("#sf-pwd-hint");
  let modalMode = "add";   // "add" | "edit"
  let modalEditId = null;

  function setModalError(text) {
    // modal-error contains an inline warning icon + a text span; only update the
    // text span so the icon survives.
    const span = modalError.querySelector(".modal-error-text");
    if (span) span.textContent = text || "";
    else modalError.textContent = text || "";
  }

  function openServerModal(mode, id) {
    modalMode = mode;
    modalEditId = id || null;
    modalError.classList.add("hidden");
    setModalError("");

    const idEl   = $("#sf-id");
    const lblEl  = $("#sf-label");
    const hostEl = $("#sf-host");
    const portEl = $("#sf-port");
    const logEl2 = $("#sf-log");
    const pwdEl  = $("#sf-password");

    if (mode === "edit") {
      modalTitle.textContent = "EDIT SERVER";
      modalPwdHint.classList.remove("hidden");
      pwdEl.required = false;
      pwdEl.placeholder = "leave blank to keep current";
      idEl.disabled = true;
      // Look up the current row from the most recent /api/status payload cache.
      const s = (lastServers || []).find((r) => r.id === id) || {};
      idEl.value   = s.id || id || "";
      lblEl.value  = s.label || "";
      hostEl.value = s.rcon_host || "";
      portEl.value = s.rcon_port || 27015;
      logEl2.value = s.log_container || "";
      pwdEl.value  = "";
    } else {
      modalTitle.textContent = "ADD SERVER";
      modalPwdHint.classList.add("hidden");
      pwdEl.required = true;
      pwdEl.placeholder = "";
      idEl.disabled = false;
      idEl.value = "";
      lblEl.value = "";
      hostEl.value = "";
      portEl.value = 27015;
      logEl2.value = "";
      pwdEl.value = "";
    }
    modalEl.classList.remove("hidden");
    modalEl.setAttribute("aria-hidden", "false");
    setTimeout(() => (mode === "edit" ? lblEl.focus() : idEl.focus()), 0);
  }

  function closeServerModal() {
    modalEl.classList.add("hidden");
    modalEl.setAttribute("aria-hidden", "true");
  }

  async function submitServerModal(ev) {
    ev.preventDefault();
    modalError.classList.add("hidden");
    setModalError("");

    const payload = {
      id:            $("#sf-id").value.trim(),
      label:         $("#sf-label").value.trim(),
      rcon_host:     $("#sf-host").value.trim(),
      rcon_port:     parseInt($("#sf-port").value, 10),
      log_container: $("#sf-log").value.trim(),
      password:      $("#sf-password").value,
    };

    const token = getToken();
    const url = modalMode === "edit"
      ? `/api/servers/${encodeURIComponent(modalEditId)}?token=${encodeURIComponent(token)}`
      : `/api/servers?token=${encodeURIComponent(token)}`;
    const method = modalMode === "edit" ? "PUT" : "POST";

    let resp;
    try {
      resp = await fetch(url, {
        method,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
    } catch (err) {
      setModalError(`network error: ${err}`);
      modalError.classList.remove("hidden");
      return;
    }
    if (!resp.ok) {
      let detail = `HTTP ${resp.status}`;
      try { const j = await resp.json(); if (j && j.detail) detail = j.detail; } catch {}
      setModalError(detail);
      modalError.classList.remove("hidden");
      return;
    }
    closeServerModal();
    await refreshDashboard();
  }

  async function deleteServer(id) {
    if (!confirm(`Delete server "${id}"? This removes it from the dashboard but keeps the password in .env.`)) return;
    const token = getToken();
    const resp = await fetch(`/api/servers/${encodeURIComponent(id)}?token=${encodeURIComponent(token)}`, {
      method: "DELETE",
    });
    if (!resp.ok) {
      let detail = `HTTP ${resp.status}`;
      try { const j = await resp.json(); if (j && j.detail) detail = j.detail; } catch {}
      alert(`Delete failed: ${detail}`);
      return;
    }
    await refreshDashboard();
  }

  let lastServers = [];
  async function refreshDashboard() {
    lastServers = await loadServers();
    renderCards(lastServers);
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, (c) => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]));
  }

  function connectBroadcast() {
    const token = getToken();
    const url = `${wsBase()}/ws/broadcast?token=${encodeURIComponent(token)}`;
    ws = new WebSocket(url);
    ws.addEventListener("message", (ev) => {
      let msg; try { msg = JSON.parse(ev.data); } catch { return; }
      if (typeof msg.text !== "string") return;
      appendLineTo(bcLog, msg.type || "rcon", msg.text);
    });
    ws.addEventListener("close", (ev) => {
      if (ev.code === 4401) {
        sessionStorage.removeItem("token");
        showAuth();
        alert("Token rejected.");
      } else {
        appendLineTo(bcLog, "error", "[broadcast disconnected — refresh]");
      }
    });
  }

  function sendBroadcast(cmd) {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({ cmd }));
  }

  async function setupDashboard() {
    document.title = "CS2 WebCon — Dashboard";
    dashPane.classList.remove("hidden");

    await refreshDashboard();

    $("#dash-refresh").addEventListener("click", refreshDashboard);
    $("#dash-add").addEventListener("click", () => openServerModal("add"));
    $("#server-modal-cancel").addEventListener("click", closeServerModal);
    $("#server-modal-close").addEventListener("click", closeServerModal);
    modalEl.addEventListener("click", (ev) => {
      if (ev.target === modalEl) closeServerModal();
    });
    document.addEventListener("keydown", (ev) => {
      if (ev.key === "Escape" && !modalEl.classList.contains("hidden")) closeServerModal();
    });
    modalForm.addEventListener("submit", submitServerModal);
    $("#dash-logout").addEventListener("click", () => {
      sessionStorage.removeItem("token");
      location.href = "/";
    });

    // Broadcast quick bar
    document.querySelectorAll("#dashboard .quick button[data-cmd]").forEach((btn) => {
      btn.addEventListener("click", () => sendBroadcast(btn.dataset.cmd));
    });
    wireWorkshop($("#bc-workshop"), $("#bc-workshop-btn"), sendBroadcast);
    $("#bc-form").addEventListener("submit", (ev) => {
      ev.preventDefault();
      const v = bcCmd.value.trim();
      if (!v) return;
      sendBroadcast(v);
      bcCmd.value = "";
    });

    connectBroadcast();
  }

  // ----- auth + bootstrap -------------------------------------------------

  function showAuth() {
    authPane.classList.remove("hidden");
    dashPane.classList.add("hidden");
    consolePane.classList.add("hidden");
  }

  function bootRoute() {
    authPane.classList.add("hidden");
    const route = currentRoute();
    if (route.kind === "console") {
      setupConsole(route.id);
    } else {
      setupDashboard();
    }
  }

  $("#auth-form").addEventListener("submit", (ev) => {
    ev.preventDefault();
    const token = $("#token").value.trim();
    if (!token) return;
    sessionStorage.setItem("token", token);
    bootRoute();
  });

  // Auto-resume if a token is already in sessionStorage.
  if (getToken()) {
    $("#token").value = getToken();
    bootRoute();
  } else {
    showAuth();
  }
})();
