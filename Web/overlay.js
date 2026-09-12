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

  var POLL_MS = 1500;
  var TICKS_PER_SECOND = 10000000; // .NET/Jellyfin ticks: 100ns units
  var MIN_MARKER_WIDTH_PCT = 0.3;

  var lastItemId = null;
  var segmentsCache = {}; // itemId -> Array<{StartTicks, EndTicks}> (empty array = "checked, none")

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
        return mine ? mine.NowPlayingItem.Id : null;
      })
      .catch(function (err) {
        log('getSessions failed: ' + err);
        return null;
      });
  }

  function fetchCommercialSegments(apiClient, itemId) {
    if (Object.prototype.hasOwnProperty.call(segmentsCache, itemId)) {
      return Promise.resolve(segmentsCache[itemId]);
    }
    return apiClient.getJSON(apiClient.getUrl('MediaSegments/' + itemId))
      .then(function (res) {
        var items = (res && res.Items) || [];
        var segs = items.filter(function (s) { return s.Type === 'Commercial'; });
        segmentsCache[itemId] = segs;
        return segs;
      })
      .catch(function (err) {
        log('MediaSegments fetch failed for ' + itemId + ': ' + err);
        segmentsCache[itemId] = [];
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
      return Promise.resolve();
    }

    return getNowPlayingItemId(apiClient).then(function (itemId) {
      if (!itemId) {
        return;
      }

      var alreadyRendered = itemId === lastItemId
        && osd.markerContainer.querySelector('.comskip-marker');
      if (alreadyRendered) {
        return;
      }

      var durationTicks = osd.video.duration && isFinite(osd.video.duration)
        ? osd.video.duration * TICKS_PER_SECOND
        : 0;

      if (!durationTicks) {
        // Duration not known yet (still opening the stream) — try again next tick
        // without marking this item as done.
        return;
      }

      return fetchCommercialSegments(apiClient, itemId).then(function (segments) {
        lastItemId = itemId;
        renderMarkers(osd.markerContainer, osd.video, segments, durationTicks);
      });
    });
  }

  setInterval(function () {
    tick().catch(function (err) { log('tick failed: ' + err); });
  }, POLL_MS);

  log('overlay script loaded');
})();
