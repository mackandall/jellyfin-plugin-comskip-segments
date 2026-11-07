/*
 * Comskip Commercial Segments — status badges in jellyfin-web.
 *
 * Loaded by overlay.js (so an index.html already patched by install-overlay.sh picks it
 * up without re-running the installer). Draws a badge per recording from
 * GET /ComskipSegments/Badges: amber while scanning, green with the minutes of commercials
 * once marked, grey "0m" when none were found, red "!" when a scan failed; shows and
 * seasons get a done/total summary. In the Live TV guide, a recording in progress that is
 * being scanned live gets a compact colored "CS" tag (GET /ComskipSegments/Badges/Guide).
 * Style (scissors + minutes, or CS letters) and scanning motion come from the plugin's
 * settings, so the web client and Wholphin look the same.
 *
 * VERSION-SENSITIVE FILE — built against this server's jellyfin-web 12.0.0. Confirmed
 * live via DOM inspection on 2026-09-26:
 *   - Cards: `.card[data-id][data-type]` > `.cardBox` > `.cardScalable` (position:
 *     relative; holds the image anchor and, at the very bottom, the progress bar).
 *   - List rows (season page episodes): `.listItem[data-id][data-type]` >
 *     `.listItem-content` > `.listItemBody` > first `.listItemBodyText` (the title).
 *   - Details page: `#/details?id=<id>`; the visible `.itemMiscInfo-primary` holds
 *     `.mediaInfoItem` entries (date, runtime, rating, "Ends at").
 *   - Guide cells: `.programCell[data-id]` > `.guideProgramName`, which holds
 *     `.guideProgramNameText` and jellyfin-web's own timer icons (`.programIcon`).
 * If this breaks after a jellyfin-web update, re-check these against the running client.
 */
(function () {
  'use strict';

  if (window.__comskipBadges) {
    return;
  }
  window.__comskipBadges = true;

  var TYPES = { Episode: 1, Movie: 1, Series: 1, Season: 1, Video: 1, Recording: 1 };
  var FRESH_MS = 120000;        // re-ask for a finished result after 2 minutes
  var FRESH_SCANNING_MS = 10000; // and for one still scanning after 10 s
  var GUIDE_POLL_MS = 10000;
  var BATCH = 100;

  var settings = { CardStyle: 'icon', GuideStyle: 'letters', ScanningMotion: 'pulse', ShowSummary: true };
  var cache = {}; // id -> { badge: object|null, at: ms }
  var pending = {};
  var guide = {}; // programId -> badge
  var guideAt = 0;

  var SCISSORS = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="6" cy="6" r="3"></circle><circle cx="6" cy="18" r="3"></circle><line x1="20" y1="4" x2="8.12" y2="15.88"></line><line x1="14.47" y1="14.48" x2="20" y2="20"></line><line x1="8.12" y1="8.12" x2="12" y2="12"></line></svg>';

  var CSS = [
    '.csb{display:inline-flex;align-items:center;gap:3px;box-sizing:border-box;height:22px;padding:0 7px;border-radius:999px;font-size:12px;font-weight:700;line-height:1;white-space:nowrap;box-shadow:0 1px 4px rgba(0,0,0,.5);pointer-events:none}',
    '.csb svg{width:13px;height:13px;flex-shrink:0}',
    '.csb-letters{height:18px;padding:0 4px;border-radius:4px;font-size:11px;font-weight:800;letter-spacing:.04em}',
    '.csb-found,.csb-summary{background:#15803d;color:#fff}',
    '.csb-scanning{background:#f59e0b;color:#1c1300}',
    '.csb-none{background:#4b5058;color:#fff}',
    '.csb-failed{background:#c62828;color:#fff}',
    '.csb-card{position:absolute;left:8px;bottom:12px;z-index:2}',
    '.csb-list{margin-left:10px;vertical-align:middle}',
    '.csb-guide{margin-left:6px;flex-shrink:0;box-shadow:none}',
    '.csb-detail{height:26px;padding:0 10px;font-size:13px;box-shadow:none}',
    '.csb-dot{width:8px;height:8px;border-radius:50%;background:#c62828;box-shadow:0 0 0 2px #101010;margin-left:3px}',
    '@keyframes csb-pulse{0%,100%{opacity:1}50%{opacity:.45}}',
    '@keyframes csb-spin{to{transform:rotate(360deg)}}',
    '.csb-m-pulse{animation:csb-pulse 2s ease-in-out infinite}',
    '.csb-m-spin{position:relative}',
    ".csb-m-spin::after{content:'';position:absolute;top:-4px;right:-4px;bottom:-4px;left:-4px;border-radius:999px;border:2px solid rgba(245,158,11,.3);border-top-color:#f59e0b;animation:csb-spin 1.1s linear infinite}",
    '@media (prefers-reduced-motion:reduce){.csb-m-pulse,.csb-m-spin::after{animation:none}}',
    // Work in progress over a card (cutting here; JellyDVR Space draws moving the same way).
    '.jdvr-work{position:absolute;top:0;left:0;right:0;bottom:0;z-index:3;pointer-events:none;overflow:hidden;display:flex;align-items:center;justify-content:center;background-color:rgba(0,0,0,.45);background-image:repeating-linear-gradient(-45deg,rgba(var(--jw),.28) 0 10px,transparent 10px 28px);background-size:39.6px 39.6px;animation:jdvr-drift 1.2s linear infinite}',
    '@keyframes jdvr-drift{from{background-position:0 0}to{background-position:39.6px 0}}',
    '.jdvr-work-label{background:rgba(0,0,0,.6);color:#fff;font-weight:700;font-size:13px;padding:2px 8px;border-radius:6px;white-space:nowrap}',
    '.jdvr-work-bar{position:absolute;left:0;right:0;bottom:0;height:5px;background:rgba(0,0,0,.6)}',
    '.jdvr-work-bar i{display:block;height:100%;background:rgb(var(--jw));transition:width 1.5s}',
    '.jdvr-work-pill{display:inline-block;margin-left:10px;padding:2px 8px;border-radius:999px;font-size:12px;font-weight:700;color:#fff;background:rgb(var(--jw));animation:csb-pulse 1.4s ease-in-out infinite;vertical-align:middle}',
    '.jdvr-work-cut{--jw:224,161,0}',
    '@media (prefers-reduced-motion:reduce){.jdvr-work,.jdvr-work-pill{animation:none}}'
  ].join('\n');

  function log(msg) {
    if (window.console && window.console.debug) {
      window.console.debug('[ComskipBadges] ' + msg);
    }
  }

  function injectStyle() {
    if (document.getElementById('comskip-badges-style')) {
      return;
    }
    var style = document.createElement('style');
    style.id = 'comskip-badges-style';
    style.textContent = CSS;
    document.head.appendChild(style);
  }

  function signedIn() {
    var apiClient = window.ApiClient;
    return apiClient && apiClient.getCurrentUserId && apiClient.getCurrentUserId();
  }

  function getJson(path, params) {
    var apiClient = window.ApiClient;
    return apiClient.getJSON(apiClient.getUrl(path, params));
  }

  // ---- what the badge says -------------------------------------------------------

  function describe(b) {
    switch (b.State) {
      case 'found':
        return 'Commercials marked: ' + b.Minutes + ' min' + (b.Source === 'edl' ? ' (from an existing .edl)' : '');
      case 'none':
        return 'Scanned: no commercials found';
      case 'failed':
        return 'Commercial scan failed';
      case 'scanning':
        return b.Total != null ? 'Scanning: ' + b.Done + ' of ' + b.Total + ' episodes marked so far'
          : 'Scanning for commercials' + (b.Minutes != null ? ': ' + b.Minutes + ' min so far' : '');
      default:
        return b.Done + ' of ' + b.Total + ' episodes marked' + (b.Failed ? ', some scans failed' : '');
    }
  }

  function shortText(b) {
    if (b.Total != null) {
      return b.Done + '/' + b.Total;
    }
    switch (b.State) {
      case 'found': return b.Minutes + 'm';
      case 'none': return '0m';
      case 'failed': return '!';
      default: return b.Minutes != null ? b.Minutes + 'm' : '';
    }
  }

  function detailText(b) {
    if (b.Total != null) {
      return b.Done + ' of ' + b.Total + ' episodes marked';
    }
    switch (b.State) {
      case 'found': return b.Minutes + ' min of ads' + (b.Source === 'edl' ? ' · from .edl' : '');
      case 'none': return 'No commercials found';
      case 'failed': return 'Commercial scan failed';
      default: return 'Scanning for commercials';
    }
  }

  function build(b, where) {
    var letters = (where === 'guide' ? settings.GuideStyle : settings.CardStyle) === 'letters' && where !== 'detail';
    var el = document.createElement('span');
    var cls = 'csb csb-' + where + ' csb-' + b.State;
    if (letters) {
      cls += ' csb-letters';
    }
    if (b.State === 'scanning' && settings.ScanningMotion !== 'none') {
      cls += ' csb-m-' + settings.ScanningMotion;
    }
    el.className = cls;
    el.title = describe(b);
    el.setAttribute('aria-label', el.title);

    if (letters) {
      el.textContent = 'CS';
    } else {
      var text = where === 'detail' ? detailText(b) : shortText(b);
      el.innerHTML = b.State === 'failed' && b.Total == null ? '' : SCISSORS;
      if (text) {
        var span = document.createElement('span');
        span.textContent = text;
        el.appendChild(span);
      }
    }

    if (b.Failed && b.Total != null) {
      var dot = document.createElement('span');
      dot.className = 'csb-dot';
      el.appendChild(dot);
    }

    el.setAttribute('data-csb-sig', signature(b, where));
    return el;
  }

  function signature(b, where) {
    return where + '|' + settings.CardStyle + settings.GuideStyle + settings.ScanningMotion + '|' + JSON.stringify(b);
  }

  // Put badge `b` (or nothing) in `host`, replacing only when it changed.
  function place(host, b, where, append) {
    var existing = host.querySelector(':scope > .csb-' + where) ||
      (where === 'detail' ? host.querySelector(':scope > .csb-detail-item') : null);
    if (!b) {
      if (existing) {
        existing.parentNode.removeChild(existing);
      }
      return;
    }

    var sig = signature(b, where);
    var current = existing && (existing.getAttribute('data-csb-sig') || (existing.firstChild && existing.firstChild.getAttribute && existing.firstChild.getAttribute('data-csb-sig')));
    if (current === sig) {
      return;
    }

    var el = build(b, where);
    if (where === 'detail') {
      var wrap = document.createElement('div');
      wrap.className = 'mediaInfoItem csb-detail-item';
      wrap.appendChild(el);
      el = wrap;
    }

    if (existing) {
      existing.parentNode.replaceChild(el, existing);
    } else {
      append(el);
    }
  }

  // ---- library items ---------------------------------------------------------------

  function detailId() {
    var hash = window.location.hash || '';
    if (hash.indexOf('#/details') !== 0 || hash.indexOf('?') === -1) {
      return null;
    }
    return new URLSearchParams(hash.slice(hash.indexOf('?') + 1)).get('id');
  }

  function visibleTargets() {
    var targets = [];
    var nodes = document.querySelectorAll('.card[data-id][data-type], .listItem[data-id][data-type]');
    for (var i = 0; i < nodes.length; i++) {
      var n = nodes[i];
      if (TYPES[n.getAttribute('data-type')] && n.offsetParent !== null) {
        targets.push(n);
      }
    }
    return targets;
  }

  function isFresh(entry) {
    if (!entry) {
      return false;
    }
    var scanning = entry.badge && entry.badge.State === 'scanning';
    return Date.now() - entry.at < (scanning ? FRESH_SCANNING_MS : FRESH_MS);
  }

  function refreshItems() {
    if (!signedIn()) {
      return Promise.resolve();
    }

    var ids = [];
    var seen = {};
    var consider = function (id) {
      if (id && !seen[id] && !pending[id] && !isFresh(cache[id])) {
        seen[id] = 1;
        ids.push(id);
      }
    };
    visibleTargets().forEach(function (n) { consider(n.getAttribute('data-id')); });
    consider(detailId());
    if (!ids.length) {
      return Promise.resolve();
    }

    var batches = [];
    for (var i = 0; i < ids.length; i += BATCH) {
      batches.push(ids.slice(i, i + BATCH));
    }

    return Promise.all(batches.map(function (batch) {
      batch.forEach(function (id) { pending[id] = 1; });
      return getJson('ComskipSegments/Badges', { ids: batch.join(',') })
        .then(function (r) {
          if (r && r.Settings) {
            settings = r.Settings;
          }
          var now = Date.now();
          batch.forEach(function (id) {
            cache[id] = { badge: (r && r.Items && r.Items[id]) || null, at: now };
          });
        })
        .catch(function () { log('badge request failed'); })
        .then(function () {
          batch.forEach(function (id) { delete pending[id]; });
        });
    })).then(render);
  }

  function badgeFor(id) {
    var entry = cache[id];
    return entry ? entry.badge : null;
  }

  function render() {
    visibleTargets().forEach(function (n) {
      var id = n.getAttribute('data-id');
      if (n.classList.contains('card')) {
        var box = n.querySelector('.cardScalable');
        if (box) {
          place(box, badgeFor(id), 'card', function (el) { box.appendChild(el); });
        }
      } else {
        var title = n.querySelector('.listItemBody > .listItemBodyText');
        if (title) {
          place(title, badgeFor(id), 'list', function (el) { title.appendChild(el); });
        }
      }
    });

    var id = detailId();
    var misc = id && [].slice.call(document.querySelectorAll('.itemMiscInfo-primary')).filter(function (e) { return e.offsetParent !== null; })[0];
    if (misc) {
      place(misc, badgeFor(id), 'detail', function (el) { misc.appendChild(el); });
    }
    playFromStartIfAsked();

    renderGuide();
  }

  // ---- recordings being cut ---------------------------------------------------------
  /*
   * A recording the plugin is cutting (program start, audio format; from a click or after a
   * recording) gets amber stripes over its card with the phase and percent, and a pulsing tag
   * in list rows — so nobody starts a second action on it meanwhile. Admins only (the list
   * comes from GET /ComskipSegments/Cuts): every 2 s while something is being cut, else 10 s.
   */
  var cuts = {}; // id without dashes -> job
  var cutsAt = 0;

  function plainId(id) {
    return (id || '').replace(/-/g, '').toLowerCase();
  }

  function cutLabel(job) {
    var pct = job.Bytes > 0 ? Math.min(99, Math.floor(job.DoneBytes * 100 / job.Bytes)) : 0;
    return job.Phase === 'cutting' ? 'Cutting ' + pct + '%' : job.Phase === 'finishing' ? 'Finishing ' + pct + '%' : 'Checking';
  }

  function refreshCuts() {
    var wait = Object.keys(cuts).length ? 2000 : 10000;
    if (!signedIn() || Date.now() - cutsAt < wait - 100) {
      return;
    }
    cutsAt = Date.now();
    // Everyone signed in sees what's being cut (stopping a cut is for editors only).
    Promise.resolve().then(function () {
      return getJson('ComskipSegments/Cuts').then(function (list) {
        var next = {};
        (list || []).forEach(function (j) {
          next[plainId(j.ItemId)] = j;
          if (j.SeriesId && !next[plainId(j.SeriesId)]) {
            next[plainId(j.SeriesId)] = j;
          }
        });
        cuts = next;
        window.__comskipCuts = cuts;
        renderCuts();
      });
    }).catch(function () { log('cut list request failed'); });
  }

  function renderCuts() {
    visibleTargets().forEach(function (n) {
      var job = cuts[plainId(n.getAttribute('data-id'))];
      var host = n.classList.contains('card') ? n.querySelector('.cardScalable') : n.querySelector('.listItemBody > .listItemBodyText');
      if (!host) {
        return;
      }
      var card = n.classList.contains('card');
      var el = host.querySelector(card ? ':scope > .jdvr-work-cut' : ':scope > .jdvr-work-pill.jdvr-work-cut');
      if (!job) {
        if (el) {
          el.remove();
        }
        return;
      }
      var text = '\u2702 ' + cutLabel(job);
      var pct = job.Bytes > 0 ? Math.min(100, job.DoneBytes * 100 / job.Bytes) : 0;
      if (!el) {
        el = document.createElement(card ? 'div' : 'span');
        el.className = card ? 'jdvr-work jdvr-work-cut' : 'jdvr-work-pill jdvr-work-cut';
        if (card) {
          el.innerHTML = '<span class="jdvr-work-label"></span><div class="jdvr-work-bar"><i></i></div>';
        }
        host.appendChild(el);
      }
      if (card) {
        el.querySelector('.jdvr-work-label').textContent = text;
        el.querySelector('.jdvr-work-bar i').style.width = pct + '%';
      } else {
        el.textContent = text;
      }
    });
  }

  // ---- guide -----------------------------------------------------------------------

  function refreshGuide() {
    if (!signedIn() || !document.querySelector('.programCell[data-id]') || Date.now() - guideAt < GUIDE_POLL_MS - 500) {
      injectWatchFromStart();
      return Promise.resolve();
    }
    guideAt = Date.now();
    return getJson('ComskipSegments/Badges/Guide')
      .then(function (r) {
        if (r && r.Settings) {
          settings = r.Settings;
        }
        guide = {};
        (r && r.Programs || []).forEach(function (p) {
          guide[String(p.ProgramId).replace(/-/g, '')] = {
            State: p.State,
            Minutes: p.Minutes,
            RecordingItemId: p.RecordingItemId || null
          };
        });
        renderGuide();
      })
      .catch(function () { log('guide badge request failed'); });
  }

  function renderGuide() {
    var cells = document.querySelectorAll('.programCell[data-id]');
    for (var i = 0; i < cells.length; i++) {
      var cell = cells[i];
      var name = cell.querySelector('.guideProgramName');
      if (name) {
        var entry = guide[cell.getAttribute('data-id')];
        // Recordings that aren't scanned (e.g. a block-listed channel) get no badge.
        var badge = entry && entry.State === 'scanning' ? { State: entry.State, Minutes: entry.Minutes } : null;
        place(name, badge, 'guide', function (el) { name.appendChild(el); });
      }
    }
    injectWatchFromStart();
  }

  // ---- "Watch from start" in the guide's program dialog ------------------------------

  /*
   * Playing a programme from the guide joins the channel live. While it is being recorded,
   * this adds "Watch from start" to its program dialog, which plays the recording from the
   * beginning instead, so commercials can be skipped while catching up (the overlay's live
   * breaks). The dialog holds no programme id, so it is taken from the guide cell just
   * clicked (the same approach as the Episode History plugin's guide script); the web
   * client exposes no player API, so it opens the recording's page and presses its
   * Play / Play-from-beginning button.
   */
  var lastProgramId = null;
  var PLAY_FROM_START_KEY = 'comskipPlayFromStart';

  document.addEventListener('click', function (e) {
    var cell = e.target && e.target.closest && e.target.closest('.programCell[data-id]');
    if (cell) {
      lastProgramId = cell.getAttribute('data-id');
    }
  }, true);

  function injectWatchFromStart() {
    var fields = document.querySelector('.recordingDialog.opened .recordingFields .recordingFields');
    var entry = lastProgramId && guide[lastProgramId];
    if (!fields || !entry || !entry.RecordingItemId || fields.querySelector('.comskipWatchFromStartRow')) {
      return;
    }

    var row = document.createElement('div');
    row.className = 'recordingFields-buttons flex align-items-center comskipWatchFromStartRow';
    var wrap = document.createElement('div');
    var button = document.createElement('button');
    button.setAttribute('is', 'emby-button');
    button.type = 'button';
    button.className = 'raised recordingButton emby-button';
    var icon = document.createElement('span');
    icon.className = 'material-icons recordingIcon replay';
    icon.setAttribute('aria-hidden', 'true');
    var text = document.createElement('span');
    text.className = 'buttonText';
    text.textContent = 'Watch from start';
    button.appendChild(icon);
    button.appendChild(text);
    wrap.appendChild(button);
    row.appendChild(wrap);
    button.addEventListener('click', function () {
      try {
        window.sessionStorage.setItem(PLAY_FROM_START_KEY, entry.RecordingItemId);
      } catch (e) {
        // Private mode: the page opens without auto-play.
      }
      var cancel = document.querySelector('.recordingDialog.opened .btnCancel');
      if (cancel) {
        cancel.click();
      }
      window.location.hash = '#/details?id=' + entry.RecordingItemId;
    });
    fields.appendChild(row);
  }

  // On the recording's page after "Watch from start": press Play from beginning (shown when
  // there's a resume point) or else Play.
  function playFromStartIfAsked() {
    var wanted = null;
    try {
      wanted = window.sessionStorage.getItem(PLAY_FROM_START_KEY);
    } catch (e) {
      return;
    }
    if (!wanted || detailId() !== wanted) {
      return;
    }
    // Only the item page's own buttons: the guide's program dialog has a "Play" (watch the
    // channel live) with the same btnPlay class, and it can still be on screen while the
    // page opens, so wait for dialogs to close and never press a button inside one.
    if (document.querySelector('.dialogContainer .dialog.opened, .recordingDialog.opened')) {
      return;
    }
    var visible = function (sel) {
      return [].slice.call(document.querySelectorAll(sel)).filter(function (b) {
        return b.offsetParent !== null && !b.classList.contains('hide') && !b.closest('.dialog, .formDialogFooter');
      })[0];
    };
    var button = visible('.btnReplay') || visible('.btnPlay');
    if (button) {
      window.sessionStorage.removeItem(PLAY_FROM_START_KEY);
      button.click();
    }
  }

  // ---- scheduling --------------------------------------------------------------------

  var scheduled = null;
  function schedule() {
    if (scheduled) {
      return;
    }
    scheduled = setTimeout(function () {
      scheduled = null;
      render();
      renderCuts();
      refreshItems();
      refreshGuide();
      refreshCuts();
    }, 300);
  }

  // Lets the item menu drop a cached result after "Scan for commercials".
  // The show and season cards count their episodes, so they're asked again too.
  window.__comskipBadgesInvalidate = function (id) {
    delete cache[id];
    Object.keys(cache).forEach(function (k) {
      var b = cache[k] && cache[k].badge;
      if (b && (b.State === 'summary' || b.Total != null)) {
        delete cache[k];
      }
    });
    schedule();
  };

  injectStyle();
  new MutationObserver(function (mutations) {
    // Ignore our own badge insertions.
    for (var i = 0; i < mutations.length; i++) {
      var added = mutations[i].addedNodes;
      for (var j = 0; j < added.length; j++) {
        var node = added[j];
        if (!(node.classList && (node.classList.contains('csb') || node.classList.contains('csb-detail-item')
            || node.classList.contains('jdvr-work') || node.classList.contains('jdvr-work-pill')))) {
          schedule();
          return;
        }
      }
    }
  }).observe(document.body, { childList: true, subtree: true });
  window.addEventListener('hashchange', schedule);
  setInterval(function () {
    if (!document.hidden) {
      schedule();
    }
  }, GUIDE_POLL_MS);
  // While something is being cut, follow it every 2 s (refreshCuts holds back otherwise).
  setInterval(function () {
    if (!document.hidden) {
      refreshCuts();
    }
  }, 2000);
  // Lets the item menu show its state at once after starting or stopping a cut.
  window.__comskipCutsRefresh = function () {
    cutsAt = 0;
    refreshCuts();
  };
  schedule();

  log('badge script loaded');
})();
