/*
 * Comskip Commercial Segments — web-player overlay.
 *
 * Injected into jellyfin-web's index.html (see Web/install-overlay.sh). Draws colored
 * bands on the video OSD's seek bar for detected commercial breaks, so you can see at a
 * glance where they are and scrub straight to the end of one instead of guessing.
 *
 * VERSION-SENSITIVE FILE — built against this server's Jellyfin 12.0.0 web client. The
 * hooks it depends on:
 *   - `.videoOsdBottom .sliderContainer .sliderMarkerContainer` — the (normally empty)
 *     div Jellyfin's own chapter-marker code uses for exactly this kind of overlay.
 *     Confirmed present via live DOM inspection on 2026-09-10.
 *   - `GET /MediaSegments/{itemId}` — returns `{ Items: [...], TotalRecordCount,
 *     StartIndex }`, PascalCase fields (StartTicks/EndTicks/Type). Confirmed live.
 *   - `window.ApiClient` — global, confirmed live (getJSON/getUrl/getItems all work).
 *   - `ApiClient.getSessions({deviceId})` to find the now-playing item id — this is the
 *     one piece NOT verified live (no active session to test against at write time).
 *     If markers never appear, check the console for "[Comskip]" warnings first — this
 *     is the most likely place a version mismatch shows up.
 *
 * If this breaks after a jellyfin-web update, re-check the class names / endpoint above
 * against the running client (devtools) and patch this file — it's served straight from
 * the plugin, no rebuild-and-copy step needed, just redeploy the plugin DLL.
 */
(function () {
  'use strict';

  // The status badges (badges.js) ride along with this script, so an index.html patched
  // before they existed picks them up without re-running install-overlay.sh.
  if (!document.querySelector('script[src*="ComskipSegments/web/badges.js"]')) {
    var badges = document.createElement('script');
    // Beside this script, so a server with a Base URL (https://host/jellyfin/) works too.
    badges.src = document.currentScript ? new URL('badges.js', document.currentScript.src).href : '../ComskipSegments/web/badges.js';
    badges.defer = true;
    document.head.appendChild(badges);
  }

  var POLL_MS = 1500;
  var TICKS_PER_SECOND = 10000000; // .NET/Jellyfin ticks: 100ns units
  var MIN_MARKER_WIDTH_PCT = 0.3;

  var lastItemId = null;
  var segmentsCache = {}; // itemId -> { segs: Array<{StartTicks, EndTicks}>, at: ms }
  // An empty answer is only trusted briefly: a video opened while its scan is still
  // running (or before a library rename's segments are republished) gets its markers
  // once they exist, without reloading the page.
  var EMPTY_RECHECK_MS = 30000;
  // A recording still in progress: Jellyfin serves no segments for it yet, so the plugin's
  // live scan (GET /ComskipSegments/Live/{id}) supplies the breaks found so far, re-asked
  // this often while it keeps recording.
  var LIVE_RECHECK_MS = 15000;
  var lastSignature = null;
  var liveBreaks = { itemId: null, segs: [] };

  function log(msg) {
    if (window.console && window.console.debug) {
      window.console.debug('[Comskip] ' + msg);
    }
  }

  function ticksToSeconds(ticks) {
    return ticks / TICKS_PER_SECOND;
  }

  function formatDuration(ticks) {
    var totalSeconds = Math.max(0, Math.round(ticksToSeconds(ticks)));
    var minutes = Math.floor(totalSeconds / 60);
    var seconds = totalSeconds % 60;
    return minutes + ':' + (seconds < 10 ? '0' : '') + seconds;
  }

  function findOsd() {
    var sliderContainer = document.querySelector('.videoOsdBottom .sliderContainer');
    var video = document.querySelector('video');
    if (!sliderContainer || !video) {
      return null;
    }
    var markerContainer = sliderContainer.querySelector('.sliderMarkerContainer');
    if (!markerContainer) {
      return null;
    }
    return { markerContainer: markerContainer, video: video };
  }

  function getNowPlayingItemId(apiClient) {
    return apiClient.getSessions({ deviceId: apiClient.deviceId() })
      .then(function (sessions) {
        var mine = (sessions || []).filter(function (s) {
          return s.DeviceId === apiClient.deviceId() && s.NowPlayingItem;
        })[0];
        if (!mine) {
          return null;
        }
        // The version playing, not the item: a recording Jellyfin restarted part-way (the
        // stream dropped) is one item with its pieces as versions, and its id is the first,
        // finished piece's. A version's own id leads to its own file, so the piece still being
        // recorded gets its live breaks. Live TV's stream ids aren't item ids; keep the item.
        var source = mine.PlayState && mine.PlayState.MediaSourceId;
        return source && /^[0-9a-f]{32}$/i.test(source) ? source : mine.NowPlayingItem.Id;
      })
      .catch(function (err) {
        log('getSessions failed: ' + err);
        return null;
      });
  }

  function fetchCommercialSegments(apiClient, itemId) {
    var cached = segmentsCache[itemId];
    if (cached) {
      var ttl = cached.live ? LIVE_RECHECK_MS : (cached.segs.length ? Infinity : EMPTY_RECHECK_MS);
      if (Date.now() - cached.at < ttl) {
        return Promise.resolve(cached.segs);
      }
    }
    return apiClient.getJSON(apiClient.getUrl('MediaSegments/' + itemId))
      .then(function (res) {
        var items = (res && res.Items) || [];
        return items.filter(function (s) { return s.Type === 'Commercial'; });
      }, function () {
        return []; // 404 while the recording is still in progress
      })
      .then(function (segs) {
        if (segs.length) {
          segmentsCache[itemId] = { segs: segs, at: Date.now() };
          return segs;
        }
        return fetchLiveSegments(apiClient, itemId);
      });
  }

  function fetchLiveSegments(apiClient, itemId) {
    return apiClient.getJSON(apiClient.getUrl('ComskipSegments/Live/' + itemId))
      .then(function (res) {
        var live = !!(res && res.InProgress);
        var segs = live ? (res.Items || []) : [];
        segmentsCache[itemId] = { segs: segs, at: Date.now(), live: live };
        liveBreaks = { itemId: live ? itemId : null, segs: segs };
        return segs;
      })
      .catch(function (err) {
        log('segment fetch failed for ' + itemId + ': ' + err);
        segmentsCache[itemId] = { segs: [], at: Date.now() };
        return [];
      });
  }

  function clearMarkers(container) {
    var existing = container.querySelectorAll('.comskip-marker');
    for (var i = 0; i < existing.length; i++) {
      existing[i].remove();
    }
  }

  function renderMarkers(container, video, segments, durationTicks) {
    clearMarkers(container);
    if (!durationTicks || !segments.length) {
      return;
    }

    segments.forEach(function (seg) {
      var startPct = clampPct((seg.StartTicks / durationTicks) * 100);
      var widthPct = Math.max(
        MIN_MARKER_WIDTH_PCT,
        ((seg.EndTicks - seg.StartTicks) / durationTicks) * 100
      );

      var mark = document.createElement('div');
      mark.className = 'comskip-marker';
      mark.style.left = startPct + '%';
      mark.style.width = widthPct + '%';
      mark.title = 'Commercial — ' + formatDuration(seg.EndTicks - seg.StartTicks);

      mark.addEventListener('click', function (e) {
        e.stopPropagation();
        e.preventDefault();
        video.currentTime = ticksToSeconds(seg.EndTicks);
      });

      container.appendChild(mark);
    });
  }

  // The video's own length, or for a recording still being written (whose stream reports an
  // infinite duration) the total jellyfin-web shows at the end of the seek bar, which is
  // what the bar's 0-100% spans.
  function playerDurationTicks(video) {
    if (video.duration && isFinite(video.duration)) {
      return video.duration * TICKS_PER_SECOND;
    }
    var text = document.querySelector('.videoOsdBottom .osdDurationText');
    var parts = text && text.textContent.trim().replace(/^-/, '').split(':');
    if (!parts || parts.length < 2) {
      return 0;
    }
    var seconds = 0;
    for (var i = 0; i < parts.length; i++) {
      var n = parseInt(parts[i], 10);
      if (isNaN(n)) {
        return 0;
      }
      seconds = seconds * 60 + n;
    }
    return seconds * TICKS_PER_SECOND;
  }

  /*
   * "Skip commercial" for breaks found by the live scan. jellyfin-web's own skip prompt
   * only knows Jellyfin's segments, which an in-progress recording doesn't have yet, so
   * while one plays this offers the skip itself (the prompt takes over once it finishes).
   */
  var skipButton = null;
  function updateLiveSkip(itemId) {
    var video = document.querySelector('video');
    var seg = null;
    if (video && itemId && liveBreaks.itemId === itemId) {
      var now = video.currentTime * TICKS_PER_SECOND;
      seg = liveBreaks.segs.filter(function (s) { return now >= s.StartTicks && now < s.EndTicks - TICKS_PER_SECOND; })[0] || null;
    }
    if (!seg) {
      if (skipButton) {
        skipButton.remove();
        skipButton = null;
      }
      return;
    }
    if (!skipButton) {
      skipButton = document.createElement('button');
      skipButton.type = 'button';
      skipButton.className = 'comskip-live-skip';
      skipButton.textContent = 'Skip commercial';
      document.body.appendChild(skipButton);
    }
    skipButton.onclick = function () {
      video.currentTime = ticksToSeconds(seg.EndTicks);
      skipButton.remove();
      skipButton = null;
    };
  }

  /*
   * Editing a recording, while it's paused (administrators, and users allowed on the plugin's
   * config page): one "✂ Edit" button, which opens the actions. "Starts here (this recording)"
   * on the program's first frame, "Remember card (all recordings)" on the card a channel shows
   * before its programs (kept, so later recordings from it find their start by themselves),
   * and, when there's one, "Remove program start" and "Undo trim". Each action asks for a
   * second press that says what it will do, so a stray click changes nothing. The position
   * comes from the session's own report, not video.currentTime: jellyfin-web sends one on
   * pause, and it stays right when a transcode restarted part-way through.
   */
  var startPanel = null;
  var editOpen = false;
  var editingPromise = null;
  // { CanEdit, ProgramStartTrims, KeepOriginal, UndoDays } for the signed-in user.
  function editing(apiClient) {
    if (!editingPromise) {
      editingPromise = apiClient.getJSON(apiClient.getUrl('ComskipSegments/Editing'))
        .catch(function () { return { CanEdit: false }; });
    }
    return editingPromise;
  }

  function pausedPositionSeconds(apiClient) {
    return apiClient.getSessions({ deviceId: apiClient.deviceId() }).then(function (sessions) {
      var mine = (sessions || []).filter(function (s) { return s.DeviceId === apiClient.deviceId() && s.NowPlayingItem; })[0];
      return mine && mine.PlayState ? (mine.PlayState.PositionTicks || 0) / TICKS_PER_SECOND : null;
    });
  }

  function notify(message) {
    if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
      window.Dashboard.alert(message);
    } else {
      log(message);
    }
  }

  function clock(seconds) {
    var s = Math.round(seconds || 0);
    return Math.floor(s / 60) + ':' + ('0' + (s % 60)).slice(-2);
  }

  // How a trim can be taken back, for the confirmations.
  function undoNote(info) {
    if (info.KeepOriginal) {
      return 'Undo trim puts it back.';
    }
    return info.UndoDays > 0
      ? 'Undo trim can put it back for ' + info.UndoDays + ' day' + (info.UndoDays === 1 ? '' : 's') + '.'
      : 'This can\'t be undone.';
  }

  function failure(err, fallback) {
    // The plugin's own reason ("Already working on this recording…") when it gave one.
    return err && typeof err.json === 'function'
      ? err.json().then(function (b) { return (b && b.Message) || fallback; }, function () { return fallback; })
      : Promise.resolve(fallback);
  }

  function changed(itemId) {
    delete segmentsCache[itemId];
    if (window.__comskipCutsRefresh) {
      window.__comskipCutsRefresh();
    }
  }

  function programStart(apiClient, itemId, card) {
    return pausedPositionSeconds(apiClient).then(function (seconds) {
      if (seconds === null) {
        throw new Error('no position');
      }
      return apiClient.ajax({
        type: 'POST',
        url: apiClient.getUrl('ComskipSegments/ProgramStart/' + itemId + (card ? '/Card' : ''), { seconds: seconds.toFixed(2) }),
        dataType: 'json'
      });
    }).then(function (r) {
      var message = (r && r.Message) || 'Done.';
      if (r && r.Trimmed) {
        message += ' Start the recording again to see it.';
      }
      notify(message);
      changed(itemId);
    }, function (err) {
      return failure(err, 'Setting the program start failed.').then(notify);
    });
  }

  // A cut can only start on a keyframe: the two either side of the paused moment, with their
  // pictures, to pick the one the recording will start on.
  function keyframeChooser(apiClient, itemId, info, panel) {
    return pausedPositionSeconds(apiClient).then(function (seconds) {
      if (seconds === null) {
        throw new Error('no position');
      }
      return apiClient.getJSON(apiClient.getUrl('ComskipSegments/ProgramStart/' + itemId + '/Keyframes', { seconds: seconds.toFixed(2) }));
    }).then(function (k) {
      var keys = [k.Before, k.After].filter(function (t) { return typeof t === 'number'; });
      if (!keys.length) {
        notify('Couldn\'t find where a cut can start near here.');
        return;
      }
      panel.innerHTML = '';
      panel.setAttribute('data-choosing', '1');
      var note = document.createElement('div');
      note.className = 'comskip-keyframe-note';
      note.textContent = 'A cut can only start on a keyframe' +
        (keys.length === 2 ? ' (every ' + (keys[1] - keys[0]).toFixed(1) + ' s here)' : '') +
        '. Pick the picture the recording will start on; everything before it is cut. ' + undoNote(info);
      panel.appendChild(note);
      keys.forEach(function (t) {
        var choice = document.createElement('button');
        choice.type = 'button';
        choice.className = 'comskip-keyframe';
        var img = document.createElement('img');
        img.alt = '';
        // Fetched with the sign-in token (an <img> can't send it), then shown from memory.
        fetch(apiClient.getUrl('ComskipSegments/ProgramStart/' + itemId + '/Frame', { seconds: t.toFixed(3), width: 400 }), {
          headers: { Authorization: 'MediaBrowser Token="' + apiClient.accessToken() + '"' }
        }).then(function (r) { return r.ok ? r.blob() : null; }).then(function (b) {
          if (b) {
            img.src = URL.createObjectURL(b);
          }
        }).catch(function () {});
        var label = document.createElement('span');
        label.textContent = 'Start at ' + clock(t) + '.' + Math.floor((t % 1) * 10) + (t <= k.Seconds + 0.02 ? ' (before)' : ' (after)');
        choice.appendChild(img);
        choice.appendChild(label);
        choice.onclick = function () {
          Array.prototype.forEach.call(panel.querySelectorAll('button'), function (b) { b.disabled = true; });
          apiClient.ajax({
            type: 'POST',
            url: apiClient.getUrl('ComskipSegments/ProgramStart/' + itemId, { seconds: t.toFixed(3), keyframe: true }),
            dataType: 'json'
          }).then(function (r) {
            notify(((r && r.Message) || 'Done.') + (r && r.Trimmed ? ' Start the recording again to see it.' : ''));
            changed(itemId);
          }, function (err) {
            return failure(err, 'Setting the program start failed.').then(notify);
          }).then(closeEdit);
        };
        panel.appendChild(choice);
      });
      var cancel = document.createElement('button');
      cancel.type = 'button';
      cancel.textContent = 'Cancel';
      cancel.onclick = closeEdit;
      panel.appendChild(cancel);
    }).catch(function (err) {
      return failure(err, 'Couldn\'t read where a cut can start.').then(notify);
    });
  }

  function simple(apiClient, itemId, type, path, fallback, after) {
    return apiClient.ajax({ type: type, url: apiClient.getUrl('ComskipSegments/ProgramStart/' + itemId + path), dataType: 'json' })
      .then(function (r) {
        notify((r && r.Message) || 'Done.' + (after || ''));
        changed(itemId);
      }, function (err) {
        return failure(err, fallback).then(notify);
      });
  }

  // A button that acts on its second press: the first shows what will happen (for 4 s).
  function armedButton(label, confirmLabel, run) {
    var button = document.createElement('button');
    button.type = 'button';
    button.textContent = label;
    var timer = null;
    button.onclick = function () {
      if (!button.classList.contains('comskip-armed')) {
        button.classList.add('comskip-armed');
        button.textContent = confirmLabel;
        timer = setTimeout(function () {
          button.classList.remove('comskip-armed');
          button.textContent = label;
        }, 4000);
        return;
      }
      clearTimeout(timer);
      button.classList.remove('comskip-armed');
      button.textContent = label;
      button.disabled = true;
      Promise.resolve(run()).then(function () {
        button.disabled = false;
        closeEdit();
      });
    };
    return button;
  }

  function closeEdit() {
    editOpen = false;
    if (startPanel) {
      startPanel.remove();
      startPanel = null;
    }
  }

  function buildPanel(apiClient, itemId, info) {
    var panel = document.createElement('div');
    panel.className = 'comskip-start-panel';
    panel.setAttribute('data-item', itemId);
    panel.setAttribute('data-open', editOpen ? '1' : '0');

    if (!editOpen) {
      var edit = document.createElement('button');
      edit.type = 'button';
      edit.textContent = '✂ Edit';
      edit.title = 'Program start, trims and their undo for this recording';
      edit.onclick = function () {
        editOpen = true;
        if (startPanel) {
          startPanel.remove();
          startPanel = null;
        }
        showPanel(apiClient, itemId, info);
      };
      panel.appendChild(edit);
      return Promise.resolve(panel);
    }

    return apiClient.getJSON(apiClient.getUrl('ComskipSegments/ProgramStart/' + itemId)).catch(function () { return null; }).then(function (status) {
      if (info.ProgramStartTrims) {
        var here = document.createElement('button');
        here.type = 'button';
        here.textContent = 'Starts here (this recording)…';
        here.title = 'Shows the keyframes either side of this moment to choose where the cut starts';
        here.onclick = function () {
          here.disabled = true;
          keyframeChooser(apiClient, itemId, info, panel).then(function () { here.disabled = false; });
        };
        panel.appendChild(here);
      } else {
        panel.appendChild(armedButton(
          'Starts here (this recording)',
          'Press again: skip to here from the start (Remove program start takes it off)',
          function () { return programStart(apiClient, itemId, false); }));
      }
      panel.appendChild(armedButton(
        'Remember card (all recordings)',
        'Press again: remember this card; recordings from this channel find their start by it (Forget it on the plugin page)',
        function () { return programStart(apiClient, itemId, true); }));
      if (status && status.Action === 'marker' && status.StartSeconds >= 1) {
        panel.appendChild(armedButton(
          'Remove program start (skips to ' + clock(status.StartSeconds) + ')',
          'Press again: play from the very start again',
          function () { return simple(apiClient, itemId, 'DELETE', '', 'Removing the program start failed.'); }));
      }
      if (status && status.CanUndo) {
        panel.appendChild(armedButton(
          'Undo trim' + (status.UndoUntil ? ' (until ' + new Date(status.UndoUntil).toLocaleDateString() + ')' : ''),
          'Press again: put the original recording back',
          function () { return simple(apiClient, itemId, 'POST', '/Undo', 'Undoing the trim failed.', ' Start the recording again to see it.'); }));
      }
      var close = document.createElement('button');
      close.type = 'button';
      close.textContent = 'Close';
      close.onclick = closeEdit;
      panel.appendChild(close);
      return panel;
    });
  }

  var building = false;
  function showPanel(apiClient, itemId, info) {
    building = true;
    buildPanel(apiClient, itemId, info).then(function (panel) {
      building = false;
      if (startPanel) {
        startPanel.remove();
      }
      startPanel = panel;
      document.body.appendChild(panel);
    }, function () { building = false; });
  }

  function updateStartPanel(apiClient, itemId, video) {
    // Hidden while the recording is being cut (badges.js keeps the cut list): one action on a
    // file at a time.
    var cutting = window.__comskipCuts && window.__comskipCuts[(itemId || '').replace(/-/g, '').toLowerCase()];
    var show = itemId && video && video.paused && liveBreaks.itemId !== itemId && !cutting;
    if (!show) {
      closeEdit();
      return;
    }
    editing(apiClient).then(function (info) {
      if (!info || !info.CanEdit || building || startPanel && startPanel.getAttribute('data-item') === itemId) {
        return;
      }
      if (startPanel && startPanel.getAttribute('data-item') !== itemId) {
        closeEdit();
      }
      showPanel(apiClient, itemId, info);
    });
  }

  function clampPct(n) {
    return Math.max(0, Math.min(100, n));
  }

  function tick() {
    var apiClient = window.ApiClient;
    if (!apiClient) {
      return Promise.resolve();
    }

    var osd = findOsd();
    if (!osd) {
      lastItemId = null;
      lastSignature = null;
      updateLiveSkip(null);
      updateStartPanel(apiClient, null, null);
      return Promise.resolve();
    }

    return getNowPlayingItemId(apiClient).then(function (itemId) {
      updateLiveSkip(itemId);
      updateStartPanel(apiClient, itemId, osd.video);
      if (!itemId) {
        return;
      }

      var durationTicks = playerDurationTicks(osd.video);
      if (!durationTicks) {
        // Duration not known yet (still opening the stream) — try again next tick
        // without marking this item as done.
        return;
      }

      return fetchCommercialSegments(apiClient, itemId).then(function (segments) {
        // Redraw only when something changed: new breaks from a live scan, a new item, a
        // growing recording's length, or jellyfin-web having cleared the container.
        var signature = itemId + '|' + Math.round(durationTicks / TICKS_PER_SECOND / 10) + '|' +
          segments.map(function (s) { return s.StartTicks + '-' + s.EndTicks; }).join(',');
        var present = !segments.length || osd.markerContainer.querySelector('.comskip-marker');
        if (signature === lastSignature && present) {
          return;
        }
        lastItemId = itemId;
        lastSignature = signature;
        renderMarkers(osd.markerContainer, osd.video, segments, durationTicks);
      });
    });
  }

  setInterval(function () {
    tick().catch(function (err) { log('tick failed: ' + err); });
  }, POLL_MS);

  log('overlay script loaded');
})();
