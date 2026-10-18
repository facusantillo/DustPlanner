/* DustPlanner panel. Plain JS, no build step: the host embeds this file. Every piece of data comes from /api/*
   (the same JSON the CLI prints with --json); the page never computes a dust value itself. */
(function () {
  "use strict";

  // ---------- i18n ---------------------------------------------------------------------------------------------------
  const STR = window.DP_STRINGS || { en: {} };
  let lang = (function () {
    try { const saved = localStorage.getItem("dp.lang"); if (saved && STR[saved]) return saved; } catch (e) { /* storage blocked */ }
    return (navigator.language || "en").toLowerCase().startsWith("es") ? "es" : "en";
  })();
  function t(key, vars) {
    let s = (STR[lang] && STR[lang][key]) || STR.en[key] || key;
    if (vars) for (const k of Object.keys(vars)) s = s.replace(new RegExp("\\{" + k + "\\}", "g"), String(vars[k]));
    return s;
  }
  function applyStaticStrings() {
    document.documentElement.lang = lang;
    document.querySelectorAll("[data-i18n]").forEach(el => { el.textContent = t(el.dataset.i18n); });
  }

  // ---------- helpers ------------------------------------------------------------------------------------------------
  const $ = (sel, root) => (root || document).querySelector(sel);
  const esc = s => String(s == null ? "" : s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const n = v => (v == null ? "—" : Number(v).toLocaleString(lang === "es" ? "es-ES" : "en-US"));
  const pct = v => (v == null ? null : Math.round(v * 1000) / 10);
  // "1 copy" / "3 copies": the string table holds the two forms under <key>_one and <key>_many.
  const count = (key, v) => t(Number(v) === 1 ? key + "_one" : key + "_many", { n: n(v) });
  const CLASS_NAMES = { DEATHKNIGHT: "Death Knight", DEMONHUNTER: "Demon Hunter" };
  const title = s => { if (!s) return "?"; const u = String(s).toUpperCase(); if (CLASS_NAMES[u]) return CLASS_NAMES[u]; return u.charAt(0) + u.slice(1).toLowerCase(); };
  const when = iso => { if (!iso) return "—"; const d = new Date(iso); return d.toLocaleString(lang === "es" ? "es-ES" : "en-GB", { dateStyle: "medium", timeStyle: "short" }); };
  const ago = iso => {
    if (!iso) return "—";
    const mins = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
    if (mins < 1) return t("just_now");
    if (mins < 60) return t("ago_minutes", { n: mins });
    if (mins < 48 * 60) return t("ago_hours", { n: Math.round(mins / 60) });
    return t("ago_days", { n: Math.round(mins / 1440) });
  };
  const rarityClass = r => "rarity-" + String(r || "").toLowerCase();
  const kindName = k => t("kind_" + ({ normal: "regular", regular: "regular", golden: "golden" }[String(k).toLowerCase()] || "unknown"));
  const setName = s => String(s || "").replace(/_/g, " ").toLowerCase().replace(/\b\w/g, c => c.toUpperCase());
  const modeName = g => { if (!g) return "?"; const m = g.replace(/^GT_/, "").toLowerCase().replace(/_/g, " "); return ({ "vs ai": t("type_ai") }[m] || m); };
  const formatName = f => ({ FT_STANDARD: "Standard", FT_WILD: "Wild", FT_CLASSIC: "Classic", FT_TWIST: "Twist" }[f] || (f ? f : "?"));

  // The account shown: the most recently exported one unless the user picked another in the header.
  let account = (function () { try { return localStorage.getItem("dp.account") || ""; } catch (e) { return ""; } })();
  const withAccount = path => (account ? path + (path.includes("?") ? "&" : "?") + "account=" + encodeURIComponent(account) : path);

  async function api(path, options) {
    const res = await fetch(path, Object.assign({ headers: { "Content-Type": "application/json" } }, options || {}));
    let body = null;
    try { body = await res.json(); } catch (e) { body = null; }
    if (!res.ok) throw new Error((body && body.error) || (res.status + " " + res.statusText));
    return body;
  }
  const post = (path, body) => api(path, { method: "POST", body: JSON.stringify(body || {}) });
  const put = (path, body, raw) => api(path, { method: "PUT", body: raw ? body : JSON.stringify(body || {}) });
  const del = path => api(path, { method: "DELETE" });

  function toast(message, kind) {
    const box = document.createElement("div");
    box.className = "toast" + (kind ? " " + kind : "");
    box.textContent = message;
    $("#toasts").appendChild(box);
    setTimeout(() => box.remove(), kind === "error" ? 9000 : 6000);
  }
  function busy(button, on, label) {
    if (!button) return;
    if (on) { button.dataset.label = button.textContent; button.textContent = label || t("deck_working"); button.disabled = true; }
    else { button.textContent = button.dataset.label || button.textContent; button.disabled = false; }
  }
  const notice = (text, kind) => '<div class="notice ' + (kind || "") + '">' + esc(text) + "</div>";

  // ---------- routing ------------------------------------------------------------------------------------------------
  const pages = { home: renderHome, deck: renderDeck, history: renderHistory, games: renderGames, settings: renderSettings };
  function route() {
    const hash = location.hash || "#/home";
    const m = hash.match(/^#\/(\w+)(?:\?(.*))?$/);
    const name = m && pages[m[1]] ? m[1] : "home";
    const params = new URLSearchParams(m && m[2] ? m[2] : "");
    document.querySelectorAll(".tabs a").forEach(a => a.classList.toggle("active", a.dataset.tab === name));
    document.querySelectorAll(".page").forEach(p => { p.hidden = p.id !== "page-" + name; });
    pages[name](params);
  }
  window.addEventListener("hashchange", route);

  // ---------- live state (SSE) ---------------------------------------------------------------------------------------
  let lastSyncAt = null;
  function connectEvents() {
    const live = $("#hdr-live");
    live.className = "live busy"; $("#hdr-live-text").textContent = t("live_connecting");
    const es = new EventSource("/events");
    es.addEventListener("state", ev => {
      live.className = "live on"; $("#hdr-live-text").textContent = t("live_connected");
      let state; try { state = JSON.parse(ev.data); } catch (e) { return; }
      if (state.last_sync && state.last_sync.at !== lastSyncAt) {
        const first = lastSyncAt === null;
        lastSyncAt = state.last_sync.at;
        if (!first && !state.last_sync.error) {
          toast(t("home_sync_fmt", { when: state.last_sync.new_snapshot ? t("home_sync_new") : t("home_sync_known"), events: state.last_sync.events }), "ok");
          if (!$("#page-home").hidden) renderHome();
          if (!$("#page-history").hidden) renderHistory();
        }
      }
    });
    es.addEventListener("kick", ev => {
      let state; try { state = JSON.parse(ev.data); } catch (e) { return; }
      kickState = state;
      const card = $("#kick-status");
      if (card) card.innerHTML = kickStatusHtml(state);
    });
    es.onerror = () => { live.className = "live off"; $("#hdr-live-text").textContent = t("live_disconnected"); };
  }
  let kickState = null;

  // ---------- home ---------------------------------------------------------------------------------------------------
  async function renderHome() {
    const page = $("#page-home");
    page.innerHTML = '<p class="muted">' + t("loading") + "</p>";
    let o;
    try { o = await api(withAccount("/api/overview")); } catch (e) { page.innerHTML = notice(t("error_prefix") + e.message, "error"); return; }

    renderAccountPicker(o);
    const rate = s => (s && s.win_rate != null ? t("home_winrate_fmt", { pct: pct(s.win_rate) }) : t("home_no_games"));
    const rec = s => (s ? t("home_games_fmt", { wins: s.wins, losses: s.losses }) : "—");
    let html = '<div class="stats">';
    html += stat(t("home_dust"), o.collection ? '<span class="dust">' + n(o.collection.dust) + "</span>" : "—", o.export ? t("home_export_age", { age: ago(o.export.exported_at) }) : t("home_export_none"));
    html += stat(t("home_rank"), o.rank ? '<span class="text">' + esc(o.rank.label) + "</span>" : "—", o.rank ? formatName("FT_" + o.rank.format.toUpperCase()) : t("home_no_rank"), true);
    html += stat(t("home_today"), rec(o.today), rate(o.today));
    html += stat(t("home_week"), rec(o.week), rate(o.week));
    html += stat(t("home_session"), rec(o.session), rate(o.session));
    html += "</div>";

    html += "<h2>" + t("home_open_plans") + "</h2>";
    if (o.open_plans.length === 0) html += '<p class="empty">' + t("home_no_open_plans") + ' <a href="#/deck">' + t("home_go_deck") + "</a></p>";
    else html += "<ul>" + o.open_plans.map(p => "<li>" + esc(t("home_plan_row", { id: p.id, deck: p.target_deck_name || "(unnamed)", need: n(p.need), gives: n(p.total_dust) })) + ' <span class="chip ' + p.status + '">' + p.status + "</span></li>").join("") + "</ul>";

    html += "<h2>" + t("home_setup") + "</h2><ul class='todo'>";
    const todo = [];
    todo.push({ ok: !!o.collection, text: o.collection ? t("home_cards_fmt", { distinct: n(o.collection.distinct_cards), regular: n(o.collection.copies.normal || 0), golden: n(o.collection.copies.golden || 0) }) : t("home_export_none") });
    if (o.collection && o.collection.copies.unknown) todo.push({ ok: false, text: t("home_unknown_kind", { n: n(o.collection.copies.unknown) }) });
    todo.push({ ok: o.deck_count > 0 && o.meta_deck_count > 0, text: t("home_decks_fmt", { decks: o.deck_count, meta: o.meta_deck_count }) });
    todo.push({ ok: !!o.last_sync && !o.last_sync.error, text: t("home_last_sync") + ": " + (o.last_sync ? (o.last_sync.error ? o.last_sync.error : t("home_sync_fmt", { when: ago(o.last_sync.at), events: o.last_sync.events })) : t("home_sync_none")) });
    todo.push({ ok: true, text: t("home_catalog", { build: o.catalog.build }) + (o.catalog.warning ? " — " + o.catalog.warning : "") });
    todo.push({ ok: false, text: t("home_warn_packs") });
    o.warnings.forEach(w => todo.push({ ok: false, text: w }));
    html += todo.map(x => '<li class="' + (x.ok ? "done" : "") + '">' + esc(x.text) + "</li>").join("") + "</ul>";
    page.innerHTML = html;
  }
  function stat(label, value, sub, text) { return '<div class="stat"><div class="label">' + esc(label) + '</div><div class="value' + (text ? " text" : "") + '">' + value + '</div><div class="sub">' + esc(sub || "") + "</div></div>"; }

  // ---------- deck ---------------------------------------------------------------------------------------------------
  const deckState = { decks: null, code: "", key: "", need: null, plan: null };
  async function renderDeck(params) {
    const page = $("#page-deck");
    if (!$("#deck-form", page)) {
      page.innerHTML =
        '<div id="deck-form">' +
        '<div class="picker"><label class="field"><span>' + t("deck_pick") + '</span><select id="deck-select"><option value="">' + t("deck_pick_placeholder") + "</option></select></label>" +
        '<label class="field grow"><span>' + t("deck_or_paste") + '</span><input type="text" id="deck-code" placeholder="' + esc(t("deck_paste_placeholder")) + '"></label>' +
        '<button class="btn" id="btn-need">' + t("deck_analyze") + '</button><button class="btn primary" id="btn-plan">' + t("deck_plan") + "</button></div>" +
        '<details><summary>' + t("deck_need_override") + " · " + t("deck_allow_no_decks") + '</summary><div class="row">' +
        '<label class="field"><span>' + t("deck_need_override") + '</span><input type="number" id="deck-need" min="0" step="5" style="width:140px"></label>' +
        '<label class="check"><input type="checkbox" id="deck-nodecks"> ' + t("deck_allow_no_decks") + "</label></div></details>" +
        "</div><div id=\"deck-result\"></div>";
      $("#btn-need").addEventListener("click", () => runDeck(false));
      $("#btn-plan").addEventListener("click", () => runDeck(true));
      $("#deck-select").addEventListener("change", ev => { $("#deck-code").value = ""; deckState.key = ev.target.value; if (ev.target.value) runDeck(true); });
      $("#deck-code").addEventListener("keydown", ev => { if (ev.key === "Enter") runDeck(true); });
      $("#deck-code").addEventListener("input", () => { $("#deck-select").value = ""; deckState.key = ""; });
    }
    if (!deckState.decks) {
      try {
        const d = await api(withAccount("/api/decks"));
        deckState.decks = d.decks;
        const sel = $("#deck-select");
        d.decks.forEach(deck => {
          const opt = document.createElement("option");
          opt.value = deck.key;
          opt.textContent = deck.name + (deck.card_class ? " — " + title(deck.card_class) : "") + (deck.format ? " · " + deck.format : "") + (deck.archived ? " " + t("deck_archived") : "");
          sel.appendChild(opt);
        });
      } catch (e) { toast(t("error_prefix") + e.message, "error"); }
    }
    const code = params.get("code"), key = params.get("key");
    if (code) { $("#deck-code").value = code; $("#deck-select").value = ""; deckState.key = ""; history.replaceState(null, "", "#/deck"); runDeck(true); }
    else if (key) { $("#deck-select").value = key; deckState.key = key; history.replaceState(null, "", "#/deck"); runDeck(true); }
  }
  function currentDeckArgument() {
    const typed = $("#deck-code").value.trim();
    if (typed) return typed;
    return $("#deck-select").value || "";
  }
  async function runDeck(withPlan) {
    const deck = currentDeckArgument();
    const out = $("#deck-result");
    if (!deck) { out.innerHTML = notice(t("deck_pick_placeholder"), "info"); return; }
    const button = withPlan ? $("#btn-plan") : $("#btn-need");
    busy(button, true);
    out.innerHTML = '<p class="muted">' + t("loading") + "</p>";
    try {
      if (withPlan) {
        const needOverride = $("#deck-need").value ? Number($("#deck-need").value) : null;
        const r = await post("/api/plan", { deck, account: account || null, need: needOverride, allow_no_decks: $("#deck-nodecks").checked, save: false });
        deckState.plan = r.plan; deckState.need = r.plan.missing; deckState.lastDeck = deck;
        out.innerHTML = renderReport(r.plan.missing, r.catalog_warning) + renderPlan(r.plan);
        const save = $("#btn-save-plan");
        if (save) save.addEventListener("click", savePlan);
      } else {
        const r = await post("/api/need", { deck, account: account || null });
        deckState.need = r.report; deckState.plan = null;
        out.innerHTML = renderReport(r.report, r.catalog_warning);
      }
    } catch (e) {
      out.innerHTML = notice(t("error_prefix") + e.message, "error");
    } finally {
      busy(button, false);
    }
  }
  async function savePlan() {
    const button = $("#btn-save-plan");
    busy(button, true);
    try {
      const needOverride = $("#deck-need").value ? Number($("#deck-need").value) : null;
      const r = await post("/api/plan", { deck: deckState.lastDeck, account: account || null, need: needOverride, allow_no_decks: $("#deck-nodecks").checked, save: true });
      const id = r.plan.saved_plan_id;
      $("#plan-saved").innerHTML = id ? notice(t("plan_saved", { id }), "ok") : "";
      if (id) toast(t("plan_saved", { id }), "ok");
      button.hidden = !!id;
    } catch (e) { toast(t("error_prefix") + e.message, "error"); }
    finally { busy(button, false); }
  }
  function cardCell(c) { return '<span class="' + rarityClass(c.rarity) + '">' + esc(c.name) + "</span>" + (c.in_sideboard ? ' <span class="faint">(' + t("sideboard") + ")</span>" : ""); }
  function renderReport(r, catalogWarning) {
    let html = '<div class="deck-meta"><b>' + esc(r.deck_name || "(unnamed deck)") + "</b> · " + esc(t("deck_header_fmt", { class: title(r.deck_class), format: r.format, cards: r.total_cards })) + "</div>";
    if (catalogWarning) html += notice(catalogWarning, "info");

    // The gap: what you have, what the plan would add, what is still missing.
    const have = r.dust_available, total = r.total_craft_cost, short = r.shortfall;
    const planGives = deckState.plan && deckState.plan.main ? deckState.plan.main.total_dust : 0;
    const scale = Math.max(total, have + planGives, 1);
    const haveW = Math.min(100, (have / scale) * 100), planW = Math.min(100 - haveW, (planGives / scale) * 100);
    html += '<div class="gap"><div class="figures">' +
      '<div><div class="big ' + (short > 0 ? "short" : "ok") + '">' + (short > 0 ? n(short) : "✓") + '</div><div class="caption">' + (short > 0 ? t("deck_still_needed") : t("deck_complete")) + "</div></div>" +
      '<div><div class="big">' + n(have) + '</div><div class="caption">' + t("deck_have") + "</div></div>" +
      '<div><div class="big gold">' + n(total) + '</div><div class="caption">' + t("deck_total") + "</div></div>" +
      (planGives ? '<div><div class="big dust">' + n(planGives) + '</div><div class="caption">' + t("deck_plan_gives") + "</div></div>" : "") +
      '</div><div class="bar"><span class="have" style="width:' + haveW + '%"></span><span class="plan" style="left:' + haveW + "%;width:" + planW + '%"></span></div>' +
      '<div class="legend"><span><i style="background:var(--dust)"></i>' + t("deck_legend_have") + "</span>" + (planGives ? '<span><i class="plan" style="background:var(--dust-dim)"></i>' + t("deck_legend_plan") + "</span>" : "") + '<span><i style="background:var(--raised)"></i>' + t("deck_legend_gap") + "</span></div></div>";

    html += "<h2>" + t("missing_title") + "</h2>";
    if (r.missing.length === 0) html += '<p class="empty">' + t("missing_none") + "</p>";
    else {
      html += "<table><thead><tr><th>" + t("col_card") + "</th><th>" + t("col_rarity") + "</th><th>" + t("col_class") + "</th><th>" + t("col_set") + '</th><th class="num">' + t("col_copies") + '</th><th class="num">' + t("col_dust") + "</th></tr></thead><tbody>";
      html += r.missing.map(c => "<tr><td>" + cardCell(c) + "</td><td>" + title(c.rarity) + "</td><td>" + title(c.card_class) + "</td><td>" + esc(setName(c.set)) + '</td><td class="num">' + c.missing + '</td><td class="num gold">' + n(c.craft_cost_missing) + "</td></tr>").join("");
      html += '</tbody><tfoot><tr><td colspan="5" class="total">' + t("col_total") + '</td><td class="num total gold">' + n(total) + "</td></tr></tfoot></table>";
    }
    if (r.not_craftable.length) {
      html += "<h3>" + t("not_craftable_title") + '</h3><p class="why">' + t("not_craftable_hint") + "</p><table><tbody>" +
        r.not_craftable.map(c => "<tr><td>" + cardCell(c) + " ×" + c.missing + "</td><td>" + esc(setName(c.set)) + '</td><td class="why">' + esc(c.not_craftable_reason || "") + "</td></tr>").join("") + "</tbody></table>";
    }
    if (r.unknown_craftability.length) {
      html += "<h3>" + t("unknown_title") + '</h3><p class="why">' + t("unknown_hint") + "</p><table><tbody>" +
        r.unknown_craftability.map(c => "<tr><td>" + cardCell(c) + " ×" + c.missing + "</td><td>" + esc(setName(c.set)) + "</td></tr>").join("") + "</tbody></table>";
    }
    if (r.companions.length) {
      html += "<h3>" + t("companions_title") + "</h3><ul>" + r.companions.map(c => "<li>" + esc(t("companions_fmt", { name: c.name, count: c.count, parent: c.parent_name })) + "</li>").join("") + "</ul>";
    }
    if (r.unknown_cards.length) {
      html += "<h3>" + t("unknown_ids_title") + '</h3><p class="why">' + t("unknown_ids_hint") + "</p><p>" + r.unknown_cards.map(c => "dbfId " + c.dbf_id + " ×" + c.count).join(", ") + "</p>";
    }
    if (r.rotation && r.rotation.cards.length) {
      html += "<details><summary>" + esc(t("rotation_title", { when: r.rotation.next_rotation })) + " · " + esc(t("rotation_risk", { dust: n(r.rotation.dust_at_risk) })) + "</summary><table><tbody>" +
        r.rotation.cards.map(c => "<tr><td>" + cardCell(c) + " ×" + c.needed + "</td><td>" + esc(setName(c.set)) + '</td><td class="why">' + (c.missing === 0 ? t("rotation_owned") : (c.craft_cost_missing != null ? t("rotation_craft", { dust: n(c.craft_cost_missing) }) : t("rotation_not_craftable"))) + "</td></tr>").join("") + "</tbody></table></details>";
    }
    if (r.warnings.length) html += r.warnings.map(w => notice(w)).join("");
    return html;
  }
  function picksTable(result) {
    let html = "<table><thead><tr><th>#</th><th>" + t("col_card") + "</th><th>" + t("col_kind") + '</th><th class="num">' + t("col_copies") + '</th><th class="num">' + t("col_dust") + '</th><th class="num">' + t("col_pain") + "</th><th>" + t("col_why") + "</th></tr></thead><tbody>";
    html += result.picks.map((p, i) => "<tr><td>" + (i + 1) + "</td><td>" + cardCell(p) + ' <span class="faint">(' + title(p.rarity) + ", " + title(p.card_class) + ')</span></td><td class="kind-' + p.kind + '">' + kindName(p.kind) + '</td><td class="num">' + p.copies + '</td><td class="num dust">' + n(p.dust_total) + '</td><td class="num">' + p.pain_each * p.copies + '</td><td class="why">' + esc(p.reasons.join(" · ")) + "</td></tr>").join("");
    return html + "</tbody></table>";
  }
  function renderPlan(p) {
    let html = "<h2>" + t("plan_title") + "</h2>";
    if (p.warnings.length) html += p.warnings.map(w => notice(w)).join("");
    if (!p.has_plan) {
      html += "<h3>" + t("plan_none_title") + "</h3>";
      if (p.need <= 0) html += notice(t("plan_already_have"), "ok");
      else html += "<ul>" + p.blockers.map(b => "<li>" + esc(b) + "</li>").join("") + "</ul>";
    } else {
      const m = p.main;
      html += '<p class="muted">' + esc(t("plan_summary", { need: n(p.need), gives: n(m.total_dust), surplus: n(m.surplus), pain: m.total_pain, copies: count("copies", m.total_copies), decks: count("saved_decks", p.protected_deck_count) })) + "</p>";
      html += picksTable(m);
      html += '<div class="row"><button class="btn primary" id="btn-save-plan">' + t("plan_save") + '</button><span class="why">' + t("plan_never") + '</span></div><div id="plan-saved"></div>';
      p.alternatives.forEach(a => {
        html += "<details><summary>" + esc(t("plan_alt", { label: a.label, gives: n(a.result.total_dust), pain: a.result.total_pain, copies: count("copies", a.result.total_copies) })) + "</summary>" + picksTable(a.result) + "</details>";
      });
    }
    if (p.notable_exclusions.length) {
      html += "<details><summary>" + t("plan_excluded") + "</summary><table><tbody>" +
        p.notable_exclusions.map(e => "<tr><td>" + esc(e.name || e.dbf_id) + '</td><td class="kind-' + e.kind + '">' + kindName(e.kind) + " ×" + e.copies + '</td><td class="num dust">' + (e.dust_each != null ? n(e.dust_each * e.copies) : "—") + '</td><td class="why">' + esc(e.reason) + "</td></tr>").join("") + "</tbody></table></details>";
    }
    return html;
  }

  // ---------- history ------------------------------------------------------------------------------------------------
  async function renderHistory() {
    const page = $("#page-history");
    page.innerHTML = '<p class="muted">' + t("loading") + "</p>";
    let h;
    try { h = await api(withAccount("/api/history")); } catch (e) { page.innerHTML = notice(t("error_prefix") + e.message, "error"); return; }
    let html = "";
    if (h.snapshots.length) html += '<p class="muted">' + esc(t("hist_snapshots", { n: h.snapshots.length, when: when(h.snapshots[0].taken_at), dust: n(h.snapshots[0].dust) })) + "</p>";
    html += "<h2>" + t("hist_plans") + "</h2>";
    if (h.plans.length === 0) html += '<p class="empty">' + t("hist_none") + "</p>";
    else {
      html += "<table><thead><tr><th>#</th><th>" + t("col_created") + "</th><th>" + t("col_target") + '</th><th class="num">' + t("col_need") + '</th><th class="num">' + t("col_gives") + "</th><th>" + t("col_status") + "</th><th>" + t("col_picks") + "</th></tr></thead><tbody>";
      html += h.plans.map(x => {
        const p = x.plan, done = x.picks.reduce((s, k) => s + k.matched_copies, 0), total = x.picks.reduce((s, k) => s + k.copies, 0);
        return "<tr><td>" + p.id + "</td><td>" + when(p.created_at) + "</td><td>" + esc(p.target_deck_name || "(unnamed)") + '</td><td class="num">' + n(p.need) + '</td><td class="num dust">' + n(p.total_dust) + '</td><td><span class="chip ' + p.status + '">' + p.status + "</span></td><td>" +
          "<details><summary>" + esc(t("hist_picks_fmt", { matched: done, copies: total })) + "</summary>" + x.picks.map(k => '<div class="why">' + esc(k.name) + " · " + kindName(k.kind) + " ×" + k.copies + " · " + n(k.dust_each * k.copies) + (k.matched_copies ? " ✓" + k.matched_copies : "") + "</div>").join("") + "</details></td></tr>";
      }).join("") + "</tbody></table>";
    }
    html += "<h2>" + t("hist_events") + "</h2>";
    if (h.events.length === 0) html += '<p class="empty">' + t("hist_no_events") + "</p>";
    else {
      html += "<table><thead><tr><th>" + t("col_detected") + "</th><th>" + t("col_change") + "</th><th>" + t("col_card") + "</th><th>" + t("col_kind") + '</th><th class="num">' + t("col_copies") + '</th><th class="num">' + t("col_dust") + "</th><th>" + t("col_plan") + "</th><th>" + t("col_confidence") + "</th></tr></thead><tbody>";
      html += h.events.map(e => "<tr><td>" + when(e.detected_at) + "</td><td>" + esc(e.event_kind) + "</td><td>" + esc(e.card_name) + "</td><td>" + kindName(e.kind) + '</td><td class="num">' + Math.abs(e.delta) + '</td><td class="num dust">' + (e.dust_estimate != null ? n(e.dust_estimate) : "—") + "</td><td>" + (e.plan_id ? "#" + e.plan_id : "—") + "</td><td>" + esc(e.confidence) + "</td></tr>").join("") + "</tbody></table>";
    }
    page.innerHTML = html;
  }

  // ---------- games --------------------------------------------------------------------------------------------------
  const gamesFilter = { days: 7, type: "", format: "" };
  async function renderGames() {
    const page = $("#page-games");
    if (!$("#games-filters", page)) {
      page.innerHTML = '<div class="row spread"><h2>' + t("games_title") + '</h2><button class="btn" id="btn-import">' + t("games_import") + "</button></div>" +
        '<div class="row" id="games-filters">' +
        '<label class="field"><span>' + t("games_period") + '</span><select id="g-days"><option value="1">' + t("period_today") + '</option><option value="7" selected>' + t("period_7") + '</option><option value="30">' + t("period_30") + '</option><option value="">' + t("period_all") + "</option></select></label>" +
        '<label class="field"><span>' + t("games_type") + '</span><select id="g-type"><option value="">' + t("type_all") + '</option><option value="GT_RANKED">' + t("type_ranked") + '</option><option value="GT_CASUAL">' + t("type_casual") + '</option><option value="GT_ARENA">' + t("type_arena") + '</option><option value="GT_VS_AI">' + t("type_ai") + "</option></select></label>" +
        '<label class="field"><span>' + t("games_format") + '</span><select id="g-format"><option value="">' + t("format_all") + '</option><option value="FT_STANDARD">' + t("format_standard") + '</option><option value="FT_WILD">' + t("format_wild") + "</option></select></label>" +
        '</div><div id="games-result"></div>';
      ["#g-days", "#g-type", "#g-format"].forEach(sel => $(sel).addEventListener("change", loadGames));
      $("#btn-import").addEventListener("click", async () => {
        const b = $("#btn-import"); busy(b, true);
        try { const r = await post("/api/games/import", {}); toast(t("games_imported", { files: r.files_read, parsed: r.games_parsed, inserted: r.games_inserted }), "ok"); r.warnings.forEach(w => toast(w)); loadGames(); }
        catch (e) { toast(t("error_prefix") + e.message, "error"); }
        finally { busy(b, false); }
      });
    }
    loadGames();
  }
  async function loadGames() {
    const out = $("#games-result");
    out.innerHTML = '<p class="muted">' + t("loading") + "</p>";
    const days = $("#g-days").value, type = $("#g-type").value, format = $("#g-format").value;
    const q = new URLSearchParams();
    if (days) q.set("days", days);
    if (type) q.set("type", type);
    if (format) q.set("format", format);
    q.set("recent", "30");
    let g;
    try { g = await api(withAccount("/api/games?" + q.toString())); } catch (e) { out.innerHTML = notice(t("error_prefix") + e.message, "error"); return; }
    if (!g.account_key) { out.innerHTML = '<p class="empty">' + t("games_none") + "</p>"; return; }
    const o = g.overall;
    let html = "<p><b>" + esc(t("games_overall", { games: o.games, wins: o.wins, losses: o.losses, ties: o.ties, unknown: o.unknown })) + "</b> · " + (o.win_rate != null ? t("games_winrate", { pct: pct(o.win_rate) }) : t("games_no_rate")) + "</p>";
    if (g.by_class.length) {
      html += "<h3>" + t("games_by_class") + "</h3><table><thead><tr><th>" + t("col_class") + '</th><th class="num">' + t("col_games") + '</th><th class="num">' + t("col_won") + '</th><th class="num">' + t("col_lost") + '</th><th class="num">' + t("col_rate") + "</th></tr></thead><tbody>" +
        g.by_class.map(c => "<tr><td>" + title(c.player_class) + '</td><td class="num">' + c.stats.games + '</td><td class="num win">' + c.stats.wins + '</td><td class="num loss">' + c.stats.losses + '</td><td class="num">' + (c.stats.win_rate != null ? pct(c.stats.win_rate) + "%" : "—") + "</td></tr>").join("") + "</tbody></table>";
    }
    html += "<h3>" + t("games_recent") + "</h3>";
    if (g.recent_games.length === 0) html += '<p class="empty">' + t("games_none") + "</p>";
    else html += "<table><thead><tr><th>" + t("col_started") + "</th><th>" + t("col_mode") + "</th><th>" + t("col_format") + "</th><th>" + t("col_class") + "</th><th>" + t("col_vs") + "</th><th>" + t("col_opponent") + "</th><th>" + t("col_result") + "</th><th>" + t("col_first") + '</th><th class="num">' + t("col_turns") + "</th></tr></thead><tbody>" +
      g.recent_games.map(x => "<tr><td>" + when(x.started_at) + "</td><td>" + esc(modeName(x.game_type)) + "</td><td>" + esc(formatName(x.format)) + "</td><td>" + title(x.player_class) + "</td><td>" + title(x.opponent_class) + "</td><td>" + esc(x.opponent_name || "?") + '</td><td><span class="chip result-' + x.result + '">' + esc(x.result) + "</span></td><td>" + (x.went_first == null ? "?" : (x.went_first ? t("yes") : t("no"))) + '</td><td class="num">' + (x.turns == null ? "?" : x.turns) + "</td></tr>").join("") + "</tbody></table>";
    out.innerHTML = html;
  }

  // ---------- settings -----------------------------------------------------------------------------------------------
  const CLASSES = ["DEATHKNIGHT", "DEMONHUNTER", "DRUID", "HUNTER", "MAGE", "PALADIN", "PRIEST", "ROGUE", "SHAMAN", "WARLOCK", "WARRIOR"];
  async function renderSettings() {
    const page = $("#page-settings");
    page.innerHTML = '<p class="muted">' + t("loading") + "</p>";
    let prefs, meta, overrides, catalog, service, kick;
    try {
      [prefs, meta, overrides, catalog, service, kick] = await Promise.all([api("/api/prefs"), api("/api/meta"), api("/api/cards/overrides"), api("/api/catalog"), api("/api/service"), api("/api/kick/status")]);
    } catch (e) { page.innerHTML = notice(t("error_prefix") + e.message, "error"); return; }
    kickState = kick.state;
    const p = prefs.prefs;
    let html = '<div class="settings-grid"><div>';
    html += "<h2>" + t("set_play") + "</h2>" +
      '<label class="field"><span>' + t("set_format") + '</span><select id="p-format"><option value="standard"' + (p.format === "standard" ? " selected" : "") + '>Standard</option><option value="wild"' + (p.format === "wild" ? " selected" : "") + ">Wild</option></select></label>" +
      '<div class="field" style="margin-top:10px"><span>' + t("set_classes") + '</span><div class="classes">' + CLASSES.map(c => '<label class="check"><input type="checkbox" class="p-class" value="' + c + '"' + (p.classes_played.includes(c) ? " checked" : "") + "> " + title(c) + "</label>").join("") + "</div></div>" +
      '<div class="row"><label class="check"><input type="checkbox" id="p-wild"' + (p.plays_wild ? " checked" : "") + "> " + t("set_plays_wild") + "</label></div>" +
      '<label class="field"><span>' + t("set_golden") + '</span><select id="p-golden"><option value="keep_golden"' + (p.golden_policy === "keep_golden" ? " selected" : "") + ">" + t("golden_keep") + '</option><option value="dust_golden"' + (p.golden_policy === "dust_golden" ? " selected" : "") + ">" + t("golden_dust") + "</option></select></label>" +
      '<div class="row"><label class="check"><input type="checkbox" id="p-require"' + (p.require_known_decks ? " checked" : "") + "> " + t("set_require_decks") + "</label></div>" +
      '<label class="field"><span>' + t("set_max_age") + '</span><input type="number" id="p-age" min="1" value="' + p.max_snapshot_age_days + '" style="width:110px"></label>' +
      '<div class="row"><button class="btn primary" id="btn-prefs">' + t("set_save") + "</button></div>";
    html += "<h2>" + t("set_meta") + '</h2><p class="why">' + t("set_meta_hint") + '</p><textarea id="meta-text">' + esc(meta.text) + "</textarea>" +
      '<div class="row"><button class="btn primary" id="btn-meta">' + t("set_meta_save") + '</button><span class="muted" id="meta-count">' + esc(t("set_meta_count", { n: meta.deck_count })) + "</span></div>" + meta.warnings.map(w => notice(w)).join("");
    html += "</div><div>";
    html += "<h2>" + t("set_blocks") + '</h2><p class="why">' + t("set_blocks_hint") + "</p>" +
      '<div class="row"><div class="search grow"><input type="text" id="block-q" placeholder="' + esc(t("set_block_search")) + '" autocomplete="off"><ul id="block-list" hidden></ul></div>' +
      '<label class="check"><input type="checkbox" id="block-golden"> ' + t("set_block_golden") + '</label>' +
      '<button class="btn" id="btn-block">' + t("set_block") + '</button><button class="btn" id="btn-allow">' + t("set_unblock_forced") + "</button></div>" +
      "<h3>" + t("set_overrides") + '</h3><div id="overrides">' + overridesTable(overrides.overrides) + "</div>";
    html += "<h2>" + t("set_data") + '</h2><p class="muted" id="catalog-line">' + esc(t("set_catalog_fmt", { build: catalog.build, cards: n(catalog.cards), locale: catalog.locale })) + "</p>" + (catalog.warning ? notice(catalog.warning) : "") +
      '<div class="row"><button class="btn" id="btn-catalog">' + t("set_catalog_update") + "</button></div>";
    html += "<h2>" + t("set_language") + '</h2><div class="row"><select id="p-lang"><option value="en"' + (lang === "en" ? " selected" : "") + ">" + t("lang_en") + '</option><option value="es"' + (lang === "es" ? " selected" : "") + ">" + t("lang_es") + "</option></select></div>";
    html += "<h2>" + t("set_service") + '</h2><div class="kv"><span>' + t("path_service") + "</span><span>" + esc(service.name + " " + service.version + " · " + service.url) + "</span><span>" + t("path_home") + "</span><span><code>" + esc(service.home) + "</code></span><span>" + t("path_prefs") + "</span><span><code>" + esc(prefs.path) + "</code></span></div>";
    html += "</div></div>";
    // Full width below the grid: the command table needs the room.
    html += chatSettings(p.chat || {}, service) + '<div class="row"><button class="btn primary" id="btn-prefs-chat">' + t("set_save") + "</button></div>";
    html += kickSettings(p.kick || {}, kick);
    page.innerHTML = html;
    $("#btn-prefs-chat").addEventListener("click", () => $("#btn-prefs").click());
    $("#btn-prefs-kick").addEventListener("click", () => $("#btn-prefs").click());
    $("#btn-kick-connect").addEventListener("click", async () => {
      const b = $("#btn-kick-connect"); busy(b, true);
      try {
        const r = await post("/api/kick/connect", {});
        const w = window.open(r.connect_url, "_blank", "noopener");
        if (!w) toast(t("kick_popup_blocked"), "error");
        $("#kick-manual").hidden = false;
      } catch (e) { toast(t("error_prefix") + e.message, "error"); } finally { busy(b, false); }
    });
    $("#btn-kick-pair").addEventListener("click", async () => {
      const b = $("#btn-kick-pair"); busy(b, true);
      try { await post("/api/kick/pair", { code: $("#kick-code").value }); $("#kick-code").value = ""; toast(t("kick_paired"), "ok"); }
      catch (e) { toast(t("error_prefix") + e.message, "error"); } finally { busy(b, false); }
    });
    $("#btn-kick-disconnect").addEventListener("click", async () => {
      if (!confirm(t("kick_confirm_disconnect"))) return;
      const b = $("#btn-kick-disconnect"); busy(b, true);
      try { await post("/api/kick/disconnect", {}); toast(t("kick_disconnected"), "ok"); }
      catch (e) { toast(t("error_prefix") + e.message, "error"); } finally { busy(b, false); }
    });

    $("#btn-prefs").addEventListener("click", async () => {
      const b = $("#btn-prefs"); busy(b, true);
      const body = Object.assign({}, p, {
        format: $("#p-format").value,
        classes_played: Array.from(document.querySelectorAll(".p-class:checked")).map(x => x.value),
        plays_wild: $("#p-wild").checked,
        golden_policy: $("#p-golden").value,
        require_known_decks: $("#p-require").checked,
        max_snapshot_age_days: Number($("#p-age").value) || p.max_snapshot_age_days,
        chat: readChatSettings(p.chat || {}),
        kick: readKickSettings(p.kick || {}),
      });
      try { const r = await put("/api/prefs", body); p.chat = r.prefs.chat; p.kick = r.prefs.kick; toast(t("set_saved"), "ok"); } catch (e) { toast(t("error_prefix") + e.message, "error"); } finally { busy(b, false); }
    });
    $("#btn-meta").addEventListener("click", async () => {
      const b = $("#btn-meta"); busy(b, true);
      try { const r = await put("/api/meta", { text: $("#meta-text").value }); $("#meta-count").textContent = t("set_meta_count", { n: r.deck_count }); r.warnings.forEach(w => toast(w)); toast(t("set_meta_saved", { n: r.deck_count }), r.warnings.length ? "" : "ok"); }
      catch (e) { toast(t("error_prefix") + e.message, "error"); } finally { busy(b, false); }
    });
    $("#btn-catalog").addEventListener("click", async () => {
      const b = $("#btn-catalog"); busy(b, true);
      try { const r = await post("/api/catalog/update", {}); $("#catalog-line").textContent = t("set_catalog_fmt", { build: r.build, cards: n(r.cards), locale: r.locale }); toast(t("set_catalog_updated", { build: r.build }), "ok"); deckState.decks = null; }
      catch (e) { toast(t("error_prefix") + e.message, "error"); } finally { busy(b, false); }
    });
    $("#p-lang").addEventListener("change", ev => { lang = ev.target.value; try { localStorage.setItem("dp.lang", lang); } catch (e) { /* ignore */ } applyStaticStrings(); deckState.decks = null; $("#page-deck").innerHTML = ""; $("#page-games").innerHTML = ""; route(); });

    // card search for blocking
    let searchTimer = null, picked = null;
    const q = $("#block-q"), list = $("#block-list");
    q.addEventListener("input", () => {
      picked = null;
      clearTimeout(searchTimer);
      const text = q.value.trim();
      if (text.length < 2) { list.hidden = true; return; }
      searchTimer = setTimeout(async () => {
        try {
          const r = await api("/api/cards/search?q=" + encodeURIComponent(text) + "&limit=12");
          list.innerHTML = r.cards.map(c => '<li data-dbf="' + c.dbf_id + '" data-name="' + esc(c.name) + '"><span class="' + rarityClass(c.rarity) + '">' + esc(c.name) + '</span><span class="faint">' + esc(setName(c.set)) + " · " + c.dbf_id + "</span></li>").join("");
          list.hidden = r.cards.length === 0;
        } catch (e) { list.hidden = true; }
      }, 180);
    });
    list.addEventListener("click", ev => {
      const li = ev.target.closest("li");
      if (!li) return;
      picked = { dbf: li.dataset.dbf, name: li.dataset.name };
      q.value = li.dataset.name; list.hidden = true;
    });
    document.addEventListener("click", ev => { if (!ev.target.closest(".search")) list.hidden = true; });
    async function applyOverride(uncraftable) {
      const card = picked ? picked.dbf : q.value.trim();
      if (!card) return;
      const b = uncraftable ? $("#btn-block") : $("#btn-allow"); busy(b, true);
      try {
        const r = await post("/api/cards/overrides", { card, golden: $("#block-golden").checked, uncraftable });
        toast(t(uncraftable ? "set_blocked" : "set_allowed", { name: r.name, kind: kindName(r.kind) }), "ok");
        q.value = ""; picked = null;
        await refreshOverrides();
      } catch (e) { toast(t("error_prefix") + e.message, "error"); } finally { busy(b, false); }
    }
    $("#btn-block").addEventListener("click", () => applyOverride(true));
    $("#btn-allow").addEventListener("click", () => applyOverride(false));
    $("#overrides").addEventListener("click", async ev => {
      const b = ev.target.closest("button[data-remove]");
      if (!b) return;
      if (!confirm(t("confirm_remove"))) return;
      try { await del("/api/cards/overrides/" + b.dataset.remove + "?golden=" + b.dataset.golden); await refreshOverrides(); }
      catch (e) { toast(t("error_prefix") + e.message, "error"); }
    });
    async function refreshOverrides() { const r = await api("/api/cards/overrides"); $("#overrides").innerHTML = overridesTable(r.overrides); }
  }
  const CHAT_COMMANDS = ["deck", "rank", "wr", "last", "dust"];
  const CHAT_PLACEHOLDERS = ["deck_name", "deckstring", "deck_class", "deck_format", "deck_url", "deck_link", "rank", "rank_format", "today_w", "today_l", "today_t", "today_wr", "session_w", "session_l", "session_wr", "streak", "streak_text", "last_result", "player_class", "opponent_class", "turns", "dust", "user"];

  /** The `chat` block of prefs.json as a form: prefix, language, cooldowns and one row per command. */
  function chatSettings(c, service) {
    const cmds = c.commands || {};
    const langSel = (v) => '<select id="c-lang"><option value=""' + (!v ? " selected" : "") + ">" + t("chat_lang_auto") + '</option><option value="es"' + (v === "es" ? " selected" : "") + '>Español</option><option value="en"' + (v === "en" ? " selected" : "") + ">English</option></select>";
    let html = "<h2>" + t("set_chat") + '</h2><p class="why">' + esc(t("set_chat_hint", { url: service.url + "/api/chat/deck" })) + ' <a href="https://github.com/facusantillo/DustPlanner/blob/main/docs/kick-streamerbot.md" target="_blank" rel="noopener">' + t("chat_guide") + "</a></p>" +
      '<div class="row"><label class="field"><span>' + t("chat_prefix") + '</span><input type="text" id="c-prefix" maxlength="3" value="' + esc(c.prefix == null ? "!" : c.prefix) + '" style="width:60px"></label>' +
      '<label class="field"><span>' + t("chat_language") + "</span>" + langSel(c.language) + "</label>" +
      '<label class="field"><span>' + t("chat_cooldown") + '</span><input type="number" id="c-cool" min="0" max="3600" value="' + (c.cooldown_seconds == null ? 10 : c.cooldown_seconds) + '" style="width:90px"></label>' +
      '<label class="field"><span>' + t("chat_user_cooldown") + '</span><input type="number" id="c-ucool" min="0" max="3600" value="' + (c.user_cooldown_seconds == null ? 30 : c.user_cooldown_seconds) + '" style="width:90px"></label></div>' +
      '<table class="chat-commands"><thead><tr><th></th><th>' + t("chat_col_command") + "</th><th>" + t("chat_col_aliases") + "</th><th>" + t("chat_col_template") + "</th></tr></thead><tbody>";
    for (const name of CHAT_COMMANDS) {
      const cmd = cmds[name] || { enabled: name !== "dust", aliases: [], template: null };
      html += '<tr data-cmd="' + name + '"><td><input type="checkbox" class="c-on"' + (cmd.enabled ? " checked" : "") + '></td><td><code>' + esc((c.prefix == null ? "!" : c.prefix) + name) + "</code><br><span class=\"faint\">" + esc(t("chat_cmd_" + name)) + '</span></td>' +
        '<td><input type="text" class="c-aliases" value="' + esc((cmd.aliases || []).join(", ")) + '" placeholder="mazo, deckcode"></td>' +
        '<td><input type="text" class="c-template" maxlength="500" value="' + esc(cmd.template || "") + '" placeholder="' + esc(t("chat_template_default")) + '"></td></tr>';
    }
    html += "</tbody></table>" +
      '<details class="why"><summary>' + t("chat_placeholders") + "</summary><p>" + CHAT_PLACEHOLDERS.map(x => "<code>{" + x + "}</code>").join(" ") + "</p></details>";
    return html;
  }

  /** Settings → Kick: the connection (status, connect / disconnect, manual code) and the opt-in features. */
  function kickSettings(k, kick) {
    const s = kick.state || {};
    const reminder = k.prediction_reminder || "panel";
    return "<h2>" + t("set_kick") + '</h2><p class="why">' + esc(t("set_kick_hint")) + "</p>" +
      '<div id="kick-status">' + kickStatusHtml(s) + "</div>" +
      '<div class="row"><button class="btn primary" id="btn-kick-connect">' + t("kick_connect") + '</button><button class="btn" id="btn-kick-disconnect">' + t("kick_disconnect") + "</button></div>" +
      '<div class="row" id="kick-manual"' + (kick.pairing_pending ? "" : " hidden") + '><label class="field grow"><span>' + t("kick_code") + '</span><input type="text" id="kick-code" autocomplete="off" spellcheck="false"></label><button class="btn" id="btn-kick-pair">' + t("kick_pair") + "</button></div>" +
      "<h3>" + t("kick_features") + '</h3><p class="why">' + esc(t("kick_features_hint")) + "</p>" +
      '<div class="row"><label class="check"><input type="checkbox" id="k-page"' + (k.public_page ? " checked" : "") + "> " + t("kick_public_page") + "</label></div>" +
      '<div class="row"><label class="check"><input type="checkbox" id="k-summary"' + (k.post_stream_summary ? " checked" : "") + "> " + t("kick_summary") + "</label></div>" +
      '<div class="row"><label class="field"><span>' + t("kick_reminder") + '</span><select id="k-reminder"><option value="off"' + (reminder === "off" ? " selected" : "") + ">" + t("kick_reminder_off") + '</option><option value="panel"' + (reminder === "panel" ? " selected" : "") + ">" + t("kick_reminder_panel") + '</option><option value="panel+chat"' + (reminder === "panel+chat" ? " selected" : "") + ">" + t("kick_reminder_chat") + "</option></select></label></div>" +
      '<div class="row"><label class="check"><input type="checkbox" id="k-redemptions"' + (k.show_redemptions ? " checked" : "") + "> " + t("kick_redemptions") + "</label>" +
      '<label class="field grow"><span>' + t("kick_redemption_filter") + '</span><input type="text" id="k-redemption-filter" value="' + esc((k.redemption_filter || []).join(", ")) + '" placeholder="' + esc(t("kick_redemption_filter_hint")) + '"></label></div>' +
      '<div class="row"><button class="btn primary" id="btn-prefs-kick">' + t("set_save") + "</button></div>";
  }

  function kickStatusHtml(s) {
    const status = s.status || "disconnected";
    const label = t("kick_status_" + status) || status;
    const chip = '<span class="chip kick-' + status + '">' + esc(label) + "</span>";
    const who = s.channel && s.channel.slug ? ' <a href="https://kick.com/' + encodeURIComponent(s.channel.slug) + '" target="_blank" rel="noopener">@' + esc(s.channel.slug) + "</a>" : "";
    let lines = "<div>" + chip + who + "</div>";
    if (s.last_error) lines += '<div class="faint">' + esc(s.last_error) + "</div>";
    if (status === "connected" && s.connected_at) lines += '<div class="faint">' + esc(t("kick_since", { when: when(s.connected_at) })) + (s.live && s.live.is_live ? " · " + t("kick_live_now") : "") + "</div>";
    if (s.last_command) lines += '<div class="faint">' + esc(t("kick_last_command", { cmd: s.last_command, when: when(s.last_command_at) })) + "</div>";
    if (status === "connecting" && s.next_retry_at) lines += '<div class="faint">' + esc(t("kick_retry", { when: when(s.next_retry_at) })) + "</div>";
    if (status === "pairing") lines += '<div class="faint">' + esc(t("kick_pairing_hint")) + "</div>";
    return '<div class="kick-card">' + lines + "</div>";
  }

  function readKickSettings(k) {
    return Object.assign({}, k, {
      public_page: $("#k-page").checked,
      post_stream_summary: $("#k-summary").checked,
      prediction_reminder: $("#k-reminder").value,
      show_redemptions: $("#k-redemptions").checked,
      redemption_filter: $("#k-redemption-filter").value.split(",").map(x => x.trim()).filter(Boolean),
    });
  }

  function readChatSettings(c) {
    const commands = {};
    document.querySelectorAll("tr[data-cmd]").forEach(row => {
      const name = row.dataset.cmd;
      const tpl = $(".c-template", row).value.trim();
      commands[name] = { enabled: $(".c-on", row).checked, aliases: $(".c-aliases", row).value.split(",").map(x => x.trim()).filter(Boolean), template: tpl || null };
    });
    return Object.assign({}, c, {
      prefix: $("#c-prefix").value,
      language: $("#c-lang").value || null,
      commands,
      cooldown_seconds: Number($("#c-cool").value),
      user_cooldown_seconds: Number($("#c-ucool").value),
    });
  }

  function overridesTable(list) {
    if (!list.length) return '<p class="empty">' + t("set_no_overrides") + "</p>";
    return "<table><thead><tr><th>" + t("col_card") + "</th><th>" + t("col_kind") + "</th><th>" + t("col_your_rule") + "</th><th>" + t("col_shipped_rule") + "</th><th></th></tr></thead><tbody>" +
      list.map(o => "<tr><td>" + esc(o.name || o.dbf_id) + ' <span class="faint">' + esc(setName(o.set)) + "</span></td><td>" + kindName(o.kind) + "</td><td>" + (o.uncraftable ? t("rule_blocked") : t("rule_allowed")) + '</td><td class="why">' + esc(o.shipped_rule || "") + '</td><td class="right"><button class="btn small danger" data-remove="' + o.dbf_id + '" data-golden="' + (o.kind === "golden") + '">' + t("remove") + "</button></td></tr>").join("") + "</tbody></table>";
  }

  // ---------- header sync button -------------------------------------------------------------------------------------
  $("#btn-sync").addEventListener("click", async () => {
    const b = $("#btn-sync"); busy(b, true, t("syncing"));
    try {
      const r = await post("/api/sync", { account: account || null });
      const s = r.sync;
      toast(t("home_sync_fmt", { when: s.new_snapshot ? t("home_sync_new") : t("home_sync_known"), events: s.events }), "ok");
      lastSyncAt = s.at;
      if (!$("#page-home").hidden) renderHome();
      if (!$("#page-history").hidden) renderHistory();
    } catch (e) { toast(t("error_prefix") + e.message, "error"); }
    finally { busy(b, false); }
  });

  async function loadHeader() {
    try {
      const o = await api(withAccount("/api/overview"));
      renderAccountPicker(o);
    } catch (e) { /* the page shows the error itself */ }
  }
  function renderAccountPicker(o) {
    const box = $("#hdr-account");
    const current = o.account ? (o.account.battle_tag || o.account.account_key) : "";
    if (!o.accounts || o.accounts.length < 2) { box.textContent = current; return; }
    const sel = document.createElement("select");
    sel.className = "account-select";
    o.accounts.forEach(a => {
      const opt = document.createElement("option");
      opt.value = a.account_key; opt.textContent = a.battle_tag || a.account_key;
      opt.selected = o.account && a.account_key === o.account.account_key;
      sel.appendChild(opt);
    });
    sel.addEventListener("change", () => {
      account = sel.value;
      try { localStorage.setItem("dp.account", account); } catch (e) { /* ignore */ }
      deckState.decks = null; $("#page-deck").innerHTML = "";
      route();
    });
    box.textContent = ""; box.appendChild(sel);
  }

  applyStaticStrings();
  connectEvents();
  loadHeader();
  route();
})();
