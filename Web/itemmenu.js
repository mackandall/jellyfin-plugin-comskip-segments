/*
 * Comskip Commercial Segments — item-details action-sheet menu entries:
 *   1. "Scan for commercials" (comskip-scan), which follows the item's state each time
 *      the menu opens: "Remove commercials" (comskip-remove) once breaks were found,
 *      "Scanning for commercials…" (disabled) while a scan runs
 *   2. "Fix opening/closing audio format" (audiotrim-apply)
 *   3. "Find program start" (comskip-program-start): a remembered channel card
 * On a show, season or other folder only the scan is offered, saying how many videos it
 * covers ("Scan all 8 episodes for commercials"); the other two work on one file.
 *
 * Injected into jellyfin-web's index.html (see Web/install-overlay.sh, same mechanism
 * as the playback overlay). jellyfin-web has no plugin extension point for the item
 * details page's "..." action sheet, so this watches the DOM for that menu opening and
 * appends matching buttons to it — same markup/classes jellyfin-web's own menu items
 * use, so they look native.
 *
 * VERSION-SENSITIVE FILE — built against this server's Jellyfin 12.0.0 web client.
 * Confirmed live via DOM inspection on 2026-09-11:
 *   - The action sheet container is `.actionSheet.opened`, holding a
 *     `.actionSheetScroller` that directly contains one `button.actionSheetMenuItem`
 *     (also `.listItem.listItem-button.emby-button`, with a `data-id` attribute) per
 *     entry — e.g. `data-id="addtocollection"`, `data-id="refresh"`. Each button holds a
 *     `span.actionsheetMenuItemIcon.listItemIcon.listItemIcon-transparent.material-icons
 *     .<icon-name>` and a `div.listItemBody.actionsheetListItemBody >
 *     div.listItemBodyText.actionSheetItemText` with the label text.
 *   - `window.Dashboard.alert(message)` exists globally (not just on the plugin config
 *     page) and shows a dismissible in-page modal — not a native browser dialog, so it's
 *     safe to use for feedback here.
 *   - `ComskipSegments/Editing` (this plugin) says whether the user may edit recordings.
 * If this breaks after a jellyfin-web update, re-check these against the running client
 * (open the item details page, click "...", inspect the action sheet in devtools).
 *
 * Nothing in the action sheet's own DOM says which item it belongs to, so the item is
 * taken from the "..." that opened it (capture-phase listener, confirmed live on
 * 2026-09-26): a row or card's own menu button is `[data-action="menu"]` inside
 * `.listItem[data-id]` / `.card[data-id]` (e.g. each episode row on a Season page), and
 * right-click on a card/row opens the same sheet; the details page's own "..." is
 * `button.btnMoreCommands`, outside any row, and means the page's item (URL
 * `#/details?id=...`). Using the page's id for everything was a real bug: an episode
 * row's "Scan for commercials" on a Season page scanned the whole season.
 *
 * Both entries always force-run (ignore show/channel filters and any existing result) —
 * same one-click-does-something expectation as jellyfin-web's own "Refresh metadata"
 * entry right next to them. No confirmation prompt for the same reason "Refresh
 * metadata" doesn't have one: the click IS the confirmation. The audio-trim entry is a
 * bigger action (it can rewrite the recording file, not just mark a skip range) but is
 * itself gated server-side by the "Enable audio-format trim" master switch in plugin
 * settings, which returns an explanatory error instead of running when off.
 */
(function () {
  'use strict';

  var canEditPromise = null;

  function log(msg) {
    if (window.console && window.console.debug) {
      window.console.debug('[ComskipMenu] ' + msg);
    }
  }

  function notify(message) {
    if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
      window.Dashboard.alert(message);
    } else {
      log(message);
    }
  }

  // Administrators, and the users the plugin's config page allows to edit recordings.
  function checkCanEdit() {
    if (!canEditPromise) {
      var apiClient = window.ApiClient;
      canEditPromise = apiClient
        ? apiClient.getJSON(apiClient.getUrl('ComskipSegments/Editing'))
          .then(function (r) { return !!(r && r.CanEdit); })
          .catch(function () { return false; })
        : Promise.resolve(false);
    }
    return canEditPromise;
  }

  // The item whose "..." was clicked last (null = the details page's own "..."), and when.
  var menuItemId = null;
  var menuClickedAt = 0;

  function rememberMenuTarget(e, isContextMenu) {
    var target = e.target;
    if (!target || !target.closest) {
      return;
    }

    var holder = target.closest('.card[data-id], .listItem[data-id]');
    if (holder && holder.classList.contains('actionSheetMenuItem')) {
      return; // a click inside an open action sheet, not one opening it
    }

    if (holder && (isContextMenu || target.closest('[data-action="menu"]'))) {
      menuItemId = holder.getAttribute('data-id');
      menuClickedAt = Date.now();
    } else if (target.closest('.btnMoreCommands')) {
      menuItemId = null;
      menuClickedAt = Date.now();
    }
  }

  document.addEventListener('click', function (e) { rememberMenuTarget(e, false); }, true);
  document.addEventListener('contextmenu', function (e) { rememberMenuTarget(e, true); }, true);

  // The item the just-opened action sheet belongs to: the row/card whose "..." opened it,
  // else the details page's item.
  function getMenuItemId() {
    if (menuItemId && Date.now() - menuClickedAt < 5000) {
      return menuItemId;
    }
    return getCurrentItemId();
  }

  function getCurrentItemId() {
    var hash = window.location.hash || '';
    if (hash.indexOf('#/details') !== 0) {
      return null;
    }
    var qIndex = hash.indexOf('?');
    if (qIndex === -1) {
      return null;
    }
    return new URLSearchParams(hash.slice(qIndex + 1)).get('id');
  }

  // The plugin's own reason (e.g. "Item has no resolvable file on disk") when the failed
  // request came back with one; apiClient.ajax rejects with the fetch Response.
  function failureMessage(err, fallback) {
    if (!err || typeof err.json !== 'function') {
      return Promise.resolve(fallback);
    }
    return err.json()
      .then(function (body) { return (body && body.Message) || fallback; })
      .catch(function () { return fallback + ' (HTTP ' + err.status + ')'; });
  }

  function runComskipScan(itemId, button) {
    var apiClient = window.ApiClient;
    button.disabled = true;
    apiClient.ajax({
      type: 'POST',
      url: apiClient.getUrl('ComskipSegments/Detect/' + itemId, { force: true }),
      dataType: 'json'
    }).then(function (r) {
      if (window.__comskipBadgesInvalidate) {
        window.__comskipBadgesInvalidate(itemId);
      }
      notify((r && r.Message) || 'Queued.');
    }).catch(function (err) {
      failureMessage(err, 'Comskip scan request failed.').then(notify);
    }).then(function () {
      button.disabled = false;
    });
  }

  // "Remove commercials": for a scan that shouldn't have happened. Removes only the
  // commercial marks and keeps them removed (automatic detection leaves the item alone);
  // "Scan for commercials" brings them back, so the click is the confirmation, as for
  // the scan itself.
  function runComskipRemove(itemId, button) {
    var apiClient = window.ApiClient;
    button.disabled = true;
    apiClient.ajax({
      type: 'POST',
      url: apiClient.getUrl('ComskipSegments/Clear/' + itemId, { keep: true }),
      dataType: 'json'
    }).then(function (r) {
      if (window.__comskipBadgesInvalidate) {
        window.__comskipBadgesInvalidate(itemId);
      }
      notify(((r && r.Message) || 'Commercials removed.') + ' "Scan for commercials" finds them again if you change your mind.');
    }).catch(function (err) {
      failureMessage(err, 'Removing the commercials failed.').then(notify);
    }).then(function () {
      button.disabled = false;
    });
  }

  // Where Comskip is with the item; null when it can't tell (a season or show, an error).
  function comskipStatus(itemId) {
    var apiClient = window.ApiClient;
    return apiClient.ajax({
      type: 'GET',
      url: apiClient.getUrl('ComskipSegments/Status/' + itemId),
      dataType: 'json'
    }).catch(function () { return null; });
  }

  // The commercials entry for this item's state: scanning now, breaks found, or not yet.
  function commercialsEntry(status) {
    if (status && status.InFlight) {
      return { dataId: 'comskip-scanning', icon: 'hourglass_top', label: 'Scanning for commercials…', disabled: true };
    }
    if (status && status.Status === 'Detected' && status.BreakCount > 0) {
      return { dataId: 'comskip-remove', icon: 'layers_clear', label: 'Remove commercials', run: runComskipRemove };
    }
    return MENU_ITEMS[0];
  }

  function runAudioTrim(itemId, button) {
    var apiClient = window.ApiClient;
    button.disabled = true;
    apiClient.ajax({
      type: 'POST',
      url: apiClient.getUrl('ComskipSegments/AudioTrim/Apply/' + itemId),
      dataType: 'json'
    }).then(function (r) {
      if (r && r.Trimmed) {
        notify(
          'Audio trimmed: removed ' + r.LeadTrimSeconds.toFixed(1) + 's from the start and '
          + r.TrailTrimSeconds.toFixed(1) + 's from the end.'
        );
      } else {
        notify((r && r.Message) || 'Nothing to trim.');
      }
    }).catch(function (err) {
      failureMessage(err, 'Audio-trim request failed — check the feature is enabled in plugin settings.').then(notify);
    }).then(function () {
      button.disabled = false;
      if (window.__comskipCutsRefresh) {
        window.__comskipCutsRefresh();
      }
    });
  }

  // "Find program start": a remembered channel card (set from a paused player with "Remember
  // this card") in the recording's opening minutes; marks or trims per the plugin's setting.
  function runFindProgramStart(itemId, button) {
    var apiClient = window.ApiClient;
    button.disabled = true;
    apiClient.ajax({
      type: 'POST',
      url: apiClient.getUrl('ComskipSegments/ProgramStart/' + itemId + '/Find'),
      dataType: 'json'
    }).then(function (r) {
      notify((r && r.Message) || 'Done.');
    }).catch(function (err) {
      failureMessage(err, 'Finding the program start failed.').then(notify);
    }).then(function () {
      button.disabled = false;
      if (window.__comskipCutsRefresh) {
        window.__comskipCutsRefresh();
      }
    });
  }

  function buildMenuItem(dataId, iconName, label, onClick) {
    var button = document.createElement('button');
    button.setAttribute('is', 'emby-button');
    button.type = 'button';
    button.className = 'listItem listItem-button actionSheetMenuItem emby-button';
    button.setAttribute('data-id', dataId);

    var icon = document.createElement('span');
    icon.className = 'actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons ' + iconName;
    icon.setAttribute('aria-hidden', 'true');

    var bodyText = document.createElement('div');
    bodyText.className = 'listItemBodyText actionSheetItemText';
    bodyText.textContent = label;

    var body = document.createElement('div');
    body.className = 'listItemBody actionsheetListItemBody';
    body.appendChild(bodyText);

    button.appendChild(icon);
    button.appendChild(body);

    // No stopPropagation() here — see the note above the MENU_ITEMS array
    // for why letting the click bubble is what actually closes the sheet.
    button.addEventListener('click', function () {
      onClick(button);
    });

    return button;
  }

  /*
   * Root-caused live via devtools, in three stages:
   *
   * 1. Originally called e.stopPropagation() here, on the assumption that
   *    jellyfin-web's own click handling on the action sheet would otherwise
   *    try to interpret our data-id ("comskip-scan"/"audiotrim-apply",
   *    neither of which it recognizes) as one of its own known actions. That
   *    assumption was never actually verified and turned out to be the bug:
   *    it also blocked whatever closes the sheet, so ours hung open after a
   *    click while every native item dismissed normally (reported: "it
   *    doesn't disappear after you select either it or the commercial skip
   *    option").
   *
   * 2. Tried to close it ourselves instead: an Escape keydown did nothing,
   *    and — more fundamentally — NO synthetic click/mousedown/pointerdown
   *    dispatched via JS closed it either, even targeting the exact
   *    `.dialogBackdrop.dialogBackdropOpened` element directly, while a real
   *    OS-level click at the same coordinates closed it instantly. Browsers
   *    don't let scripts fake a trusted user gesture (`Event.isTrusted`),
   *    and this dismiss handler evidently requires one — no JS-only
   *    workaround exists. Directly removing the dialog/backdrop elements
   *    ourselves "worked" visually but left the action sheet's OWN
   *    open/close bookkeeping desynced from the DOM — confirmed live: every
   *    later attempt to open ANY action sheet on the page silently did
   *    nothing for the rest of that page load. Worse than the original bug.
   *
   * 3. The actual fix: clicking a NATIVE item that doesn't open a follow-up
   *    dialog (e.g. "Copy Stream URL", which just copies to the clipboard
   *    and shows a toast — functionally identical in shape to our two
   *    buttons) closes the sheet too. So closing happens generically on any
   *    item click via normal event bubbling, which our own
   *    stopPropagation() had been blocking the whole time. Removing it
   *    fixes the hang with zero side effects — confirmed live end to end,
   *    including the real audio-trim network call, toast, and a clean
   *    reopen of a fresh action sheet right afterward.
   */

  var MENU_ITEMS = [
    {
      dataId: 'comskip-scan',
      icon: 'content_cut',
      label: 'Scan for commercials',
      run: runComskipScan
    },
    {
      dataId: 'audiotrim-apply',
      icon: 'graphic_eq',
      label: 'Fix opening/closing audio format',
      run: runAudioTrim
    },
    {
      dataId: 'comskip-program-start',
      icon: 'start',
      label: 'Find program start',
      run: runFindProgramStart
    }
  ];

  // The cut under way on this item (program start, audio format), or null.
  function cutFor(itemId) {
    var apiClient = window.ApiClient;
    return apiClient.ajax({
      type: 'GET',
      url: apiClient.getUrl('ComskipSegments/Cuts'),
      dataType: 'json'
    }).then(function (list) {
      var want = (itemId || '').replace(/-/g, '').toLowerCase();
      return (list || []).filter(function (j) { return (j.ItemId || '').replace(/-/g, '').toLowerCase() === want; })[0] || null;
    }).catch(function () { return null; });
  }

  // The item's program start: a skip marker to remove, or a kept original to put back.
  function programStartStatus(itemId) {
    var apiClient = window.ApiClient;
    return apiClient.ajax({
      type: 'GET',
      url: apiClient.getUrl('ComskipSegments/ProgramStart/' + itemId),
      dataType: 'json'
    }).catch(function () { return null; });
  }

  function clock(seconds) {
    var s = Math.round(seconds);
    var m = Math.floor(s / 60);
    return m + ':' + ('0' + (s % 60)).slice(-2);
  }

  function runRemoveProgramStart(itemId, button) {
    var apiClient = window.ApiClient;
    button.disabled = true;
    apiClient.ajax({
      type: 'DELETE',
      url: apiClient.getUrl('ComskipSegments/ProgramStart/' + itemId),
      dataType: 'json'
    }).then(function (r) {
      notify((r && r.Message) || 'Removed.');
    }).catch(function (err) {
      failureMessage(err, 'Removing the program start failed.').then(notify);
    });
  }

  function runUndoTrim(itemId, button) {
    var apiClient = window.ApiClient;
    button.disabled = true;
    apiClient.ajax({
      type: 'POST',
      url: apiClient.getUrl('ComskipSegments/ProgramStart/' + itemId + '/Undo'),
      dataType: 'json'
    }).then(function (r) {
      notify((r && r.Message) || 'Done.');
    }).catch(function (err) {
      failureMessage(err, 'Undoing the trim failed.').then(notify);
    });
  }

  function programStartEntries(status) {
    var entries = [];
    if (status && status.Action === 'marker' && status.StartSeconds >= 1) {
      entries.push({ dataId: 'comskip-remove-start', icon: 'restart_alt', label: 'Remove program start (skips to ' + clock(status.StartSeconds) + ')', run: runRemoveProgramStart });
    }
    if (status && status.CanUndo) {
      var until = status.UndoUntil ? ', until ' + new Date(status.UndoUntil).toLocaleDateString() : '';
      entries.push({ dataId: 'comskip-undo-trim', icon: 'undo', label: 'Undo trim (put the original back' + until + ')', run: runUndoTrim });
    }
    return entries;
  }

  function runStopCut(itemId, button) {
    var apiClient = window.ApiClient;
    button.disabled = true;
    apiClient.ajax({
      type: 'POST',
      url: apiClient.getUrl('ComskipSegments/Cuts/' + itemId + '/Stop'),
      dataType: 'json'
    }).then(function (r) {
      notify((r && r.Message) || 'Stopping.');
    }).catch(function (err) {
      failureMessage(err, 'Could not stop it.').then(notify);
    }).then(function () {
      if (window.__comskipCutsRefresh) {
        window.__comskipCutsRefresh();
      }
    });
  }

  // While a recording is being cut: its progress, and Stop while the original is untouched —
  // instead of the entries that would start a second action on the same file.
  function cuttingEntries(job) {
    var pct = job.Bytes > 0 ? Math.min(99, Math.floor(job.DoneBytes * 100 / job.Bytes)) : 0;
    var label = job.Phase === 'cutting' ? 'Cutting\u2026 ' + pct + '%'
      : job.Phase === 'finishing' ? 'Finishing the cut\u2026 ' + pct + '% (can\u2019t be stopped now)'
        : 'Checking before cutting\u2026';
    var entries = [{ dataId: 'comskip-cutting', icon: 'content_cut', label: label, disabled: true }];
    if (job.Stoppable) {
      entries.push({ dataId: 'comskip-stop-cut', icon: 'stop_circle', label: 'Stop cutting', run: runStopCut });
    }
    return entries;
  }

  // A show, season or other folder: what's under it (the scan entry acts on all of it), or null
  // for a single recording. The audio-format fix and program start only work on one file, and
  // can cut it, so a folder doesn't offer them.
  function folderInfo(itemId) {
    var apiClient = window.ApiClient;
    var userId = apiClient.getCurrentUserId();
    return apiClient.getItem(userId, itemId).then(function (item) {
      if (!item || !item.IsFolder) {
        return null;
      }
      return apiClient.getItems(userId, {
        ParentId: itemId,
        Recursive: true,
        IncludeItemTypes: 'Episode,Movie,Video',
        IsMissing: false,
        Limit: 0
      }).then(function (r) {
        return { type: item.Type, count: (r && r.TotalRecordCount) || 0 };
      });
    }).catch(function () { return null; });
  }

  function folderEntries(folder) {
    if (folder.count === 0) {
      return [];
    }
    var noun = folder.type === 'Series' || folder.type === 'Season'
      ? (folder.count === 1 ? 'episode' : 'episodes')
      : (folder.count === 1 ? 'video' : 'videos');
    var label = folder.count === 1 ? 'Scan its one ' + noun + ' for commercials' : 'Scan all ' + folder.count + ' ' + noun + ' for commercials';
    return [{ dataId: 'comskip-scan', icon: 'content_cut', label: label, run: runComskipScan }];
  }

  function tryInject(scroller) {
    var itemId = getMenuItemId();
    if (!itemId) {
      return;
    }

    checkCanEdit().then(function (canEdit) {
      if (!canEdit) {
        return null;
      }
      return folderInfo(itemId).then(function (folder) {
        if (folder) {
          return folderEntries(folder);
        }
        return Promise.all([comskipStatus(itemId), cutFor(itemId), programStartStatus(itemId)]).then(function (r) {
          return r[1] ? cuttingEntries(r[1]) : [commercialsEntry(r[0])].concat(MENU_ITEMS.slice(1), programStartEntries(r[2]));
        });
      });
    }).then(function (entries) {
      if (!entries || scroller.querySelector('[data-id^="comskip-"]')) {
        return; // may not edit recordings, or already injected (this can fire more than once per open)
      }

      entries.forEach(function (entry) {
        if (scroller.querySelector('[data-id="' + entry.dataId + '"]')) {
          return;
        }

        var button = buildMenuItem(entry.dataId, entry.icon, entry.label, function (b) {
          if (entry.run) {
            entry.run(itemId, b);
          }
        });
        if (entry.disabled) {
          button.disabled = true;
        }
        scroller.appendChild(button);
      });
    });
  }

  var observer = new MutationObserver(function (mutations) {
    for (var i = 0; i < mutations.length; i++) {
      var added = mutations[i].addedNodes;
      for (var j = 0; j < added.length; j++) {
        var node = added[j];
        if (!(node instanceof HTMLElement)) {
          continue;
        }

        var scroller = node.classList && node.classList.contains('actionSheetScroller')
          ? node
          : (node.querySelector && node.querySelector('.actionSheetScroller'));
        if (scroller) {
          tryInject(scroller);
        }
      }
    }
  });

  observer.observe(document.body, { childList: true, subtree: true });

  log('item-menu script loaded');
})();
