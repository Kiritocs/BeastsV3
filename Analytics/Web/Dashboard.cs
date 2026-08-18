namespace BeastsV3.Analytics.Web;

// The dashboard page, served inline at "/" and "/index.html".
public static class Dashboard
{
    public const string Page = """
<!DOCTYPE html>
<html lang="en" data-theme="dark">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>Beasts V3 | Analytics</title>
<style>
:root{--bg:#0f1418;--panel:#161d23;--panel2:#1d2630;--border:#2b3642;--text:#e9eef4;--muted:#92a0b3;--accent:#f58f3d;--good:#46d38c;--bad:#f06a76;--warn:#f2c24f;--scrollbar-track:#101820;--scrollbar-thumb:#5b6775;--scrollbar-thumb-hover:#738195}
:root[data-theme='light']{--bg:#f5f8fb;--panel:#fff;--panel2:#eef3f8;--border:#d7e0ea;--text:#16202d;--muted:#5d6a79;--accent:#bb6a1f;--good:#1f9f63;--bad:#d14a58;--warn:#b8860b;--scrollbar-track:#e6edf5;--scrollbar-thumb:#b6c1ce;--scrollbar-thumb-hover:#9facbc}
*{box-sizing:border-box;scrollbar-color:var(--scrollbar-thumb) var(--scrollbar-track);scrollbar-width:thin}
*::-webkit-scrollbar{width:12px;height:12px}
*::-webkit-scrollbar-track{background:var(--scrollbar-track)}
*::-webkit-scrollbar-thumb{background:var(--scrollbar-thumb);border-radius:10px;border:2px solid var(--scrollbar-track)}
*::-webkit-scrollbar-thumb:hover{background:var(--scrollbar-thumb-hover)}
*::-webkit-scrollbar-corner{background:var(--scrollbar-track)}
html,body{height:100%;margin:0}
body{background:var(--bg);color:var(--text);font:13px/1.4 'Segoe UI',system-ui,sans-serif}
.app{max-width:1600px;margin:0 auto;padding:10px;height:100%;display:grid;grid-template-rows:auto 1fr;gap:10px}
.header{background:var(--panel);border:1px solid var(--border);border-radius:10px;padding:10px;display:grid;grid-template-columns:1fr auto;align-items:center}
.status{display:flex;gap:8px;align-items:center;flex-wrap:wrap;justify-content:flex-end}
.badge,.pill{border:1px solid var(--border);background:var(--panel2);border-radius:999px;padding:3px 10px;font-size:11px;font-weight:700;display:inline-flex;align-items:center;gap:6px}
.live{color:var(--good)}
.idle{color:var(--muted)}
.paused{color:var(--warn)}
.tone-seen{color:var(--warn)}
.tone-captured{color:var(--good)}
.tone-missed{color:var(--bad)}
button,input,select{color:inherit;font:inherit;border-radius:8px;border:1px solid var(--border);background:var(--panel2);padding:6px 9px}
button{cursor:pointer}
.primary{border-color:var(--accent)}
.danger{border-color:var(--bad);color:var(--bad)}
.main{min-height:0;display:grid;grid-template-columns:2.35fr 1fr;gap:10px}
.col{min-height:0;overflow:auto;display:grid;gap:10px;align-content:start}
.card{background:var(--panel);border:1px solid var(--border);border-radius:10px;padding:10px}
.card h2{margin:0 0 8px;font-size:14px;color:var(--accent);display:flex;justify-content:space-between;align-items:center;gap:8px}
.grid{display:grid;gap:8px}
.g4{grid-template-columns:repeat(4,minmax(0,1fr))}
.g3{grid-template-columns:repeat(3,minmax(0,1fr))}
.g2{grid-template-columns:repeat(2,minmax(0,1fr))}
.metric{background:var(--panel2);border:1px solid var(--border);border-radius:8px;padding:7px}
.k{font-size:10px;color:var(--muted);text-transform:uppercase}
.v{font-size:18px;font-weight:700}
.good{color:var(--good)}
.bad{color:var(--bad)}
.small{font-size:12px;color:var(--muted)}
.err{display:none;margin:0 0 8px;padding:8px;border:1px solid var(--bad);border-radius:8px;color:var(--bad)}
.table{overflow:auto;border:1px solid var(--border);border-radius:8px}
table{width:100%;border-collapse:collapse}
th,td{padding:7px 8px;border-bottom:1px solid var(--border);text-align:left;vertical-align:top}
th{position:sticky;top:0;background:var(--panel2);font-size:11px;color:var(--muted)}
.toolbar{display:flex;gap:7px;flex-wrap:wrap;margin-bottom:8px;align-items:center}
.stretch{flex:1 1 180px}
.row{cursor:pointer}
.row td:first-child::before{content:'> ';color:var(--muted)}
.row.open td:first-child::before{content:'v ';color:var(--accent)}
.detail{display:none}
.detail.open{display:table-row}
.detail-grid{display:grid;grid-template-columns:1.1fr 0.9fr 1.1fr;gap:8px;padding:8px}
.heat-cell{min-width:110px;border-radius:6px;padding:6px 7px}
.mono{font-family:Consolas,'Courier New',monospace}
@media (max-width:1300px){.main{grid-template-columns:1fr}.g4{grid-template-columns:repeat(2,minmax(0,1fr))}.detail-grid{grid-template-columns:1fr}}
@media (max-width:800px){.g4,.g3,.g2{grid-template-columns:1fr}}
</style>
</head>
<body>
<div class="app">
<header class="header">
<div>
<div style="display:flex;align-items:center;gap:10px">
<img src="/beast-icon.png" width="36" height="36" style="border-radius:8px;border:1px solid var(--border)" onerror="this.style.display='none'"/>
<h1 style="margin:0;font-size:22px">Beasts V3 <span class="small">Analytics</span></h1>
</div>
<div id="sessionMeta" class="small">Loading...</div>
</div>
<div class="status">
<span id="statusBadge" class="badge idle">IDLE</span>
<button id="themeBtn">Theme</button>
<button id="expJson" class="primary">Export JSON</button>
<button id="expCsv">Export CSV</button>
</div>
</header>

<main class="main">
<section class="col">
<div id="globalErr" class="err"></div>

<article class="card">
<h2>Session Overview <span id="overviewMeta" class="small">-</span></h2>
<div class="grid g4">
<div class="metric"><div class="k">Captured</div><div class="v" id="sCap">0c</div></div>
<div class="metric"><div class="k">Cost</div><div class="v" id="sCost">0c</div></div>
<div class="metric"><div class="k">Net</div><div class="v" id="sNet">0c</div></div>
<div class="metric"><div class="k">Maps</div><div class="v" id="sMaps">0</div></div>
<div class="metric"><div class="k">Captured / Hour</div><div class="v" id="sCapH">0c</div></div>
<div class="metric"><div class="k">Net / Hour</div><div class="v" id="sNetH">0c</div></div>
<div class="metric"><div class="k">Avg Captured / Map</div><div class="v" id="sCapM">0c</div></div>
<div class="metric"><div class="k">Avg Net / Map</div><div class="v" id="sNetM">0c</div></div>
</div>
</article>

<article class="card">
<h2>Current Map <span id="mapMeta" class="small">-</span></h2>
<div class="grid g4">
<div class="metric"><div class="k">Time</div><div class="v" id="mTime">--</div></div>
<div class="metric"><div class="k">Beasts</div><div class="v" id="mBeasts">0</div></div>
<div class="metric"><div class="k">Reds</div><div class="v" id="mReds">0</div></div>
<div class="metric"><div class="k">First Red</div><div class="v" id="mFirstRed">--</div></div>
<div class="metric"><div class="k">Captured</div><div class="v" id="mCap">0c</div></div>
<div class="metric"><div class="k">Cost</div><div class="v" id="mCost">0c</div></div>
<div class="metric"><div class="k">Net</div><div class="v" id="mNet">0c</div></div>
<div class="metric"><div class="k">Dup Scarab</div><div class="v" id="mDup">No</div></div>
</div>
<div class="small" id="mBreak" style="margin-top:8px">Cost breakdown: n/a</div>
</article>

<article class="card">
<h2>Current Replay Log <span id="replayMeta" class="small">0 events</span></h2>
<div class="table"><table><thead><tr><th>Time</th><th>Event</th><th>Beast</th><th>Unit</th></tr></thead><tbody id="curReplayBody"></tbody></table></div>
</article>

<article class="card">
<h2>Rolling Stats <span id="rollMeta" class="small">Last 0 maps</span></h2>
<div class="grid g4">
<div class="metric"><div class="k">Avg Captured</div><div class="v" id="rCap">0c</div></div>
<div class="metric"><div class="k">Avg Net</div><div class="v" id="rNet">0c</div></div>
<div class="metric"><div class="k">Avg Reds</div><div class="v" id="rReds">0</div></div>
<div class="metric"><div class="k">Avg Duration</div><div class="v" id="rDur">--</div></div>
<div class="metric"><div class="k">Median</div><div class="v" id="rMed">0c</div></div>
<div class="metric"><div class="k">P90 / P95</div><div class="v" id="rP">0c / 0c</div></div>
<div class="metric"><div class="k">StdDev</div><div class="v" id="rStd">0c</div></div>
<div class="metric"><div class="k">Best / Worst</div><div class="v" id="rBw">0c / 0c</div></div>
</div>
</article>

<article class="card">
<h2>Layout Efficiency <span id="layoutMeta" class="small">By map area</span></h2>
<div class="table"><table><thead><tr><th>Area</th><th>Runs</th><th>Reds / Min</th><th>Target Hit %</th><th>Avg First Red</th><th>Avg Net</th><th>Top Target</th></tr></thead><tbody id="layoutBody"></tbody></table></div>
</article>

<article class="card">
<h2>Map History</h2>
<div class="toolbar">
<input id="fArea" class="stretch" placeholder="Filter area"/>
<input id="fMin" type="number" style="width:110px" placeholder="Min net"/>
<input id="fMax" type="number" style="width:110px" placeholder="Max net"/>
<button id="refMap">Refresh</button>
</div>
<div class="small" style="margin-bottom:8px">Click a map row to expand its beast breakdown. Dup Scarab usage and per-beast duped status appear in the expanded detail.</div>
<div class="table"><table><thead><tr><th>Area</th><th>Time</th><th>Reds</th><th>First Red</th><th>Captured</th><th>Cost</th><th>Net</th><th>Dup Scarab</th><th>Completed</th></tr></thead><tbody id="mapBody"></tbody></table></div>
</article>

<article class="card">
<h2>Beast Distribution</h2>
<div class="table"><table><thead><tr><th>Beast</th><th>Captured</th><th>Unit</th><th>Value</th><th>Share</th></tr></thead><tbody id="distBody"></tbody></table></div>
</article>

<article class="card">
<h2>Spawn Rates</h2>
<div class="table"><table><thead><tr><th>Beast</th><th>Seen</th><th>Captures</th><th>Rate / Map</th><th>Maps Hit</th><th>Hit %</th></tr></thead><tbody id="spawnBody"></tbody></table></div>
</article>

<article class="card">
<h2>Correlation Explorer</h2>
<div class="toolbar">
<label class="small" for="corrBeast">Focus Beast</label>
<select id="corrBeast" class="stretch"></select>
</div>
<div id="corrMeta" class="small" style="margin-bottom:8px">Pick a beast to inspect co-spawn lift.</div>
<div class="table"><table><thead><tr><th>Companion</th><th>Maps Together</th><th>Focus Maps</th><th>Co-Hit %</th><th>Baseline %</th><th>Lift</th><th>Best Layout</th></tr></thead><tbody id="corrBody"></tbody></table></div>
</article>

<article class="card">
<h2>Streaks &amp; Droughts</h2>
<div class="table"><table><thead><tr><th>Beast</th><th>Maps Since Seen</th><th>Maps Since Captured</th><th>Longest Seen Drought</th><th>Longest Capture Drought</th><th>Current Hit Streak</th></tr></thead><tbody id="streakBody"></tbody></table></div>
</article>

<article class="card">
<h2>Clear Speed</h2>
<div class="table"><table><thead><tr><th>Area</th><th>Runs</th><th>Avg Time</th><th>Best Time</th><th>Avg Reds</th><th>Avg Net</th><th>Net / Min</th></tr></thead><tbody id="clearBody"></tbody></table></div>
</article>

<article class="card">
<h2>Family Heatmap</h2>
<div class="table"><table><thead id="famHeatHead"></thead><tbody id="famHeatBody"></tbody></table></div>
</article>

<article class="card">
<h2>Beast Heatmap</h2>
<div class="table"><table><thead id="beastHeatHead"></thead><tbody id="beastHeatBody"></tbody></table></div>
</article>
</section>

<aside class="col">
<article class="card">
<h2>Family Breakdown</h2>
<div class="table"><table><thead><tr><th>Family</th><th>Captured</th><th>Value</th></tr></thead><tbody id="famBody"></tbody></table></div>
</article>

<article class="card">
<h2>Saved Sessions <button id="refSave">Refresh</button></h2>
<div id="saveMsg"></div>
<div class="grid g2" style="margin-bottom:8px">
<input id="saveName" style="grid-column:span 2" placeholder="Session name"/>
<input id="saveStrategy" placeholder="Strategy"/>
<input id="saveScarab" placeholder="Scarab"/>
<input id="saveAtlas" placeholder="Atlas"/>
<input id="savePool" placeholder="Map pool"/>
<button id="saveBtn" class="primary">Save Session</button>
<div style="display:flex;gap:8px"><button id="saveA">Save A</button><button id="saveB">Save B</button></div>
</div>
<div class="table"><table><thead><tr><th>Name</th><th>Maps</th><th>Saved</th><th>Actions</th></tr></thead><tbody id="saveBody"></tbody></table></div>
</article>

<article class="card">
<h2>A/B Compare</h2>
<div class="grid g2" style="margin-bottom:8px">
<div><div class="small">Session A</div><select id="abA"></select></div>
<div><div class="small">Session B</div><select id="abB"></select></div>
<label class="small"><input id="abMatch" type="checkbox"/> Match areas</label>
<div><div class="small">Trim %</div><input id="abTrim" type="number" min="0" max="45" value="0" title="Trim outlier maps by net profit on both sides (%)"/></div>
<div><div class="small">Min maps</div><input id="abMin" type="number" min="1" value="30" title="Minimum map count required per bucket for sample-ok status"/></div>
<button id="abRun" class="primary">Run Compare</button>
</div>
<div class="toolbar"><button id="abExp">Export Compare CSV</button><span id="abMeta" class="small">Select sessions to compare.</span></div>
<div id="abErr" class="err"></div>
<div class="table"><table><thead><tr><th>Metric</th><th>A</th><th>B</th><th>Delta (B-A)</th></tr></thead><tbody id="abBody"></tbody></table></div>
</article>
</aside>
</main>
</div>

<script>
const S={cur:null,maps:[],saves:[],cmp:[],focusBeast:'',openMapRows:new Set()};
const beastStatsCache=new WeakMap();
const targetCache={maps:null,trackedKey:'',targets:[]};
const I=id=>document.getElementById(id);
const C=v=>`${Number(v||0).toFixed(1)}c`;
const N=v=>{v=Number(v||0);return `<span class="${v>=0?'good':'bad'}">${v>=0?'+':''}${v.toFixed(1)}c</span>`};
const D=s=>{s=Math.max(0,Number(s||0));const h=Math.floor(s/3600),m=Math.floor((s%3600)/60),x=Math.floor(s%60);return h>0?`${h}h ${m}m ${x}s`:`${m}m ${x}s`};
const DT=s=>Number.isFinite(Number(s))&&Number(s)>0?D(s):'--';
const LDT=(display,utc)=>{const text=normalized(display);return text||(utc?new Date(utc).toLocaleString():'')};
const E=s=>String(s||'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const Q=r=>r.json().catch(()=>({}));
const tone=t=>t==='captured'?'tone-captured':t==='missed'?'tone-missed':'tone-seen';
const normalized=v=>String(v||'').trim();
const dupLabel=v=>v?'Yes':'No';

async function api(u,o={}){const r=await fetch(u,{cache:'no-store',...o});const d=await Q(r);if(!r.ok){const e=new Error(d.message||`HTTP ${r.status}`);e.code=d.code||'request_failed';throw e}return d}
function theme(t){document.documentElement.setAttribute('data-theme',t);localStorage.setItem('beastsv3.theme',t)}
function err(m){const el=I('globalErr');el.style.display=m?'block':'none';el.textContent=m||''}
function saveMsg(m,ok=true){const el=I('saveMsg');if(!m){el.innerHTML='';return}el.innerHTML=`<div class="${ok?'ok':'err'}" style="display:block">${E(m)}</div>`;setTimeout(()=>{if(el.textContent.includes(m))el.innerHTML=''},2500)}
function costBreakdown(items){const list=Array.isArray(items)?items:[];return list.length?list.map(x=>`${x.itemName}: ${C(x.unitPriceChaos)}`).join(' | '):'n/a'}
function beastBreakdown(map){return Array.isArray(map?.beastBreakdown)?map.beastBreakdown:[]}
function replayEvents(map){return Array.isArray(map?.replayEvents)?map.replayEvents:[]}
function currentReplayEvents(){return Array.isArray(S.cur?.currentMapReplayEvents)?S.cur.currentMapReplayEvents:[]}
function mapStats(map){if(!map||typeof map!=='object')return {lookup:new Map(),hits:new Set(),captures:new Set()};const cached=beastStatsCache.get(map);if(cached)return cached;const lookup=new Map(),hits=new Set(),captures=new Set();beastBreakdown(map).forEach(b=>{const name=b.beastName||'';if(!name)return;lookup.set(name,b);if(Number(b.count||0)>0)hits.add(name);if(Number(b.capturedCount||0)>0)captures.add(name)});const stats={lookup,hits,captures};beastStatsCache.set(map,stats);return stats}
function beastLookup(map){return mapStats(map).lookup}
function hasBeastHit(map,name){return mapStats(map).hits.has(name)}
function hasBeastCapture(map,name){return mapStats(map).captures.has(name)}
function activeTargets(){const curTargets=Array.isArray(S.cur?.trackedBeastNames)?S.cur.trackedBeastNames.filter(Boolean):[];const trackedKey=curTargets.join('\u0001');if(targetCache.maps===S.maps&&targetCache.trackedKey===trackedKey)return targetCache.targets;let targets;if(curTargets.length){targets=[...new Set(curTargets)]}else{const discovered=new Set();S.maps.forEach(m=>mapStats(m).lookup.forEach((_,name)=>discovered.add(name)));targets=[...discovered].sort((a,b)=>a.localeCompare(b))}targetCache.maps=S.maps;targetCache.trackedKey=trackedKey;targetCache.targets=targets;return targets}
function beastFamily(name){name=normalized(name);if(!name)return 'Other';const lower=name.toLowerCase();if(lower.startsWith('craicic'))return 'The Deep';if(lower.startsWith('farric')||lower==='vicious hound')return 'The Wilds';if(lower.startsWith('fenumal'))return 'The Caverns';if(lower.startsWith('saqawine'))return 'The Sands';if(lower.startsWith('saqawal,')||lower.startsWith('craiceann,')||lower.startsWith('farrul,')||lower.startsWith('fenumus,'))return 'Spirit Bosses';if(lower==='wild bristle matron'||lower==='wild hellion alpha'||lower==='wild brambleback'||lower==='primal cystcaller'||lower==='primal rhex matriarch'||lower==='black mórrigan')return 'The Wilds';if(lower==='primal crushclaw'||lower==='vivid watcher')return 'The Deep';if(lower==='vivid vulture')return 'The Sands';if(lower==='vivid abberarach')return 'The Caverns';return 'Other'}
function replayRows(events,colspan){const list=Array.isArray(events)?events:[];if(!list.length)return `<tr><td colspan="${colspan}" class="small">No replay events recorded.</td></tr>`;return list.map(ev=>`<tr><td class="mono">${DT(ev.offsetSeconds)}</td><td><span class="pill ${tone(ev.eventType)}">${E(ev.eventType)}</span></td><td>${E(ev.beastName)}</td><td>${C(ev.unitPriceChaos)}</td></tr>`).join('')}

function renderCur(){
    const d=S.cur;if(!d)return;
    const st=d.isPaused?'PAUSED':(d.isCurrentAreaTrackable?'LIVE':'IDLE');
    const b=I('statusBadge');
    b.textContent=st;
    b.className=`badge ${st.toLowerCase()}`;

    I('sessionMeta').textContent=`Area: ${d.activeAreaName||'n/a'} | Session: ${D(d.sessionDurationSeconds)} | Avg map: ${D(d.averageMapDurationSeconds)}`;
    I('overviewMeta').textContent=`${d.sessionBeastsFound||0} beasts | ${d.sessionRedBeastsFound||0} reds`;
    I('sCap').textContent=C(d.sessionCapturedChaos);
    I('sCost').textContent=C(d.sessionCostChaos);
    I('sNet').innerHTML=N(d.sessionNetChaos);
    I('sMaps').textContent=d.completedMapCount||0;
    I('sCapH').textContent=C(d.sessionCapturedPerHourChaos);
    I('sNetH').innerHTML=N(d.sessionNetPerHourChaos);
    I('sCapM').textContent=C(d.averageCapturedPerMapChaos);
    I('sNetM').innerHTML=N(d.averageNetPerMapChaos);

    I('mapMeta').textContent=d.isCurrentAreaTrackable?'Tracking active map':'No active trackable map';
    I('mTime').textContent=D(d.currentMapDurationSeconds);
    I('mBeasts').textContent=d.currentMapBeastsFound||0;
    I('mReds').textContent=d.currentMapRedBeastsFound||0;
    I('mFirstRed').textContent=DT(d.currentMapFirstRedSeenSeconds);
    I('mCap').textContent=C(d.currentMapCapturedChaos);
    I('mCost').textContent=C(d.currentMapCostChaos);
    I('mNet').innerHTML=N(d.currentMapNetChaos);
    I('mDup').textContent=dupLabel(d.currentMapUsesDuplicatingScarab);
    I('mBreak').textContent=`Cost breakdown: ${costBreakdown(d.currentMapCostBreakdown)}`;

    const events=currentReplayEvents();
    I('replayMeta').textContent=`${events.length} event${events.length===1?'':'s'}`;
    I('curReplayBody').innerHTML=replayRows(events,4);

    const r=d.rolling||{};
    I('rollMeta').textContent=`Last ${r.windowMapCount||0} maps`;
    I('rCap').textContent=C(r.avgCapturedChaos);
    I('rNet').innerHTML=N(r.avgNetChaos);
    I('rReds').textContent=Number(r.avgRedsPerMap||0).toFixed(2);
    I('rDur').textContent=D(r.avgDurationSeconds);
    I('rMed').textContent=C(r.medianCapturedChaos);
    I('rP').textContent=`${C(r.p90CapturedChaos)} / ${C(r.p95CapturedChaos)}`;
    I('rStd').textContent=C(r.stdDevCapturedChaos);
    I('rBw').textContent=`${C(r.bestCapturedChaos)} / ${C(r.worstCapturedChaos)}`;

    const fam=Array.isArray(d.familyTotals)?d.familyTotals:[];
    I('famBody').innerHTML=fam.length?fam.sort((a,b)=>Number(b.capturedChaos||0)-Number(a.capturedChaos||0)).map(x=>`<tr><td>${E(x.familyName)}</td><td>${x.capturedCount||0}</td><td>${C(x.capturedChaos)}</td></tr>`).join(''):'<tr><td colspan="3" class="small">No family data yet.</td></tr>';
}

function filteredMaps(){
    const q=I('fArea').value.trim().toLowerCase();
    const min=Number(I('fMin').value||'-Infinity');
    const max=Number(I('fMax').value||'Infinity');
    return S.maps.filter(m=>{
        const area=String(m.areaName||m.areaHash||'').toLowerCase();
        const net=Number(m.netChaos||0);
        if(q&&!area.includes(q))return false;
        if(Number.isFinite(min)&&net<min)return false;
        if(Number.isFinite(max)&&net>max)return false;
        return true;
    });
}

function detailBeastTable(map){
    const bs=beastBreakdown(map);
    if(!bs.length)return '<span class="small">No tracked beasts recorded.</span>';
    // Falls back to the map-level flag for records written without isDuplicated.
    const dupActive=Boolean(map?.usedBestiaryScarabOfDuplicating);
    const dupNote=dupActive?'<div class="small" style="margin-bottom:6px">Dup Scarab active: Captured already counts both copies, so Seen 1 / Captured 2 is expected.</div>':'';
    return `${dupNote}<table><thead><tr><th>Beast</th><th>Seen</th><th>Captured</th><th>Duped</th><th>Unit</th><th>Value</th></tr></thead><tbody>${bs.map(b=>{
        const captured=Number(b.capturedCount||0);
        const duped=(b.isDuplicated!==undefined?Boolean(b.isDuplicated):(dupActive&&captured>0));
        return `<tr><td>${E(b.beastName)}</td><td>${b.count||0}</td><td>${captured}</td><td>${dupLabel(duped)}</td><td>${C(b.unitPriceChaos)}</td><td>${C(captured*Number(b.unitPriceChaos||0))}</td></tr>`;
    }).join('')}</tbody></table>`;
}

function detailCostTable(map){
    const cs=Array.isArray(map?.costBreakdown)?map.costBreakdown:[];
    if(!cs.length)return '<span class="small">No map costs recorded.</span>';
    return `<table><thead><tr><th>Cost Item</th><th>Unit</th></tr></thead><tbody>${cs.map(c=>`<tr><td>${E(c.itemName)}</td><td>${C(c.unitPriceChaos)}</td></tr>`).join('')}</tbody></table>`;
}

function detailReplayTable(map){
    return `<table><thead><tr><th>Time</th><th>Event</th><th>Beast</th><th>Unit</th></tr></thead><tbody>${replayRows(replayEvents(map),4)}</tbody></table>`;
}

function stableMapRowId(map,index){
    const mapId=normalized(map?.mapId);
    if(mapId)return `map_${mapId}`;
    const completedAt=normalized(map?.completedAtUtc);
    if(completedAt)return `map_${completedAt.replace(/[^a-zA-Z0-9]/g,'_')}_${index}`;
    return `map_fallback_${index}`;
}

function captureOpenMapRows(){
    const body=I('mapBody');
    if(!body)return;
    S.openMapRows=new Set([...body.querySelectorAll('tr.row.open')]
        .map(row=>row.getAttribute('data-detail'))
        .filter(Boolean));
}

function applyMapRowOpenState(rowId){
    if(!S.openMapRows.has(rowId))return '';
    return ' open';
}

function renderMaps(){
    const body=I('mapBody');
    captureOpenMapRows();
    const maps=filteredMaps();
    if(!maps.length){body.innerHTML='<tr><td colspan="9" class="small">No maps recorded.</td></tr>';renderDerived([]);return}
    body.innerHTML=maps.map((m,i)=>{
        const id=stableMapRowId(m,i);
        const openClass=applyMapRowOpenState(id);
        return `<tr class="row${openClass}" data-detail="${id}"><td>${E(m.areaName||m.areaHash||'n/a')}</td><td>${D(m.durationSeconds)}</td><td>${m.redBeastsFound||0}</td><td>${DT(m.firstRedSeenSeconds)}</td><td>${C(m.capturedChaos)}</td><td>${C(m.costChaos)}</td><td>${N(m.netChaos)}</td><td>${dupLabel(m.usedBestiaryScarabOfDuplicating)}</td><td>${E(LDT(m.completedAtDisplay,m.completedAtUtc))}</td></tr><tr id="${id}" class="detail${openClass}"><td colspan="9"><div class="detail-grid"><div class="card" style="margin:0;padding:8px"><h2 style="margin-bottom:6px">Beast Breakdown</h2>${detailBeastTable(m)}</div><div class="card" style="margin:0;padding:8px"><h2 style="margin-bottom:6px">Cost Breakdown</h2>${detailCostTable(m)}</div><div class="card" style="margin:0;padding:8px"><h2 style="margin-bottom:6px">Replay Log</h2>${detailReplayTable(m)}</div></div></td></tr>`;
    }).join('');
    renderDerived(maps);
}

function renderDistribution(maps){
    const totals=new Map();
    let totalValue=0;
    maps.forEach(map=>beastBreakdown(map).forEach(b=>{
        const name=b.beastName||'';if(!name)return;
        const captured=Number(b.capturedCount||0),unit=Number(b.unitPriceChaos||0),value=captured*unit;
        totalValue+=value;
        const row=totals.get(name)||{name,captured:0,unit:0,value:0};
        row.captured+=captured;row.value+=value;if(unit>0)row.unit=unit;totals.set(name,row);
    }));
    const rows=[...totals.values()].sort((a,b)=>b.value-a.value);
    I('distBody').innerHTML=rows.length?rows.map(r=>`<tr><td>${E(r.name)}</td><td>${r.captured}</td><td>${C(r.unit)}</td><td>${C(r.value)}</td><td>${totalValue>0?((r.value/totalValue)*100).toFixed(1):'0.0'}%</td></tr>`).join(''):'<tr><td colspan="5" class="small">No distribution data.</td></tr>';
}

// Rate / Map counts beasts seen, not captured; captures get their own column.
function renderSpawnRates(maps){
    const totals=new Map();
    const mapCount=Math.max(1,maps.length);
    maps.forEach(map=>{
        const seenThisMap=new Set();
        beastBreakdown(map).forEach(b=>{
            const name=b.beastName||'';if(!name)return;
            const row=totals.get(name)||{name,seen:0,captures:0,mapsHit:0};
            row.seen+=Number(b.count||0);
            row.captures+=Number(b.capturedCount||0);
            if(Number(b.count||0)>0&&!seenThisMap.has(name)){row.mapsHit++;seenThisMap.add(name)}
            totals.set(name,row);
        });
    });
    const rows=[...totals.values()].sort((a,b)=>b.seen-a.seen);
    I('spawnBody').innerHTML=rows.length?rows.map(r=>`<tr><td>${E(r.name)}</td><td>${r.seen}</td><td>${r.captures}</td><td>${(r.seen/mapCount).toFixed(3)}</td><td>${r.mapsHit}</td><td>${((r.mapsHit/mapCount)*100).toFixed(1)}%</td></tr>`).join(''):'<tr><td colspan="6" class="small">No spawn rate data.</td></tr>';
}

function renderClearSpeed(maps){
    const areas=new Map();
    maps.forEach(m=>{
        const key=m.areaName||m.areaHash||'Unknown';
        const row=areas.get(key)||{key,runs:0,duration:0,best:0,reds:0,net:0};
        row.runs++;row.duration+=Number(m.durationSeconds||0);row.reds+=Number(m.redBeastsFound||0);row.net+=Number(m.netChaos||0);
        const d=Number(m.durationSeconds||0);if(d>0&&(row.best<=0||d<row.best))row.best=d;
        areas.set(key,row);
    });
    const rows=[...areas.values()].sort((a,b)=>(b.net/Math.max(1,b.runs))-(a.net/Math.max(1,a.runs)));
    I('clearBody').innerHTML=rows.length?rows.map(r=>{const avgTime=r.duration/Math.max(1,r.runs),avgNet=r.net/Math.max(1,r.runs),netPerMin=r.duration>0?r.net/(r.duration/60):0;return `<tr><td>${E(r.key)}</td><td>${r.runs}</td><td>${D(avgTime)}</td><td>${r.best>0?D(r.best):'--'}</td><td>${(r.reds/Math.max(1,r.runs)).toFixed(2)}</td><td>${N(avgNet)}</td><td>${N(netPerMin)}</td></tr>`}).join(''):'<tr><td colspan="7" class="small">No clear-speed data.</td></tr>';
}

function renderLayoutEfficiency(maps){
    const targets=activeTargets();
    const targetSet=new Set(targets);
    const groups=new Map();
    maps.forEach(map=>{
        const area=map.areaName||map.areaHash||'Unknown';
        const row=groups.get(area)||{area,runs:0,minutes:0,reds:0,firstRedSum:0,firstRedCount:0,targetMaps:0,net:0,beasts:new Map()};
        row.runs++;row.minutes+=Math.max(0,Number(map.durationSeconds||0))/60;row.reds+=Number(map.redBeastsFound||0);row.net+=Number(map.netChaos||0);
        if(Number.isFinite(Number(map.firstRedSeenSeconds))&&Number(map.firstRedSeenSeconds)>0){row.firstRedSum+=Number(map.firstRedSeenSeconds);row.firstRedCount++}
        let mapHasTarget=false;
        const seenThisMap=new Set();
        beastBreakdown(map).forEach(b=>{
            const name=b.beastName||'';if(!name)return;
            const treatAsTarget=targetSet.size===0||targetSet.has(name);
            if(treatAsTarget&&Number(b.count||0)>0)mapHasTarget=true;
            if(Number(b.count||0)>0&&!seenThisMap.has(name)){
                seenThisMap.add(name);
                const beastRow=row.beasts.get(name)||{mapsHit:0,captures:0};
                beastRow.mapsHit++;beastRow.captures+=Number(b.capturedCount||0);row.beasts.set(name,beastRow);
            }
        });
        if(mapHasTarget)row.targetMaps++;
        groups.set(area,row);
    });
    const rows=[...groups.values()].sort((a,b)=>((b.net/Math.max(1,b.runs))-(a.net/Math.max(1,a.runs))));
    I('layoutBody').innerHTML=rows.length?rows.map(r=>{const best=[...r.beasts.entries()].sort((a,b)=>(b[1].mapsHit-a[1].mapsHit)||(b[1].captures-a[1].captures)||a[0].localeCompare(b[0]))[0];return `<tr><td>${E(r.area)}</td><td>${r.runs}</td><td>${r.minutes>0?(r.reds/r.minutes).toFixed(2):'0.00'}</td><td>${((r.targetMaps/Math.max(1,r.runs))*100).toFixed(1)}%</td><td>${r.firstRedCount>0?D(r.firstRedSum/r.firstRedCount):'--'}</td><td>${N(r.net/Math.max(1,r.runs))}</td><td>${best?`${E(best[0])} (${best[1].mapsHit} hits)`:'--'}</td></tr>`}).join(''):'<tr><td colspan="7" class="small">No layout data yet.</td></tr>';
    I('layoutMeta').textContent=targets.length?`Targets: ${targets.length}`:'Using all tracked beasts';
}

function syncCorrelationOptions(){
    const select=I('corrBeast');
    const targets=activeTargets();
    if(!targets.length){select.innerHTML='<option value="">No beasts</option>';S.focusBeast='';return}
    if(!targets.includes(S.focusBeast))S.focusBeast=targets[0];
    select.innerHTML=targets.map(name=>`<option value="${E(name)}" ${name===S.focusBeast?'selected':''}>${E(name)}</option>`).join('');
}

function renderCorrelation(maps){
    syncCorrelationOptions();
    const focus=S.focusBeast;
    if(!focus){I('corrMeta').textContent='No target beasts available.';I('corrBody').innerHTML='<tr><td colspan="7" class="small">No correlation data.</td></tr>';return}
    const allTargets=activeTargets().filter(name=>name!==focus);
    const companionSet=new Set(allTargets);
    const baselineCounts=new Map();
    const togetherCounts=new Map();
    const areaCountsByCompanion=new Map();
    let focusMapCount=0;
    maps.forEach(map=>{
        const stats=mapStats(map);
        const focusHit=stats.hits.has(focus);
        if(focusHit)focusMapCount++;
        if(stats.hits.size===0)return;
        const area=map.areaName||map.areaHash||'Unknown';
        stats.hits.forEach(name=>{
            if(name===focus||!companionSet.has(name))return;
            baselineCounts.set(name,(baselineCounts.get(name)||0)+1);
            if(!focusHit)return;
            togetherCounts.set(name,(togetherCounts.get(name)||0)+1);
            const areaCounts=areaCountsByCompanion.get(name)||new Map();
            areaCounts.set(area,(areaCounts.get(area)||0)+1);
            areaCountsByCompanion.set(name,areaCounts);
        });
    });
    const totalMaps=Math.max(1,maps.length);
    I('corrMeta').textContent=`${focusMapCount} of ${maps.length} maps contained ${focus}. Lift > 1.00 means a companion appears more often when ${focus} is present.`;
    const rows=allTargets.map(name=>{
        const together=togetherCounts.get(name)||0;
        const baselineMaps=baselineCounts.get(name)||0;
        const coHit=focusMapCount?together/focusMapCount:0;
        const baseline=baselineMaps/totalMaps;
        const lift=baseline>0?coHit/baseline:0;
        const topArea=[...(areaCountsByCompanion.get(name)||new Map()).entries()].sort((a,b)=>b[1]-a[1])[0];
        return {name,together,focusMaps:focusMapCount,coHit,baseline,lift,bestArea:topArea?`${topArea[0]} (${topArea[1]})`:'--'};
    }).sort((a,b)=>(b.lift-a.lift)||(b.together-a.together)||a.name.localeCompare(b.name));
    I('corrBody').innerHTML=rows.length?rows.map(r=>`<tr><td>${E(r.name)}</td><td>${r.together}</td><td>${r.focusMaps}</td><td>${(r.coHit*100).toFixed(1)}%</td><td>${(r.baseline*100).toFixed(1)}%</td><td>${r.lift>0?r.lift.toFixed(2):'--'}</td><td>${E(r.bestArea)}</td></tr>`).join(''):'<tr><td colspan="7" class="small">No companion data.</td></tr>';
}

function renderStreaks(maps){
    const targets=activeTargets();
    if(!targets.length){I('streakBody').innerHTML='<tr><td colspan="6" class="small">No tracked beasts available.</td></tr>';return}
    const ordered=[...maps].sort((a,b)=>new Date(a.completedAtUtc||0)-new Date(b.completedAtUtc||0));
    const rows=targets.map(name=>{
        let currentSeenDrought=0,currentCaptureDrought=0,longestSeenDrought=0,longestCaptureDrought=0,currentHitStreak=0;
        ordered.forEach(map=>{
            const seen=hasBeastHit(map,name);
            const captured=hasBeastCapture(map,name);
            if(seen){currentSeenDrought=0;currentHitStreak++}else{currentSeenDrought++;longestSeenDrought=Math.max(longestSeenDrought,currentSeenDrought);currentHitStreak=0}
            if(captured){currentCaptureDrought=0}else{currentCaptureDrought++;longestCaptureDrought=Math.max(longestCaptureDrought,currentCaptureDrought)}
        });
        let sinceSeen=0,sinceCaptured=0;
        for(let i=ordered.length-1;i>=0;i--){if(hasBeastHit(ordered[i],name))break;sinceSeen++}
        for(let i=ordered.length-1;i>=0;i--){if(hasBeastCapture(ordered[i],name))break;sinceCaptured++}
        return {name,sinceSeen,sinceCaptured,longestSeenDrought,longestCaptureDrought,currentHitStreak};
    }).sort((a,b)=>(b.sinceCaptured-a.sinceCaptured)||(b.longestCaptureDrought-a.longestCaptureDrought)||a.name.localeCompare(b.name));
    I('streakBody').innerHTML=rows.map(r=>`<tr><td>${E(r.name)}</td><td>${r.sinceSeen}</td><td>${r.sinceCaptured}</td><td>${r.longestSeenDrought}</td><td>${r.longestCaptureDrought}</td><td>${r.currentHitStreak}</td></tr>`).join('');
}

function buildHeatData(maps,mode){
    const rows=new Map();
    const cols=new Map();
    maps.forEach(map=>{
        const area=map.areaName||map.areaHash||'Unknown';
        const row=rows.get(area)||new Map();
        beastBreakdown(map).forEach(b=>{
            const key=mode==='family'?beastFamily(b.beastName):b.beastName;
            if(!key)return;
            const cell=row.get(key)||{value:0,captures:0};
            const addCaptures=Number(b.capturedCount||0);
            const addValue=addCaptures*Number(b.unitPriceChaos||0);
            cell.captures+=addCaptures;
            cell.value+=addValue;
            row.set(key,cell);
            const col=cols.get(key)||{value:0,captures:0};
            col.captures+=addCaptures;
            col.value+=addValue;
            cols.set(key,col);
        });
        rows.set(area,row);
    });
    const columns=[...cols.entries()].sort((a,b)=>b[1].value-a[1].value).slice(0,6).map(([key])=>key);
    const max=Math.max(0,...[...rows.values()].flatMap(row=>columns.map(col=>Number(row.get(col)?.value||0))));
    return {rows:[...rows.entries()].sort((a,b)=>a[0].localeCompare(b[0])),columns,max};
}

function heatCell(cell,max){
    const value=Number(cell?.value||0),captures=Number(cell?.captures||0);
    if(value<=0&&captures<=0)return '<div class="small">-</div>';
    const alpha=max>0?(0.12+(value/max)*0.48):0.12;
    return `<div class="heat-cell" style="background:rgba(245,143,61,${alpha.toFixed(3)})"><div>${C(value)}</div><div class="small">${captures} cap</div></div>`;
}

function renderHeatmap(mode,headId,bodyId){
    const data=buildHeatData(filteredMaps(),mode);
    const head=I(headId),body=I(bodyId);
    if(!data.rows.length||!data.columns.length){head.innerHTML='<tr><th>Area</th></tr>';body.innerHTML='<tr><td class="small">No heatmap data yet.</td></tr>';return}
    head.innerHTML=`<tr><th>Area</th>${data.columns.map(col=>`<th>${E(col)}</th>`).join('')}</tr>`;
    body.innerHTML=data.rows.map(([area,row])=>`<tr><td>${E(area)}</td>${data.columns.map(col=>`<td>${heatCell(row.get(col),data.max)}</td>`).join('')}</tr>`).join('');
}

function renderDerived(maps){
    renderDistribution(maps);
    renderSpawnRates(maps);
    renderClearSpeed(maps);
    renderLayoutEfficiency(maps);
    renderCorrelation(maps);
    renderStreaks(maps);
    renderHeatmap('family','famHeatHead','famHeatBody');
    renderHeatmap('beast','beastHeatHead','beastHeatBody');
}

function savesRender(){
    const body=I('saveBody');
    const saves=S.saves||[];
    if(!saves.length){body.innerHTML='<tr><td colspan="4" class="small">No saved sessions yet.</td></tr>';syncSaveSelectors();return}
    body.innerHTML=saves.map(x=>{const tags=[x.tags?.strategy,x.tags?.scarab,x.tags?.atlas,x.tags?.mapPool].filter(Boolean).join(' | '),label=x.name||x.saveId,auto=x.isAutoSave?'<span class="badge" style="padding:2px 6px">AutoSave</span>':'',loaded=x.alreadyLoaded?'<span class="badge live" style="padding:2px 6px">Loaded</span>':'';return `<tr><td>${E(label)}<div style="display:flex;gap:6px;flex-wrap:wrap;margin-top:4px">${auto}${loaded}</div><div class="small">${E(tags||'no tags')}</div></td><td>${x.summary?.mapsCompleted||0}</td><td>${E(LDT(x.savedAtDisplay,x.savedAtUtc))}</td><td><div style="display:flex;gap:8px;flex-wrap:wrap">${x.alreadyLoaded?`<button onclick="unloadSave('${x.saveId}')">Unload</button>`:`<button onclick="loadSave('${x.saveId}')">Load</button>`}<button onclick="exportSave('${x.saveId}')">JSON</button><button class="danger" onclick="deleteSave('${x.saveId}')">Delete</button></div></td></tr>`}).join('');
    syncSaveSelectors();
}

function syncSaveSelectors(){
    const options=S.saves.map(x=>`<option value="${x.saveId}">${E((x.name||x.saveId)+(x.isAutoSave?' [AutoSave]':''))}</option>`).join('');
    I('abA').innerHTML=`<option value="">Session A</option>${options}`;
    I('abB').innerHTML=`<option value="">Session B</option>${options}`;
}

async function refresh(){
    const [cur,maps]=await Promise.all([api('/api/session/current'),api('/api/session/maps?offset=0&limit=1000')]);
    S.cur=cur;
    S.maps=Array.isArray(maps.items)?maps.items:[];
    renderCur();
    renderMaps();
}

async function refreshSaves(){S.saves=await api('/api/session/saves');if(!Array.isArray(S.saves))S.saves=[];savesRender()}
async function save(tags=null){const p={name:I('saveName').value.trim(),strategyTag:I('saveStrategy').value.trim(),scarabTag:I('saveScarab').value.trim(),atlasTag:I('saveAtlas').value.trim(),mapPoolTag:I('savePool').value.trim()};if(tags){if(tags.strategyTag)p.strategyTag=tags.strategyTag;if(tags.name)p.name=tags.name;I('saveStrategy').value=p.strategyTag;I('saveName').value=p.name}const r=await api('/api/session/saves',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(p)});saveMsg(r.message||'Saved session.',true);await refreshSaves()}
async function load(id){const r=await api(`/api/session/saves/${encodeURIComponent(id)}/load`,{method:'POST'});saveMsg(r.message||'Session loaded.',true);await Promise.all([refresh(),refreshSaves()])}
async function unload(id){const r=await api(`/api/session/saves/${encodeURIComponent(id)}/unload`,{method:'POST'});saveMsg(r.message||'Session unloaded.',true);await Promise.all([refresh(),refreshSaves()])}
async function del(id){if(!confirm('Delete this saved session permanently?'))return;const r=await api(`/api/session/saves/${encodeURIComponent(id)}`,{method:'DELETE'});saveMsg(r.message||'Session deleted.',true);await Promise.all([refresh(),refreshSaves()])}
async function exp(id){const d=await api(`/api/session/saves/${encodeURIComponent(id)}`);const n=(d.session?.name||id||'session').replace(/\s+/g,'-').toLowerCase();download(`${n}.json`,JSON.stringify(d.session,null,2),'application/json')}
function download(name,text,mime){const a=document.createElement('a');a.href=URL.createObjectURL(new Blob([text],{type:mime}));a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(a.href),3000)}
function localDateStamp(d=new Date()){const y=d.getFullYear(),m=`${d.getMonth()+1}`.padStart(2,'0'),day=`${d.getDate()}`.padStart(2,'0');return `${y}-${m}-${day}`}

async function compare(){
    I('abErr').style.display='none';
    I('abBody').innerHTML='';
    const p={saveAId:I('abA').value,saveBId:I('abB').value,matchAreas:I('abMatch').checked,trimPercent:Number(I('abTrim').value||0),minMaps:Number(I('abMin').value||30)};
    if(!p.saveAId||!p.saveBId){I('abErr').style.display='block';I('abErr').textContent='Select Session A and Session B.';return}
    const r=await api('/api/session/compare',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(p)});
    I('abMeta').textContent=`${r.message} ${r.recommendation||''}`;
    const rows=[['Maps',r.sessionA.count,r.sessionB.count,r.delta.count,false],['Net / Map',r.sessionA.netPerMapChaos,r.sessionB.netPerMapChaos,r.delta.netPerMapChaos,true],['Net / Min',r.sessionA.netPerMinuteChaos,r.sessionB.netPerMinuteChaos,r.delta.netPerMinuteChaos,true],['Captured / Min',r.sessionA.capturedPerMinuteChaos,r.sessionB.capturedPerMinuteChaos,r.delta.capturedPerMinuteChaos,true],['Cost / Map',r.sessionA.costPerMapChaos,r.sessionB.costPerMapChaos,r.delta.costPerMapChaos,true],['Reds / Map',r.sessionA.redsPerMap,r.sessionB.redsPerMap,r.delta.redsPerMap,false]];
    S.cmp=rows;
    I('abBody').innerHTML=rows.map(x=>{const fmt=v=>x[4]?C(v):Number(v||0).toFixed(2);return `<tr><td>${x[0]}</td><td>${fmt(x[1])}</td><td>${fmt(x[2])}</td><td>${x[4]?N(x[3]):Number(x[3]||0).toFixed(2)}</td></tr>`}).join('');
}

function expCmp(){if(!S.cmp.length)return;const rows=S.cmp.map(x=>`${x[0]},${x[1]},${x[2]},${x[3]}`);download('beast-ab-compare.csv',['Metric,A,B,Delta',...rows].join('\n'),'text/csv')}
function expJson(){if(!S.cur)return;download('beasts-v3-analytics.json',JSON.stringify({current:S.cur,maps:S.maps,exportedAtUtc:new Date().toISOString()},null,2),'application/json')}
function expCsv(){const rows=(S.maps||[]).map(m=>({area:m.areaName||m.areaHash||'',durationSeconds:Number(m.durationSeconds||0).toFixed(0),firstRedSeconds:Number(m.firstRedSeenSeconds||0).toFixed(0),reds:m.redBeastsFound||0,capturedChaos:Number(m.capturedChaos||0).toFixed(1),costChaos:Number(m.costChaos||0).toFixed(1),netChaos:Number(m.netChaos||0).toFixed(1),usedDuplicatingScarab:m.usedBestiaryScarabOfDuplicating?'Yes':'No',completedAtUtc:m.completedAtUtc||''}));if(!rows.length)return;const keys=Object.keys(rows[0]);const csv=[keys.join(',')].concat(rows.map(r=>keys.map(k=>`${r[k]}`.replace(/,/g,';')).join(','))).join('\n');download('beasts-v3-analytics.csv',csv,'text/csv')}

async function tick(){try{await refresh();err('')}catch(e){err(e.message||'Failed to refresh analytics data.')}}

function wire(){
    I('themeBtn').addEventListener('click',()=>theme(document.documentElement.getAttribute('data-theme')==='dark'?'light':'dark'));
    I('expJson').addEventListener('click',expJson);
    I('expCsv').addEventListener('click',expCsv);
    I('fArea').addEventListener('input',renderMaps);
    I('fMin').addEventListener('input',renderMaps);
    I('fMax').addEventListener('input',renderMaps);
    I('refMap').addEventListener('click',tick);
    I('corrBeast').addEventListener('change',e=>{S.focusBeast=e.target.value;renderCorrelation(filteredMaps())});
    I('mapBody').addEventListener('click',e=>{const row=e.target.closest('.row');if(!row)return;const detailId=row.getAttribute('data-detail');row.classList.toggle('open');const isOpen=row.classList.contains('open');if(detailId){if(isOpen)S.openMapRows.add(detailId);else S.openMapRows.delete(detailId)}const detail=I(detailId);if(detail)detail.classList.toggle('open',isOpen)});
    I('refSave').addEventListener('click',async()=>{try{await refreshSaves()}catch(e){saveMsg(e.message||'Failed to refresh saves.',false)}});
    I('saveBtn').addEventListener('click',async()=>{try{await save()}catch(e){saveMsg(e.message||'Failed to save session.',false)}});
    I('saveA').addEventListener('click',async()=>{const s='Strategy-A',n=`${s}-${localDateStamp()}`;try{await save({strategyTag:s,name:n})}catch(e){saveMsg(e.message||'Save A failed.',false)}});
    I('saveB').addEventListener('click',async()=>{const s='Strategy-B',n=`${s}-${localDateStamp()}`;try{await save({strategyTag:s,name:n})}catch(e){saveMsg(e.message||'Save B failed.',false)}});
    I('abRun').addEventListener('click',async()=>{try{await compare()}catch(e){const box=I('abErr');box.style.display='block';box.textContent=e.message||'Compare failed.'}});
    I('abExp').addEventListener('click',expCmp);
}

window.loadSave=id=>load(id).catch(e=>saveMsg(e.message||'Load failed.',false));
window.unloadSave=id=>unload(id).catch(e=>saveMsg(e.message||'Unload failed.',false));
window.deleteSave=id=>del(id).catch(e=>saveMsg(e.message||'Delete failed.',false));
window.exportSave=id=>exp(id).catch(e=>saveMsg(e.message||'Export failed.',false));

document.addEventListener('DOMContentLoaded',async()=>{
    theme(localStorage.getItem('beastsv3.theme')||'dark');
    wire();
    try{await Promise.all([tick(),refreshSaves()])}catch(e){err(e.message||'Initial load failed.')}
    setInterval(tick,2000);
    setInterval(()=>refreshSaves().catch(()=>{}),15000);
});
</script>
</body>
</html>
""";
}

