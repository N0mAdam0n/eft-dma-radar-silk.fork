/* ═══════════════════════════════════════════════════════════════════════════
   EFT WebRadar — Canvas-based radar with HTTP polling
   Modern UI — Map + Players
   ═══════════════════════════════════════════════════════════════════════════ */

const canvas = document.getElementById("radar");
const ctx    = canvas.getContext("2d", { alpha: true });

const aimviewEl     = document.getElementById("aimviewWidget");
const aimviewCanvas = document.getElementById("aimviewCanvas");
const avCtx         = aimviewCanvas.getContext("2d", { alpha: true });

const statusEl    = document.getElementById("status");
const statusLabel = statusEl.querySelector(".label");
const subline     = document.getElementById("subline");
const sidebar     = document.getElementById("sidebar");
const toggle      = document.getElementById("toggle");
const menuBtn     = document.getElementById("menuBtn");
const edgeZone    = document.getElementById("edgeZone");
const tooltipEl   = document.getElementById("tooltip");
const playerCountsEl = document.getElementById("playerCounts");

let dpr = window.devicePixelRatio || 1;
let cw = 0, ch = 0;

const ZOOM_MIN = 0.05;
const ZOOM_MAX = 4.0;

/* ═══════════════════════════════════════════════════════════════════════════
   SIDEBAR
   ═══════════════════════════════════════════════════════════════════════════ */
let sidebarTempOpen = false;
let sidebarCloseTimer = null;

function isSidebarOpen() { return !sidebar.classList.contains("collapsed"); }

function setSidebarCollapsed(collapsed, temp = false) {
  sidebar.classList.toggle("collapsed", collapsed);
  sidebarTempOpen = (!collapsed && temp);
  toggle.textContent = collapsed ? ">" : "<";
}
function toggleSidebarPinned() {
  const nowOpen = !isSidebarOpen();
  setSidebarCollapsed(!nowOpen, false);
  state.sidebarCollapsed = !nowOpen;
  saveSettings();
}

toggle.onclick = () => toggleSidebarPinned();
menuBtn.onclick = () => toggleSidebarPinned();

edgeZone.addEventListener("mouseenter", () => {
  if (!state.hoverOpenSidebar || isSidebarOpen()) return;
  if (state.sidebarCollapsed) setSidebarCollapsed(false, true);
});
sidebar.addEventListener("mouseenter", () => clearTimeout(sidebarCloseTimer));
sidebar.addEventListener("mouseleave", () => {
  if (!sidebarTempOpen) return;
  clearTimeout(sidebarCloseTimer);
  sidebarCloseTimer = setTimeout(() => {
    if (sidebarTempOpen && state.sidebarCollapsed) setSidebarCollapsed(true, false);
  }, 250);
});

/* ═══════════════════════════════════════════════════════════════════════════
   TABS
   ═══════════════════════════════════════════════════════════════════════════ */
function activateTab(tabId) {
  document.querySelectorAll(".tab,.tab-content").forEach(e => e.classList.remove("active"));
  document.querySelectorAll(`.tab[data-tab='${tabId}']`).forEach(e => e.classList.add("active"));
  const content = document.getElementById(tabId);
  if (content) content.classList.add("active");
}
document.querySelectorAll(".tab").forEach(tab => tab.onclick = () => activateTab(tab.dataset.tab));

/* ═══════════════════════════════════════════════════════════════════════════
   CANVAS RESIZE
   ═══════════════════════════════════════════════════════════════════════════ */
function resizeCanvas() {
  dpr = window.devicePixelRatio || 1;
  const rect = canvas.getBoundingClientRect();
  cw = Math.max(1, rect.width);
  ch = Math.max(1, rect.height);
  const bw = Math.max(1, Math.round(cw * dpr));
  const bh = Math.max(1, Math.round(ch * dpr));
  if (canvas.width !== bw) canvas.width = bw;
  if (canvas.height !== bh) canvas.height = bh;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
}
window.addEventListener("resize", resizeCanvas);
resizeCanvas();

/* ═══════════════════════════════════════════════════════════════════════════
   PERSISTENCE
   ═══════════════════════════════════════════════════════════════════════════ */
const LS_KEY = "eft_webradar_settings_v1";

function deepClone(obj) {
  try { return structuredClone(obj); } catch { return JSON.parse(JSON.stringify(obj)); }
}

const defaults = {
  __savedAt: 0,
  // Phase 5: bottom shell is the primary mobile-first UI. Start with the
  // legacy right sidebar collapsed; users reach it from Settings → Advanced.
  sidebarCollapsed: true,
  hoverOpenSidebar: true,

  // Active web-client preset (independent of the desktop host's preset).
  // Each buddy chooses their own view. "Custom" = user-tweaked, no preset.
  activeWebPreset: "Custom",

  showMap: true,
  useSatellite: false,
  zoom: 1.0,
  rotateWithLocal: false,
  pollMs: 50,
  freeMode: false,

  showPlayers: true,
  showAim: true,
  showNames: false,
  showHeight: true,
  showGroups: true,
  playerSize: 6,

  showLoot: true,
  showLootNames: true,
  lootMinPrice: 50000,
  lootMaxDist: 0,
  lootSearch: "",
  lootMode: "all", // all | important | rare | wishlist | quest
  lootHideNormal: false,

  // Buddy-side loot importance/tier configuration (independent of host).
  // Tier = 0 below threshold, 1 at threshold (Important), 2 at rareMult× (Rare), 3 at topMult× (Top).
  lootImportantPrice: 50000,
  lootRareMult: 2,
  lootTopMult: 5,

  // Buddy wishlist/blacklist: arrays of BSG ids (item.bsgId). Managed via
  // the searchable Wishlist & Blacklist panel in the Loot tab — mirrors the
  // local LootFiltersPanel UX.
  lootWishlist: [],
  lootBlacklist: [],
  itemSearch: "",
  showContainers: true,
  showContainerNames: true,
  containerMaxDist: 0,
  selectedContainers: [],
  showCorpses: true,
  showExfils: true,
  showAimview: false,
  aimviewWidth: 280,
  aimviewHeight: 220,
  aimviewZoom: 1.0,
  aimviewFov: 90,                 // horizontal FOV in degrees (used when aimviewUseFov)
  aimviewUseFov: false,           // when true, derive zoom from FOV instead of slider
  aimviewAspect: "auto",          // "auto" or numeric H/V ratio
  aimviewFollowHostFov: true,     // mirror host's live FOV (reflects ADS iron-sight zoom)
  aimviewScopeZoom: 4.0,          // multiplier applied on top of FOV when host IsScoped
  aimviewShowAimStatus: true,     // overlay HUD text for ADS / SCOPE state
  aimviewEyeHeight: 1.62,
  aimviewBgOpacity: 60,           // 0..100 (%)
  aimviewShowCrosshair: true,
  aimviewShowPlayers: true,
  aimviewHideAI: false,
  aimviewHideDead: true,
  aimviewShowLabels: true,
  aimviewPlayerDist: 300,
  aimviewShowLoot: true,
  aimviewShowContainers: false,
  aimviewShowCorpses: false,
  aimviewLootDist: 100,
  aimviewShowItemLabels: false,
  aimviewMaxLoot: 40,
  aimviewX: null,
  aimviewY: null,
  followTarget: null,

  // World features (live entities from server)
  showSwitches: false,
  showDoors: false,
  showTransits: true,
  showBtr: true,
  showBtrRoute: false,
  showAirdrops: true,

  // Buddy quest tracker (client-side, sourced from /api/questdata)
  showQuestItems: true,       // highlight items belonging to actively tracked quests
  showAllQuestItems: false,   // show every server-flagged static quest item on the current map
  showQuestZones: true,
  questsOnlyActiveMap: true,
  questSearch: "",
  trackedQuests: [], // quest ids the buddy actively tracks

  colors: {
    local:    "#22c55e",
    teammate: "#4ade80",
    pmc:      "#38bdf8",
    scav:     "#f59e0b",
    pscav:    "#facc15",
    raider:   "#fb7185",
    boss:     "#ef4444",
    dead:     "#9ca3af",
    loot:     "#a78bfa",
    lootImportant: "#4ade80",
    lootRare:      "#22d3ee",
    lootTop:       "#f97316",
    lootWishlist:  "#fbbf24",
    lootQuest:     "#ef4444",
    container:     "#60a5fa",
    corpse:        "#9ca3af",
    exfilOpen:     "#4ade80",
    exfilPending:  "#facc15",
    exfilClosed:   "#f87171",

    questItem:   "#ef4444",
    questZone:   "#facc15",
    switch:      "#a78bfa",
    doorLocked:  "#f87171",
    doorOpen:    "#4ade80",
    transit:     "#22d3ee",
    btr:         "#fbbf24",
    airdrop:     "#f97316",
  }
};

let state = deepClone(defaults);

function mergeState(parsed) {
  return {
    ...deepClone(defaults),
    ...parsed,
    colors: { ...deepClone(defaults.colors), ...(parsed.colors || {}) },
    zoom: clamp(Number(parsed.zoom) || 1, ZOOM_MIN, ZOOM_MAX),
    selectedContainers: Array.isArray(parsed.selectedContainers) ? parsed.selectedContainers : [],
    trackedQuests: Array.isArray(parsed.trackedQuests) ? parsed.trackedQuests : [],
    lootWishlist: Array.isArray(parsed.lootWishlist) ? parsed.lootWishlist : [],
    lootBlacklist: Array.isArray(parsed.lootBlacklist) ? parsed.lootBlacklist : [],
  };
}

function loadSettings() {
  try {
    const raw = localStorage.getItem(LS_KEY);
    if (raw) state = mergeState(JSON.parse(raw));
  } catch { /* use defaults */ }
}

function saveSettings() {
  state.__savedAt = Date.now();
  try { localStorage.setItem(LS_KEY, JSON.stringify(state)); } catch { /* ignore */ }
  const badge = document.getElementById("cacheBadge");
  if (badge) {
    badge.textContent = "saved ✓";
    clearTimeout(saveSettings._t);
    saveSettings._t = setTimeout(() => badge.textContent = "cache", 1000);
  }
}

function resetSettings() {
  try { localStorage.removeItem(LS_KEY); } catch { /* ignore */ }
  state = deepClone(defaults);
  freeAnchor = { x: 0, y: 0, mapId: "" };
  bindAllInputs();
  applyUiFromState();
  updateAllRangeValues();
  saveSettings();
}

/* ═══════════════════════════════════════════════════════════════════════════
   INPUT BINDING
   ═══════════════════════════════════════════════════════════════════════════ */
const $ = id => document.getElementById(id);

const inputs = {
  showMap:         $("showMap"),
  useSatellite:    $("useSatellite"),
  zoom:            $("zoom"),
  rotateWithLocal: $("rotateWithLocal"),
  pollMs:          $("pollMs"),
  freeMode:        $("freeMode"),
  centerOnLocal:   $("centerOnLocal"),
  modeBadge:       $("modeBadge"),
  hoverOpenSidebar:$("hoverOpenSidebar"),
  showAimview:     $("showAimview"),
  aimviewZoom:     $("aimviewZoom"),
  aimviewFov:      $("aimviewFov"),
  aimviewUseFov:   $("aimviewUseFov"),
  aimviewAspect:   $("aimviewAspect"),
  aimviewFollowHostFov: $("aimviewFollowHostFov"),
  aimviewScopeZoom: $("aimviewScopeZoom"),
  aimviewShowAimStatus: $("aimviewShowAimStatus"),
  aimviewEyeHeight:$("aimviewEyeHeight"),
  aimviewBgOpacity:$("aimviewBgOpacity"),
  aimviewShowCrosshair: $("aimviewShowCrosshair"),
  aimviewShowPlayers:   $("aimviewShowPlayers"),
  aimviewHideAI:        $("aimviewHideAI"),
  aimviewHideDead:      $("aimviewHideDead"),
  aimviewShowLabels:    $("aimviewShowLabels"),
  aimviewPlayerDist:    $("aimviewPlayerDist"),
  aimviewShowLoot:      $("aimviewShowLoot"),
  aimviewShowContainers:$("aimviewShowContainers"),
  aimviewShowCorpses:   $("aimviewShowCorpses"),
  aimviewLootDist:      $("aimviewLootDist"),
  aimviewShowItemLabels:$("aimviewShowItemLabels"),
  aimviewMaxLoot:       $("aimviewMaxLoot"),

  showPlayers:     $("showPlayers"),
  showAim:         $("showAim"),
  showNames:       $("showNames"),
  showHeight:      $("showHeight"),
  showGroups:      $("showGroups"),
  playerSize:      $("playerSize"),

  showLoot:        $("showLoot"),
  showLootNames:   $("showLootNames"),
  lootMinPrice:    $("lootMinPrice"),
  lootMaxDist:     $("lootMaxDist"),
  lootSearch:      $("lootSearch"),
  lootMode:        $("lootMode"),
  lootHideNormal:  $("lootHideNormal"),
  showContainers:  $('showContainers'),
  showContainerNames: $('showContainerNames'),
  containerMaxDist: $('containerMaxDist'),
  showCorpses:     $('showCorpses'),
  showExfils:      $("showExfils"),

  // World
  showSwitches:    $("showSwitches"),
  showDoors:       $("showDoors"),
  showTransits:    $("showTransits"),
  showBtr:         $("showBtr"),
  showBtrRoute:    $("showBtrRoute"),
  showAirdrops:    $("showAirdrops"),

  // Quests
  showQuestItems:  $("showQuestItems"),
  showAllQuestItems: $("showAllQuestItems"),
  showQuestZones:  $("showQuestZones"),
  questsOnlyActiveMap: $("questsOnlyActiveMap"),
  questSearch:     $("questSearch"),
  questsTrackKappa: $("questsTrackKappa"),
  questsClear:     $("questsClear"),
  questsBadge:     $("questsBadge"),
  questItemColor:  $("questItemColor"),
  questZoneColor:  $("questZoneColor"),
  switchColor:     $("switchColor"),
  doorLockedColor: $("doorLockedColor"),
  doorOpenColor:   $("doorOpenColor"),
  transitColor:    $("transitColor"),
  btrColor:        $("btrColor"),
  airdropColor:    $("airdropColor"),

  localColor:      $("localColor"),
  teammateColor:   $("teammateColor"),
  pmcColor:        $("pmcColor"),
  scavColor:       $("scavColor"),
  pscavColor:      $("pscavColor"),
  raiderColor:     $("raiderColor"),
  bossColor:       $("bossColor"),
  deadColor:       $("deadColor"),
  lootColor:       $("lootColor"),
  lootImportantColor: $("lootImportantColor"),
  lootRareColor:      $("lootRareColor"),
  lootTopColor:       $("lootTopColor"),
  lootWishlistColor:  $("lootWishlistColor"),
  lootQuestColor:     $("lootQuestColor"),
  containerColor:  $("containerColor"),
  corpseColor:     $("corpseColor"),
  exfilOpenColor:  $("exfilOpenColor"),
  exfilPendingColor: $("exfilPendingColor"),
  exfilClosedColor:  $("exfilClosedColor"),

  resetSettings:   $("resetSettings"),

  lootImportantPrice: $("lootImportantPrice"),
  lootRareMult:       $("lootRareMult"),
  lootTopMult:        $("lootTopMult"),
  wishlistClear:      $("wishlistClear"),
  wishlistBadge:      $("wishlistBadge"),
  blacklistClear:     $("blacklistClear"),
  blacklistBadge:     $("blacklistBadge"),
  itemSearch:         $("itemSearch"),
};

/* Range value display elements */
const rangeValueEls = {
  playerSize: $("playerSizeVal"),
  zoom:       $("zoomVal"),
  pollMs:     $("pollMsVal"),
  lootMinPrice: $("lootMinPriceVal"),
  lootMaxDist:  $("lootMaxDistVal"),
  aimviewZoom:     $("aimviewZoomVal"),
  aimviewFov:      $("aimviewFovVal"),
  aimviewScopeZoom: $("aimviewScopeZoomVal"),
  aimviewEyeHeight:$("aimviewEyeHeightVal"),
  aimviewBgOpacity:$("aimviewBgOpacityVal"),
  aimviewPlayerDist:$("aimviewPlayerDistVal"),
  aimviewLootDist: $("aimviewLootDistVal"),
  aimviewMaxLoot:  $("aimviewMaxLootVal"),
  containerMaxDist: $("containerMaxDistVal"),
  lootImportantPrice: $("lootImportantPriceVal"),
  lootRareMult: $("lootRareMultVal"),
  lootTopMult:  $("lootTopMultVal"),
};

function updateRangeValue(key) {
  const el = rangeValueEls[key];
  if (!el) return;
  const v = state[key];
  if (key === "zoom") {
    el.textContent = Number(v).toFixed(2);
  } else if (key === "pollMs") {
    el.innerHTML = v + "<small>ms</small>";
  } else if (key === "lootMinPrice") {
    el.textContent = formatPrice(v);
  } else if (key === "lootImportantPrice") {
    el.textContent = formatPrice(v);
  } else if (key === "lootRareMult") {
    el.textContent = v + "×";
  } else if (key === "lootTopMult") {
    el.textContent = v + "×";
  } else if (key === "lootMaxDist") {
    el.textContent = v <= 0 ? "Off" : v + "m";
  } else if (key === "containerMaxDist") {
    el.textContent = v <= 0 ? "Off" : v + "m";
  } else if (key === "aimviewZoom") {
    el.textContent = Number(v).toFixed(1) + "×";
  } else if (key === "aimviewFov") {
    el.textContent = Math.round(Number(v)) + "°";
  } else if (key === "aimviewScopeZoom") {
    el.textContent = Number(v).toFixed(1) + "×";
  } else if (key === "aimviewEyeHeight") {
    el.textContent = Number(v).toFixed(2);
  } else if (key === "aimviewBgOpacity") {
    el.textContent = v + "%";
  } else if (key === "aimviewPlayerDist" || key === "aimviewLootDist") {
    el.textContent = v + "m";
  } else {
    el.textContent = v;
  }
}

function updateAllRangeValues() {
  for (const key of Object.keys(rangeValueEls)) updateRangeValue(key);
}

function applyUiFromState() {
  setSidebarCollapsed(!!state.sidebarCollapsed, false);
  if (inputs.modeBadge) inputs.modeBadge.textContent = state.freeMode ? "free" : "follow";
}

function bindAllInputs() {
  const bind = (el, key, isColor) => {
    if (!el) return;
    const src = isColor ? state.colors : state;
    if (el.type === "checkbox") el.checked = !!src[key];
    else el.value = src[key] ?? el.value;
  };

  bind(inputs.showMap, "showMap");
  bind(inputs.useSatellite, "useSatellite");
  bind(inputs.zoom, "zoom");
  bind(inputs.rotateWithLocal, "rotateWithLocal");
  bind(inputs.pollMs, "pollMs");
  bind(inputs.freeMode, "freeMode");
  bind(inputs.hoverOpenSidebar, "hoverOpenSidebar");
  bind(inputs.showAimview, "showAimview");
  bind(inputs.aimviewZoom, "aimviewZoom");
  bind(inputs.aimviewFov, "aimviewFov");
  bind(inputs.aimviewUseFov, "aimviewUseFov");
  bind(inputs.aimviewAspect, "aimviewAspect");
  bind(inputs.aimviewFollowHostFov, "aimviewFollowHostFov");
  bind(inputs.aimviewScopeZoom, "aimviewScopeZoom");
  bind(inputs.aimviewShowAimStatus, "aimviewShowAimStatus");
  bind(inputs.aimviewEyeHeight, "aimviewEyeHeight");
  bind(inputs.aimviewBgOpacity, "aimviewBgOpacity");
  bind(inputs.aimviewShowCrosshair, "aimviewShowCrosshair");
  bind(inputs.aimviewShowPlayers, "aimviewShowPlayers");
  bind(inputs.aimviewHideAI, "aimviewHideAI");
  bind(inputs.aimviewHideDead, "aimviewHideDead");
  bind(inputs.aimviewShowLabels, "aimviewShowLabels");
  bind(inputs.aimviewPlayerDist, "aimviewPlayerDist");
  bind(inputs.aimviewShowLoot, "aimviewShowLoot");
  bind(inputs.aimviewShowContainers, "aimviewShowContainers");
  bind(inputs.aimviewShowCorpses, "aimviewShowCorpses");
  bind(inputs.aimviewLootDist, "aimviewLootDist");
  bind(inputs.aimviewShowItemLabels, "aimviewShowItemLabels");
  bind(inputs.aimviewMaxLoot, "aimviewMaxLoot");

  bind(inputs.showPlayers, "showPlayers");
  bind(inputs.showAim, "showAim");
  bind(inputs.showNames, "showNames");
  bind(inputs.showHeight, "showHeight");
  bind(inputs.showGroups, "showGroups");
  bind(inputs.playerSize, "playerSize");

  bind(inputs.showLoot, "showLoot");
  bind(inputs.showLootNames, "showLootNames");
  bind(inputs.lootMinPrice, "lootMinPrice");
  bind(inputs.lootMaxDist, "lootMaxDist");
  bind(inputs.lootSearch, "lootSearch");
  bind(inputs.lootMode, "lootMode");
  bind(inputs.lootHideNormal, "lootHideNormal");
  bind(inputs.lootImportantPrice, "lootImportantPrice");
  bind(inputs.lootRareMult, "lootRareMult");
  bind(inputs.lootTopMult, "lootTopMult");
  bind(inputs.showContainers, "showContainers");
  bind(inputs.showContainerNames, "showContainerNames");
  bind(inputs.containerMaxDist, "containerMaxDist");
  bind(inputs.showCorpses, "showCorpses");
  bind(inputs.showExfils, "showExfils");

  bind(inputs.showSwitches, "showSwitches");
  bind(inputs.showDoors, "showDoors");
  bind(inputs.showTransits, "showTransits");
  bind(inputs.showBtr, "showBtr");
  bind(inputs.showBtrRoute, "showBtrRoute");
  bind(inputs.showAirdrops, "showAirdrops");

  bind(inputs.showQuestItems, "showQuestItems");
  bind(inputs.showAllQuestItems, "showAllQuestItems");
  bind(inputs.showQuestZones, "showQuestZones");
  bind(inputs.questsOnlyActiveMap, "questsOnlyActiveMap");
  bind(inputs.questSearch, "questSearch");

  bind(inputs.localColor, "local", true);
  bind(inputs.teammateColor, "teammate", true);
  bind(inputs.pmcColor, "pmc", true);
  bind(inputs.scavColor, "scav", true);
  bind(inputs.pscavColor, "pscav", true);
  bind(inputs.raiderColor, "raider", true);
  bind(inputs.bossColor, "boss", true);
  bind(inputs.deadColor, "dead", true);
  bind(inputs.lootColor, "loot", true);
  bind(inputs.lootImportantColor, "lootImportant", true);
  bind(inputs.lootRareColor, "lootRare", true);
  bind(inputs.lootTopColor, "lootTop", true);
  bind(inputs.lootWishlistColor, "lootWishlist", true);
  bind(inputs.lootQuestColor, "lootQuest", true);
  bind(inputs.containerColor, "container", true);
  bind(inputs.corpseColor, "corpse", true);
  bind(inputs.exfilOpenColor, "exfilOpen", true);
  bind(inputs.exfilPendingColor, "exfilPending", true);
  bind(inputs.exfilClosedColor, "exfilClosed", true);

  bind(inputs.questItemColor, "questItem", true);
  bind(inputs.questZoneColor, "questZone", true);
  bind(inputs.switchColor, "switch", true);
  bind(inputs.doorLockedColor, "doorLocked", true);
  bind(inputs.doorOpenColor, "doorOpen", true);
  bind(inputs.transitColor, "transit", true);
  bind(inputs.btrColor, "btr", true);
  bind(inputs.airdropColor, "airdrop", true);
}

function listen(el, key, isColor, transform) {
  if (!el) return;
  const evt = (el.type === "color" || el.type === "range") ? "input" : "change";
  el.addEventListener(evt, () => {
    const v = el.type === "checkbox" ? el.checked : (transform ? transform(el.value) : el.value);
    if (isColor) state.colors[key] = v;
    else state[key] = v;
    saveSettings();
    updateRangeValue(key);
    if (key === "freeMode") updateFollowBadge();
    if (key === "pollMs") startPolling();
    // Toggling the projection (SVG ↔ satellite) invalidates the free-pan
    // anchor — its coords belong to the old projection. Reset so the next
    // frame re-anchors on the focus player.
    if (key === "useSatellite") freeAnchor = { x: 0, y: 0, mapId: "" };
  });
}

listen(inputs.showMap, "showMap");
listen(inputs.useSatellite, "useSatellite");
listen(inputs.zoom, "zoom", false, Number);
listen(inputs.rotateWithLocal, "rotateWithLocal");
listen(inputs.pollMs, "pollMs", false, Number);
listen(inputs.freeMode, "freeMode");
listen(inputs.hoverOpenSidebar, "hoverOpenSidebar");
listen(inputs.showAimview, "showAimview");
listen(inputs.aimviewZoom, "aimviewZoom", false, Number);
listen(inputs.aimviewFov, "aimviewFov", false, Number);
listen(inputs.aimviewUseFov, "aimviewUseFov");
listen(inputs.aimviewAspect, "aimviewAspect");
listen(inputs.aimviewFollowHostFov, "aimviewFollowHostFov");
listen(inputs.aimviewScopeZoom, "aimviewScopeZoom", false, Number);
listen(inputs.aimviewShowAimStatus, "aimviewShowAimStatus");
listen(inputs.aimviewEyeHeight, "aimviewEyeHeight", false, Number);
listen(inputs.aimviewBgOpacity, "aimviewBgOpacity", false, Number);
listen(inputs.aimviewShowCrosshair, "aimviewShowCrosshair");
listen(inputs.aimviewShowPlayers, "aimviewShowPlayers");
listen(inputs.aimviewHideAI, "aimviewHideAI");
listen(inputs.aimviewHideDead, "aimviewHideDead");
listen(inputs.aimviewShowLabels, "aimviewShowLabels");
listen(inputs.aimviewPlayerDist, "aimviewPlayerDist", false, Number);
listen(inputs.aimviewShowLoot, "aimviewShowLoot");
listen(inputs.aimviewShowContainers, "aimviewShowContainers");
listen(inputs.aimviewShowCorpses, "aimviewShowCorpses");
listen(inputs.aimviewLootDist, "aimviewLootDist", false, Number);
listen(inputs.aimviewShowItemLabels, "aimviewShowItemLabels");
listen(inputs.aimviewMaxLoot, "aimviewMaxLoot", false, Number);

listen(inputs.showPlayers, "showPlayers");
listen(inputs.showAim, "showAim");
listen(inputs.showNames, "showNames");
listen(inputs.showHeight, "showHeight");
listen(inputs.showGroups, "showGroups");
listen(inputs.playerSize, "playerSize", false, Number);

listen(inputs.showLoot, "showLoot");
listen(inputs.showLootNames, "showLootNames");
listen(inputs.lootMinPrice, "lootMinPrice", false, Number);
if (inputs.lootMinPrice) {
  inputs.lootMinPrice.addEventListener("input", updateLootPresetActive);
}
listen(inputs.lootMaxDist, "lootMaxDist", false, Number);
listen(inputs.lootSearch, "lootSearch");
listen(inputs.lootMode, "lootMode");
listen(inputs.lootHideNormal, "lootHideNormal");
listen(inputs.lootImportantPrice, "lootImportantPrice", false, Number);
listen(inputs.lootRareMult,       "lootRareMult",       false, Number);
listen(inputs.lootTopMult,        "lootTopMult",        false, Number);
listen(inputs.showContainers, "showContainers");
listen(inputs.showContainerNames, "showContainerNames");
listen(inputs.containerMaxDist, "containerMaxDist", false, Number);
listen(inputs.showCorpses, "showCorpses");
listen(inputs.showExfils, "showExfils");

listen(inputs.showSwitches, "showSwitches");
listen(inputs.showDoors, "showDoors");
listen(inputs.showTransits, "showTransits");
listen(inputs.showBtr, "showBtr");
listen(inputs.showBtrRoute, "showBtrRoute");
listen(inputs.showAirdrops, "showAirdrops");

listen(inputs.showQuestItems, "showQuestItems");
listen(inputs.showAllQuestItems, "showAllQuestItems");
listen(inputs.showQuestZones, "showQuestZones");
listen(inputs.questsOnlyActiveMap, "questsOnlyActiveMap");
if (inputs.questsOnlyActiveMap) {
  inputs.questsOnlyActiveMap.addEventListener("change", () => rebuildQuestList());
}
if (inputs.questSearch) {
  inputs.questSearch.addEventListener("input", () => {
    state.questSearch = inputs.questSearch.value;
    saveSettings();
    rebuildQuestList();
  });
}

listen(inputs.localColor, "local", true);
listen(inputs.teammateColor, "teammate", true);
listen(inputs.pmcColor, "pmc", true);
listen(inputs.scavColor, "scav", true);
listen(inputs.pscavColor, "pscav", true);
listen(inputs.raiderColor, "raider", true);
listen(inputs.bossColor, "boss", true);
listen(inputs.deadColor, "dead", true);
listen(inputs.lootColor, "loot", true);
listen(inputs.lootImportantColor, "lootImportant", true);
listen(inputs.lootRareColor, "lootRare", true);
listen(inputs.lootTopColor, "lootTop", true);
listen(inputs.lootWishlistColor, "lootWishlist", true);
listen(inputs.lootQuestColor, "lootQuest", true);
listen(inputs.containerColor, "container", true);
listen(inputs.corpseColor, "corpse", true);
listen(inputs.exfilOpenColor, "exfilOpen", true);
listen(inputs.exfilPendingColor, "exfilPending", true);
listen(inputs.exfilClosedColor, "exfilClosed", true);

listen(inputs.questItemColor, "questItem", true);
listen(inputs.questZoneColor, "questZone", true);
listen(inputs.switchColor, "switch", true);
listen(inputs.doorLockedColor, "doorLocked", true);
listen(inputs.doorOpenColor, "doorOpen", true);
listen(inputs.transitColor, "transit", true);
listen(inputs.btrColor, "btr", true);
listen(inputs.airdropColor, "airdrop", true);

if (inputs.centerOnLocal) {
  inputs.centerOnLocal.onclick = () => {
    state.freeMode = false;
    state.followTarget = null;
    freeAnchor = { x: 0, y: 0, mapId: "" };
    if (inputs.freeMode) inputs.freeMode.checked = false;
    updateFollowBadge();
    saveSettings();
  };
}

// Double-click on a player to follow them
canvas.addEventListener("dblclick", e => {
  const mx = e.clientX, my = e.clientY;
  let best = null, bestDist = Infinity;
  for (const h of hitList) {
    if (h.kind !== "player") continue;
    const dx = mx - h.px, dy = my - h.py;
    const d = dx * dx + dy * dy;
    if (d < h.r * h.r && d < bestDist) { bestDist = d; best = h; }
  }
  if (best) {
    const p = best.data;
    if (p.isLocal) {
      state.followTarget = null;
    } else {
      state.followTarget = p.name || null;
    }
    state.freeMode = false;
    freeAnchor = { x: 0, y: 0, mapId: "" };
    if (inputs.freeMode) inputs.freeMode.checked = false;
    updateFollowBadge();
    saveSettings();
  }
});

/* ── Wishlist / Blacklist (searchable, mirrors local LootFiltersPanel) ── */

const ITEMS_LS_KEY = "eft_webradar_items_v1";
const MAX_ITEM_RESULTS = 30;
let itemCatalog = [];          // [{ bsgId, name, shortName, price }]
let _catalogById = new Map();

function rememberCatalogItem(it) {
  if (!it || !it.bsgId) return;
  _catalogById.set(it.bsgId, it);
  if (!_itemNameCache.has(it.bsgId)) {
    _itemNameCache.set(it.bsgId, { shortName: it.shortName, name: it.name });
  }
}

function applyItemCatalog(list) {
  itemCatalog = Array.isArray(list) ? list : [];
  _catalogById = new Map();
  for (const it of itemCatalog) rememberCatalogItem(it);
  rebuildItemSearchResults();
  rebuildWishlistList();
  rebuildBlacklistList();
}

async function fetchItemCatalog() {
  try {
    const cached = localStorage.getItem(ITEMS_LS_KEY);
    if (cached) {
      try { applyItemCatalog(JSON.parse(cached)); } catch { /* ignore */ }
    }
    const res = await fetch("/api/items", { cache: "no-store" });
    if (!res.ok) return;
    const data = await res.json();
    if (!Array.isArray(data)) return;
    applyItemCatalog(data);
    try { localStorage.setItem(ITEMS_LS_KEY, JSON.stringify(data)); } catch { /* quota */ }
  } catch { /* offline */ }
}

function lookupItemMeta(id) {
  return _catalogById.get(id) || _itemNameCache.get(id) || null;
}
function itemDisplayName(id) {
  const m = lookupItemMeta(id);
  return m?.shortName || m?.name || id;
}

function addToWishlist(id) {
  const arr = Array.isArray(state.lootWishlist) ? state.lootWishlist : [];
  if (!arr.includes(id)) arr.push(id);
  state.lootWishlist = arr;
  // Mutually exclusive with blacklist.
  removeFromBlacklist(id, true);
  rebuildWishlistSet();
  saveSettings();
  rebuildWishlistList();
  rebuildItemSearchResults();
}
function removeFromWishlist(id, skipRender) {
  const arr = Array.isArray(state.lootWishlist) ? state.lootWishlist : [];
  const idx = arr.indexOf(id);
  if (idx < 0) return;
  arr.splice(idx, 1);
  state.lootWishlist = arr;
  rebuildWishlistSet();
  saveSettings();
  if (!skipRender) { rebuildWishlistList(); rebuildItemSearchResults(); }
}
function addToBlacklist(id) {
  const arr = Array.isArray(state.lootBlacklist) ? state.lootBlacklist : [];
  if (!arr.includes(id)) arr.push(id);
  state.lootBlacklist = arr;
  removeFromWishlist(id, true);
  rebuildBlacklistSet();
  saveSettings();
  rebuildBlacklistList();
  rebuildItemSearchResults();
}
function removeFromBlacklist(id, skipRender) {
  const arr = Array.isArray(state.lootBlacklist) ? state.lootBlacklist : [];
  const idx = arr.indexOf(id);
  if (idx < 0) return;
  arr.splice(idx, 1);
  state.lootBlacklist = arr;
  rebuildBlacklistSet();
  saveSettings();
  if (!skipRender) { rebuildBlacklistList(); rebuildItemSearchResults(); }
}

function rebuildItemSearchResults() {
  const wrap = document.getElementById("itemSearchResults");
  if (!wrap) return;
  wrap.innerHTML = "";
  const q = (state.itemSearch || "").trim().toLowerCase();
  if (q.length < 2) {
    const hint = document.createElement("div");
    hint.className = "hint";
    hint.style.padding = "4px 2px";
    hint.textContent = itemCatalog.length
      ? "Type at least 2 characters to search."
      : "Loading item database…";
    wrap.appendChild(hint);
    return;
  }
  const results = [];
  for (const it of itemCatalog) {
    if (results.length >= MAX_ITEM_RESULTS) break;
    if (!it) continue;
    const sn = (it.shortName || "").toLowerCase();
    const nm = (it.name || "").toLowerCase();
    if (sn.includes(q) || nm.includes(q)) results.push(it);
  }
  if (!results.length) {
    const hint = document.createElement("div");
    hint.className = "hint";
    hint.style.padding = "4px 2px";
    hint.textContent = "No matches.";
    wrap.appendChild(hint);
    return;
  }
  for (const it of results) {
    const isWL = _wishlistSet.has(it.bsgId);
    const isBL = _blacklistSet.has(it.bsgId);
    const row = document.createElement("div");
    row.className = "container-item";
    const priceStr = it.price > 0 ? ` <span class="mono" style="color:var(--text-dim)">${formatPrice(it.price)}</span>` : "";
    row.innerHTML =
      `<span class="cname" title="${esc(it.name || "")}">${esc(it.shortName || it.bsgId)}${priceStr}</span>` +
      `<button type="button" class="small wl-add" title="${isWL ? "Remove from wishlist" : "Add to wishlist"}" style="color:${isWL ? state.colors.lootWishlist : ""}">${isWL ? "★" : "+W"}</button>` +
      `<button type="button" class="small bl-add" title="${isBL ? "Remove from blacklist" : "Add to blacklist"}" style="color:${isBL ? "#f87171" : ""}">${isBL ? "✕" : "+B"}</button>`;
    row.querySelector(".wl-add").addEventListener("click", (e) => {
      e.preventDefault(); e.stopPropagation();
      if (isWL) removeFromWishlist(it.bsgId);
      else addToWishlist(it.bsgId);
    });
    row.querySelector(".bl-add").addEventListener("click", (e) => {
      e.preventDefault(); e.stopPropagation();
      if (isBL) removeFromBlacklist(it.bsgId);
      else addToBlacklist(it.bsgId);
    });
    wrap.appendChild(row);
  }
}

function renderIdList(wrap, ids, badge, glyph, onRemove, emptyText) {
  if (!wrap) return;
  wrap.innerHTML = "";
  if (badge) badge.textContent = String(ids.length);
  if (!ids.length) {
    const empty = document.createElement("div");
    empty.className = "hint";
    empty.style.padding = "4px 2px";
    empty.textContent = emptyText;
    wrap.appendChild(empty);
    return;
  }
  const sorted = [...ids].sort((a, b) =>
    itemDisplayName(a).toLowerCase().localeCompare(itemDisplayName(b).toLowerCase()));
  for (const id of sorted) {
    const meta = lookupItemMeta(id);
    const display = itemDisplayName(id);
    const row = document.createElement("label");
    row.className = "container-item";
    row.innerHTML = `<span class="cname" title="${esc(meta?.name || id)}">${glyph} ${esc(display)}</span><button class="small wl-rm" type="button">×</button>`;
    row.querySelector(".wl-rm").addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      onRemove(id);
    });
    wrap.appendChild(row);
  }
}

function rebuildWishlistList() {
  renderIdList(
    document.getElementById("wishlistList"),
    Array.isArray(state.lootWishlist) ? state.lootWishlist : [],
    inputs.wishlistBadge,
    "★",
    (id) => removeFromWishlist(id),
    "Empty — search above to add items."
  );
}
function rebuildBlacklistList() {
  renderIdList(
    document.getElementById("blacklistList"),
    Array.isArray(state.lootBlacklist) ? state.lootBlacklist : [],
    inputs.blacklistBadge,
    "✕",
    (id) => removeFromBlacklist(id),
    "Empty — search above to blacklist items."
  );
}

if (inputs.itemSearch) {
  inputs.itemSearch.value = state.itemSearch || "";
  inputs.itemSearch.addEventListener("input", () => {
    state.itemSearch = inputs.itemSearch.value || "";
    rebuildItemSearchResults();
  });
}

if (inputs.wishlistClear) {
  inputs.wishlistClear.onclick = () => {
    if (!Array.isArray(state.lootWishlist) || !state.lootWishlist.length) return;
    state.lootWishlist = [];
    rebuildWishlistSet();
    saveSettings();
    rebuildWishlistList();
    rebuildItemSearchResults();
  };
}
if (inputs.blacklistClear) {
  inputs.blacklistClear.onclick = () => {
    if (!Array.isArray(state.lootBlacklist) || !state.lootBlacklist.length) return;
    state.lootBlacklist = [];
    rebuildBlacklistSet();
    saveSettings();
    rebuildBlacklistList();
    rebuildItemSearchResults();
  };
}

function updateFollowBadge() {
  if (inputs.modeBadge) {
    if (state.followTarget) {
      inputs.modeBadge.textContent = state.followTarget;
      inputs.modeBadge.style.color = "var(--accent)";
    } else {
      inputs.modeBadge.textContent = state.freeMode ? "free" : "follow";
      inputs.modeBadge.style.color = "";
    }
  }
}
if (inputs.resetSettings) {
  inputs.resetSettings.onclick = () => resetSettings();
}

/* ── Loot Min Price presets ── */
function updateLootPresetActive() {
  document.querySelectorAll(".loot-presets button[data-price]").forEach(b => {
    const v = Number(b.dataset.price) || 0;
    b.classList.toggle("active", v === (Number(state.lootMinPrice) || 0));
  });
}
document.querySelectorAll(".loot-presets button[data-price]").forEach(btn => {
  btn.addEventListener("click", () => {
    const v = Number(btn.dataset.price) || 0;
    state.lootMinPrice = v;
    if (inputs.lootMinPrice) inputs.lootMinPrice.value = v;
    updateRangeValue("lootMinPrice");
    updateLootPresetActive();
    saveSettings();
  });
});

/* ═══════════════════════════════════════════════════════════════════════════
   HTTP POLLING
   ═══════════════════════════════════════════════════════════════════════════ */
let radarData = null;
let pollTimer = null;
let _lastMapId = null;

async function fetchRadar() {
  try {
    const res = await fetch("/api/radar", { cache: "no-store" });
    if (!res.ok) throw new Error("HTTP " + res.status);
    radarData = await res.json();

    const inRaid    = !!(radarData?.inRaid ?? radarData?.inGame);
    const inHideout = !!(radarData?.inHideout);
    const statusText = radarData?.status ?? (inRaid ? "In Raid" : "Waiting for Raid Start");
    statusLabel.textContent = statusText;
    statusEl.className = inRaid ? "ok" : inHideout ? "warn" : "warn";

    const mapId = radarData?.mapID ?? "unknown";
    if (mapId !== _lastMapId) {
      _lastMapId = mapId;
      if (state.questsOnlyActiveMap) rebuildQuestList();
    }
    const pc = Array.isArray(radarData?.players) ? radarData.players.length : 0;
    subline.textContent = inRaid
      ? `${mapId} · ${pc} player${pc !== 1 ? "s" : ""}`
      : statusText !== "In Raid" ? "\u2014" : `${mapId} · ${pc} player${pc !== 1 ? "s" : ""}`;

    updatePlayerCounts(radarData?.players);
  } catch {
    radarData = null;
    statusLabel.textContent = "Disconnected";
    statusEl.className = "bad";
    subline.textContent = "waiting\u2026";
    updatePlayerCounts(null);
  }
}

function startPolling() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = setInterval(fetchRadar, state.pollMs);
}

/* ═══════════════════════════════════════════════════════════════════════════
   PLAYER COUNT CHIPS
   ═══════════════════════════════════════════════════════════════════════════ */
const typeNames  = ["Bot", "You", "Team", "PMC", "PScav", "Raider", "Boss"];
const typeColors = () => [
  state.colors.scav,
  state.colors.local,
  state.colors.teammate,
  state.colors.pmc,
  state.colors.pscav,
  state.colors.raider,
  state.colors.boss,
];

function updatePlayerCounts(players) {
  if (!playerCountsEl) return;
  if (!players || !players.length) {
    playerCountsEl.innerHTML = "";
    return;
  }

  const counts = {};
  let alive = 0;
  for (const p of players) {
    if (!p || !p.isActive) continue;
    if (p.isLocal) continue;
    const t = p.type ?? 0;
    counts[t] = (counts[t] || 0) + 1;
    if (p.isAlive !== false) alive++;
  }

  const cols = typeColors();
  let html = "";
  // Show PMC, PScav, Raider, Boss, Bot (skip local=1, teammate=2 from chip display)
  const order = [3, 4, 5, 6, 0];
  for (const t of order) {
    if (!counts[t]) continue;
    const name = typeNames[t] || "Bot";
    const col = cols[t] || "#9ca3af";
    html += `<span class="pcount-chip"><span class="chip-dot" style="background:${col}"></span>${counts[t]} ${name}</span>`;
  }

  // Teammates
  if (counts[2]) {
    const col = cols[2] || "#4ade80";
    html += `<span class="pcount-chip"><span class="chip-dot" style="background:${col}"></span>${counts[2]} Team</span>`;
  }

  playerCountsEl.innerHTML = html;
}

/* ═══════════════════════════════════════════════════════════════════════════
   SVG MAP CACHE
   ═══════════════════════════════════════════════════════════════════════════ */
const svgImgCache = new Map();
const svgMetaCache = new Map();

/* ═══════════════════════════════════════════════════════════════════════════
   SATELLITE TILE CACHE
   ═══════════════════════════════════════════════════════════════════════════ */
// Image elements keyed by tile URL — the browser cache handles bytes;
// this map dedupes <img> objects so the same tile reuses the same image.
const tileImgCache = new Map();
// Memoized coverage rect per satellite-info object (cleared when the
// satellite info reference changes — i.e. when the map switches).
let _satCovOwner = null;
let _satCov = null;
function getSatelliteCoverage(sat) {
  if (sat === _satCovOwner && _satCov) return _satCov;
  const ox = sat.originX, oy = sat.originY, sc = sat.scale, svgSc = sat.svgScale;
  // Mirror the C# projection in MapParams.ToMapPos / SatelliteMap ctor.
  const p1x = (ox * svgSc) + (sat.boundsLng1 * sc * svgSc);
  const p1y = (oy * svgSc) - (sat.boundsLat1 * sc * svgSc);
  const p2x = (ox * svgSc) + (sat.boundsLng2 * sc * svgSc);
  const p2y = (oy * svgSc) - (sat.boundsLat2 * sc * svgSc);
  const ts = sat.tileSize || 256;
  const n = 1 << sat.loadZoom;
  const minPx = Math.min(p1x, p2x), maxPx = Math.max(p1x, p2x);
  const minPy = Math.min(p1y, p2y), maxPy = Math.max(p1y, p2y);
  const minTileX = Math.max(0, Math.floor(minPx / ts));
  const maxTileX = Math.min(n - 1, Math.floor(maxPx / ts));
  const minTileY = Math.max(0, Math.floor(minPy / ts));
  const maxTileY = Math.min(n - 1, Math.floor(maxPy / ts));
  const origX = minTileX * ts;
  const origY = minTileY * ts;
  const w = (maxTileX - minTileX + 1) * ts;
  const h = (maxTileY - minTileY + 1) * ts;
  _satCov = { minTileX, maxTileX, minTileY, maxTileY, origX, origY, w, h };
  _satCovOwner = sat;
  return _satCov;
}

function getTileImage(template, z, x, y) {
  const url = template.replace("{z}", z).replace("{x}", x).replace("{y}", y);
  let img = tileImgCache.get(url);
  if (img) return img;
  img = new Image();
  // Don't set crossOrigin — we only display tiles, never read back pixels.
  // Setting it would require the CDN to send CORS headers and would otherwise
  // fail the load. drawImage works with tainted images.
  img.src = url;
  tileImgCache.set(url, img);
  const el = document.getElementById("mapCacheInfo");
  if (el) el.textContent = String(svgImgCache.size + tileImgCache.size);
  return img;
}

// Are we rendering satellite for this frame? Both the user toggle and
// the map having satellite metadata must be true.
function isSatelliteActive(map) {
  return !!(state.useSatellite && map && map.satellite);
}

// Active projection scale. Negative for 180°-rotated satellite maps; SVG
// configs are always positive. Used to flip player heading by π so chevrons/
// aimlines point the right way on a flipped tile mosaic — mirrors the C#
// local renderer's "if (Config.Scale < 0) { sin = -sin; cos = -cos; }".
function getActiveScale(map) {
  if (isSatelliteActive(map)) return map.satellite.scale ?? 1;
  return map.scale ?? 1;
}

function ensureSvgMeta(filename) {
  if (svgMetaCache.has(filename)) return;
  svgMetaCache.set(filename, { w: 0, h: 0, ready: false });

  fetch("/Maps/" + filename, { cache: "force-cache" })
    .then(r => r.text())
    .then(txt => {
      let w = 0, h = 0;
      const vb = /viewBox\s*=\s*["']\s*([-\d.eE]+)\s+([-\d.eE]+)\s+([-\d.eE]+)\s+([-\d.eE]+)\s*["']/i.exec(txt);
      if (vb) { w = Number(vb[3]) || 0; h = Number(vb[4]) || 0; }
      if (!(w > 0 && h > 0)) {
        const mw = /width\s*=\s*["']\s*([-\d.eE]+)\s*(?:px)?\s*["']/i.exec(txt);
        const mh = /height\s*=\s*["']\s*([-\d.eE]+)\s*(?:px)?\s*["']/i.exec(txt);
        if (mw && mh) { w = Number(mw[1]) || 0; h = Number(mh[1]) || 0; }
      }
      const meta = svgMetaCache.get(filename);
      if (meta) { meta.w = w > 0 ? w : 0; meta.h = h > 0 ? h : 0; meta.ready = true; }
    })
    .catch(() => {});
}

function getSvg(filename) {
  if (svgImgCache.has(filename)) return svgImgCache.get(filename);
  ensureSvgMeta(filename);
  const img = new Image();
  img.src = "/Maps/" + filename;
  svgImgCache.set(filename, img);
  const el = document.getElementById("mapCacheInfo");
  if (el) el.textContent = String(svgImgCache.size);
  return img;
}

function getSvgDims(filename, img) {
  const meta = svgMetaCache.get(filename);
  if (meta && meta.ready && meta.w > 0 && meta.h > 0) return { w: meta.w, h: meta.h };
  const nw = img?.naturalWidth || 0, nh = img?.naturalHeight || 0;
  if (nw > 0 && nh > 0) return { w: nw, h: nh };
  return null;
}

/* ═══════════════════════════════════════════════════════════════════════════
   MAP HELPERS
   ═══════════════════════════════════════════════════════════════════════════ */
function clamp(v, lo, hi) { return Math.max(lo, Math.min(v, hi)); }

function getMapLayers(map) {
  const a = Array.isArray(map?.layers) ? map.layers : [];
  const b = Array.isArray(map?.mapLayers) ? map.mapLayers : [];
  return a.length ? a : b;
}
function hmin(l) { return l?.minHeight ?? l?.MinHeight ?? null; }
function hmax(l) { return l?.maxHeight ?? l?.MaxHeight ?? null; }

function getBaseLayer(map) {
  const layers = getMapLayers(map);
  if (!layers.length) return null;
  return layers.find(l => l && hmin(l) == null && hmax(l) == null) || layers[0];
}

function getHeightLayer(map, localWorldY) {
  const layers = getMapLayers(map);
  if (!layers.length || localWorldY == null) return null;
  const candidates = layers.filter(l => {
    if (!l || (hmin(l) == null && hmax(l) == null)) return false;
    return (hmin(l) == null || localWorldY >= hmin(l)) &&
           (hmax(l) == null || localWorldY < hmax(l));
  });
  if (!candidates.length) return null;
  candidates.sort((a, b) => (hmin(a) ?? -999999) - (hmin(b) ?? -999999));
  return candidates[candidates.length - 1];
}

function rotatePoint(px, py, rad) {
  const c = Math.cos(rad), s = Math.sin(rad);
  return { x: px * c - py * s, y: px * s + py * c };
}

function worldToMapUnzoomed(worldX, worldZ, map) {
  // Pick projection: satellite uses the side-car transform; SVG uses the
  // top-level fields. Satellite returns coords offset so (0,0) is the
  // top-left of the tile mosaic — matches the SVG convention so the rest
  // of the rendering pipeline (mapRect, anchor, mapXYToScreen) is reused.
  const sat = isSatelliteActive(map);
  if (sat) {
    const s = map.satellite;
    const ox = s.originX, oy = s.originY, sc = s.scale, svgSc = s.svgScale;
    const cov = getSatelliteCoverage(s);
    return {
      x: (ox * svgSc) + (worldX * sc * svgSc) - cov.origX,
      y: (oy * svgSc) - (worldZ * sc * svgSc) - cov.origY,
    };
  }
  const ox = map.originX ?? map.x ?? 0;
  const oy = map.originY ?? map.y ?? 0;
  const sc = map.scale ?? 1;
  const svgSc = map.svgScale ?? 1;
  return {
    x: (ox * svgSc) + (worldX * sc * svgSc),
    y: (oy * svgSc) - (worldZ * sc * svgSc)
  };
}

function readPlayerMapXY(p, map) {
  const wx = p?.worldX;
  const wz = p?.worldZ;
  if (Number.isFinite(wx) && Number.isFinite(wz) && map) {
    return worldToMapUnzoomed(wx, wz, map);
  }
  return { x: 0, y: 0 };
}

function readWorldY(e) {
  const wy = e?.worldY;
  return Number.isFinite(wy) ? wy : null;
}

function getViewportCenter() {
  const sbOpen = isSidebarOpen();
  const insetRight = sbOpen ? sidebar.getBoundingClientRect().width : 0;
  return { cx: (cw - insetRight) / 2, cy: ch / 2 };
}

/* ═══════════════════════════════════════════════════════════════════════════
   MAP DRAWING
   ═══════════════════════════════════════════════════════════════════════════ */
function drawSvgLayerAnchored(filename, map, cx, cy, zoom, rotRad, anchor, alpha = 1) {
  if (!filename) return false;
  const img = getSvg(filename);
  if (!img.complete) return false;
  const dims = getSvgDims(filename, img);
  if (!dims) return false;

  const svgSc = map.svgScale ?? 1;
  const w = dims.w * svgSc * zoom;
  const h = dims.h * svgSc * zoom;

  ctx.save();
  ctx.globalAlpha = alpha;
  ctx.translate(cx, cy);
  if (state.rotateWithLocal) ctx.rotate(-rotRad);
  ctx.translate(-(anchor?.x ?? 0) * zoom, -(anchor?.y ?? 0) * zoom);
  ctx.drawImage(img, 0, 0, w, h);
  ctx.restore();
  return true;
}

function getMapScreenRect(map, cx, cy, zoom, anchor) {
  let w, h;
  if (isSatelliteActive(map)) {
    const cov = getSatelliteCoverage(map.satellite);
    // Satellite "image" extent is the tile mosaic in projection-space pixels
    // (already at loadZoom resolution — svgScale is baked into worldToMapUnzoomed),
    // so no svgScale multiplier here.
    w = cov.w * zoom;
    h = cov.h * zoom;
  } else {
    const base = getBaseLayer(map);
    if (!base) return null;
    const bFile = base.filename || base.Filename;
    if (!bFile) return null;
    const img = getSvg(bFile);
    if (!img.complete) return null;
    const dims = getSvgDims(bFile, img);
    if (!dims) return null;
    const svgSc = map.svgScale ?? 1;
    w = dims.w * svgSc * zoom;
    h = dims.h * svgSc * zoom;
  }
  const ax = (anchor?.x ?? 0) * zoom;
  const ay = (anchor?.y ?? 0) * zoom;
  return { left: cx - ax, top: cy - ay, w, h };
}

function drawMap(map, localWorldY, cx, cy, zoom, rotRad, anchor) {
  if (isSatelliteActive(map)) {
    return drawSatelliteMap(map.satellite, cx, cy, zoom, rotRad, anchor);
  }

  const base = getBaseLayer(map);
  if (!base) return false;

  const disableDimming = !!(map.disableDimming ?? map.DisableDimming);
  const overlay = (!disableDimming) ? getHeightLayer(map, localWorldY) : null;

  let baseAlpha = 1;
  if (!disableDimming && overlay) {
    if (overlay.dimBaseLayer === true || overlay.DimBaseLayer === true) baseAlpha = 0.55;
  }

  const bFile = base.filename || base.Filename;
  const ok = drawSvgLayerAnchored(bFile, map, cx, cy, zoom, rotRad, anchor, baseAlpha);
  if (!ok) return false;

  if (overlay) {
    const oFile = overlay.filename || overlay.Filename;
    if (oFile && oFile !== bFile) drawSvgLayerAnchored(oFile, map, cx, cy, zoom, rotRad, anchor, 1);
  }
  return true;
}

function drawSatelliteMap(sat, cx, cy, zoom, rotRad, anchor) {
  const cov = getSatelliteCoverage(sat);

  // Pick a pyramid zoom level matching screen pixels per base-pixel.
  // Each base-pixel (at loadZoom) renders as `zoom` screen pixels. A tile at
  // pyramid z covers (tileSize << (loadZoom - z)) base-pixels, so its
  // on-screen size is tileSize * (1 << (loadZoom - z)) * zoom. We want
  // this >= tileSize → loadZoom - z >= -log2(zoom) → z <= loadZoom + log2(zoom).
  let z = sat.loadZoom + Math.ceil(Math.log2(Math.max(zoom, 1e-6)));
  z = clamp(z, sat.minZoom, sat.maxZoom);
  const zShift = sat.loadZoom - z;
  const tilePxAtBase = sat.tileSize << zShift; // base-pixels per tile at this z

  // Background fill for missing tiles
  ctx.save();
  ctx.fillStyle = "#14141a";
  ctx.translate(cx, cy);
  if (state.rotateWithLocal) ctx.rotate(-rotRad);
  ctx.translate(-(anchor?.x ?? 0) * zoom, -(anchor?.y ?? 0) * zoom);
  ctx.fillRect(0, 0, cov.w * zoom, cov.h * zoom);

  // Coverage in tile-at-z space
  const firstTx = Math.max(0, Math.floor(cov.origX / tilePxAtBase));
  const lastTx  = Math.min((1 << z) - 1, Math.floor((cov.origX + cov.w - 1) / tilePxAtBase));
  const firstTy = Math.max(0, Math.floor(cov.origY / tilePxAtBase));
  const lastTy  = Math.min((1 << z) - 1, Math.floor((cov.origY + cov.h - 1) / tilePxAtBase));

  for (let tx = firstTx; tx <= lastTx; tx++) {
    for (let ty = firstTy; ty <= lastTy; ty++) {
      const img = getTileImage(sat.tileUrlTemplate, z, tx, ty);
      if (!img.complete || img.naturalWidth === 0) continue;
      // Tile top-left in projection space → image-relative
      const px = tx * tilePxAtBase - cov.origX;
      const py = ty * tilePxAtBase - cov.origY;
      ctx.drawImage(img, px * zoom, py * zoom, tilePxAtBase * zoom, tilePxAtBase * zoom);
    }
  }
  ctx.restore();
  return true;
}

function mapXYToScreen(mx, my, mapRect, cx, cy, rotRad) {
  let px = mapRect.left + mx * state.zoom;
  let py = mapRect.top + my * state.zoom;
  if (state.rotateWithLocal) {
    const v = rotatePoint(px - cx, py - cy, -rotRad);
    px = cx + v.x;
    py = cy + v.y;
  }
  return { px, py };
}

/* ═══════════════════════════════════════════════════════════════════════════
   PLAYER DRAWING
   ═══════════════════════════════════════════════════════════════════════════ */
// WebPlayerType enum from C#:
// Bot=0, LocalPlayer=1, Teammate=2, Player=3, PlayerScav=4, Raider=5, Boss=6
function playerColor(p) {
  const isDead = p?.isAlive === false;
  if (isDead) return state.colors.dead;
  switch (p?.type) {
    case 1: return state.colors.local;
    case 2: return state.colors.teammate;
    case 3: return state.colors.pmc;
    case 4: return state.colors.pscav;
    case 5: return state.colors.raider;
    case 6: return state.colors.boss;
    default: return state.colors.scav;
  }
}

function drawPlayerMarker(px, py, r, color, ang, isDead) {
  ctx.save();
  ctx.strokeStyle = color;
  ctx.fillStyle = color;
  const lw = Math.max(2, r * 0.45);
  ctx.lineWidth = lw;
  ctx.lineCap = "round";

  if (isDead) {
    const d = r * 0.7;
    ctx.globalAlpha = 0.6;
    ctx.beginPath();
    ctx.moveTo(px - d, py - d);
    ctx.lineTo(px + d, py + d);
    ctx.moveTo(px + d, py - d);
    ctx.lineTo(px - d, py + d);
    ctx.stroke();
    ctx.restore();
    return;
  }

  // Outer glow
  ctx.shadowColor = color;
  ctx.shadowBlur = 6;

  // Open arc (chevron facing direction)
  const gap = Math.PI / 3;
  const start = ang + gap * 0.5;
  const end = ang + (Math.PI * 2) - gap * 0.5;
  ctx.beginPath();
  ctx.arc(px, py, r, start, end, false);
  ctx.stroke();

  ctx.shadowBlur = 0;
  ctx.restore();
}

function drawHeightArrow(px, py, above) {
  const sz = 5;
  ctx.beginPath();
  if (above) {
    ctx.moveTo(px, py - sz);
    ctx.lineTo(px - sz, py + sz);
    ctx.lineTo(px + sz, py + sz);
  } else {
    ctx.moveTo(px, py + sz);
    ctx.lineTo(px - sz, py - sz);
    ctx.lineTo(px + sz, py - sz);
  }
  ctx.closePath();
  ctx.fill();
}

function drawGroupConnectors(players, map, cx, cy, rotRad, mapRect) {
  const groups = new Map();
  for (const p of players) {
    if (!p || p.isAlive === false) continue;
    const gid = p.groupId ?? -1;
    if (gid <= 0) continue;
    if (!groups.has(gid)) groups.set(gid, []);
    groups.get(gid).push(p);
  }

  ctx.save();
  ctx.globalAlpha = 0.35;
  ctx.lineWidth = 1.5;
  ctx.lineCap = "round";
  ctx.setLineDash([4, 6]);

  for (const [, members] of groups) {
    if (members.length < 2) continue;
    const col = playerColor(members[0]);
    ctx.strokeStyle = col;

    for (let i = 0; i < members.length - 1; i++) {
      const a = readPlayerMapXY(members[i], map);
      const b = readPlayerMapXY(members[i + 1], map);
      const sa = mapXYToScreen(a.x, a.y, mapRect, cx, cy, rotRad);
      const sb = mapXYToScreen(b.x, b.y, mapRect, cx, cy, rotRad);
      ctx.beginPath();
      ctx.moveTo(sa.px, sa.py);
      ctx.lineTo(sb.px, sb.py);
      ctx.stroke();
    }
  }

  ctx.setLineDash([]);
  ctx.restore();
}

function drawPlayers(players, map, cx, cy, rotRad, mapRect, localWorldY, hitList, distOrigin) {
  const size = Number(state.playerSize) || 6;
  const haveHeights = (localWorldY != null);
  const hasDistOrigin = distOrigin && Number.isFinite(distOrigin.worldX);

  for (const p of players) {
    if (!p || !p.isActive) continue;

    const isDead = p.isAlive === false;
    const pm = readPlayerMapXY(p, map);
    const s = mapXYToScreen(pm.x, pm.y, mapRect, cx, cy, rotRad);
    const px = s.px, py = s.py;

    hitList.push({ kind: "player", px, py, r: Math.max(10, size + 8), data: p });

    const col = playerColor(p);
    const yaw = Number(p.yaw) || 0;
    let ang = state.rotateWithLocal ? (yaw - rotRad) : yaw;
    // 180°-rotated satellite tiles flip world axes in our projection;
    // rotate the heading by π so the chevron/aimline match.
    if (getActiveScale(map) < 0) ang += Math.PI;

    drawPlayerMarker(px, py, size, col, ang, isDead);

    // Aimline
    if (state.showAim && !isDead) {
      const len = 22;
      ctx.save();
      ctx.strokeStyle = col;
      ctx.globalAlpha = 0.7;
      ctx.lineWidth = 1.5;
      ctx.lineCap = "round";
      ctx.beginPath();
      ctx.moveTo(px + Math.cos(ang) * (size * 0.15), py + Math.sin(ang) * (size * 0.15));
      ctx.lineTo(px + Math.cos(ang) * len, py + Math.sin(ang) * len);
      ctx.stroke();
      ctx.restore();
    }

    // Height arrows
    if (state.showHeight && haveHeights) {
      const pyWorld = readWorldY(p);
      if (pyWorld != null) {
        const dy = pyWorld - localWorldY;
        ctx.fillStyle = col;
        if (dy > 1.0) drawHeightArrow(px, py - (size + 10), true);
        else if (dy < -1.0) drawHeightArrow(px, py + (size + 10), false);
      }
    }

    // Names + Distance
    if (state.showNames) {
      let label = p.name || "";
      if (hasDistOrigin && p !== distOrigin) {
        const dx = p.worldX - distOrigin.worldX;
        const dy = (p.worldY ?? 0) - (distOrigin.worldY ?? 0);
        const dz = p.worldZ - distOrigin.worldZ;
        const dist = Math.sqrt(dx * dx + dy * dy + dz * dz);
        label += " [" + Math.round(dist) + "m]";
      }
      ctx.save();
      ctx.fillStyle = "rgba(229, 231, 235, 0.85)";
      ctx.font = "600 11px system-ui, sans-serif";
      ctx.textAlign = "center";
      ctx.shadowColor = "rgba(0,0,0,.7)";
      ctx.shadowBlur = 3;
      ctx.fillText(label, px, py - size - 6);
      ctx.restore();
    }
  }
}

/* ═══════════════════════════════════════════════════════════════════════════
   PRICE FORMATTER
   ═══════════════════════════════════════════════════════════════════════════ */
function formatPrice(p) {
  if (p >= 1000000) return (p / 1000000).toFixed(1) + "M";
  if (p >= 1000) return (p / 1000).toFixed(0) + "K";
  return String(p);
}

/* ═══════════════════════════════════════════════════════════════════════════
   LOOT DRAWING
   ───────────────────────────────────────────────────────────────────────────
   Importance / tier / wishlist are computed entirely on the buddy side from
   their own settings — the server only sends raw price + bsgId + questItem.
   ═══════════════════════════════════════════════════════════════════════════ */

// Wishlist/Blacklist sets kept in sync with state for O(1) lookup.
let _wishlistSet = new Set();
let _blacklistSet = new Set();
// Cache of best-known short names per BSG id, populated from live loot frames.
const _itemNameCache = new Map();
function rememberItemName(item) {
  if (!item || !item.bsgId) return;
  const cached = _itemNameCache.get(item.bsgId);
  if (!cached || cached.shortName !== item.shortName) {
    _itemNameCache.set(item.bsgId, { shortName: item.shortName, name: item.name });
  }
}
function rebuildWishlistSet() {
  _wishlistSet = new Set(Array.isArray(state.lootWishlist) ? state.lootWishlist : []);
}
function rebuildBlacklistSet() {
  _blacklistSet = new Set(Array.isArray(state.lootBlacklist) ? state.lootBlacklist : []);
}
function isWishlisted(item) {
  return !!(item && item.bsgId && _wishlistSet.has(item.bsgId));
}
function isBlacklisted(item) {
  return !!(item && item.bsgId && _blacklistSet.has(item.bsgId));
}
function getTier(price) {
  const threshold = Number(state.lootImportantPrice) || 0;
  if (threshold <= 0 || price < threshold) return 0;
  const top = threshold * (Number(state.lootTopMult) || 5);
  if (price >= top) return 3;
  const rare = threshold * (Number(state.lootRareMult) || 2);
  if (price >= rare) return 2;
  return 1;
}
function isImportant(price) {
  const threshold = Number(state.lootImportantPrice) || 0;
  return threshold > 0 && price >= threshold;
}

function lootColor(item) {
  if (isQuestRelevant(item)) return state.colors.lootQuest;
  if (isWishlisted(item))    return state.colors.lootWishlist;
  const tier = getTier(item.price || 0);
  if (tier >= 3)             return state.colors.lootTop;
  if (tier === 2)            return state.colors.lootRare;
  if (tier === 1)            return state.colors.lootImportant;
  return state.colors.loot;
}

// True when this loot item should highlight as quest-related:
//  - tracked by the buddy via the Quests tab AND "Show Tracked Quest Items" is on, OR
//  - server-flagged static quest item AND "Show All Map Quest Items" is on.
function isQuestRelevant(item) {
  if (!item) return false;
  if (state.showQuestItems && isTrackedQuestItem(item)) return true;
  if (state.showAllQuestItems && item.questItem) return true;
  return false;
}

function lootPasses(item, local) {
  if (!item) return false;

  // Buddy blacklist always wins.
  if (isBlacklisted(item)) return false;

  // Server-flagged static quest items are hidden unless either:
  //  - the buddy enabled "Show All Map Quest Items", or
  //  - the item belongs to a tracked quest and "Show Tracked Quest Items" is on.
  if (item.questItem && !state.showAllQuestItems &&
      !(state.showQuestItems && isTrackedQuestItem(item))) {
    return false;
  }

  const wishlisted = isWishlisted(item);
  const questHit   = isQuestRelevant(item) && (isTrackedQuestItem(item) || item.questItem);

  // Quest hits and wishlist hits bypass the price/mode filters but still
  // respect the buddy's explicit name search and distance settings.
  if (questHit || wishlisted) {
    const q = (state.lootSearch || "").trim().toLowerCase();
    if (q.length > 0) {
      const n = (item.shortName || "").toLowerCase();
      const f = (item.name || "").toLowerCase();
      if (!n.includes(q) && !f.includes(q)) return false;
    }
    const maxDist = Number(state.lootMaxDist) || 0;
    if (maxDist > 0 && local && Number.isFinite(local.worldX)) {
      const dx = item.worldX - local.worldX;
      const dz = item.worldZ - local.worldZ;
      if ((dx * dx + dz * dz) > (maxDist * maxDist)) return false;
    }
    return true;
  }

  const tier = getTier(item.price || 0);
  const important = tier >= 1;

  // Buddy-side display mode
  const mode = state.lootMode || "all";
  if (mode === "wishlist") return false;            // already handled above
  if (mode === "quest")    return false;            // already handled above
  if (mode === "important" && !important) return false;
  if (mode === "rare"      && tier < 2)   return false;

  // Min-price filter (no questItem/wishlist bypass — those took the early path)
  const minPrice = Number(state.lootMinPrice) || 0;
  if ((item.price || 0) < minPrice) return false;

  // "Hide normal" quick filter
  if (state.lootHideNormal && tier === 0) return false;

  // Name search
  const q = (state.lootSearch || "").trim().toLowerCase();
  if (q.length > 0) {
    const n = (item.shortName || "").toLowerCase();
    const f = (item.name || "").toLowerCase();
    if (!n.includes(q) && !f.includes(q)) return false;
  }

  // Distance filter
  const maxDist = Number(state.lootMaxDist) || 0;
  if (maxDist > 0 && local && Number.isFinite(local.worldX)) {
    const dx = item.worldX - local.worldX;
    const dz = item.worldZ - local.worldZ;
    if ((dx * dx + dz * dz) > (maxDist * maxDist)) return false;
  }

  return true;
}

function drawLoot(lootItems, map, cx, cy, rotRad, mapRect, localWorldY, hitList, local) {
  if (!lootItems || !lootItems.length) return;

  for (const item of lootItems) {
    rememberItemName(item);
    if (!lootPasses(item, local)) continue;

    const pm = worldToMapUnzoomed(item.worldX, item.worldZ, map);
    const s = mapXYToScreen(pm.x, pm.y, mapRect, cx, cy, rotRad);
    const px = s.px, py = s.py;
    const col = lootColor(item);

    // Diamond marker
    const r = 3.5;
    ctx.save();
    ctx.fillStyle = col;
    ctx.globalAlpha = 0.9;
    ctx.beginPath();
    ctx.moveTo(px, py - r);
    ctx.lineTo(px + r, py);
    ctx.lineTo(px, py + r);
    ctx.lineTo(px - r, py);
    ctx.closePath();
    ctx.fill();
    ctx.restore();

    // Label
    if (state.showLootNames) {
      const label = item.price > 0 ? `${item.shortName} (${formatPrice(item.price)})` : item.shortName;
      ctx.save();
      ctx.fillStyle = col;
      ctx.font = "500 10px system-ui, sans-serif";
      ctx.textAlign = "left";
      ctx.shadowColor = "rgba(0,0,0,.7)";
      ctx.shadowBlur = 2;
      ctx.fillText(label, px + 6, py + 3.5);
      ctx.restore();
    }

    hitList.push({
      kind: "loot", px, py, r: 12,
      data: { name: item.shortName, fullName: item.name, bsgId: item.bsgId, price: item.price, wishlisted: isWishlisted(item), questItem: item.questItem, tier: getTier(item.price || 0) }
    });
  }
}

/* ═══════════════════════════════════════════════════════════════════════════
   CONTAINER DRAWING
   ═══════════════════════════════════════════════════════════════════════════ */
function drawContainers(containers, map, cx, cy, rotRad, mapRect, hitList, local) {
  if (!containers || !containers.length) return;
  const col = state.colors.container;
  const maxDist = Number(state.containerMaxDist) || 0;
  const hasLocal = local && Number.isFinite(local.worldX);
  const selNames = state.selectedContainers;
  const hasFilter = Array.isArray(selNames) && selNames.length > 0;

  for (const c of containers) {
    if (!c) continue;

    // Name-based selection filter (client-side)
    if (hasFilter && !selNames.includes(c.name)) continue;

    // Distance filter
    if (maxDist > 0 && hasLocal) {
      const dx = c.worldX - local.worldX;
      const dz = c.worldZ - local.worldZ;
      const dist = Math.sqrt(dx * dx + dz * dz);
      if (dist > maxDist) continue;
    }

    const pm = worldToMapUnzoomed(c.worldX, c.worldZ, map);
    const s = mapXYToScreen(pm.x, pm.y, mapRect, cx, cy, rotRad);
    const px = s.px, py = s.py;

    // Square marker
    const hs = 3.5;
    ctx.save();
    ctx.strokeStyle = col;
    ctx.lineWidth = 1.6;
    ctx.globalAlpha = c.searched ? 0.4 : 0.9;
    ctx.strokeRect(px - hs, py - hs, hs * 2, hs * 2);
    ctx.restore();

    if (state.showContainerNames) {
      ctx.save();
      ctx.fillStyle = col;
      ctx.globalAlpha = c.searched ? 0.4 : 0.85;
      ctx.font = "500 10px system-ui, sans-serif";
      ctx.textAlign = "left";
      ctx.shadowColor = "rgba(0,0,0,.7)";
      ctx.shadowBlur = 2;
      ctx.fillText(c.name, px + 6, py + 3.5);
      ctx.restore();
    }

    hitList.push({ kind: "container", px, py, r: 10, data: c });
  }
}

/* ═══════════════════════════════════════════════════════════════════════════
   CORPSE DRAWING
   ═══════════════════════════════════════════════════════════════════════════ */
function drawCorpses(corpses, map, cx, cy, rotRad, mapRect, hitList) {
  if (!corpses || !corpses.length) return;
  const col = state.colors.corpse;

  for (const c of corpses) {
    if (!c) continue;
    const pm = worldToMapUnzoomed(c.worldX, c.worldZ, map);
    const s = mapXYToScreen(pm.x, pm.y, mapRect, cx, cy, rotRad);
    const px = s.px, py = s.py;

    // X marker
    const d = 4;
    ctx.save();
    ctx.strokeStyle = col;
    ctx.lineWidth = 2;
    ctx.lineCap = "round";
    ctx.globalAlpha = 0.7;
    ctx.beginPath();
    ctx.moveTo(px - d, py - d); ctx.lineTo(px + d, py + d);
    ctx.moveTo(px + d, py - d); ctx.lineTo(px - d, py + d);
    ctx.stroke();
    ctx.restore();

    hitList.push({ kind: "corpse", px, py, r: 10, data: c });
  }
}

/* ═══════════════════════════════════════════════════════════════════════════
   EXFIL DRAWING
   ═══════════════════════════════════════════════════════════════════════════ */
function exfilColor(status) {
  // 0=Closed, 1=Pending, 2=Open
  switch (status) {
    case 2: return state.colors.exfilOpen;
    case 1: return state.colors.exfilPending;
    default: return state.colors.exfilClosed;
  }
}

function drawExfils(exfils, map, cx, cy, rotRad, mapRect, hitList) {
  if (!exfils || !exfils.length) return;

  for (const e of exfils) {
    if (!e) continue;
    // Hide closed exfils — buddies only care about active/open ones.
    if (e.status === 0) continue;
    const pm = worldToMapUnzoomed(e.worldX, e.worldZ, map);
    const s = mapXYToScreen(pm.x, pm.y, mapRect, cx, cy, rotRad);
    const px = s.px, py = s.py;
    const col = exfilColor(e.status);

    // Circle marker
    ctx.save();
    ctx.beginPath();
    ctx.arc(px, py, 5, 0, Math.PI * 2);
    ctx.strokeStyle = "rgba(0,0,0,.5)";
    ctx.lineWidth = 2.5;
    ctx.stroke();
    ctx.strokeStyle = col;
    ctx.lineWidth = 1.6;
    ctx.stroke();
    ctx.restore();

    // Name label
    ctx.save();
    ctx.fillStyle = col;
    ctx.font = "600 10px system-ui, sans-serif";
    ctx.textAlign = "center";
    ctx.shadowColor = "rgba(0,0,0,.7)";
    ctx.shadowBlur = 3;
    ctx.fillText(e.name, px, py - 9);
    ctx.restore();

    hitList.push({ kind: "exfil", px, py, r: 14, data: e });
  }
}

/* ═══════════════════════════════════════════════════════════════════════════
   AIMVIEW DRAG
   ═══════════════════════════════════════════════════════════════════════════ */
let avDragging = false;
let avDragStart = { x: 0, y: 0 };
let avDragOrigin = { x: 0, y: 0 };
const aimviewHeader = document.getElementById("aimviewHeader") || aimviewEl;

aimviewHeader.addEventListener("mousedown", e => {
  if (e.button !== 0) return;
  e.preventDefault();
  avDragging = true;
  aimviewEl.classList.add("dragging");
  const rect = aimviewEl.getBoundingClientRect();
  avDragOrigin = { x: rect.left, y: rect.top };
  avDragStart = { x: e.clientX, y: e.clientY };
});

window.addEventListener("mousemove", e => {
  if (!avDragging) return;
  const nx = avDragOrigin.x + (e.clientX - avDragStart.x);
  const ny = avDragOrigin.y + (e.clientY - avDragStart.y);
  applyAimviewPos(nx, ny);
});

window.addEventListener("mouseup", () => {
  if (!avDragging) return;
  avDragging = false;
  aimviewEl.classList.remove("dragging");
  saveSettings();
});

// Touch support
aimviewHeader.addEventListener("touchstart", e => {
  if (e.touches.length !== 1) return;
  e.preventDefault();
  avDragging = true;
  aimviewEl.classList.add("dragging");
  const rect = aimviewEl.getBoundingClientRect();
  avDragOrigin = { x: rect.left, y: rect.top };
  avDragStart = { x: e.touches[0].clientX, y: e.touches[0].clientY };
}, { passive: false });

aimviewHeader.addEventListener("touchmove", e => {
  if (!avDragging || e.touches.length !== 1) return;
  e.preventDefault();
  const nx = avDragOrigin.x + (e.touches[0].clientX - avDragStart.x);
  const ny = avDragOrigin.y + (e.touches[0].clientY - avDragStart.y);
  applyAimviewPos(nx, ny);
}, { passive: false });

aimviewHeader.addEventListener("touchend", () => {
  if (!avDragging) return;
  avDragging = false;
  aimviewEl.classList.remove("dragging");
  saveSettings();
});

function applyAimviewPos(x, y) {
  const w = Number(state.aimviewWidth) || 280;
  const h = Number(state.aimviewHeight) || 220;
  const maxX = window.innerWidth - w;
  const maxY = window.innerHeight - h;
  x = Math.max(0, Math.min(x, maxX));
  y = Math.max(0, Math.min(y, maxY));
  state.aimviewX = Math.round(x);
  state.aimviewY = Math.round(y);
  aimviewEl.style.left = x + "px";
  aimviewEl.style.top = y + "px";
  aimviewEl.style.right = "auto";
  aimviewEl.style.bottom = "auto";
}

/* ═══════════════════════════════════════════════════════════════════════════
   AIMVIEW RESIZE (edges + corners)
   ═══════════════════════════════════════════════════════════════════════════ */
const AIMVIEW_MIN_W = 160;
const AIMVIEW_MIN_H = 120;

let avResize = null; // { dir, startX, startY, origX, origY, origW, origH }

function beginResize(dir, clientX, clientY) {
  const rect = aimviewEl.getBoundingClientRect();
  avResize = {
    dir,
    startX: clientX,
    startY: clientY,
    origX:  rect.left,
    origY:  rect.top,
    origW:  rect.width,
    origH:  rect.height,
  };
  aimviewEl.classList.add("dragging");
}

function applyResize(clientX, clientY) {
  if (!avResize) return;
  const dx = clientX - avResize.startX;
  const dy = clientY - avResize.startY;
  let { origX, origY, origW, origH, dir } = avResize;
  let nx = origX, ny = origY, nw = origW, nh = origH;

  if (dir.includes("e")) nw = Math.max(AIMVIEW_MIN_W, origW + dx);
  if (dir.includes("s")) nh = Math.max(AIMVIEW_MIN_H, origH + dy);
  if (dir.includes("w")) {
    nw = Math.max(AIMVIEW_MIN_W, origW - dx);
    nx = origX + (origW - nw);
  }
  if (dir.includes("n")) {
    nh = Math.max(AIMVIEW_MIN_H, origH - dy);
    ny = origY + (origH - nh);
  }

  // Clamp inside the viewport.
  nx = Math.max(0, Math.min(nx, window.innerWidth  - nw));
  ny = Math.max(0, Math.min(ny, window.innerHeight - nh));

  state.aimviewX = Math.round(nx);
  state.aimviewY = Math.round(ny);
  state.aimviewWidth  = Math.round(nw);
  state.aimviewHeight = Math.round(nh);

  aimviewEl.style.left   = nx + "px";
  aimviewEl.style.top    = ny + "px";
  aimviewEl.style.width  = nw + "px";
  aimviewEl.style.height = nh + "px";
  aimviewEl.style.right  = "auto";
  aimviewEl.style.bottom = "auto";
}

document.querySelectorAll("#aimviewWidget .aimview-resize").forEach(el => {
  el.addEventListener("mousedown", e => {
    if (e.button !== 0) return;
    e.preventDefault();
    e.stopPropagation();
    beginResize(el.dataset.resize, e.clientX, e.clientY);
  });
  el.addEventListener("touchstart", e => {
    if (e.touches.length !== 1) return;
    e.preventDefault();
    e.stopPropagation();
    beginResize(el.dataset.resize, e.touches[0].clientX, e.touches[0].clientY);
  }, { passive: false });
});

window.addEventListener("mousemove", e => {
  if (avResize) applyResize(e.clientX, e.clientY);
});
window.addEventListener("mouseup", () => {
  if (!avResize) return;
  avResize = null;
  aimviewEl.classList.remove("dragging");
  saveSettings();
});
window.addEventListener("touchmove", e => {
  if (!avResize || e.touches.length !== 1) return;
  e.preventDefault();
  applyResize(e.touches[0].clientX, e.touches[0].clientY);
}, { passive: false });
window.addEventListener("touchend", () => {
  if (!avResize) return;
  avResize = null;
  aimviewEl.classList.remove("dragging");
  saveSettings();
});

/* ═══════════════════════════════════════════════════════════════════════════
   AIMVIEW WIDGET
   First-person projection from the followed player using raw yaw/pitch.
   Mirrors AimviewWidget.cs camera math (synthetic mode).
   ═══════════════════════════════════════════════════════════════════════════ */
function isAIPlayerType(t) {
  // 0 Bot, 5 Raider, 6 Boss are AI; 1 Local, 2 Teammate, 3 Player, 4 PScav are humans.
  return t === 0 || t === 5 || t === 6;
}

function drawAimview(camera, players, lootItems, containers, corpses, hostCam) {
  if (!state.showAimview || !camera) {
    aimviewEl.classList.add("hidden");
    return;
  }

  aimviewEl.classList.remove("hidden");

  const w = Math.max(120, Number(state.aimviewWidth)  || 280);
  const h = Math.max(100, Number(state.aimviewHeight) || 220);
  aimviewEl.style.width  = w + "px";
  aimviewEl.style.height = h + "px";

  // Pick a default position the first time we show: top-left, clear of the sidebar.
  // After the first drag, state.aimviewX/Y is persisted and used.
  if (state.aimviewX == null || state.aimviewY == null) {
    state.aimviewX = 16;
    state.aimviewY = 80;
  }
  const maxX = Math.max(0, window.innerWidth  - w);
  const maxY = Math.max(0, window.innerHeight - h);
  const px = Math.max(0, Math.min(state.aimviewX, maxX));
  const py = Math.max(0, Math.min(state.aimviewY, maxY));
  aimviewEl.style.left   = px + "px";
  aimviewEl.style.top    = py + "px";
  aimviewEl.style.right  = "auto";
  aimviewEl.style.bottom = "auto";

  const avDpr = window.devicePixelRatio || 1;
  // Canvas is flex-sized below the drag header — measure the actual render area.
  const cRect = aimviewCanvas.getBoundingClientRect();
  const cw_ = Math.max(1, Math.round(cRect.width));
  const ch_ = Math.max(1, Math.round(cRect.height));
  const bw = Math.max(1, Math.round(cw_ * avDpr));
  const bh = Math.max(1, Math.round(ch_ * avDpr));
  if (aimviewCanvas.width  !== bw) aimviewCanvas.width  = bw;
  if (aimviewCanvas.height !== bh) aimviewCanvas.height = bh;

  avCtx.setTransform(avDpr, 0, 0, avDpr, 0, 0);
  avCtx.clearRect(0, 0, cw_, ch_);

  // Background
  const bgAlpha = clamp(Number(state.aimviewBgOpacity) || 0, 0, 100) / 100;
  if (bgAlpha > 0) {
    avCtx.fillStyle = `rgba(11, 18, 32, ${bgAlpha})`;
    avCtx.fillRect(0, 0, cw_, ch_);
  }

  // Use canvas-local dimensions for projection/midpoints below.
  const drawW = cw_;
  const drawH = ch_;
  const midX = drawW / 2;
  const midY = drawH / 2;

  // Crosshair
  if (state.aimviewShowCrosshair) {
    avCtx.strokeStyle = "rgba(255,255,255,.22)";
    avCtx.lineWidth = 1;
    avCtx.beginPath();
    avCtx.moveTo(midX - 8, midY); avCtx.lineTo(midX + 8, midY);
    avCtx.moveTo(midX, midY - 8); avCtx.lineTo(midX, midY + 8);
    avCtx.stroke();
  }

  // Build synthetic forward/right/up from raw yaw + pitch (matches AimviewWidget.cs).
  const yaw = Number(camera.rawYaw) || 0;
  const pitch = Number(camera.pitch) || 0;
  const cosY = Math.cos(yaw),  sinY = Math.sin(yaw);
  const cosP = Math.cos(pitch), sinP = Math.sin(pitch);

  const fwd   = { x: sinY * cosP, y: -sinP, z: cosY * cosP };
  const right = { x: cosY,        y: 0,     z: -sinY };
  const upX = right.y * fwd.z - right.z * fwd.y;
  const upY = right.z * fwd.x - right.x * fwd.z;
  const upZ = right.x * fwd.y - right.y * fwd.x;
  const up = { x: -upX, y: -upY, z: -upZ };

  const eyeHeight = Number(state.aimviewEyeHeight) || 1.62;
  // Camera origin: horizontal position comes from the followed player (head bone
  // for buddies, in-game look transform for the host), vertical comes from the
  // user-configurable Eye Height slider on top of the followed player's foot Y.
  // This keeps the slider authoritative — the previous code locked Y to the
  // host's LookPosition.Y, which sits a few cm below true eye level and made
  // remote targets project above the crosshair.
  const isFollowingHost = !!camera.isLocal;
  let originX, originZ;
  if (isFollowingHost && Number.isFinite(camera.eyeX) && Number.isFinite(camera.eyeZ)) {
    originX = camera.eyeX;
    originZ = camera.eyeZ;
  } else if (Array.isArray(camera.bones) && camera.bones.length >= 3
      && Number.isFinite(camera.bones[0]) && Number.isFinite(camera.bones[2])) {
    originX = camera.bones[0];
    originZ = camera.bones[2];
  } else {
    originX = camera.worldX;
    originZ = camera.worldZ;
  }
  const localPos = {
    x: originX,
    y: (Number(camera.worldY) || 0) + eyeHeight,
    z: originZ,
  };
  const zoom = (() => {
    // 1. Mirror host FOV ONLY when following the host. Host ADS/scope/FOV
    //    has no relationship to what a buddy-followed player sees.
    if (isFollowingHost && state.aimviewFollowHostFov && hostCam
        && Number(hostCam.fov) > 1 && Number(hostCam.fov) < 170) {
      let vfov = Number(hostCam.fov);
      // Scope zoom is not currently readable from memory — apply a user
      // configurable multiplier whenever the host reports IsScoped.
      if (hostCam.isScoped) {
        const scopeMul = Math.max(1, Number(state.aimviewScopeZoom) || 1);
        vfov = vfov / scopeMul;
      }
      const v = vfov * Math.PI / 180;
      return 1 / Math.tan(v / 2);
    }
    // 2. Remote followed player: scope state isn't readable, but ADS is.
    //    When the followed player is ADS, apply the configured zoom multiplier
    //    on top of the user's base FOV/zoom so the aimview reflects their iron-sight zoom.
    let baseZoom;
    if (state.aimviewUseFov) {
      const fovDeg = Math.max(10, Math.min(170, Number(state.aimviewFov) || 90));
      baseZoom = 1 / Math.tan((fovDeg * Math.PI / 180) / 2);
    } else {
      baseZoom = Number(state.aimviewZoom) || 1.0;
    }
    if (!isFollowingHost && camera.isADS && state.aimviewFollowHostFov) {
      const adsMul = Math.max(1, Number(state.aimviewScopeZoom) || 1);
      return baseZoom * adsMul;
    }
    return baseZoom;
  })();
  const playerMaxDist = Math.max(20, Number(state.aimviewPlayerDist) || 300);
  const lootMaxDist   = Math.max(10, Number(state.aimviewLootDist)   || 100);
  const cameraName = camera.name;

  // Half-extents for projection scaling — keep aspect-correct. When the user
  // pins a viewport aspect (e.g. 16:9 to match the in-game render target),
  // letterbox the projection inside the canvas instead of stretching to it.
  let halfX = midX;
  let halfY = midY;
  if (state.aimviewAspect && state.aimviewAspect !== "auto") {
    const targetAspect = Number(state.aimviewAspect);
    if (Number.isFinite(targetAspect) && targetAspect > 0) {
      const canvasAspect = drawW / drawH;
      if (canvasAspect > targetAspect) {
        // Canvas is wider than target — pillarbox (X is the limiting axis).
        halfX = (drawH * targetAspect) / 2;
        halfY = drawH / 2;
      } else {
        // Canvas is taller than target — letterbox.
        halfX = drawW / 2;
        halfY = (drawW / targetAspect) / 2;
      }
    }
  }

  function projectAV(wx, wy, wz, maxDist) {
    const dx = wx - localPos.x;
    const dy = wy - localPos.y;
    const dz = wz - localPos.z;

    const dotF = dx * fwd.x + dy * fwd.y + dz * fwd.z;
    if (dotF < 0.5) return null;
    if (dotF > maxDist) return null;

    const dotR = dx * right.x + dy * right.y + dz * right.z;
    const dotU = dx * up.x    + dy * up.y    + dz * up.z;

    const sx = midX + (dotR / dotF) * zoom * halfX;
    const sy = midY - (dotU / dotF) * zoom * halfY;

    if (sx < -20 || sx > drawW + 20 || sy < -20 || sy > drawH + 20) return null;
    return { sx, sy, dist: dotF };
  }

  // ── Loot (drawn first, players on top) ────────────────────────────────────
  if (state.aimviewShowLoot && lootItems) {
    const showLabels = !!state.aimviewShowItemLabels;
    const maxItems = Math.max(1, Number(state.aimviewMaxLoot) || 40);
    const buf = [];
    for (const item of lootItems) {
      if (!lootPasses(item, camera)) continue;
      const proj = projectAV(item.worldX, item.worldY, item.worldZ, lootMaxDist);
      if (!proj) continue;
      buf.push({ proj, item });
    }
    buf.sort((a, b) => b.proj.dist - a.proj.dist);
    const limit = Math.min(buf.length, maxItems);
    for (let i = 0; i < limit; i++) {
      const { proj, item } = buf[i];
      const col = lootColor(item);
      const alpha = Math.max(0.35, 1 - proj.dist / lootMaxDist);
      const r = Math.max(2, 4 - proj.dist * 0.02);
      avCtx.save();
      avCtx.globalAlpha = alpha;
      avCtx.fillStyle = col;
      avCtx.beginPath();
      avCtx.moveTo(proj.sx, proj.sy - r);
      avCtx.lineTo(proj.sx + r, proj.sy);
      avCtx.lineTo(proj.sx, proj.sy + r);
      avCtx.lineTo(proj.sx - r, proj.sy);
      avCtx.closePath();
      avCtx.fill();
      if (showLabels && item && item.shortName) {
        avCtx.font = "10px ui-sans-serif, system-ui";
        avCtx.fillStyle = "rgba(255,255,255,0.9)";
        avCtx.textAlign = "left";
        avCtx.fillText(item.shortName, proj.sx + r + 2, proj.sy + 3);
      }
      avCtx.restore();
    }
  }

  // ── Containers ────────────────────────────────────────────────────────────
  if (state.aimviewShowContainers && containers) {
    avCtx.save();
    avCtx.strokeStyle = state.colors.container;
    avCtx.lineWidth = 1.2;
    for (const c of containers) {
      if (!c) continue;
      const proj = projectAV(c.worldX, c.worldY, c.worldZ, lootMaxDist);
      if (!proj) continue;
      avCtx.globalAlpha = Math.max(0.4, 1 - proj.dist / lootMaxDist) * 0.9;
      avCtx.strokeRect(proj.sx - 2.5, proj.sy - 2.5, 5, 5);
    }
    avCtx.restore();
  }

  // ── Corpses ───────────────────────────────────────────────────────────────
  if (state.aimviewShowCorpses && corpses) {
    avCtx.save();
    avCtx.fillStyle = state.colors.corpse;
    for (const c of corpses) {
      if (!c) continue;
      const proj = projectAV(c.worldX, c.worldY, c.worldZ, lootMaxDist);
      if (!proj) continue;
      avCtx.globalAlpha = Math.max(0.4, 1 - proj.dist / lootMaxDist);
      avCtx.fillRect(proj.sx - 2, proj.sy - 2, 4, 4);
    }
    avCtx.restore();
  }

  // ── Players (drawn on top) ────────────────────────────────────────────────
  if (state.aimviewShowPlayers && players) {
    const hideAI   = !!state.aimviewHideAI;
    const hideDead = !!state.aimviewHideDead;
    const showLabels = !!state.aimviewShowLabels;
    for (const p of players) {
      if (!p || !p.isActive) continue;
      if (p.name === cameraName) continue;
      if (hideDead && p.isAlive === false) continue;
      if (hideAI && isAIPlayerType(p.type)) continue;

      // Pose-aware body height (head above feet): stand≈1.7, crouch≈1.1, prone≈0.4.
      const poseLvl = Number.isFinite(p.poseLevel) ? Math.max(0.2, Math.min(1, p.poseLevel)) : 1;
      const isProne = (p.pose | 0) === 2;
      const bodyHeight = isProne ? 0.4 : (0.6 + 1.1 * poseLvl);

      // ── Skeleton rendering (mirrors local AimviewWidget) ───────────────────
      // Backend serializes 16 bones in fixed order: 0=Head 1=Neck 2=Spine3
      // 3=Spine2 4=Spine1 5=Pelvis 6=LCollar 7=RCollar 8=LElbow 9=RElbow
      // 10=LHand 11=RHand 12=LKnee 13=RKnee 14=LFoot 15=RFoot. NaN = missing.
      const bones = Array.isArray(p.bones) && p.bones.length >= 48 ? p.bones : null;
      let skeletonDrawn = false;
      if (bones) {
        const projB = (i) => {
          const x = bones[i * 3], y = bones[i * 3 + 1], z = bones[i * 3 + 2];
          if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(z)) return null;
          return projectAV(x, y, z, playerMaxDist);
        };
        const segs = [
          [0, 1], [1, 2], [2, 3], [3, 4], [4, 5],   // spine
          [5, 12], [12, 14],                        // left leg
          [5, 13], [13, 15],                        // right leg
          [6, 8], [8, 10],                          // left arm
          [7, 9], [9, 11],                          // right arm
        ];
        const col = playerColor(p);
        const headProjBone = projB(0);
        const distRef = headProjBone ? headProjBone.dist : (projB(3)?.dist || projB(5)?.dist || 50);
        const lw = Math.max(1.2, 2.4 - distRef * 0.01);
        avCtx.save();
        avCtx.globalAlpha = Math.max(0.5, 1 - distRef / playerMaxDist);
        avCtx.strokeStyle = col;
        avCtx.lineWidth = lw;
        avCtx.lineCap = "round";
        avCtx.beginPath();
        let drewAny = false;
        for (const [a, b] of segs) {
          const pa = projB(a), pb = projB(b);
          if (!pa || !pb) continue;
          avCtx.moveTo(pa.sx, pa.sy);
          avCtx.lineTo(pb.sx, pb.sy);
          drewAny = true;
        }
        if (drewAny) avCtx.stroke();
        avCtx.lineCap = "butt";
        avCtx.restore();

        if (drewAny || headProjBone) {
          skeletonDrawn = true;
          // Use head bone (or fall back to projected feet) as label/velocity anchor
          const anchor = headProjBone || projB(3) || projB(5);
          if (anchor) {
            if (showLabels) {
              let label = `${p.name || "?"} (${Math.round(distRef)}m)`;
              if (isProne) label += " · prone";
              else if (poseLvl < 0.85) label += " · crouch";
              avCtx.save();
              avCtx.globalAlpha = Math.max(0.5, 1 - distRef / playerMaxDist);
              avCtx.font = "11px ui-sans-serif, system-ui";
              avCtx.textAlign = "center";
              avCtx.fillStyle = "rgba(0,0,0,0.75)";
              avCtx.fillText(label, anchor.sx + 1, anchor.sy - 9 + 1);
              avCtx.fillStyle = col;
              avCtx.fillText(label, anchor.sx, anchor.sy - 9);
              avCtx.restore();
            }
          }
          continue; // Skip the legacy capsule rendering for this player.
        }
      }

      // Project both feet (root) and head so the player has visible vertical extent
      // matching what the local widget shows via skeleton rendering. Same-ground
      // targets have their feet below the crosshair by exactly the camera eye height,
      // and their head near eye level — just like the local AimviewWidget.
      const feetProj = projectAV(p.worldX, p.worldY, p.worldZ, playerMaxDist);
      const headProj = projectAV(p.worldX, p.worldY + bodyHeight, p.worldZ, playerMaxDist);
      if (!feetProj && !headProj) continue;
      // Use whichever is available; prefer head as the marker anchor for labels.
      const proj = headProj || feetProj;
      const col = playerColor(p);
      const r = Math.max(2.5, 6 - proj.dist * 0.018);
      avCtx.save();
      avCtx.globalAlpha = Math.max(0.5, 1 - proj.dist / playerMaxDist);

      // Body capsule: line from feet to head with a head dot on top — visually
      // mirrors the local skeleton output even though we only have two points.
      if (feetProj && headProj && !isProne) {
        avCtx.strokeStyle = col;
        avCtx.lineWidth = Math.max(1.2, r * 0.45);
        avCtx.lineCap = "round";
        avCtx.beginPath();
        avCtx.moveTo(feetProj.sx, feetProj.sy);
        avCtx.lineTo(headProj.sx, headProj.sy);
        avCtx.stroke();
        avCtx.lineCap = "butt";
      }

      // Head/marker dot — flatten when prone, oval when crouching.
      avCtx.fillStyle = col;
      if (isProne) {
        avCtx.beginPath();
        avCtx.ellipse(proj.sx, proj.sy, r * 1.5, r * 0.55, 0, 0, Math.PI * 2);
        avCtx.fill();
      } else {
        avCtx.beginPath();
        avCtx.ellipse(proj.sx, proj.sy, r, r * (0.85 + 0.15 * poseLvl), 0, 0, Math.PI * 2);
        avCtx.fill();
      }
      avCtx.lineWidth = 1;
      avCtx.strokeStyle = "rgba(0,0,0,0.65)";
      avCtx.stroke();

      // Body-yaw tick — short line from the head indicating where the torso faces.
      if (Number.isFinite(p.bodyYaw) && p.bodyYaw !== 0) {
        const by = p.bodyYaw;
        const bdx = Math.sin(by);
        const bdz = Math.cos(by);
        // Project ~0.6m in front of the head so the tick stays at marker height.
        const tipProj = projectAV(p.worldX + bdx * 0.6, p.worldY + bodyHeight, p.worldZ + bdz * 0.6, playerMaxDist);
        if (tipProj) {
          avCtx.strokeStyle = col;
          avCtx.lineWidth = 1.5;
          avCtx.beginPath();
          avCtx.moveTo(proj.sx, proj.sy);
          avCtx.lineTo(tipProj.sx, tipProj.sy);
          avCtx.stroke();
        }
      }

      if (showLabels) {
        let label = `${p.name || "?"} (${Math.round(proj.dist)}m)`;
        if (isProne) label += " · prone";
        else if (poseLvl < 0.85) label += " · crouch";
        avCtx.font = "11px ui-sans-serif, system-ui";
        avCtx.textAlign = "center";
        avCtx.fillStyle = "rgba(0,0,0,0.75)";
        avCtx.fillText(label, proj.sx + 1, proj.sy - r - 3 + 1);
        avCtx.fillStyle = col;
        avCtx.fillText(label, proj.sx, proj.sy - r - 3);
      }
      avCtx.restore();
    }
  }

  // Border
  avCtx.strokeStyle = "rgba(255,255,255,.10)";
  avCtx.lineWidth = 1;
  avCtx.strokeRect(0.5, 0.5, drawW - 1, drawH - 1);

  // HUD: aim status (ADS / SCOPE) + effective FOV / zoom indicator.
  // For the host we have FOV + IsScoped + IsADS; for buddy-followed players
  // we surface the per-player IsADS read from their ProceduralWeaponAnimationObs.
  if (state.aimviewShowAimStatus) {
    const lines = [];
    if (isFollowingHost) {
      if (hostCam) {
        if (hostCam.isScoped) lines.push("SCOPE");
        else if (hostCam.isADS) lines.push("ADS");
        if (Number(hostCam.fov) > 0) {
          let vfov = Number(hostCam.fov);
          if (hostCam.isScoped) vfov /= Math.max(1, Number(state.aimviewScopeZoom) || 1);
          lines.push(`FOV ${vfov.toFixed(1)}°`);
        }
      } else {
        lines.push("no host cam");
      }
    } else if (camera.isADS) {
      lines.push("ADS");
    }
    if (lines.length > 0) {
      avCtx.save();
      avCtx.font = "11px ui-sans-serif, system-ui";
      avCtx.textAlign = "left";
      avCtx.textBaseline = "top";
      let ty = 6;
      for (const ln of lines) {
        avCtx.fillStyle = "rgba(0,0,0,0.65)";
        avCtx.fillText(ln, 7, ty + 1);
        avCtx.fillStyle = (ln === "SCOPE") ? "#ffd166"
                         : (ln === "ADS")   ? "#7dd3fc"
                         : "rgba(255,255,255,0.85)";
        avCtx.fillText(ln, 6, ty);
        ty += 13;
      }
      avCtx.restore();
    }
  }
}

/* ═══════════════════════════════════════════════════════════════════════════
   TOOLTIP
   ═══════════════════════════════════════════════════════════════════════════ */
let hitList = [];
let mouseX = 0, mouseY = 0;

canvas.addEventListener("mousemove", e => {
  mouseX = e.clientX;
  mouseY = e.clientY;
});
canvas.addEventListener("mouseleave", () => hideTooltip());

function hideTooltip() { tooltipEl.classList.add("hidden"); }

function updateHover() {
  let found = null;
  let bestDist = Infinity;

  for (const h of hitList) {
    const dx = mouseX - h.px;
    const dy = mouseY - h.py;
    const dist = dx * dx + dy * dy;
    if (dist < h.r * h.r && dist < bestDist) {
      bestDist = dist;
      found = h;
    }
  }

  if (!found) { hideTooltip(); return; }

  let html = "";

  if (found.kind === "player") {
    const p = found.data;
    const col = playerColor(p);
    const typeName = typeNames[p.type] || "Bot";
    const status = p.isAlive === false ? "Dead" : "Alive";
    const statusClass = p.isAlive === false ? "bad" : "ok";

    html += `<div class="t-header"><span class="t-dot" style="background:${col}"></span><span class="t-name">${esc(p.name || "Unknown")}</span></div>`;
    html += `<div class="t-type">${typeName} · <span style="color:var(--${statusClass})">${status}</span></div>`;

    // Compute distance from local player
    let distStr = null;
    const distOrigin = lastFocusPlayer || lastLocalPlayer;
    if (distOrigin && distOrigin !== p && Number.isFinite(distOrigin.worldX) && Number.isFinite(p.worldX)) {
      const dx = p.worldX - distOrigin.worldX;
      const dy = (p.worldY ?? 0) - (distOrigin.worldY ?? 0);
      const dz = p.worldZ - distOrigin.worldZ;
      distStr = Math.round(Math.sqrt(dx * dx + dy * dy + dz * dz)) + "m";
    }

    const wy = readWorldY(p);
    const alive = p.isAlive !== false;
    const showHealth = alive && p.health != null;
    const showHands = !!p.inHandsItem;
    const showOptics = p.hasThermal || p.hasNVG;
    const hasGrid = distStr || showHealth || showHands || (p.gearValue > 0) || showOptics || (wy != null);
    if (hasGrid) {
      html += `<div class="t-sep"></div><div class="t-grid">`;
      if (distStr) {
        html += `<span class="k">Distance</span><span class="v">${distStr}</span>`;
      }
      if (showHealth) {
        html += `<span class="k">Health</span><span class="v" style="color:${healthColor(p.health)}">${healthLabel(p.health)}</span>`;
      }
      if (showHands) {
        html += `<span class="k">In Hands</span><span class="v">${esc(p.inHandsItem)}</span>`;
        if (p.inHandsAmmo) {
          html += `<span class="k">Ammo</span><span class="v">${esc(p.inHandsAmmo)}</span>`;
        }
      }
      if (p.gearValue > 0) {
        html += `<span class="k">Gear</span><span class="v">₽${p.gearValue.toLocaleString()}</span>`;
      }
      if (showOptics) {
        const optics = [];
        if (p.hasThermal) optics.push(`<span style="color:var(--bad)">Thermal</span>`);
        if (p.hasNVG) optics.push(`<span style="color:var(--ok)">NVG</span>`);
        html += `<span class="k">Optics</span><span class="v">${optics.join(" · ")}</span>`;
      }
      if (wy != null) {
        html += `<span class="k">Height</span><span class="v">${wy.toFixed(1)}</span>`;
      }
      html += `</div>`;
    }

    // Equipment breakdown — mirrors the desktop player tooltip's gear list.
    if (Array.isArray(p.gear) && p.gear.length) {
      html += `<div class="t-sep"></div><div class="t-eq-h">Equipment</div><div class="t-grid t-eq">`;
      for (const g of orderGear(p.gear)) {
        const price = g.price > 0 ? ` <span class="t-price">(₽${g.price.toLocaleString()})</span>` : "";
        html += `<span class="k">${esc(slotLabel(g.slot))}</span><span class="v">${esc(g.name)}${price}</span>`;
      }
      html += `</div>`;
    }
  } else if (found.kind === "loot") {
    const d = found.data;
    const col = lootColor(d);
    html += `<div class="t-header"><span class="t-dot" style="background:${col}"></span><span class="t-name">${esc(d.name)}</span></div>`;
    html += `<div class="t-type">Loot</div>`;
    html += `<div class="t-sep"></div><div class="t-grid">`;
    if (d.price > 0) html += `<span class="k">Price</span><span class="v">₽${d.price.toLocaleString()}</span>`;
    if (d.questItem)   html += `<span class="k">Status</span><span class="v" style="color:${state.colors.lootQuest}">Quest Item</span>`;
    else if (d.wishlisted) html += `<span class="k">Status</span><span class="v" style="color:${state.colors.lootWishlist}">★ Wishlist</span>`;
    else if (d.tier >= 3) html += `<span class="k">Tier</span><span class="v" style="color:${state.colors.lootTop}">Top (${state.lootTopMult || 5}×)</span>`;
    else if (d.tier === 2) html += `<span class="k">Tier</span><span class="v" style="color:${state.colors.lootRare}">Rare (${state.lootRareMult || 2}×)</span>`;
    else if (d.tier === 1) html += `<span class="k">Tier</span><span class="v" style="color:${state.colors.lootImportant}">Important</span>`;
    html += `</div>`;
    html += `<div class="t-hint">Manage wishlist &amp; blacklist from the Loot tab.</div>`;
  } else if (found.kind === "container") {
    const c = found.data;
    const col = state.colors.container;
    html += `<div class="t-header"><span class="t-dot" style="background:${col}"></span><span class="t-name">${esc(c.name)}</span></div>`;
    html += `<div class="t-type">Container${c.searched ? " · <span style='color:var(--text-dim)'>Searched</span>" : ""}</div>`;
  } else if (found.kind === "corpse") {
    const c = found.data;
    const col = state.colors.corpse;
    html += `<div class="t-header"><span class="t-dot" style="background:${col}"></span><span class="t-name">${esc(c.name)}</span></div>`;
    html += `<div class="t-type">Corpse</div>`;
    if (c.totalValue > 0) {
      html += `<div class="t-sep"></div><div class="t-grid">`;
      html += `<span class="k">Gear Value</span><span class="v">₽${c.totalValue.toLocaleString()}</span>`;
      html += `</div>`;
    }
  } else if (found.kind === "exfil") {
    const e = found.data;
    const col = exfilColor(e.status);
    const statusText = e.status === 2 ? "Open" : e.status === 1 ? "Pending" : "Closed";
    const statusClass = e.status === 2 ? "ok" : e.status === 1 ? "warn" : "bad";
    html += `<div class="t-header"><span class="t-dot" style="background:${col}"></span><span class="t-name">${esc(e.name)}</span></div>`;
    html += `<div class="t-type">Exfil · <span style="color:var(--${statusClass})">${statusText}</span></div>`;
  }

  tooltipEl.innerHTML = html;
  tooltipEl.classList.remove("hidden");

  // Position with bounds checking
  const pad = 14;
  let tx = mouseX + pad;
  let ty = mouseY + pad;
  const tw = tooltipEl.offsetWidth;
  const th = tooltipEl.offsetHeight;
  if (tx + tw > window.innerWidth - 8) tx = mouseX - tw - pad;
  if (ty + th > window.innerHeight - 8) ty = mouseY - th - pad;

  tooltipEl.style.left = tx + "px";
  tooltipEl.style.top = ty + "px";
}

function esc(s) {
  const d = document.createElement("div");
  d.textContent = s;
  return d.innerHTML;
}

/* Observed health tiers (match Player.Draw HealthLabels / EHealthStatus). */
function healthLabel(h) {
  switch (h) {
    case 1: return "Injured";
    case 2: return "Badly Injured";
    case 3: return "Dying";
    default: return "Healthy";
  }
}
function healthColor(h) {
  switch (h) {
    case 1: return "var(--warn)";
    case 2:
    case 3: return "var(--bad)";
    default: return "var(--ok)";
  }
}

/* Equipment slot display names + ordering (mirror PlayerInfoWidget.FormatSlotName). */
const GEAR_ORDER = [
  "FirstPrimaryWeapon", "SecondPrimaryWeapon", "Holster", "Headwear", "FaceCover",
  "ArmorVest", "TacticalVest", "Backpack", "SecuredContainer", "Eyewear",
  "Earpiece", "Scabbard",
];
function slotLabel(slot) {
  switch (slot) {
    case "FirstPrimaryWeapon":  return "Primary";
    case "SecondPrimaryWeapon": return "Secondary";
    case "Holster":             return "Pistol";
    case "Headwear":            return "Head";
    case "FaceCover":           return "Face";
    case "ArmorVest":           return "Armor";
    case "TacticalVest":        return "Rig";
    case "Backpack":            return "Backpack";
    case "SecuredContainer":    return "Secure";
    case "Eyewear":             return "Eyes";
    case "Earpiece":            return "Ears";
    case "Scabbard":            return "Melee";
    default:                    return slot;
  }
}
function orderGear(gear) {
  return [...gear].sort((a, b) => {
    const ia = GEAR_ORDER.indexOf(a.slot), ib = GEAR_ORDER.indexOf(b.slot);
    return (ia === -1 ? 99 : ia) - (ib === -1 ? 99 : ib);
  });
}

/* ═══════════════════════════════════════════════════════════════════════════
   MOUSE / TOUCH — PAN & ZOOM
   ═══════════════════════════════════════════════════════════════════════════ */
let freeAnchor = { x: 0, y: 0, mapId: "" };
let isDragging = false;
let dragStart = { x: 0, y: 0 };

canvas.addEventListener("mousedown", e => {
  if (e.button !== 0 || !state.freeMode) return;
  isDragging = true;
  dragStart = { x: e.clientX, y: e.clientY };
});
window.addEventListener("mousemove", e => {
  if (!isDragging) return;
  const dx = e.clientX - dragStart.x;
  const dy = e.clientY - dragStart.y;
  dragStart = { x: e.clientX, y: e.clientY };

  const zoom = state.zoom;
  if (state.rotateWithLocal) {
    const v = rotatePoint(dx, dy, lastRotRad);
    freeAnchor.x -= v.x / zoom;
    freeAnchor.y -= v.y / zoom;
  } else {
    freeAnchor.x -= dx / zoom;
    freeAnchor.y -= dy / zoom;
  }
});
window.addEventListener("mouseup", () => { isDragging = false; });

canvas.addEventListener("wheel", e => {
  e.preventDefault();
  const delta = e.deltaY > 0 ? -0.1 : 0.1;
  state.zoom = clamp(state.zoom + delta, ZOOM_MIN, ZOOM_MAX);
  if (inputs.zoom) inputs.zoom.value = state.zoom;
  updateRangeValue("zoom");
  saveSettings();
}, { passive: false });

/* ── Touch: pinch-to-zoom ── */
let touches = [];
let lastPinchDist = 0;

canvas.addEventListener("touchstart", e => {
  touches = [...e.touches];
  if (touches.length === 2) {
    lastPinchDist = pinchDist(touches);
    e.preventDefault();
  } else if (touches.length === 1 && state.freeMode) {
    isDragging = true;
    dragStart = { x: touches[0].clientX, y: touches[0].clientY };
    e.preventDefault();
  }
}, { passive: false });

canvas.addEventListener("touchmove", e => {
  touches = [...e.touches];
  if (touches.length === 2) {
    const d = pinchDist(touches);
    if (lastPinchDist > 0) {
      const scale = d / lastPinchDist;
      state.zoom = clamp(state.zoom * scale, ZOOM_MIN, ZOOM_MAX);
      if (inputs.zoom) inputs.zoom.value = state.zoom;
      updateRangeValue("zoom");
      saveSettings();
    }
    lastPinchDist = d;
    e.preventDefault();
  } else if (touches.length === 1 && isDragging) {
    const dx = touches[0].clientX - dragStart.x;
    const dy = touches[0].clientY - dragStart.y;
    dragStart = { x: touches[0].clientX, y: touches[0].clientY };
    const zoom = state.zoom;
    if (state.rotateWithLocal) {
      const v = rotatePoint(dx, dy, lastRotRad);
      freeAnchor.x -= v.x / zoom;
      freeAnchor.y -= v.y / zoom;
    } else {
      freeAnchor.x -= dx / zoom;
      freeAnchor.y -= dy / zoom;
    }
    e.preventDefault();
  }
}, { passive: false });

canvas.addEventListener("touchend", e => {
  touches = [...e.touches];
  if (touches.length < 2) lastPinchDist = 0;
  if (touches.length === 0) isDragging = false;
});

function pinchDist(t) {
  const dx = t[0].clientX - t[1].clientX;
  const dy = t[0].clientY - t[1].clientY;
  return Math.sqrt(dx * dx + dy * dy);
}

/* ═══════════════════════════════════════════════════════════════════════════
   RENDER LOOP
   ═══════════════════════════════════════════════════════════════════════════ */
let lastRotRad = 0;
let lastLocalPlayer = null;
let lastFocusPlayer = null;

function frame() {
  requestAnimationFrame(frame);
  resizeCanvas();

  ctx.clearRect(0, 0, cw, ch);
  hitList = [];

  if (!radarData) {
    aimviewEl.classList.add("hidden");
    return;
  }

  const map = radarData.map || null;
  const players = Array.isArray(radarData.players) ? radarData.players : [];
  const { cx, cy } = getViewportCenter();

  // Find local player
  const local = players.find(p => p?.isLocal) || null;
  lastLocalPlayer = local;

  // Resolve follow target: a specific player name, or fall back to local
  let focusPlayer = local;
  if (state.followTarget) {
    const target = players.find(p => p && p.name === state.followTarget && p.isActive);
    if (target) {
      focusPlayer = target;
    } else {
      // Target no longer available — clear follow
      state.followTarget = null;
      updateFollowBadge();
    }
  }
  lastFocusPlayer = focusPlayer;

  // Rotation — always based on local player yaw for map orientation
  const localYaw = local ? (Number(local.yaw) || 0) : 0;
  lastRotRad = localYaw;

  // Anchor — center on the focus player
  let anchor = null;
  if (state.freeMode) {
    const mapId = radarData.mapID ?? "";
    if (freeAnchor.mapId !== mapId) {
      if (focusPlayer && map) {
        const lm = readPlayerMapXY(focusPlayer, map);
        freeAnchor.x = lm.x;
        freeAnchor.y = lm.y;
      } else {
        freeAnchor.x = 0;
        freeAnchor.y = 0;
      }
      freeAnchor.mapId = mapId;
    }
    anchor = freeAnchor;
  } else {
    if (focusPlayer && map) {
      const tm = readPlayerMapXY(focusPlayer, map);
      anchor = { x: tm.x, y: tm.y };
    } else {
      anchor = { x: 0, y: 0 };
    }
  }

  // Draw map
  if (state.showMap && map) {
    const localY = readWorldY(local);
    drawMap(map, localY, cx, cy, state.zoom, lastRotRad, anchor);
  }

  // Map entities (require map for world-to-screen projection)
  if (map) {
    const mapRect = getMapScreenRect(map, cx, cy, state.zoom, anchor);
    if (mapRect) {
      if (state.showGroups) drawGroupConnectors(players, map, cx, cy, lastRotRad, mapRect);
      if (state.showExfils) drawExfils(radarData.exfils, map, cx, cy, lastRotRad, mapRect, hitList);
      if (state.showCorpses) drawCorpses(radarData.corpses, map, cx, cy, lastRotRad, mapRect, hitList);
      if (state.showContainers) drawContainers(radarData.containers, map, cx, cy, lastRotRad, mapRect, hitList, local);
      if (state.showQuestZones) drawQuestZones(map, cx, cy, lastRotRad, mapRect, radarData.mapID);
      if (state.showSwitches) drawSwitches(radarData.switches, map, cx, cy, lastRotRad, mapRect, hitList);
      if (state.showDoors) drawDoors(radarData.doors, map, cx, cy, lastRotRad, mapRect, hitList);
      if (state.showTransits) drawTransits(radarData.transits, map, cx, cy, lastRotRad, mapRect, hitList);
      if (state.showBtr && radarData.btr) drawBtr(radarData.btr, map, cx, cy, lastRotRad, mapRect, hitList);
      if (state.showAirdrops) drawAirdrops(radarData.airdrops, map, cx, cy, lastRotRad, mapRect, hitList);
      if (state.showLoot) drawLoot(radarData.loot, map, cx, cy, lastRotRad, mapRect, readWorldY(local), hitList, local);
      if (state.showPlayers) drawPlayers(players, map, cx, cy, lastRotRad, mapRect, readWorldY(local), hitList, focusPlayer);
    }
  }

  // Aimview (independent of map — uses world-space projection)
  // Fall back to the first reasonable active player when there's no local player
  // (buddy/spectator mode) so the aimview tab is still usable.
  let aimviewCam = focusPlayer;
  if (!aimviewCam) {
    aimviewCam = players.find(p => p && p.isActive && p.isAlive !== false && p.isHuman)
              || players.find(p => p && p.isActive && p.isAlive !== false)
              || players.find(p => p && p.isActive)
              || null;
  }
  drawAimview(aimviewCam, players, radarData.loot, radarData.containers, radarData.corpses, radarData.camera);

  // Tooltip
  updateHover();
}

/* ═══════════════════════════════════════════════════════════════════════════
   CONTAINER SELECTION
   ═══════════════════════════════════════════════════════════════════════════ */
let containerTypes = []; // { id, name, selected }

async function fetchContainerTypes() {
  try {
    const res = await fetch("/api/containers", { cache: "no-store" });
    if (!res.ok) return;
    const data = await res.json();
    if (!Array.isArray(data)) return;
    containerTypes = data;
    buildContainerList();
  } catch { /* ignore */ }
}

function buildContainerList() {
  const wrap = document.getElementById("containerList");
  if (!wrap) return;
  wrap.innerHTML = "";
  const sel = state.selectedContainers;

  // Sort alphabetically by name
  const sorted = [...containerTypes].sort((a, b) => (a.name || "").localeCompare(b.name || ""));

  for (const ct of sorted) {
    const isOn = sel.length === 0 || sel.includes(ct.name);
    const lbl = document.createElement("label");
    lbl.className = "container-item";
    lbl.innerHTML = `<span class="toggle-switch small"><input type="checkbox" ${isOn ? "checked" : ""}><span class="slider"></span></span><span class="cname">${esc(ct.name)}</span>`;
    const cb = lbl.querySelector("input");
    cb.addEventListener("change", () => {
      if (cb.checked) {
        // Remove from filter (show it)
        const idx = state.selectedContainers.indexOf(ct.name);
        if (idx >= 0) state.selectedContainers.splice(idx, 1);
        // If all are now checked, clear the array (= show all)
        const allChecked = wrap.querySelectorAll("input[type=checkbox]:not(:checked)").length === 0;
        if (allChecked) state.selectedContainers = [];
      } else {
        // First time unchecking: populate all names then remove this one
        if (state.selectedContainers.length === 0) {
          state.selectedContainers = sorted.map(c => c.name);
        }
        const idx = state.selectedContainers.indexOf(ct.name);
        if (idx >= 0) state.selectedContainers.splice(idx, 1);
      }
      saveSettings();
    });
    wrap.appendChild(lbl);
  }

  // Select All / Deselect All buttons
  const btnWrap = document.getElementById("containerBtns");
  if (btnWrap) {
    btnWrap.innerHTML = "";
    const btnAll = document.createElement("button");
    btnAll.className = "small";
    btnAll.textContent = "Select All";
    btnAll.onclick = () => {
      state.selectedContainers = [];
      wrap.querySelectorAll("input[type=checkbox]").forEach(cb => cb.checked = true);
      saveSettings();
    };
    const btnNone = document.createElement("button");
    btnNone.className = "small";
    btnNone.textContent = "Deselect All";
    btnNone.onclick = () => {
      state.selectedContainers = ["__none__"];
      wrap.querySelectorAll("input[type=checkbox]").forEach(cb => cb.checked = false);
      saveSettings();
    };
    btnWrap.appendChild(btnAll);
    btnWrap.appendChild(btnNone);
  }
}

/* ═══════════════════════════════════════════════════════════════════════════
   QUEST DATA (buddy-side tracker)
   ───────────────────────────────────────────────────────────────────────────
   Quest definitions come from /api/questdata (bundled tarkov.dev snapshot
   served by the host). The buddy chooses which quests to track; tracked
   quests light up matching loose loot items by BSG id and render zone
   overlays on the active map.
   ═══════════════════════════════════════════════════════════════════════════ */
let questData = null;          // { quests: [...] }
let questById = new Map();
let trackedItemIds = new Set();
let trackedZonesByMap = new Map(); // mapId -> [{zone, quest}]

const QUEST_LS_KEY = "eft_webradar_questdata_v1";

async function fetchQuestData() {
  try {
    // Hydrate from local cache first for instant UI.
    const cached = localStorage.getItem(QUEST_LS_KEY);
    if (cached) {
      try { applyQuestData(JSON.parse(cached)); } catch { /* ignore */ }
    }
    const res = await fetch("/api/questdata", { cache: "no-store" });
    if (!res.ok) return;
    const data = await res.json();
    if (!data || !Array.isArray(data.quests)) return;
    applyQuestData(data);
    try { localStorage.setItem(QUEST_LS_KEY, JSON.stringify(data)); } catch { /* ignore quota */ }
  } catch { /* offline — keep cached */ }
}

function applyQuestData(data) {
  questData = data;
  questById = new Map();
  for (const q of data.quests || []) {
    if (q && q.id) questById.set(q.id, q);
  }
  recomputeTrackedDerivatives();
  rebuildQuestList();
  updateQuestsBadge();
}

function recomputeTrackedDerivatives() {
  trackedItemIds = new Set();
  trackedZonesByMap = new Map();
  if (!questData) return;
  const tracked = state.trackedQuests || [];
  for (const id of tracked) {
    const q = questById.get(id);
    if (!q || !Array.isArray(q.objectives)) continue;
    for (const o of q.objectives) {
      if (!o) continue;
      if (o.itemId)       trackedItemIds.add(o.itemId);
      if (o.questItemId)  trackedItemIds.add(o.questItemId);
      if (o.markerItemId) trackedItemIds.add(o.markerItemId);
      if (Array.isArray(o.zones)) {
        for (const z of o.zones) {
          let m = z?.mapId || (Array.isArray(o.mapIds) && o.mapIds[0]) || q.mapId || "";
          if (!m) continue;
          m = m.toString().toLowerCase();
          m = ENGINE_TO_NORMALIZED[m] || m;
          if (!trackedZonesByMap.has(m)) trackedZonesByMap.set(m, []);
          trackedZonesByMap.get(m).push({ zone: z, quest: q, objective: o });
        }
      }
    }
  }
}

function isTrackedQuestItem(item) {
  if (!trackedItemIds || trackedItemIds.size === 0) return false;
  return !!(item && item.bsgId && trackedItemIds.has(item.bsgId));
}

function isQuestTracked(id) { return (state.trackedQuests || []).includes(id); }

function setQuestTracked(id, on) {
  const arr = state.trackedQuests || [];
  const idx = arr.indexOf(id);
  if (on && idx < 0) arr.push(id);
  else if (!on && idx >= 0) arr.splice(idx, 1);
  state.trackedQuests = arr;
  saveSettings();
  recomputeTrackedDerivatives();
  updateQuestsBadge();
}

function updateQuestsBadge() {
  if (!inputs.questsBadge) return;
  const total = questById.size;
  const tracked = (state.trackedQuests || []).length;
  inputs.questsBadge.textContent = `${tracked} / ${total}`;
}

function currentMapNorm() {
  const id = (radarData?.mapID || "").toString().toLowerCase();
  if (!id) return "";
  // Engine map id (Memory.MapID) → tarkov.dev normalizedName (used by
  // WebRadarQuestData). Without this, map filtering and zone lookup never
  // match for the buddy.
  return ENGINE_TO_NORMALIZED[id] || id;
}

const ENGINE_TO_NORMALIZED = {
  "bigmap":         "customs",
  "factory4_day":   "factory",
  "factory4_night": "factory",
  "woods":          "woods",
  "lighthouse":     "lighthouse",
  "shoreline":      "shoreline",
  "rezervbase":     "reserve",
  "interchange":    "interchange",
  "tarkovstreets":  "streets-of-tarkov",
  "laboratory":     "the-lab",
  "sandbox":        "ground-zero",
  "sandbox_high":   "ground-zero",
  "labyrinth":      "labyrinth",
};

function rebuildQuestList() {
  const wrap = document.getElementById("questList");
  if (!wrap) return;
  wrap.innerHTML = "";
  if (!questData) return;

  const q = (state.questSearch || "").trim().toLowerCase();
  const onlyActive = !!state.questsOnlyActiveMap;
  const activeMap = currentMapNorm();

  const items = [];
  for (const quest of questData.quests) {
    if (!quest) continue;
    if (onlyActive && activeMap && quest.mapId && quest.mapId.toString().toLowerCase() !== activeMap) continue;
    if (q.length > 0) {
      const hay = `${quest.name || ""} ${quest.trader || ""} ${quest.id || ""}`.toLowerCase();
      if (!hay.includes(q)) continue;
    }
    items.push(quest);
  }

  items.sort((a, b) => {
    const ta = (a.trader || "zz").localeCompare(b.trader || "zz");
    if (ta !== 0) return ta;
    return (a.name || "").localeCompare(b.name || "");
  });

  for (const quest of items) {
    const tracked = isQuestTracked(quest.id);
    const lbl = document.createElement("label");
    lbl.className = "container-item";
    const tag = quest.kappaRequired ? '<span class="quest-kappa" title="Kappa required">★</span>' : "";
    const tr = quest.trader ? `<span class="quest-trader">${esc(quest.trader)}</span>` : "";
    lbl.innerHTML = `<span class="toggle-switch small"><input type="checkbox" ${tracked ? "checked" : ""}><span class="slider"></span></span><span class="cname">${tag}${esc(quest.name || quest.id)} ${tr}</span>`;
    const cb = lbl.querySelector("input");
    cb.addEventListener("change", () => setQuestTracked(quest.id, cb.checked));
    wrap.appendChild(lbl);
  }
}

if (inputs.questsTrackKappa) {
  inputs.questsTrackKappa.onclick = () => {
    if (!questData) return;
    const onlyActive = !!state.questsOnlyActiveMap;
    const activeMap = currentMapNorm();
    const set = new Set(state.trackedQuests || []);
    for (const q of questData.quests) {
      if (!q.kappaRequired) continue;
      if (onlyActive && activeMap && q.mapId && q.mapId.toString().toLowerCase() !== activeMap) continue;
      set.add(q.id);
    }
    state.trackedQuests = [...set];
    saveSettings();
    recomputeTrackedDerivatives();
    rebuildQuestList();
    updateQuestsBadge();
  };
}

if (inputs.questsClear) {
  inputs.questsClear.onclick = () => {
    state.trackedQuests = [];
    saveSettings();
    recomputeTrackedDerivatives();
    rebuildQuestList();
    updateQuestsBadge();
  };
}

/* ═══════════════════════════════════════════════════════════════════════════
   QUEST ZONE DRAWING
   ═══════════════════════════════════════════════════════════════════════════ */
function drawQuestZones(map, cx, cy, rotRad, mapRect, mapId) {
  if (!trackedZonesByMap.size) return;
  const raw = (mapId || "").toString().toLowerCase();
  if (!raw) return;
  const id = ENGINE_TO_NORMALIZED[raw] || raw;
  const zones = trackedZonesByMap.get(id);
  if (!zones || !zones.length) return;

  const col = state.colors.questZone;

  for (const entry of zones) {
    const z = entry.zone;
    if (!z) continue;

    // Outline polygon
    if (Array.isArray(z.outline) && z.outline.length >= 9) {
      ctx.save();
      ctx.beginPath();
      let started = false;
      for (let i = 0; i + 2 < z.outline.length; i += 3) {
        const wx = z.outline[i], wz = z.outline[i + 2];
        const pm = worldToMapUnzoomed(wx, wz, map);
        const s = mapXYToScreen(pm.x, pm.y, mapRect, cx, cy, rotRad);
        if (!started) { ctx.moveTo(s.px, s.py); started = true; }
        else ctx.lineTo(s.px, s.py);
      }
      ctx.closePath();
      ctx.fillStyle = col;
      ctx.globalAlpha = 0.12;
      ctx.fill();
      ctx.globalAlpha = 0.7;
      ctx.strokeStyle = col;
      ctx.lineWidth = 1.2;
      ctx.stroke();
      ctx.restore();
    }

    if (z.hasPosition) {
      const pm = worldToMapUnzoomed(z.x, z.z, map);
      const s = mapXYToScreen(pm.x, pm.y, mapRect, cx, cy, rotRad);
      ctx.save();
      ctx.beginPath();
      ctx.arc(s.px, s.py, 4, 0, Math.PI * 2);
      ctx.strokeStyle = col;
      ctx.lineWidth = 1.4;
      ctx.globalAlpha = 0.85;
      ctx.stroke();
      ctx.fillStyle = col;
      ctx.globalAlpha = 0.9;
      ctx.font = "600 10px system-ui, sans-serif";
      ctx.textAlign = "center";
      ctx.shadowColor = "rgba(0,0,0,.7)";
      ctx.shadowBlur = 3;
      ctx.fillText(entry.quest.name || "", s.px, s.py - 7);
      ctx.restore();
    }
  }
}

/* ═══════════════════════════════════════════════════════════════════════════
   WORLD FEATURES (switches, doors, transits, BTR, airdrops)
   ═══════════════════════════════════════════════════════════════════════════ */
function drawSwitches(switches, map, cx, cy, rotRad, mapRect, hitList) {
  if (!switches || !switches.length) return;
  const col = state.colors.switch;
  for (const sw of switches) {
    if (!sw) continue;
    const pm = worldToMapUnzoomed(sw.worldX, sw.worldZ, map);
    const s = mapXYToScreen(pm.x, pm.y, mapRect, cx, cy, rotRad);
    ctx.save();
    ctx.fillStyle = col;
    ctx.globalAlpha = 0.85;
    ctx.beginPath();
    ctx.arc(s.px, s.py, 3, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
    hitList.push({ kind: "switch", px: s.px, py: s.py, r: 8, data: sw });
  }
}

function drawDoors(doors, map, cx, cy, rotRad, mapRect, hitList) {
  if (!doors || !doors.length) return;
  // Door state flags: 1=Locked, 2=Shut, 4=Open, 8=Interacting, 16=Breaching
  for (const d of doors) {
    if (!d) continue;
    const open = (d.state & 4) !== 0;
    const col = open ? state.colors.doorOpen : state.colors.doorLocked;
    const pm = worldToMapUnzoomed(d.worldX, d.worldZ, map);
    const s = mapXYToScreen(pm.x, pm.y, mapRect, cx, cy, rotRad);
    ctx.save();
    ctx.strokeStyle = col;
    ctx.lineWidth = 1.4;
    ctx.globalAlpha = 0.85;
    ctx.strokeRect(s.px - 2.5, s.py - 2.5, 5, 5);
    ctx.restore();
    hitList.push({ kind: "door", px: s.px, py: s.py, r: 8, data: d });
  }
}

function drawTransits(transits, map, cx, cy, rotRad, mapRect, hitList) {
  if (!transits || !transits.length) return;
  const col = state.colors.transit;
  for (const t of transits) {
    if (!t) continue;
    const pm = worldToMapUnzoomed(t.worldX, t.worldZ, map);
    const s = mapXYToScreen(pm.x, pm.y, mapRect, cx, cy, rotRad);
    ctx.save();
    ctx.strokeStyle = col;
    ctx.lineWidth = 1.6;
    ctx.globalAlpha = t.isActive ? 0.95 : 0.5;
    ctx.beginPath();
    ctx.arc(s.px, s.py, 6, 0, Math.PI * 2);
    ctx.stroke();
    ctx.fillStyle = col;
    ctx.font = "600 10px system-ui, sans-serif";
    ctx.textAlign = "center";
    ctx.shadowColor = "rgba(0,0,0,.7)";
    ctx.shadowBlur = 3;
    ctx.fillText(t.name || "Transit", s.px, s.py - 9);
    ctx.restore();
    hitList.push({ kind: "transit", px: s.px, py: s.py, r: 12, data: t });
  }
}

function drawBtr(btr, map, cx, cy, rotRad, mapRect, hitList) {
  if (!btr) return;
  const col = state.colors.btr;

  // BTR stops (positions + names only — no connecting route line).
  if (state.showBtrRoute && Array.isArray(btr.routeStops) && btr.routeStops.length > 0) {
    for (const st of btr.routeStops) {
      const pm = worldToMapUnzoomed(st.worldX, st.worldZ, map);
      const s = mapXYToScreen(pm.x, pm.y, mapRect, cx, cy, rotRad);
      ctx.save();
      ctx.fillStyle = col;
      ctx.globalAlpha = 0.85;
      ctx.beginPath();
      ctx.arc(s.px, s.py, 3, 0, Math.PI * 2);
      ctx.fill();
      if (st.name) {
        ctx.fillStyle = col;
        ctx.globalAlpha = 1;
        ctx.font = "600 9px system-ui, sans-serif";
        ctx.textAlign = "center";
        ctx.shadowColor = "rgba(0,0,0,.7)";
        ctx.shadowBlur = 3;
        ctx.fillText(st.name, s.px, s.py - 6);
      }
      ctx.restore();
    }
  }

  const pm = worldToMapUnzoomed(btr.worldX, btr.worldZ, map);
  const s = mapXYToScreen(pm.x, pm.y, mapRect, cx, cy, rotRad);
  ctx.save();
  ctx.fillStyle = col;
  ctx.globalAlpha = 0.95;
  ctx.beginPath();
  ctx.arc(s.px, s.py, 6, 0, Math.PI * 2);
  ctx.fill();
  ctx.fillStyle = "#000";
  ctx.font = "700 9px system-ui, sans-serif";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillText("BTR", s.px, s.py + 0.5);
  ctx.restore();

  hitList.push({ kind: "btr", px: s.px, py: s.py, r: 14, data: btr });
}

function drawAirdrops(drops, map, cx, cy, rotRad, mapRect, hitList) {
  if (!drops || !drops.length) return;
  const col = state.colors.airdrop;
  for (const a of drops) {
    if (!a) continue;
    const pm = worldToMapUnzoomed(a.worldX, a.worldZ, map);
    const s = mapXYToScreen(pm.x, pm.y, mapRect, cx, cy, rotRad);
    ctx.save();
    ctx.strokeStyle = col;
    ctx.lineWidth = 2;
    ctx.globalAlpha = 0.9;
    ctx.beginPath();
    ctx.moveTo(s.px, s.py - 6);
    ctx.lineTo(s.px + 5, s.py + 4);
    ctx.lineTo(s.px - 5, s.py + 4);
    ctx.closePath();
    ctx.stroke();
    ctx.restore();
    hitList.push({ kind: "airdrop", px: s.px, py: s.py, r: 12, data: a });
  }
}

/* ═══════════════════════════════════════════════════════════════════════════
   MOBILE-FIRST BOTTOM SHELL  (Phase 5)
   ───────────────────────────────────────────────────────────────────────────
   The bottom tab bar + sliding bottom sheets are the primary UI for phone /
   tablet / AnyDesk users. Each sheet proxies a subset of the legacy sidebar
   controls — when a sheet toggle / range / button changes, we mutate the
   underlying `<input id="...">` and dispatch its 'input'/'change' event so
   the existing listen()/bind() handlers do all the state + persistence work.
   No state is duplicated.
   ═══════════════════════════════════════════════════════════════════════════ */

const presetChipEl = document.getElementById("presetChip");
const presetNameEl = document.getElementById("presetName");

/* ═══════════════════════════════════════════════════════════════════════════
   WEB-CLIENT PRESETS
   ───────────────────────────────────────────────────────────────────────────
   The web client has its own preset system, separate from the desktop host's.
   Each buddy picks a view that suits how they're playing (spotting, fighting,
   looting, questing). Presets only flip the legacy sidebar inputs by ID —
   exactly the same as user toggles — so they hit the existing persistence
   pipeline and survive page reloads.

   Drift detection: any manual change to a tracked key automatically demotes
   the active preset to "Custom". Switching presets re-applies all tracked
   keys and saves once. The chip is tap-to-cycle (forward).
   ═══════════════════════════════════════════════════════════════════════════ */

const WEB_PRESET_CUSTOM = "Custom";

const WEB_PRESETS = [
  // ── Spotter ────────────────────────────────────────────────────────────
  // Help the squad — players visible, no aim-line clutter so the PMC can
  // read the loot dots underneath. Important loot only (cuts the noise).
  {
    id: "Spotter",
    name: "Spotter",
    desc: "Help the squad — important loot + threats, no aim-line clutter",
    state: {
      showPlayers: true, showAim: false, showNames: true,
      showHeight: true, showGroups: true,
      showLoot: true, showLootNames: true, lootMode: "important",
      lootHideNormal: false,
      showContainers: true, showContainerNames: false,
      showCorpses: false,
      showExfils: true, showDoors: true, showAirdrops: true,
      showSwitches: false, showTransits: true,
      showBtr: true, showBtrRoute: false,
      showQuestItems: true, showAllQuestItems: false, showQuestZones: false,
    },
  },
  // ── Battle Buddy ───────────────────────────────────────────────────────
  // Fight-focused — aim lines, names, height. Loot off so the map stays
  // legible during contact.
  {
    id: "Battle",
    name: "Battle Buddy",
    desc: "Combat-focused — players + aim lines, no loot clutter",
    state: {
      showPlayers: true, showAim: true, showNames: true,
      showHeight: true, showGroups: true,
      showLoot: false, lootHideNormal: true,
      showContainers: false, showCorpses: false,
      showExfils: true, showDoors: true, showAirdrops: true,
      showSwitches: false, showTransits: false,
      showBtr: true, showBtrRoute: false,
      showQuestItems: false, showAllQuestItems: false, showQuestZones: false,
    },
  },
  // ── Loot Hunter ────────────────────────────────────────────────────────
  // Max-info loot view — every item, every container, names on.
  {
    id: "Loot",
    name: "Loot Hunter",
    desc: "Every item + container — names on, no filtering",
    state: {
      showPlayers: true, showAim: true, showNames: false,
      showHeight: true, showGroups: true,
      showLoot: true, showLootNames: true, lootMode: "all",
      lootHideNormal: false,
      showContainers: true, showContainerNames: true,
      showCorpses: true,
      showExfils: true, showDoors: true, showAirdrops: true,
      showSwitches: true, showTransits: true,
      showBtr: true, showBtrRoute: false,
      showQuestItems: true, showAllQuestItems: false, showQuestZones: false,
    },
  },
  // ── Quest Helper ───────────────────────────────────────────────────────
  // Quest items + zones only — quest-mode loot filter, switches on.
  {
    id: "Quests",
    name: "Quest Helper",
    desc: "Quest items, zones, and mechanics — nothing else",
    state: {
      showPlayers: true, showAim: true, showNames: true,
      showHeight: true, showGroups: true,
      showLoot: true, showLootNames: true, lootMode: "quest",
      lootHideNormal: false,
      showContainers: false, showCorpses: false,
      showExfils: true, showDoors: true, showAirdrops: false,
      showSwitches: true, showTransits: true,
      showBtr: false, showBtrRoute: false,
      showQuestItems: true, showAllQuestItems: false, showQuestZones: true,
    },
  },
];

const WEB_PRESET_IDS = [...WEB_PRESETS.map(p => p.id), WEB_PRESET_CUSTOM];

function findWebPreset(id) {
  return WEB_PRESETS.find(p => p.id === id) || null;
}
function webPresetName(id) {
  return id === WEB_PRESET_CUSTOM ? "Custom" : (findWebPreset(id)?.name || id);
}

// Suppresses drift checks while a preset is being applied — we're mutating
// every tracked key in a tight loop, so the in-progress states between
// each dispatch would otherwise demote us back to Custom mid-apply.
let _applyingWebPreset = false;

function applyWebPreset(id) {
  if (!WEB_PRESET_IDS.includes(id)) return;

  if (id === WEB_PRESET_CUSTOM) {
    state.activeWebPreset = WEB_PRESET_CUSTOM;
    saveSettings();
    updatePresetChip();
    refreshOpenSheetState();
    return;
  }

  const p = findWebPreset(id);
  if (!p) return;

  _applyingWebPreset = true;
  try {
    for (const [key, value] of Object.entries(p.state)) {
      const inp = inputs[key];
      if (!inp) continue;
      if (inp.type === "checkbox") {
        if (inp.checked !== !!value) {
          inp.checked = !!value;
          inp.dispatchEvent(new Event("change", { bubbles: true }));
        }
      } else {
        const str = String(value);
        if (inp.value !== str) {
          inp.value = str;
          inp.dispatchEvent(new Event("input",  { bubbles: true }));
          inp.dispatchEvent(new Event("change", { bubbles: true }));
        }
      }
    }
  } finally {
    _applyingWebPreset = false;
  }

  state.activeWebPreset = id;
  saveSettings();
  updatePresetChip();
  refreshOpenSheetState();
}

function webPresetMatches() {
  const id = state.activeWebPreset;
  if (!id || id === WEB_PRESET_CUSTOM) return true;
  const p = findWebPreset(id);
  if (!p) return false;
  for (const [key, value] of Object.entries(p.state)) {
    const cur = state[key];
    // Loose-compare booleans (state may serialise back as bool); strict-compare
    // strings / numbers.
    if (typeof value === "boolean") {
      if (!!cur !== value) return false;
    } else if (cur !== value) {
      return false;
    }
  }
  return true;
}

function reconcileWebPresetDrift() {
  if (_applyingWebPreset) return;
  if (state.activeWebPreset === WEB_PRESET_CUSTOM) return;
  if (!webPresetMatches()) {
    state.activeWebPreset = WEB_PRESET_CUSTOM;
    saveSettings();
    updatePresetChip();
    refreshOpenSheetState();
  }
}

function cycleWebPreset(delta) {
  let idx = WEB_PRESET_IDS.indexOf(state.activeWebPreset);
  if (idx < 0) idx = WEB_PRESET_IDS.length - 1;
  const next = (idx + delta + WEB_PRESET_IDS.length) % WEB_PRESET_IDS.length;
  applyWebPreset(WEB_PRESET_IDS[next]);
}

function updatePresetChip() {
  if (!presetChipEl) return;
  const id = state.activeWebPreset || WEB_PRESET_CUSTOM;
  presetNameEl.textContent = webPresetName(id);
  presetChipEl.hidden = false;
}

// Make the chip itself a click target so a buddy can cycle presets without
// digging into the Settings sheet. Pointer-events are explicitly enabled
// here (the chip CSS sets pointer-events: none for layout reasons).
if (presetChipEl) {
  presetChipEl.style.pointerEvents = "auto";
  presetChipEl.style.cursor = "pointer";
  presetChipEl.title = "Tap to cycle preset";
  presetChipEl.addEventListener("click", () => cycleWebPreset(+1));
}

/* ── Sheet definitions ─────────────────────────────────────────────────────
   Each sheet describes its rows declaratively. Row types:
     - toggle      : pill switch bound to a checkbox input
     - range       : slider bound to a range input, with formatted readout
     - chips       : grid of pill chips (for price presets, layer multi-toggle)
     - select      : dropdown bound to <select>
     - button      : primary/secondary action
     - section     : heading text
     - hint        : muted descriptive line
*/
const SHEET_DEFS = {
  players: {
    title: "Players",
    rows: [
      { type: "section", text: "Display" },
      { type: "toggle", label: "Show Players",  id: "showPlayers" },
      { type: "toggle", label: "Aim Lines",     id: "showAim" },
      { type: "toggle", label: "Names",         id: "showNames" },
      { type: "toggle", label: "Height Arrows", id: "showHeight" },
      { type: "toggle", label: "Group Lines",   id: "showGroups" },
      { type: "range",  label: "Player Size",   id: "playerSize" },

      { type: "section", text: "Aimview (first-person PiP)" },
      { type: "toggle", label: "Show Aimview",     id: "showAimview" },
      { type: "toggle", label: "Show Aim Status",  id: "aimviewShowAimStatus" },
      { type: "toggle", label: "Mirror Host FOV",  id: "aimviewFollowHostFov",
        hint: "Match the host's live FOV / scope zoom" },
    ],
  },
  loot: {
    title: "Loot",
    rows: [
      { type: "section", text: "Loot" },
      { type: "toggle", label: "Show Loot",       id: "showLoot" },
      { type: "toggle", label: "Loot Names",      id: "showLootNames" },
      { type: "toggle", label: "Hide Normal",     id: "lootHideNormal",
        hint: "Keep Important / Wishlist / Quest only" },
      { type: "select", label: "Display Mode",    id: "lootMode" },
      { type: "range",  label: "Min Price",       id: "lootMinPrice" },
      { type: "chips",  label: "Price Preset",    target: "lootMinPrice",
        options: [
          { value: 0,       text: "Any" },
          { value: 10000,   text: "10K" },
          { value: 50000,   text: "50K" },
          { value: 100000,  text: "100K" },
          { value: 200000,  text: "200K" },
          { value: 500000,  text: "500K" },
          { value: 1000000, text: "1M" },
        ],
      },
      { type: "range",  label: "Max Distance",    id: "lootMaxDist" },

      { type: "section", text: "Containers & Corpses" },
      { type: "toggle", label: "Show Containers", id: "showContainers" },
      { type: "toggle", label: "Container Names", id: "showContainerNames" },
      { type: "toggle", label: "Show Corpses",    id: "showCorpses" },

      { type: "section", text: "Wishlist & Blacklist" },
      { type: "hint",   text: "Open Advanced (Settings → Advanced) to manage the searchable wishlist & blacklist." },
    ],
  },
  layers: {
    title: "Layers",
    rows: [
      { type: "section", text: "Map" },
      { type: "toggle", label: "Show Map",        id: "showMap" },
      { type: "toggle", label: "Satellite Map",   id: "useSatellite",
        hint: "Use satellite tiles when available" },
      { type: "toggle", label: "Rotate With Local", id: "rotateWithLocal" },

      { type: "section", text: "World" },
      { type: "toggle", label: "Exfils",          id: "showExfils" },
      { type: "toggle", label: "Switches",        id: "showSwitches" },
      { type: "toggle", label: "Doors (keyed)",   id: "showDoors" },
      { type: "toggle", label: "Transits",        id: "showTransits" },
      { type: "toggle", label: "BTR",             id: "showBtr" },
      { type: "toggle", label: "BTR Stops",       id: "showBtrRoute" },
      { type: "toggle", label: "Airdrops",        id: "showAirdrops" },

      { type: "section", text: "Quests" },
      { type: "toggle", label: "Quest Items",     id: "showQuestItems" },
      { type: "toggle", label: "All Quest Items", id: "showAllQuestItems" },
      { type: "toggle", label: "Quest Zones",     id: "showQuestZones" },
      { type: "toggle", label: "Only Active Map", id: "questsOnlyActiveMap" },
    ],
  },
  settings: {
    title: "Settings",
    rows: [
      { type: "section", text: "Preset" },
      { type: "hint", text: "Switch view at a glance. Any manual tweak shifts to Custom." },
      { type: "presetChips" },

      { type: "section", text: "Map" },
      { type: "range",  label: "Zoom",            id: "zoom" },
      { type: "range",  label: "Poll Rate",       id: "pollMs" },

      { type: "section", text: "Camera" },
      { type: "toggle", label: "Free Mode",       id: "freeMode",
        hint: "Off = camera follows your player" },
      { type: "button", label: "Center on Local", click: () => {
          const b = document.getElementById("centerOnLocal");
          if (b) b.click();
        } },
      { type: "hint",   text: "Tip: double-tap the map to recenter on your player." },

      { type: "section", text: "Preferences" },
      { type: "toggle", label: "Hover-open Sidebar", id: "hoverOpenSidebar" },

      { type: "section", text: "Advanced" },
      { type: "button", label: "Open Advanced Sidebar", primary: true, click: () => {
          // Reuse the existing toggleSidebarPinned() helper at the top of this file.
          if (typeof toggleSidebarPinned === "function") toggleSidebarPinned();
          closeBottomSheet();
        } },
      { type: "button", label: "Reset Settings", click: () => {
          if (confirm("Reset all web client settings to defaults?")) {
            const b = document.getElementById("resetSettings");
            if (b) b.click();
          }
        } },
    ],
  },
};

/* ── Range-row value formatters (mirror the sidebar's updateRangeValue) ── */
function fmtRange(id, v) {
  switch (id) {
    case "zoom":            return Number(v).toFixed(2);
    case "pollMs":          return v + " ms";
    case "lootMinPrice":    return typeof formatPrice === "function" ? formatPrice(Number(v)) : String(v);
    case "lootMaxDist":     return Number(v) <= 0 ? "Off" : v + " m";
    case "playerSize":      return String(v);
    default:                return String(v);
  }
}

/* ── Sheet renderer ───────────────────────────────────────────────────────── */
const bsheetEl       = document.getElementById("bottomSheet");
const bsheetTitleEl  = document.getElementById("bsheetTitle");
const bsheetBodyEl   = document.getElementById("bsheetBody");
const bsheetGrabber  = document.getElementById("bsheetGrabber");
const bsheetCloseBtn = document.getElementById("bsheetClose");
const bottomBarEl    = document.getElementById("bottomBar");

let _activeSheet = null;

function renderSheet(name) {
  const def = SHEET_DEFS[name];
  if (!def || !bsheetBodyEl) return;
  bsheetTitleEl.textContent = def.title;

  const frag = document.createDocumentFragment();
  for (const row of def.rows) {
    frag.appendChild(buildRow(row));
  }
  bsheetBodyEl.replaceChildren(frag);
}

function buildRow(row) {
  if (row.type === "section") {
    const el = document.createElement("div");
    el.className = "bs-section";
    el.textContent = row.text;
    return el;
  }
  if (row.type === "hint") {
    const el = document.createElement("p");
    el.className = "hint";
    el.textContent = row.text;
    return el;
  }
  if (row.type === "button") {
    const el = document.createElement("button");
    el.className = "bs-btn" + (row.primary ? " primary" : "");
    el.textContent = row.label;
    el.style.marginBottom = "8px";
    el.addEventListener("click", row.click);
    return el;
  }
  if (row.type === "toggle") {
    const src = document.getElementById(row.id);
    const wrap = document.createElement("div");
    wrap.className = "bs-row";

    const stack = document.createElement("div");
    stack.className = "bs-stack";
    const lbl = document.createElement("span");
    lbl.className = "bs-label";
    lbl.textContent = row.label;
    stack.appendChild(lbl);
    if (row.hint) {
      const h = document.createElement("span");
      h.className = "bs-hint";
      h.textContent = row.hint;
      stack.appendChild(h);
    }
    wrap.appendChild(stack);

    const tog = document.createElement("span");
    tog.className = "bs-toggle";
    if (src && src.checked) tog.classList.add("on");
    wrap.appendChild(tog);

    wrap.addEventListener("click", () => {
      if (!src) return;
      src.checked = !src.checked;
      src.dispatchEvent(new Event("change", { bubbles: true }));
      tog.classList.toggle("on", src.checked);
      refreshOpenSheetState();
    });
    return wrap;
  }
  if (row.type === "range") {
    const src = document.getElementById(row.id);
    const wrap = document.createElement("div");
    wrap.className = "bs-row";

    const lbl = document.createElement("span");
    lbl.className = "bs-label";
    lbl.style.minWidth = "92px";
    lbl.textContent = row.label;
    wrap.appendChild(lbl);

    const grp = document.createElement("div");
    grp.className = "bs-range";

    const r = document.createElement("input");
    r.type = "range";
    if (src) {
      r.min = src.min; r.max = src.max; r.step = src.step;
      r.value = src.value;
    }
    grp.appendChild(r);

    const v = document.createElement("span");
    v.className = "bs-val";
    v.textContent = src ? fmtRange(row.id, src.value) : "";
    grp.appendChild(v);

    wrap.appendChild(grp);

    r.addEventListener("input", () => {
      if (!src) return;
      src.value = r.value;
      src.dispatchEvent(new Event("input", { bubbles: true }));
      src.dispatchEvent(new Event("change", { bubbles: true }));
      v.textContent = fmtRange(row.id, r.value);
    });
    return wrap;
  }
  if (row.type === "select") {
    const src = document.getElementById(row.id);
    const wrap = document.createElement("div");
    wrap.className = "bs-row";

    const lbl = document.createElement("span");
    lbl.className = "bs-label";
    lbl.textContent = row.label;
    wrap.appendChild(lbl);

    const sel = document.createElement("select");
    sel.className = "text-input";
    sel.style.maxWidth = "55%";
    if (src) {
      for (const o of src.options)
        sel.appendChild(o.cloneNode(true));
      sel.value = src.value;
      sel.addEventListener("change", () => {
        src.value = sel.value;
        src.dispatchEvent(new Event("change", { bubbles: true }));
      });
    }
    wrap.appendChild(sel);
    return wrap;
  }
  if (row.type === "chips") {
    const src = document.getElementById(row.target);
    const wrap = document.createElement("div");
    wrap.className = "bs-row-flex";

    for (const o of row.options) {
      const c = document.createElement("button");
      c.className = "bs-chip";
      c.textContent = o.text;
      c.dataset.value = String(o.value);
      if (src && Number(src.value) === o.value) c.classList.add("active");
      c.addEventListener("click", () => {
        if (!src) return;
        src.value = String(o.value);
        src.dispatchEvent(new Event("input", { bubbles: true }));
        src.dispatchEvent(new Event("change", { bubbles: true }));
        refreshOpenSheetState();
      });
      wrap.appendChild(c);
    }
    return wrap;
  }
  if (row.type === "presetChips") {
    // Web-client preset selector — one chip per preset + Custom. Tapping a
    // chip applies the preset (via applyWebPreset) which dispatches change
    // events on the legacy inputs so the existing pipeline persists state.
    const wrap = document.createElement("div");
    wrap.className = "bs-row-flex";
    wrap.style.flexWrap = "wrap";

    for (const id of WEB_PRESET_IDS) {
      const c = document.createElement("button");
      c.className = "bs-chip";
      c.textContent = webPresetName(id);
      c.dataset.presetId = id;
      const def = findWebPreset(id);
      if (def && def.desc) c.title = def.desc;
      if (state.activeWebPreset === id) c.classList.add("active");
      c.addEventListener("click", () => applyWebPreset(id));
      wrap.appendChild(c);
    }
    return wrap;
  }
  const fallback = document.createElement("div");
  return fallback;
}

/* Refresh on/off / active marks across the currently-open sheet without
   re-rendering (avoids losing scroll position when a row toggles). */
function refreshOpenSheetState() {
  if (!_activeSheet) return;
  const def = SHEET_DEFS[_activeSheet];
  if (!def) return;
  const rows = bsheetBodyEl.children;
  let idx = 0;
  for (const row of def.rows) {
    const el = rows[idx++];
    if (!el) continue;
    if (row.type === "toggle") {
      const src = document.getElementById(row.id);
      const tog = el.querySelector(".bs-toggle");
      if (src && tog) tog.classList.toggle("on", src.checked);
    } else if (row.type === "chips") {
      const src = document.getElementById(row.target);
      if (!src) continue;
      el.querySelectorAll(".bs-chip").forEach(c => {
        c.classList.toggle("active", Number(c.dataset.value) === Number(src.value));
      });
    } else if (row.type === "presetChips") {
      el.querySelectorAll(".bs-chip").forEach(c => {
        c.classList.toggle("active", c.dataset.presetId === state.activeWebPreset);
      });
    } else if (row.type === "range") {
      const src = document.getElementById(row.id);
      const r = el.querySelector('input[type="range"]');
      const v = el.querySelector(".bs-val");
      if (src && r && Number(r.value) !== Number(src.value)) {
        r.value = src.value;
        if (v) v.textContent = fmtRange(row.id, src.value);
      }
    }
  }
}

function openBottomSheet(name) {
  if (!bsheetEl) return;
  _activeSheet = name;
  renderSheet(name);
  bsheetEl.hidden = false;
  bsheetEl.classList.remove("closing");
  // Highlight the active tab.
  for (const t of bottomBarEl.querySelectorAll(".bb-tab"))
    t.classList.toggle("active", t.dataset.sheet === name);
}

function closeBottomSheet() {
  if (!bsheetEl || bsheetEl.hidden) return;
  bsheetEl.classList.add("closing");
  for (const t of bottomBarEl.querySelectorAll(".bb-tab")) t.classList.remove("active");
  setTimeout(() => {
    bsheetEl.hidden = true;
    bsheetEl.classList.remove("closing");
    _activeSheet = null;
  }, 240);
}

if (bottomBarEl) {
  for (const tab of bottomBarEl.querySelectorAll(".bb-tab")) {
    tab.addEventListener("click", () => {
      const name = tab.dataset.sheet;
      if (_activeSheet === name) closeBottomSheet();
      else openBottomSheet(name);
    });
  }
}
if (bsheetCloseBtn) bsheetCloseBtn.addEventListener("click", closeBottomSheet);

/* ── Sheet swipe-down to dismiss ──────────────────────────────────────────── */
(function () {
  if (!bsheetGrabber || !bsheetEl) return;
  let startY = 0;
  let dragging = false;
  let baseY = 0;

  const onStart = e => {
    const t = e.touches ? e.touches[0] : e;
    startY = t.clientY;
    baseY = 0;
    dragging = true;
    bsheetEl.classList.add("dragging");
  };
  const onMove = e => {
    if (!dragging) return;
    const t = e.touches ? e.touches[0] : e;
    const dy = Math.max(0, t.clientY - startY);
    baseY = dy;
    bsheetEl.style.transform = `translateY(${dy}px)`;
    e.preventDefault();
  };
  const onEnd = () => {
    if (!dragging) return;
    dragging = false;
    bsheetEl.classList.remove("dragging");
    bsheetEl.style.transform = "";
    if (baseY > 80) closeBottomSheet();
  };

  bsheetGrabber.addEventListener("touchstart", onStart, { passive: false });
  bsheetGrabber.addEventListener("touchmove",  onMove,  { passive: false });
  bsheetGrabber.addEventListener("touchend",   onEnd);
  bsheetGrabber.addEventListener("mousedown",  onStart);
  window.addEventListener("mousemove",         onMove);
  window.addEventListener("mouseup",           onEnd);
})();

/* Live-update the open sheet whenever any legacy input fires its change
   event, so the bottom shell never goes stale (e.g. when the user toggles
   from the FAB radial or the sidebar). */
document.addEventListener("change", refreshOpenSheetState, true);
document.addEventListener("input",  refreshOpenSheetState, true);

// Drift detection — runs after every config-relevant change. listen()/bind()
// updates `state` before firing 'change' on the bubble phase, so by the time
// this capture listener sees it, state already reflects the new value.
// We use a microtask so we read state AFTER the bubble phase listen() has
// run, which mutates state.
document.addEventListener("change", () => queueMicrotask(reconcileWebPresetDrift), true);
document.addEventListener("input",  () => queueMicrotask(reconcileWebPresetDrift), true);

/* ═══════════════════════════════════════════════════════════════════════════
   FAB RADIAL MENU (Phase 5 — mirrors desktop QuickMenu)
   Hold the FAB → radial opens at center. Drag to a slice (or release on a
   slice) → toggles that action. Tap-then-release without dragging also opens
   the radial; tapping a slice then toggles. Works with mouse and touch.
   ═══════════════════════════════════════════════════════════════════════════ */

const RADIAL_ACTIONS = [
  { id: "showAim",        ico: "→", lbl: "Aim" },
  { id: "showLoot",       ico: "◆", lbl: "Loot" },
  { id: "showExfils",     ico: "▲", lbl: "Exfils" },
  { id: "showDoors",      ico: "□", lbl: "Doors" },
  { id: "showAirdrops",   ico: "✈", lbl: "Airdrop" },
  { id: "showContainers", ico: "▣", lbl: "Containers" },
  { id: "showNames",      ico: "A", lbl: "Names" },
  { id: "showQuestItems", ico: "❁", lbl: "Quests" },
];

const fabEl       = document.getElementById("fab");
const radialOverlayEl = document.getElementById("radialOverlay");
const radialMenuEl    = document.getElementById("radialMenu");

function buildRadial() {
  if (!radialMenuEl) return;
  radialMenuEl.replaceChildren();
  const N = RADIAL_ACTIONS.length;
  const radius = 100; // px from center
  for (let i = 0; i < N; i++) {
    const a = RADIAL_ACTIONS[i];
    const angle = (-Math.PI / 2) + (i / N) * Math.PI * 2; // start at top
    const x = Math.cos(angle) * radius;
    const y = Math.sin(angle) * radius;

    const el = document.createElement("div");
    el.className = "radial-slice";
    el.style.transform = `translate(${x}px, ${y}px)`;
    el.dataset.action = a.id;

    const ico = document.createElement("span");
    ico.className = "rs-ico";
    ico.textContent = a.ico;
    el.appendChild(ico);

    const lbl = document.createElement("span");
    lbl.className = "rs-lbl";
    lbl.textContent = a.lbl;
    el.appendChild(lbl);

    radialMenuEl.appendChild(el);
  }
  const center = document.createElement("div");
  center.className = "radial-center";
  center.textContent = "Quick";
  radialMenuEl.appendChild(center);
}

function refreshRadialState() {
  if (!radialMenuEl) return;
  for (const slice of radialMenuEl.querySelectorAll(".radial-slice")) {
    const id = slice.dataset.action;
    const src = document.getElementById(id);
    slice.classList.toggle("on", !!(src && src.checked));
  }
}

function openRadial() {
  if (!radialOverlayEl) return;
  refreshRadialState();
  radialOverlayEl.hidden = false;
  fabEl.classList.add("open");
}
function closeRadial() {
  if (!radialOverlayEl) return;
  radialOverlayEl.hidden = true;
  fabEl.classList.remove("open");
  if (radialMenuEl) {
    for (const s of radialMenuEl.querySelectorAll(".radial-slice"))
      s.classList.remove("hover");
  }
}

function radialSliceAt(clientX, clientY) {
  if (!radialMenuEl) return null;
  let best = null, bestDist = 9999;
  for (const slice of radialMenuEl.querySelectorAll(".radial-slice")) {
    const r = slice.getBoundingClientRect();
    const cx = r.left + r.width / 2;
    const cy = r.top + r.height / 2;
    const dx = clientX - cx, dy = clientY - cy;
    const d = Math.sqrt(dx * dx + dy * dy);
    if (d < bestDist && d < 60) { bestDist = d; best = slice; }
  }
  return best;
}

function toggleRadialAction(slice) {
  if (!slice) return;
  const id = slice.dataset.action;
  const src = document.getElementById(id);
  if (!src) return;
  src.checked = !src.checked;
  src.dispatchEvent(new Event("change", { bubbles: true }));
}

buildRadial();

/* ── Gesture state for the FAB radial ─────────────────────────────────────
   `pressActive` gates every window-level handler so they only do work while
   the user is actively interacting with the FAB. Without this guard, the
   previous implementation reacted to every mouseup / touchend anywhere on
   the page — which opened the radial on any tap, blocked the bottom sheet,
   and made the map unusable. */
let _radialPressActive = false;
let _radialPressTimer = null;
let _radialActiveSlice = null;
let _radialHoldOpen = false;
let _radialPressX = 0;
let _radialPressY = 0;
// Suppresses the synthetic click that fires ~300ms after a hold-release on
// touch devices, so we don't double-toggle whatever sits under the radial.
let _radialSuppressClickUntil = 0;

function _radialReset() {
  _radialPressActive = false;
  clearTimeout(_radialPressTimer);
  _radialPressTimer = null;
  if (_radialActiveSlice) _radialActiveSlice.classList.remove("hover");
  _radialActiveSlice = null;
  _radialHoldOpen = false;
}

if (fabEl) {
  const HOLD_DELAY_MS = 220;     // press-and-hold threshold to open the radial
  const CANCEL_SLOP_SQ = 196;    // 14px² — pre-open movement that cancels hold

  const onDown = e => {
    _radialPressActive = true;
    _radialHoldOpen = false;
    _radialActiveSlice = null;
    const t = e.touches ? e.touches[0] : e;
    _radialPressX = t.clientX;
    _radialPressY = t.clientY;
    clearTimeout(_radialPressTimer);
    _radialPressTimer = setTimeout(() => {
      if (!_radialPressActive) return;
      _radialHoldOpen = true;
      openRadial();
    }, HOLD_DELAY_MS);
    // Don't preventDefault on touchstart — leave focus / click semantics
    // alone. The hold gesture only takes over once the timer fires.
  };

  const onMove = e => {
    if (!_radialPressActive) return;
    const t = e.touches ? e.touches[0] : e;
    if (!t) return;

    if (!_radialHoldOpen) {
      // Pre-open: if the user moves significantly we assume they're not
      // holding the FAB (e.g. scrolling) and abandon the hold timer.
      const dx = t.clientX - _radialPressX;
      const dy = t.clientY - _radialPressY;
      if (dx * dx + dy * dy > CANCEL_SLOP_SQ) {
        _radialReset();
      }
      return;
    }

    // Hold mode: highlight whichever slice the finger / cursor is over.
    const slice = radialSliceAt(t.clientX, t.clientY);
    if (slice !== _radialActiveSlice) {
      if (_radialActiveSlice) _radialActiveSlice.classList.remove("hover");
      _radialActiveSlice = slice;
      if (_radialActiveSlice) _radialActiveSlice.classList.add("hover");
    }
    // Only suppress scroll while actually inside a hold gesture — never for
    // unrelated touchmoves elsewhere on the page.
    if (e.cancelable) e.preventDefault();
  };

  const onUp = e => {
    if (!_radialPressActive) return;
    _radialPressActive = false;
    clearTimeout(_radialPressTimer);
    _radialPressTimer = null;

    if (_radialHoldOpen) {
      // Hold-release: confirm the highlighted slice (if any) and close.
      if (_radialActiveSlice) toggleRadialAction(_radialActiveSlice);
      closeRadial();
      if (_radialActiveSlice) _radialActiveSlice.classList.remove("hover");
      _radialActiveSlice = null;
      _radialHoldOpen = false;
      _radialSuppressClickUntil = performance.now() + 500;
      // Stop the browser from synthesising a click under the radial.
      if (e && e.cancelable) e.preventDefault();
    } else {
      // Short tap: toggle the radial open / closed (tap mode).
      if (radialOverlayEl.hidden) openRadial();
      else closeRadial();
    }
  };

  const onCancel = () => _radialReset();

  fabEl.addEventListener("mousedown",  onDown);
  fabEl.addEventListener("touchstart", onDown, { passive: true });

  // Window listeners are guarded by `_radialPressActive` so they're no-ops
  // when the user isn't holding the FAB. Without that guard, every click
  // anywhere on the page opened the radial.
  window.addEventListener("mousemove",   onMove);
  window.addEventListener("touchmove",   onMove, { passive: false });
  window.addEventListener("mouseup",     onUp);
  window.addEventListener("touchend",    onUp);
  window.addEventListener("touchcancel", onCancel);
  window.addEventListener("blur",        onCancel);
}

if (radialOverlayEl) {
  // Tap mode: user taps the FAB, the radial opens, then they tap a slice.
  // Slices have pointer-events: none in CSS (so the hold-drag gesture isn't
  // hijacked by them) — we hit-test by position on the overlay's click.
  radialOverlayEl.addEventListener("click", e => {
    // Swallow the synthetic click that follows a hold-release on touch.
    if (performance.now() < _radialSuppressClickUntil) {
      e.preventDefault();
      return;
    }
    const slice = radialSliceAt(e.clientX, e.clientY);
    if (slice) {
      toggleRadialAction(slice);
      refreshRadialState();
      return;
    }
    closeRadial();
  });
}

/* ═══════════════════════════════════════════════════════════════════════════
   DOUBLE-TAP RECENTER (Phase 5)
   Double-tapping the map (not on a player) recenters on the local player —
   mirrors the existing dblclick-on-player follow gesture but for empty map
   space. Works with both mouse and touch.
   ═══════════════════════════════════════════════════════════════════════════ */
(function () {
  let lastTap = 0;
  let lastX = 0, lastY = 0;

  function pointWasOnPlayer(x, y) {
    for (const h of hitList) {
      if (h.kind !== "player") continue;
      const dx = x - h.px, dy = y - h.py;
      if (dx * dx + dy * dy < h.r * h.r) return true;
    }
    return false;
  }

  function recenterOnLocal() {
    state.freeMode = false;
    state.followTarget = null;
    freeAnchor = { x: 0, y: 0, mapId: "" };
    const fm = inputs.freeMode; if (fm) fm.checked = false;
    if (typeof updateFollowBadge === "function") updateFollowBadge();
    saveSettings();
  }

  canvas.addEventListener("touchend", e => {
    if (e.touches.length > 0) return; // multi-touch — let pinch handler win
    const t = (e.changedTouches && e.changedTouches[0]);
    if (!t) return;
    const now = performance.now();
    const dx = t.clientX - lastX, dy = t.clientY - lastY;
    if (now - lastTap < 320 && Math.abs(dx) < 24 && Math.abs(dy) < 24) {
      if (!pointWasOnPlayer(t.clientX, t.clientY)) {
        recenterOnLocal();
        e.preventDefault();
      }
      lastTap = 0;
    } else {
      lastTap = now;
      lastX = t.clientX; lastY = t.clientY;
    }
  }, { passive: false });

  // Desktop double-click on empty map space → recenter.
  canvas.addEventListener("dblclick", e => {
    if (pointWasOnPlayer(e.clientX, e.clientY)) return; // existing handler does player-follow
    recenterOnLocal();
  });
})();

/* ═══════════════════════════════════════════════════════════════════════════
   INIT
   ═══════════════════════════════════════════════════════════════════════════ */
loadSettings();
rebuildWishlistSet();
rebuildBlacklistSet();
bindAllInputs();
applyUiFromState();
updateAllRangeValues();
updateFollowBadge();
updateLootPresetActive();
updatePresetChip();
rebuildWishlistList();
rebuildBlacklistList();
rebuildItemSearchResults();
startPolling();
fetchRadar();
fetchContainerTypes();
fetchQuestData();
fetchItemCatalog();
requestAnimationFrame(frame);
